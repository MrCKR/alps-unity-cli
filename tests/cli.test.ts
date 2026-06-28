import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { access, mkdir, mkdtemp, readFile, symlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute, isCliEntryPoint } from "../src/cli.js";
import { failure, printJson } from "../src/output.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { defaultDataDir } from "../src/platform.js";
import { isProjectLocked } from "../src/cli-commands/unity-job.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-test-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function makeFakeUnity(body: string): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "fake-unity-"));
  if (process.platform === "win32") {
    const script = join(dir, "UnityFake.js");
    const path = join(dir, "Unity.cmd");
    await writeFile(script, body, "utf8");
    await writeFile(path, `@echo off\r\n"${process.execPath}" "%~dp0UnityFake.js" %*\r\nif errorlevel 1 exit /b %errorlevel%\r\n`, "utf8");
    return path;
  }
  const path = join(dir, "Unity.exe");
  await writeFile(path, `#!/usr/bin/env node\n${body}\n`, { mode: 0o755 });
  return path;
}

async function runCli(args: string[], env: NodeJS.ProcessEnv = {}): Promise<{ code: number | null; stdout: string; stderr: string }> {
  const child = spawn(process.execPath, [fileURLToPath(new URL("../src/cli.js", import.meta.url)), ...args], {
    env: { ...process.env, UNITY_CLI_ALLOW_NON_WINDOWS: "1", ...env },
    stdio: ["ignore", "pipe", "pipe"]
  });

  let stdout = "";
  let stderr = "";
  child.stdout.on("data", (chunk) => {
    stdout += String(chunk);
  });
  child.stderr.on("data", (chunk) => {
    stderr += String(chunk);
  });

  const code = await new Promise<number | null>((resolve) => {
    child.on("close", resolve);
  });
  return { code, stdout, stderr };
}

test("CLI entrypoint detection handles normalized and linked file paths", async () => {
  const cliPath = fileURLToPath(new URL("../src/cli.js", import.meta.url));
  const dir = await mkdtemp(join(tmpdir(), "unity_cli-entrypoint-"));
  const linkedPath = join(dir, "unity_cli");
  await symlink(cliPath, linkedPath);

  assert.equal(isCliEntryPoint(pathToFileURL(cliPath).href, cliPath), true);
  assert.equal(isCliEntryPoint(pathToFileURL(cliPath).href, linkedPath), true);
  assert.equal(isCliEntryPoint(pathToFileURL(cliPath).href, join(cliPath, "..")), false);
});

test("node dist cli help prints an envelope", async () => {
  const result = await runCli(["--help"]);
  assert.equal(result.code, 0, result.stderr);
  const payload = JSON.parse(result.stdout) as { ok: boolean; data: { usage: string } };
  assert.equal(payload.ok, true);
  assert.equal(payload.data.usage, "unity_cli <command> [options]");
});

test("node dist cli version prints package version", async () => {
  const result = await runCli(["--version"]);
  assert.equal(result.code, 0, result.stderr);
  const payload = JSON.parse(result.stdout) as { ok: boolean; data: { version: string } };
  const packageJson = JSON.parse(await readFile(new URL("../package.json", import.meta.url), "utf8")) as { version: string };
  assert.equal(payload.ok, true);
  assert.equal(payload.data.version, packageJson.version);
});

test("frozen top-level directories exist", async () => {
  for (const directory of ["unitypkg", "schemas", "skills", "docs", "tests"]) {
    await access(new URL(`../${directory}/`, import.meta.url));
  }
});

test("final CLI failure envelope preserves structured error details", () => {
  const originalWrite = process.stdout.write;
  let output = "";
  process.stdout.write = ((chunk: string | Uint8Array) => {
    output += String(chunk);
    return true;
  }) as typeof process.stdout.write;

  try {
    printJson(failure("req_ambiguous", "E_TARGET_AMBIGUOUS", "UI hierarchy target is ambiguous: HUD", ["Pass a hierarchy path or instance id."], {
      target: "HUD",
      matchCount: 2,
      matches: [
        { id: 101, name: "HUD", path: "Canvas/HUD", components: ["RectTransform"] },
        { id: 202, name: "HUD", path: "Overlay/HUD", components: ["UIDocument"] }
      ]
    }));
  } finally {
    process.stdout.write = originalWrite;
  }

  const payload = JSON.parse(output) as { ok: boolean; error: { code: string; target: string; matchCount: number; matches: unknown[]; next_actions: string[] } };
  assert.equal(payload.ok, false);
  assert.equal(payload.error.code, "E_TARGET_AMBIGUOUS");
  assert.equal(payload.error.target, "HUD");
  assert.equal(payload.error.matchCount, 2);
  assert.deepEqual(payload.error.matches, [
    { id: 101, name: "HUD", path: "Canvas/HUD", components: ["RectTransform"] },
    { id: 202, name: "HUD", path: "Overlay/HUD", components: ["UIDocument"] }
  ]);
  assert.deepEqual(payload.error.next_actions, ["Pass a hierarchy path or instance id."]);
});

