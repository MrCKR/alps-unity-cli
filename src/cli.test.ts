import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { access, mkdir, mkdtemp, readFile, symlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute, isCliEntryPoint } from "./cli.js";
import { failure, printJson } from "./output.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import { defaultDataDir } from "./platform.js";

process.env.ALPS_UNITY_ALLOW_NON_WINDOWS = "1";

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
  const child = spawn(process.execPath, [fileURLToPath(new URL("cli.js", import.meta.url)), ...args], {
    env: { ...process.env, ALPS_UNITY_ALLOW_NON_WINDOWS: "1", ...env },
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
  const cliPath = fileURLToPath(new URL("cli.js", import.meta.url));
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
  assert.equal(payload.ok, true);
  assert.equal(payload.data.version, "0.1.0");
});

test("frozen top-level directories exist", async () => {
  for (const directory of ["cli", "unitypkg", "schemas", "skills", "docs", "tests"]) {
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
const fs = require("node:fs");
const logIndex = process.argv.indexOf("-logFile");
if (logIndex >= 0) fs.appendFileSync(process.argv[logIndex + 1], "tests ok\\n");
process.exit(0);
`);

  const created = await execute(["test", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
  assert.equal(created.ok, true);
  const jobId = (created.data as { jobId: string }).jobId;

  const waited = await runtime.runner.wait(jobId, 3000);
  assert.equal(waited.status, "succeeded");

  const status = await execute(["job", "status", "--id", jobId], runtime);
  assert.equal((status.data as { status: string }).status, "succeeded");
});

test("job logs returns tail lines", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`
const fs = require("node:fs");
const logIndex = process.argv.indexOf("-logFile");
if (logIndex >= 0) fs.appendFileSync(process.argv[logIndex + 1], "one\\ntwo\\nthree\\n");
process.exit(0);
`);

  const created = await execute(["build", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  await runtime.runner.wait(jobId, 3000);

  const logs = await execute(["job", "logs", "--id", jobId, "--tail", "2"], runtime);
  assert.deepEqual((logs.data as { lines: string[] }).lines, ["two", "three"]);
});

test("wait timeout returns structured error while job keeps running", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 1000);`);

  const result = await execute(["editor", "refresh", "--compile", "--wait", "--wait-timeout-ms", "10", "--unity", unity, "--project", runtime.dataDir], runtime);
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

  const compile = await execute(["editor", "refresh", "--compile", "--unity", unity, "--project", runtime.dataDir], runtime);
  const refresh = await execute(["editor", "refresh", "--unity", unity, "--project", runtime.dataDir], runtime);

  assert.equal((compile.data as { command: string[] }).command[(compile.data as { command: string[] }).command.indexOf("-executeMethod") + 1], "AlpsUnityCli.Batch.RefreshAndCompile");
  assert.equal((refresh.data as { command: string[] }).command[(refresh.data as { command: string[] }).command.indexOf("-executeMethod") + 1], "AlpsUnityCli.Batch.Refresh");
});