test("test run returns a job and status reaches succeeded", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`
// With -logFile -, Unity writes its log to stdout.
process.stdout.write("tests ok\\n");
process.exit(0);
`);

  const created = await execute(["tools", "run", "test.run", "--unity", unity, "--project", runtime.dataDir], runtime);
  assert.equal(created.ok, true);
  const jobId = (created.data as { jobId: string }).jobId;

  const waited = await runtime.runner.wait(jobId, 3000);
  assert.equal(waited.status, "succeeded");

  const status = await execute(["jobs", "status", jobId], runtime);
  assert.equal((status.data as { status: string }).status, "succeeded");
});

test("job logs returns tail lines", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`
// With -logFile -, Unity writes its log to stdout.
process.stdout.write("one\\ntwo\\nthree\\n");
process.exit(0);
`);

  const created = await execute(["tools", "run", "build.run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  await runtime.runner.wait(jobId, 3000);

  const logs = await execute(["jobs", "logs", jobId, "--tail", "2"], runtime);
  assert.deepEqual((logs.data as { lines: string[] }).lines, ["two", "three"]);

  const buildLogs = await execute(["jobs", "logs", jobId, "--tail", "1"], runtime);
  assert.deepEqual((buildLogs.data as { lines: string[] }).lines, ["three"]);
});

test("wait timeout returns structured error while job keeps running", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 1000);`);

  const result = await execute(["tools", "run", "editor.refresh", "--compile", "--wait", "--wait-timeout-ms", "10", "--unity", unity, "--project", runtime.dataDir], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_EXECUTION_TIMEOUT");
  assert.deepEqual(result.error?.details, {
    jobId: (result.error?.details as { jobId: string }).jobId,
    timeoutMs: 10,
    cancelAttempted: false,
    cancelled: false,
    processKilled: false,
    domainReloadObserved: false
  });
});

test("editor refresh compile uses batch refresh-and-compile method", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(0);`);

  const compile = await execute(["tools", "run", "editor.refresh", "--compile", "--unity", unity, "--project", runtime.dataDir], runtime);
  const refresh = await execute(["tools", "run", "editor.refresh", "--unity", unity, "--project", runtime.dataDir], runtime);

  assert.equal((compile.data as { command: string[] }).command[(compile.data as { command: string[] }).command.indexOf("-executeMethod") + 1], "UnityCli.UnityCliBatch.RefreshAndCompile");
  assert.equal((refresh.data as { command: string[] }).command[(refresh.data as { command: string[] }).command.indexOf("-executeMethod") + 1], "UnityCli.UnityCliBatch.Refresh");
});

test("cancel marks running job as cancelled", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 5000);`);

  const created = await execute(["tools", "run", "build.run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  await runtime.runner.wait(jobId, 100);

  const described = await execute(["jobs", "describe", jobId], runtime);
  assert.equal(described.ok, true);
  assert.equal((described.data as { cancellation: { mode: string } }).cancellation.mode, "immediate");

  const cancelled = await execute(["jobs", "cancel", jobId, "--yes"], runtime);
  assert.equal(cancelled.ok, true);
  assert.equal((cancelled.data as { status: string }).status, "cancelled");
  assert.equal((cancelled.data as { cancellation: { mode: string } }).cancellation.mode, "immediate");
});

test("failed Unity exit is captured with summary", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(7);`);

  const created = await execute(["tools", "run", "test.run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const waited = await runtime.runner.wait(jobId, 3000);

  assert.equal(waited.status, "failed");
  assert.equal(waited.exitCode, 7);
  assert.equal(waited.error?.code, "UNITY_BATCHMODE_FAILED");
});

test("successful Unity job clears stale error from summary", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(0);`);
  const now = new Date().toISOString();
  await mkdir(join(runtime.dataDir, "logs"), { recursive: true });
  const job = await runtime.store.create({
    id: "job_stale_error",
    kind: "package",
    status: "running",
    requestId: "req_stale_error",
    createdAt: now,
    updatedAt: now,
    projectPath: runtime.dataDir,
    unityPath: unity,
    logPath: join(runtime.dataDir, "logs", "job_stale_error.log"),
    command: [unity],
    result: { action: "resolve" },
    error: { code: "UNITY_BATCHMODE_FAILED", message: "Unity exited with code 1." }
  });

  const completed = await runtime.runner.runWorker(job.id);
  assert.equal(completed.status, "succeeded");
  assert.equal(completed.exitCode, 0);
  assert.equal(completed.error, undefined);

  const status = await execute(["jobs", "status", job.id], runtime);
  assert.equal(status.ok, true);
  assert.equal((status.data as { status: string }).status, "succeeded");
  assert.equal("error" in (status.data as Record<string, unknown>), false);
});

test("test run attaches Unity XML result summary", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`
const fs = require("node:fs");
const resultIndex = process.argv.indexOf("-testResults");
if (resultIndex >= 0) fs.writeFileSync(process.argv[resultIndex + 1], '<test-run total="2" passed="1" failed="1" skipped="0"><test-case fullname="Example.Fail"><failure><message>boom &amp; fail</message></failure></test-case></test-run>');
process.exit(1);
`);

  const created = await execute(["tools", "run", "test.run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const waited = await runtime.runner.wait(jobId, 3000);
  const summary = (waited.result?.summary ?? {}) as { total: number; failed: number; failures: Array<{ name: string; message: string }> };

  assert.equal(waited.status, "failed");
  assert.equal(summary.total, 2);
  assert.equal(summary.failed, 1);
  assert.deepEqual(summary.failures, [{ name: "Example.Fail", message: "boom & fail" }]);
});

test("build run forwards output path to batch method", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(0);`);

  const created = await execute(["tools", "run", "build.run", "--unity", unity, "--project", runtime.dataDir, "--output", "Builds/App.exe"], runtime);
  const command = (created.data as { command: string[] }).command;

  assert.ok(command.includes("-unityCliBuildOutput"));
  assert.equal(command[command.indexOf("-unityCliBuildOutput") + 1].replace(/\\/g, "/").endsWith("Builds/App.exe"), true);
});

test("build run accepts multiple targets and returns build job batch", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(0);`);

  const created = await execute(["tools", "run", "build.run", "--unity", unity, "--project", runtime.dataDir, "--targets", "StandaloneWindows64,WebGL", "--output", "Builds/{target}/Player"], runtime);

  assert.equal(created.ok, true);
  const data = created.data as { command: string; mode: string; targetCount: number; jobs: Array<{ jobId: string; result: { target: string; outputPath: string } }> };
  assert.equal(data.command, "build.run");
  assert.equal(data.mode, "batch");
  assert.equal(data.targetCount, 2);
  assert.deepEqual(data.jobs.map((job) => job.result.target), ["StandaloneWindows64", "WebGL"]);
  assert.ok(data.jobs[0].result.outputPath.replace(/\\/g, "/").includes("Builds/StandaloneWindows64/Player"));
  assert.ok(data.jobs[1].result.outputPath.replace(/\\/g, "/").includes("Builds/WebGL/Player"));
});

test("build status and cancel wrap build jobs only", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 5000);`);

  const created = await execute(["tools", "run", "build.run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const status = await execute(["tools", "run", "build.status", "--input", JSON.stringify({ jobId })], runtime);
  const cancelled = await execute(["tools", "run", "build.cancel", "--input", JSON.stringify({ jobId })], runtime);

  assert.equal(status.ok, true);
  assert.equal((status.data as { command: string; jobId: string; kind: string }).command, "build.status");
  assert.equal((status.data as { command: string; jobId: string; kind: string }).kind, "build");
  assert.equal(cancelled.ok, true);
  assert.equal((cancelled.data as { command: string; status: string }).command, "build.cancel");
  assert.equal((cancelled.data as { command: string; status: string }).status, "cancelled");
});

test("non-wait CLI exits quickly and worker persists terminal status", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-process-"));
  assert.equal(defaultDataDir({ UNITY_CLI_DATA_DIR: dataDir, LOCALAPPDATA: join("C:\\", "Users", "tester", "AppData", "Local") }, "win32"), dataDir);
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 2000);`);

  const created = await runCli(["tools", "run", "editor.refresh", "--compile", "--unity", unity, "--project", dataDir], { UNITY_CLI_DATA_DIR: dataDir });
  assert.equal(created.code, 0, created.stderr);
  const payload = JSON.parse(created.stdout) as { ok: boolean; data: { jobId: string; status: string } };
  assert.equal(payload.ok, true);
  assert.equal(payload.data.status, "queued");

  const runtime = { dataDir, store: new JobStore(dataDir), runner: new JobRunner(new JobStore(dataDir), dataDir) };
  const waited = await runtime.runner.wait(payload.data.jobId, 5000);
  assert.equal(waited.status, "succeeded");
});

test("process timeout marks job as timeout", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 5000);`);

  const created = await execute(["tools", "run", "editor.refresh", "--compile", "--timeout-ms", "50", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const waited = await runtime.runner.wait(jobId, 3000);

  assert.equal(waited.status, "timeout");
  assert.equal(waited.error?.code, "E_EXECUTION_TIMEOUT");
  assert.deepEqual(waited.error?.details, {
    cancelAttempted: true,
    cancelled: false,
    processKilled: true,
    domainReloadObserved: false
  });
});

async function makeUnityProject(): Promise<string> {
  const project = await mkdtemp(join(tmpdir(), "unity-project-"));
  const packages = join(project, "Packages");
  await mkdir(packages, { recursive: true });
  await writeFile(join(packages, "manifest.json"), JSON.stringify({
    registry: "https://packages.unity.com",
    dependencies: {
      "com.unity.textmeshpro": "3.0.6",
      "com.example.tool": "1.2.3"
    },
    scopedRegistries: [{ name: "Example", url: "https://registry.example.com", scopes: ["com.example"] }]
  }, null, 2));
  await writeFile(join(packages, "packages-lock.json"), JSON.stringify({
    dependencies: {
      "com.unity.textmeshpro": { version: "3.0.6", source: "registry", depth: 0 },
      "com.example.tool": { version: "1.2.3", source: "registry", dependencies: { "com.unity.textmeshpro": "3.0.6" } }
    }
  }, null, 2));
  return project;
}