test("cancel marks running job as cancelled", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 5000);`);

  const created = await execute(["build", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  await runtime.runner.wait(jobId, 100);

  const cancelled = await execute(["build", "cancel", "--id", jobId], runtime);
  assert.equal(cancelled.ok, true);
  assert.equal((cancelled.data as { status: string }).status, "cancelled");
});

test("failed Unity exit is captured with summary", async () => {
  const runtime = await makeRuntime();
  const unity = await makeFakeUnity(`process.exit(7);`);

  const created = await execute(["test", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
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

  const status = await execute(["job", "status", "--id", job.id], runtime);
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

  const created = await execute(["test", "run", "--unity", unity, "--project", runtime.dataDir], runtime);
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

  const created = await execute(["build", "run", "--unity", unity, "--project", runtime.dataDir, "--output", "Builds/App.exe"], runtime);
  const command = (created.data as { command: string[] }).command;

  assert.ok(command.includes("-alpsBuildOutput"));
  assert.equal(command[command.indexOf("-alpsBuildOutput") + 1].replace(/\\/g, "/").endsWith("Builds/App.exe"), true);
});

test("non-wait CLI exits quickly and worker persists terminal status", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-process-"));
  assert.equal(defaultDataDir({ ALPS_UNITY_DATA_DIR: dataDir, LOCALAPPDATA: join("C:\\", "Users", "tester", "AppData", "Local") }, "win32"), dataDir);
  const unity = await makeFakeUnity(`setTimeout(() => process.exit(0), 2000);`);

  const created = await runCli(["editor", "refresh", "--compile", "--unity", unity, "--project", dataDir], { ALPS_UNITY_DATA_DIR: dataDir });
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

  const created = await execute(["editor", "refresh", "--compile", "--timeout-ms", "50", "--unity", unity, "--project", runtime.dataDir], runtime);
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

  const list = await execute(["package", "list", "--project", project], runtime);
  assert.equal(list.ok, true);
  const data = list.data as { registry: string; packages: Array<{ name: string; requestedVersion?: string; lockedVersion?: string; source?: string }> };
  assert.equal(data.registry, "https://packages.unity.com");
  assert.deepEqual(data.packages.map((entry) => entry.name), ["com.example.tool", "com.unity.textmeshpro"]);
  assert.equal(data.packages[0].lockedVersion, "1.2.3");

  const info = await execute(["package", "info", "com.example.tool", "--project", project], runtime);
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

  const list = await execute(["package", "list", "--project", project], runtime);
  const info = await execute(["package", "info", "com.example.tool", "--project", project], runtime);

  assert.equal(list.ok, true);
  assert.equal(info.ok, true);
  assert.equal(((info.data as { package: { dependencies: Record<string, string> } }).package.dependencies)["com.unity.textmeshpro"], "3.0.6");
});

test("init deploys bundled bridge into Unity project without requiring Unity", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const initialized = await execute(["init", "--project", project, "--bridge-endpoint", "http://127.0.0.1:9"], runtime);
  assert.equal(initialized.ok, true);
  const data = initialized.data as { action: string; status: string; bridge: { dependencyName: string; bridgePath: string; buildStampPath: string; buildStamp: string }; liveRefresh: { status: string }; resolve: string };
  assert.equal(data.action, "init");
  assert.equal(data.status, "bridge_installed");
  assert.equal(data.bridge.dependencyName, "com.alps.unity-cli.bridge");
  assert.equal(data.liveRefresh.status, "skipped");
  assert.equal(data.resolve, "skipped");
  assert.match(await readFile(join(data.bridge.bridgePath, "Editor", "AlpsUnityBridge.cs"), "utf8"), /class AlpsUnityBridge/);
  assert.match(await readFile(data.bridge.buildStampPath, "utf8"), new RegExp(data.bridge.buildStamp));

  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string> };
  assert.match(manifest.dependencies["com.alps.unity-cli.bridge"], /^file:/);
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

  const installed = await execute(["package", "install", "com.acme.lib@2.0.0", "--project", project, "--unity", unity], runtime);
  assert.equal(installed.ok, true);
  const jobData = installed.data as { kind: string; result: { action: string; package: string; installedVersion: string; bridge: { dependencyName: string; bridgePath: string } } };
  assert.equal(jobData.kind, "package");
  assert.equal(jobData.result.action, "install");
  assert.equal(jobData.result.package, "com.acme.lib");
  assert.equal(jobData.result.installedVersion, "2.0.0");
  assert.equal(jobData.result.bridge.dependencyName, "com.alps.unity-cli.bridge");
  assert.match(await readFile(join(jobData.result.bridge.bridgePath, "Editor", "AlpsUnityBatch.cs"), "utf8"), /ResolvePackages/);
  const bridgeManifest = JSON.parse(await readFile(join(jobData.result.bridge.bridgePath, "package.json"), "utf8")) as { name: string };
  assert.equal(bridgeManifest.name, "com.alps.unity-cli.bridge");

  const manifest = JSON.parse(await readFile(join(project, "Packages", "manifest.json"), "utf8")) as { dependencies: Record<string, string> };
  assert.equal(manifest.dependencies["com.acme.lib"], "2.0.0");
  assert.match(manifest.dependencies["com.alps.unity-cli.bridge"], /^file:/);
});

test("package remove returns structured diagnostic for missing package", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const removed = await execute(["package", "remove", "com.missing", "--project", project], runtime);
  assert.equal(removed.ok, false);
  assert.equal(removed.error?.code, "PACKAGE_NOT_FOUND");
  assert.equal(removed.error?.next_actions?.[0].includes("package list"), true);
});

test("package write commands require an existing Unity manifest", async () => {
  const runtime = await makeRuntime();
  const project = await mkdtemp(join(tmpdir(), "not-unity-project-"));

  const result = await execute(["package", "install", "com.acme.lib", "--project", project], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "MANIFEST_NOT_FOUND");
});

test("package resolve job maps version conflicts to package diagnostics", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();
  const unity = await makeFakeUnity(`
const fs = require("node:fs");
const logIndex = process.argv.indexOf("-logFile");
if (logIndex >= 0) fs.appendFileSync(process.argv[logIndex + 1], "E_PACKAGE_RESOLVE_FAILED: version conflict for com.acme.lib\\n");
process.exit(1);
`);

  const created = await execute(["package", "resolve", "--project", project, "--unity", unity], runtime);
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
const fs = require("node:fs");
const logIndex = process.argv.indexOf("-logFile");
if (logIndex >= 0) fs.appendFileSync(process.argv[logIndex + 1], "Packages\\\\com.alps.unity-cli.bridge\\\\Editor\\\\AlpsUnityBatch.cs(13,17): error CS0815: Cannot assign void to an implicitly-typed variable\\n[Package Manager] UpmClient::Connect -- IPC stream closed\\n");
process.exit(1);
`);

  const created = await execute(["package", "resolve", "--project", project, "--unity", unity], runtime);
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

  const registry = await execute(["package", "registry", "set", "--url", "https://npm.example.com", "--project", project], runtime);
  assert.equal(registry.ok, true);
  assert.equal((registry.data as { registry: string }).registry, "https://npm.example.com");

  const scoped = await execute(["package", "scoped-registry", "add", "--name", "Acme", "--url", "https://registry.acme.test", "--scope", "com.acme,com.acme.tools", "--project", project], runtime);
  assert.equal(scoped.ok, true);
  assert.deepEqual((scoped.data as { registry: { scopes: string[] } }).registry.scopes, ["com.acme", "com.acme.tools"]);

  const list = await execute(["package", "scoped-registry", "list", "--project", project], runtime);
  assert.equal(list.ok, true);
  assert.equal((list.data as { scopedRegistries: Array<{ name: string }> }).scopedRegistries.some((entry) => entry.name === "Acme"), true);
});

test("package resolve without Unity returns structured diagnostic when job requested", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject();

  const result = await execute(["package", "resolve", "--project", project, "--job"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNITY_NOT_FOUND");
});

test("Unity package batch hook exposes PackageManager resolve API", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/AlpsUnityBatch.cs", import.meta.url), "utf8");

  assert.match(source, /public static void ResolvePackages\(\)/);
  assert.match(source, /Client\.Resolve\(\)/);
  assert.match(source, /Client\.List\(false, true\)/);
  assert.doesNotMatch(source, /var\s+\w+\s*=\s*Client\.Resolve\(\)/);
  assert.match(source, /E_PACKAGE_RESOLVE_FAILED/);
});