test("package list and info read manifest and lock data", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const list = await execute(["tools", "run", "package.list", "--project", project], runtime);
  assert.equal(list.ok, true);
  const data = list.data as { registry: string; packages: Array<{ name: string; requestedVersion?: string; lockedVersion?: string; source?: string }> };
  assert.equal(data.registry, "https://packages.unity.com");
  assert.deepEqual(data.packages.map((entry) => entry.name), ["com.example.tool", "com.unity.textmeshpro"]);
  assert.equal(data.packages[0].lockedVersion, "1.2.3");

  const info = await execute(["tools", "run", "package.info", "com.example.tool", "--project", project], runtime);
  assert.equal(info.ok, true);
  assert.equal(((info.data as { package: { dependencies: Record<string, string> } }).package.dependencies)["com.unity.textmeshpro"], "3.0.6");
});

test("package list and info accept UTF-8 BOM manifest and lock", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const packages = join(project, "Packages");
  const manifest = await readFile(join(packages, "manifest.json"), "utf8");
  const lock = await readFile(join(packages, "packages-lock.json"), "utf8");
  await writeFile(join(packages, "manifest.json"), `\ufeff${manifest}`, "utf8");
  await writeFile(join(packages, "packages-lock.json"), `\ufeff${lock}`, "utf8");

  const list = await execute(["tools", "run", "package.list", "--project", project], runtime);
  const info = await execute(["tools", "run", "package.info", "com.example.tool", "--project", project], runtime);

  assert.equal(list.ok, true);
  assert.equal(info.ok, true);
  assert.equal(((info.data as { package: { dependencies: Record<string, string> } }).package.dependencies)["com.unity.textmeshpro"], "3.0.6");
});

test("init copies com.unity_cli package to Packages/com.unity_cli without requiring Unity", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const initialized = await execute(["init", "--project", project, "--bridge-endpoint", "http://127.0.0.1:9"], runtime);
  assert.equal(initialized.ok, true);
  const data = initialized.data as { action: string; status: string; projectPath: string; package: { status: string; bridge: { dependencyName: string; bridgePath: string; packageVersion: string; sourceHash: string; projectHash: string; installMode: string; installTime: string; cliCompatibilityRange: string; installMetadataPath: string; fileManifestPath: string }; liveRefresh: { status: string } }; liveRefresh: { status: string }; resolve: string };
  assert.equal(data.action, "init");
  assert.equal(data.status, "package_installed");
  assert.equal(data.package.status, "package_installed");
  assert.equal(data.package.bridge.dependencyName, "com.unity_cli");
  assert.equal(data.package.bridge.packageVersion, "0.1.0");
  assert.equal(data.package.bridge.projectHash.length, 64);
  assert.equal(data.package.bridge.sourceHash.length, 64);
  assert.equal(data.package.bridge.installMode, "copy");
  assert.equal(data.package.bridge.cliCompatibilityRange, ">=0.1.0 <0.2.0");
  assert.equal(data.package.liveRefresh.status, "skipped");
  assert.equal(data.resolve, "skipped");
  assert.match(await readFile(join(data.package.bridge.bridgePath, "Editor", "UnityCliBridge.cs"), "utf8"), /class UnityCliBridge/);

  // No file: dependency in manifest — copied package is discovered by Unity directly
  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string>; scopedRegistries?: unknown[] };
  assert.equal(manifest.dependencies["com.unity_cli"], undefined);
  assert.deepEqual(manifest.scopedRegistries, [{ name: "Example", url: "https://registry.example.com", scopes: ["com.example"] }]);

  const metadata = JSON.parse(await readFile(data.package.bridge.installMetadataPath, "utf8")) as { packageName: string; packageVersion: string; sourceHash: string; projectHash: string; installMode: string; installTime: string; cliCompatibilityRange: string };
  assert.equal(metadata.packageName, "com.unity_cli");
  assert.equal(metadata.packageVersion, data.package.bridge.packageVersion);
  assert.equal(metadata.sourceHash, data.package.bridge.sourceHash);
  assert.equal(metadata.projectHash, data.package.bridge.projectHash);
  assert.equal(metadata.installMode, "copy");
  assert.equal(metadata.cliCompatibilityRange, data.package.bridge.cliCompatibilityRange);

  // File manifest should list managed files
  const fileManifest = JSON.parse(await readFile(data.package.bridge.fileManifestPath, "utf8")) as { version: number; files: string[] };
  assert.equal(fileManifest.version, 1);
  assert.ok(fileManifest.files.length > 0);
  assert.ok(fileManifest.files.some((f) => f === "package.json"));
});

test("init --check verifies copy-mode install metadata", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const missing = await execute(["init", "--check", "--project", project], runtime);
  assert.equal(missing.ok, true);
  const missingData = missing.data as { action: string; package: { status: string; code: string; message: string } };
  assert.equal(missingData.package.status, "not_installed");

  await execute(["init", "--project", project, "--bridge-endpoint", "http://127.0.0.1:9"], runtime);
  const checked = await execute(["init", "--check", "--project", project], runtime);

  assert.equal(checked.ok, true);
  const data = checked.data as { action: string; package: { status: string; bridge: { dependencyName: string; packageVersion: string; sourceHash: string; projectHash: string; installMode: string; cliCompatibilityRange: string } } };
  assert.equal(data.action, "init_check");
  assert.equal(data.package.status, "installed");
  assert.equal(data.package.bridge.dependencyName, "com.unity_cli");
  assert.equal(data.package.bridge.installMode, "copy");
  assert.equal(data.package.bridge.packageVersion, "0.1.0");
  assert.equal(data.package.bridge.projectHash.length, 64);
  assert.equal(data.package.bridge.sourceHash.length, 64);
  assert.equal(data.package.bridge.cliCompatibilityRange, ">=0.1.0 <0.2.0");
});

test("init asks an already open bridge to refresh assets", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const requests: Array<{ command: string; args: Record<string, unknown> }> = [];

  const initialized = await execute(["init", "--project", project], {
    ...runtime,
    bridgeExecute: async (payload) => {
      requests.push({ command: payload.command, args: payload.args });
      return { ok: true, data: { command: payload.command, result: { executed: true } } };
    }
  });

  assert.equal(initialized.ok, true);
  assert.deepEqual(requests, [{ command: "editor.menu", args: { path: "Assets/Refresh", timeoutMs: 7000 } }]);
  assert.equal(((initialized.data as { liveRefresh: { status: string } }).liveRefresh.status), "triggered");
});

test("package install updates manifest, deploys bridge and can start package resolve job", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const unity = await makeFakeUnity(`process.exit(0);`);

  const installed = await execute(["tools", "run", "package.install", "com.acme.lib@2.0.0", "--project", project, "--unity", unity, "--yes"], runtime);
  assert.equal(installed.ok, true);
  const jobData = installed.data as { kind: string; result: { action: string; package: string; installedVersion: string; bridge: { dependencyName: string; bridgePath: string; installMode: string } } };
  assert.equal(jobData.kind, "package");
  assert.equal(jobData.result.action, "install");
  assert.equal(jobData.result.package, "com.acme.lib");
  assert.equal(jobData.result.installedVersion, "2.0.0");
  assert.equal(jobData.result.bridge.dependencyName, "com.unity_cli");
  assert.equal(jobData.result.bridge.installMode, "copy");
  assert.match(await readFile(join(jobData.result.bridge.bridgePath, "Editor", "UnityCliBatch.cs"), "utf8"), /ResolvePackages/);
  const bridgeManifest = JSON.parse(await readFile(join(jobData.result.bridge.bridgePath, "package.json"), "utf8")) as { name: string };
  assert.equal(bridgeManifest.name, "com.unity_cli");

  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string> };
  assert.equal(manifest.dependencies["com.acme.lib"], "2.0.0");
  // com.unity_cli is NOT in manifest dependencies (copy mode: no file: reference)
  assert.equal(manifest.dependencies["com.unity_cli"], undefined);
});

test("package install requires an explicit manifest version", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const installed = await execute(["tools", "run", "package.install", "com.acme.lib", "--project", project, "--yes"], runtime);
  assert.equal(installed.ok, false);
  assert.equal(installed.error?.code, "PACKAGE_VERSION_REQUIRED");
  assert.equal(installed.error?.next_actions?.[0].includes("@<version>"), true);
});

test("package remove returns structured diagnostic for missing package", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const removed = await execute(["tools", "run", "package.remove", "com.missing", "--project", project, "--yes"], runtime);
  assert.equal(removed.ok, false);
  assert.equal(removed.error?.code, "PACKAGE_NOT_FOUND");
  assert.equal(removed.error?.next_actions?.[0].includes("tools run package.list"), true);
});

test("package remove with --yes asks an open Editor to refresh via bridge instead of batchmode", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const unity = await makeFakeUnity(`process.exit(0);`);
  const requests: Array<{ command: string; args: Record<string, unknown> }> = [];

  const removed = await execute(["tools", "run", "package.remove", "com.example.tool", "--project", project, "--unity", unity, "--yes"], {
    ...runtime,
    bridgeExecute: async (payload) => {
      requests.push({ command: payload.command, args: payload.args });
      return { ok: true, data: { command: payload.command, result: { executed: true } } };
    }
  });

  assert.equal(removed.ok, true);
  assert.deepEqual(requests, [{ command: "editor.menu", args: { path: "Assets/Refresh", timeoutMs: 7000 } }]);
  const data = removed.data as { action: string; package: string; resolve: string; liveRefresh: { status: string } };
  assert.equal(data.action, "remove");
  assert.equal(data.package, "com.example.tool");
  assert.equal(data.resolve, "live_bridge_refresh");
  assert.equal(data.liveRefresh.status, "triggered");

  // Manifest should have the package removed even though batchmode was not started.
  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string> };
  assert.equal(manifest.dependencies["com.example.tool"], undefined);
});

test("package remove with --yes returns UNITY_BUSY when the project is locked by an open Editor", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const unity = await makeFakeUnity(`process.exit(0);`);
  // Simulate an open Unity Editor by creating the lock file.
  await mkdir(join(project, "Temp"), { recursive: true });
  await writeFile(join(project, "Temp", "UnityLockfile"), "");

  const removed = await execute(["tools", "run", "package.remove", "com.example.tool", "--project", project, "--unity", unity, "--bridge-endpoint", "http://127.0.0.1:9", "--yes"], runtime);

  assert.equal(removed.ok, false);
  assert.equal(removed.error?.code, "UNITY_BUSY");
  assert.equal(removed.error?.transient, true);
  assert.equal((removed.error as unknown as { resolve: string }).resolve, "blocked_by_open_editor");
  assert.equal((removed.error as unknown as { manifestUpdated: boolean }).manifestUpdated, true);

  // Manifest should still have been updated (package removed) before the resolve was blocked.
  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string> };
  assert.equal(manifest.dependencies["com.example.tool"], undefined);
});

test("package remove with --yes returns UNITY_BUSY when bridge refresh fails in an open Editor", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const unity = await makeFakeUnity(`process.exit(0);`);

  const removed = await execute(["tools", "run", "package.remove", "com.example.tool", "--project", project, "--unity", unity, "--yes"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "BRIDGE_COMMAND_FAILED", message: "Unity is compiling" } })
  });

  assert.equal(removed.ok, false);
  assert.equal(removed.error?.code, "UNITY_BUSY");
  assert.equal(removed.error?.transient, true);
  assert.equal((removed.error as unknown as { resolve: string }).resolve, "blocked_by_open_editor");
});

test("package write commands require an existing Unity manifest", async () => {
  const runtime = await makeRuntime();
  const project = await mkdtemp(join(tmpdir(), "not-unity-project-"));

  const result = await execute(["tools", "run", "package.install", "com.acme.lib", "--project", project, "--yes"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "MANIFEST_NOT_FOUND");
});

test("package resolve job maps version conflicts to package diagnostics", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const unity = await makeFakeUnity(`
// With -logFile -, Unity writes its log to stdout.
process.stdout.write("E_PACKAGE_RESOLVE_FAILED: version conflict for com.acme.lib\\n");
process.exit(1);
`);

  const created = await execute(["tools", "run", "package.resolve", "--project", project, "--unity", unity], runtime);
  assert.equal(created.ok, true);
  const waited = await runtime.runner.wait((created.data as { jobId: string }).jobId, 3000);
  assert.equal(waited.status, "failed");
  assert.equal(waited.error?.code, "PACKAGE_VERSION_CONFLICT");
  assert.equal((waited.result?.diagnostic as { code: string }).code, "PACKAGE_VERSION_CONFLICT");
});

test("package resolve job maps bridge compile errors to compile diagnostics", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const unity = await makeFakeUnity(`
// With -logFile -, Unity writes its log to stdout.
process.stdout.write("Packages\\\\com.unity_cli\\\\Editor\\\\UnityCliBatch.cs(13,17): error CS0815: Cannot assign void to an implicitly-typed variable\\n[Package Manager] UpmClient::Connect -- IPC stream closed\\n");
process.exit(1);
`);

  const created = await execute(["tools", "run", "package.resolve", "--project", project, "--unity", unity], runtime);
  assert.equal(created.ok, true);
  const waited = await runtime.runner.wait((created.data as { jobId: string }).jobId, 3000);
  assert.equal(waited.status, "failed");
  assert.equal(waited.error?.code, "BRIDGE_COMPILE_FAILED");
  assert.equal((waited.result?.diagnostic as { code: string }).code, "BRIDGE_COMPILE_FAILED");
  assert.match(waited.error?.message ?? "", /CS0815/);
});

test("package registry and scoped registry commands mutate manifest", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const registry = await execute(["tools", "run", "package.registry", "set", "--url", "https://npm.example.com", "--project", project], runtime);
  assert.equal(registry.ok, true);
  assert.equal((registry.data as { registry: string }).registry, "https://npm.example.com");

  const scoped = await execute(["tools", "run", "package.scoped-registry", "add", "--name", "Acme", "--url", "https://registry.acme.test", "--scope", "com.acme,com.acme.tools", "--project", project], runtime);
  assert.equal(scoped.ok, true);
  assert.deepEqual((scoped.data as { registry: { scopes: string[] } }).registry.scopes, ["com.acme", "com.acme.tools"]);

  const list = await execute(["tools", "run", "package.scoped-registry", "list", "--project", project], runtime);
  assert.equal(list.ok, true);
  assert.equal((list.data as { scopedRegistries: Array<{ name: string }> }).scopedRegistries.some((entry) => entry.name === "Acme"), true);
});

test("package resolve without Unity returns structured diagnostic when job requested", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["tools", "run", "package.resolve", "--project", project, "--job"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNITY_NOT_FOUND");
});

test("Unity package batch hook exposes PackageManager resolve API", async () => {
  const source = await readFile(new URL("../../unitypkg/Editor/UnityCliBatch.cs", import.meta.url), "utf8");

  assert.match(source, /public static void ResolvePackages\(\)/);
  assert.match(source, /Client\.Resolve\(\)/);
  assert.match(source, /Client\.List\(false, true\)/);
  assert.doesNotMatch(source, /var\s+\w+\s*=\s*Client\.Resolve\(\)/);
  assert.match(source, /E_PACKAGE_RESOLVE_FAILED/);
});

test("Unity package batch hook exposes BuildProject executeMethod", async () => {
  const source = await readFile(new URL("../../unitypkg/Editor/UnityCliBatch.cs", import.meta.url), "utf8");

  assert.match(source, /public static void BuildProject\(\)/);
  assert.match(source, /ArgValue\("-unityCliBuildOutput"\)/);
  assert.match(source, /BuildPipeline\.BuildPlayer\(/);
  assert.match(source, /BuildResult\.Succeeded/);
  assert.match(source, /E_BUILD_OUTPUT_REQUIRED/);
});

test("job-routing: test run returns UNITY_BUSY when project is locked by open Editor", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(0);`);
  // Simulate an open Unity Editor by creating the lock file
  await mkdir(join(runtime.dataDir, "Temp"), { recursive: true });
  await writeFile(join(runtime.dataDir, "Temp", "UnityLockfile"), "lock", "utf8");

  const result = await execute(["tools", "run", "test.run", "--unity", unity, "--project", runtime.dataDir], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNITY_BUSY");
  assert.equal(result.error?.transient, true);
  assert.equal(result.error?.retry_after_ms, 15000);
  assert.deepEqual(result.error?.details, {
    projectPath: runtime.dataDir,
    kind: "test",
    lockFilePath: join(runtime.dataDir, "Temp", "UnityLockfile")
  });
  assert.ok((result.error?.next_actions ?? []).length > 0);
  assert.ok((result.error?.next_actions ?? []).some((action) => action.includes("UnityLockfile")));
});

test("job-routing: build run returns UNITY_BUSY when project is locked by open Editor", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(0);`);
  await mkdir(join(runtime.dataDir, "Temp"), { recursive: true });
  await writeFile(join(runtime.dataDir, "Temp", "UnityLockfile"), "lock", "utf8");

  const result = await execute(["tools", "run", "build.run", "--unity", unity, "--project", runtime.dataDir], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNITY_BUSY");
  assert.equal(result.error?.transient, true);
});

test("job-routing: isProjectLocked detects lock file and allows clean project", async () => {
  const project = await mkdtemp(join(tmpdir(), "unity-project-lock-"));
  assert.equal(isProjectLocked(project), false);

  await mkdir(join(project, "Temp"), { recursive: true });
  await writeFile(join(project, "Temp", "UnityLockfile"), "locked", "utf8");
  assert.equal(isProjectLocked(project), true);
});
test("init --agent pi installs unitypkg + pi skill", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--project", project, "--agent", "pi", "--bridge-endpoint", "http://127.0.0.1:9"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.action, "init");
  assert.ok(data.package, "package segment should be present");
  assert.ok(data.skills, "skills segment should be present");

  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills.length, 1);
  assert.equal(skills[0].agent, "pi");
  assert.equal(skills[0].status, "installed");

  const skillContent = await readFile(join(project, ".pi", "skills", "unity_cli", "SKILL.md"), "utf8");
  assert.ok(skillContent.length > 0);
});

test("init --agent pi,claude installs unitypkg + two skills", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--project", project, "--agent", "pi,claude", "--bridge-endpoint", "http://127.0.0.1:9"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.ok(data.package);
  assert.ok(data.skills);

  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills.length, 2);
  assert.equal(skills[0].agent, "pi");
  assert.equal(skills[1].agent, "claude");
});

test("init --pkg-only installs unitypkg only, no skills", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--project", project, "--pkg-only", "--bridge-endpoint", "http://127.0.0.1:9"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.ok(data.package, "package segment should be present");
  assert.equal(data.skills, undefined, "skills should not be present with --pkg-only");
});

test("init --skill-only --agent pi installs only skill, no unitypkg", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--skill-only", "--agent", "pi", "--project", project], runtime);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.package, undefined, "package segment should not be present with --skill-only");
  assert.ok(data.skills, "skills segment should be present");

  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills[0].agent, "pi");
  assert.equal(skills[0].status, "installed");
});

test("init --skill-only without --agent or agent dir returns error", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--skill-only", "--project", project], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "INVALID_INIT_FLAGS");
  assert.match(result.error?.message ?? "", /--skill-only requires --agent or an existing/);
});

test("init --pkg-only --skill-only returns mutual exclusion error", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--pkg-only", "--skill-only", "--agent", "pi", "--project", project], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "INVALID_INIT_FLAGS");
  assert.match(result.error?.message ?? "", /mutually exclusive/);
});

test("init --pkg-only --agent pi returns mutual exclusion error", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--pkg-only", "--agent", "pi", "--project", project], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "INVALID_INIT_FLAGS");
  assert.match(result.error?.message ?? "", /--pkg-only and --agent are mutually exclusive/);
});

test("init --agent unknown returns validation error", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--agent", "unknown", "--project", project], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_AGENT_KIND");
  assert.match(result.error?.message ?? "", /Unknown agent kind/);
});

test("init --check without --agent checks only unitypkg when no agent dir exists", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  // Install first
  await execute(["init", "--project", project, "--bridge-endpoint", "http://127.0.0.1:9"], runtime);

  const checkResult = await execute(["init", "--check", "--project", project], runtime);
  assert.equal(checkResult.ok, true);
  const data = checkResult.data as Record<string, unknown>;
  assert.ok(data.package, "package segment should be present");
  assert.equal(data.skills, undefined, "skills should not be present without an agent directory");
});

test("init --check without --agent checks auto-detected agent skills", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  await mkdir(join(project, ".pi"), { recursive: true });

  // Install first
  await execute(["init", "--project", project, "--bridge-endpoint", "http://127.0.0.1:9"], runtime);

  const checkResult = await execute(["init", "--check", "--project", project], runtime);
  assert.equal(checkResult.ok, true);
  const data = checkResult.data as Record<string, unknown>;
  assert.ok(data.package, "package segment should be present");
  assert.ok(data.skills, "skills segment should be present for detected agent directory");

  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills.length, 1);
  assert.equal(skills[0].agent, "pi");
  assert.equal(skills[0].status, "installed");
});

test("init --check --agent pi checks unitypkg + skill", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  // Install first
  await execute(["init", "--project", project, "--agent", "pi", "--bridge-endpoint", "http://127.0.0.1:9"], runtime);

  const checkResult = await execute(["init", "--check", "--project", project, "--agent", "pi"], runtime);
  assert.equal(checkResult.ok, true);
  const data = checkResult.data as Record<string, unknown>;
  assert.ok(data.package, "package segment should be present");
  assert.ok(data.skills, "skills segment should be present");

  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills[0].agent, "pi");
  assert.equal(skills[0].status, "installed");
});

test("init --check --skill-only --agent pi checks only skill", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  // Install first
  await execute(["init", "--project", project, "--agent", "pi", "--bridge-endpoint", "http://127.0.0.1:9"], runtime);

  const checkResult = await execute(["init", "--check", "--skill-only", "--agent", "pi", "--project", project], runtime);
  assert.equal(checkResult.ok, true);
  const data = checkResult.data as Record<string, unknown>;
  assert.equal(data.package, undefined, "package segment should not be present with --skill-only");
  assert.ok(data.skills, "skills segment should be present");
});

test("init without --agent defaults to unitypkg-only when no agent dir exists", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["init", "--project", project, "--bridge-endpoint", "http://127.0.0.1:9"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.ok(data.package, "package segment should be present");
  assert.equal(data.skills, undefined, "skills should not be present without an agent directory");
});

test("init without --agent installs skill for existing agent directory", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  await mkdir(join(project, ".pi"), { recursive: true });

  const result = await execute(["init", "--project", project, "--bridge-endpoint", "http://127.0.0.1:9"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.ok(data.package, "package segment should be present");
  assert.ok(data.skills, "skills segment should be present for detected agent directory");

  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills.length, 1);
  assert.equal(skills[0].agent, "pi");
  assert.equal(skills[0].status, "installed");

  const skillContent = await readFile(join(project, ".pi", "skills", "unity_cli", "SKILL.md"), "utf8");
  assert.ok(skillContent.length > 0);
});

test("init --check on missing project returns package not_installed", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const checkResult = await execute(["init", "--check", "--project", project], runtime);
  assert.equal(checkResult.ok, true);
  const data = checkResult.data as Record<string, unknown>;
  const pkg = data.package as { status: string; code?: string; message?: string };
  assert.equal(pkg.status, "not_installed");
});

test("init --skill-only without --agent installs skill for existing agent directory", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  await mkdir(join(project, ".claude"), { recursive: true });

  const result = await execute(["init", "--skill-only", "--project", project], runtime);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.package, undefined, "package segment should not be present with --skill-only");
  assert.ok(data.skills, "skills segment should be present for detected agent directory");

  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills.length, 1);
  assert.equal(skills[0].agent, "claude");
  assert.equal(skills[0].status, "installed");
});

test("init --skill-only without --agent installs skill for existing .agents directory", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  await mkdir(join(project, ".agents"), { recursive: true });

  const result = await execute(["init", "--skill-only", "--project", project], runtime);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.package, undefined, "package segment should not be present with --skill-only");
  assert.ok(data.skills, "skills segment should be present for detected agent directory");

  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills.length, 1);
  assert.equal(skills[0].agent, ".agents");
  assert.equal(skills[0].status, "installed");

  const skillContent = await readFile(join(project, ".agents", "skills", "unity_cli", "SKILL.md"), "utf8");
  assert.ok(skillContent.length > 0);
});

test("init --check --agent on missing skill reports missing", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  // Install unitypkg only (not skills)
  await execute(["init", "--project", project, "--bridge-endpoint", "http://127.0.0.1:9"], runtime);

  const checkResult = await execute(["init", "--check", "--agent", "pi", "--project", project], runtime);
  assert.equal(checkResult.ok, true);
  const data = checkResult.data as Record<string, unknown>;
  const skills = data.skills as Array<{ agent: string; status: string }>;
  assert.equal(skills[0].agent, "pi");
  assert.equal(skills[0].status, "missing");
});
