import assert from "node:assert/strict";
import { mkdir, mkdtemp, readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import type { BridgeRequest } from "../src/bridge/index.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const editorUrl = new URL("../../unitypkg/Editor/", import.meta.url);

async function readUnityBridgeSources(): Promise<string> {
  const files = await listCSharpFiles(editorUrl);
  return (await Promise.all(files.map(async (url) => readFile(url, "utf8")))).join("\n");
}

async function listCSharpFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCSharpFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-code-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function makeUnityProject(root: string): Promise<string> {
  const project = join(root, "Project");
  await mkdir(join(project, "Packages"), { recursive: true });
  await writeFile(join(project, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }, null, 2), "utf8");
  return project;
}

async function makeFakeUnity(dataDir: string, body: string): Promise<string> {
  if (process.platform === "win32") {
    const script = join(dataDir, "UnityFake.js");
    const path = join(dataDir, "Unity.cmd");
    await writeFile(script, body, "utf8");
    await writeFile(path, `@echo off\r\n"${process.execPath}" "%~dp0UnityFake.js" %*\r\nif errorlevel 1 exit /b %errorlevel%\r\n`, "utf8");
    return path;
  }
  const path = join(dataDir, "Unity.exe");
  await writeFile(path, `#!/usr/bin/env node\n${body}\n`, { mode: 0o755 });
  return path;
}

test("code exec --file forwards live bridge payload with source hash and artifact manifest", async () => {
  const runtime = await makeRuntime();
  const sourcePath = join(runtime.dataDir, "hello.csx");
  const artifactDir = join(runtime.dataDir, "artifacts", "manual");
  await writeFile(sourcePath, "Console.WriteLine(\"hi\"); return 7;\n", "utf8");
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "code.exec", "--file", sourcePath, "--args", "{\"name\":\"UnityCli\"}", "--artifact-dir", artifactDir, "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { compile: { ok: true, diagnostics: [] }, runtime: { ok: true, returnValue: 7, stdout: "hi\n", stderr: "" } } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "code.exec");
  assert.equal(requests[0].args.sourceKind, "file");
  assert.equal(requests[0].args.sourcePath, sourcePath);
  assert.equal(requests[0].args.channel, "live");
  assert.equal(requests[0].args.timeoutMs, 60000);
  assert.match(String(requests[0].args.sourceSha256), /^[a-f0-9]{64}$/);
  const manifest = JSON.parse(await readFile(join(artifactDir, "manifest.json"), "utf8")) as { requestId: string; jobId: string | null; command: string; artifact: Array<{ kind: string; path: string; relativePath: string; sha256: string; sizeBytes: number }>; artifacts: Array<{ kind: string; path: string; relativePath: string; sha256: string; sizeBytes: number }>; source: { kind: string; sha256: string } };
  assert.equal(manifest.command, "code.exec");
  assert.equal(manifest.jobId, null);
  assert.equal(manifest.source.kind, "file");
  assert.equal(manifest.source.sha256, requests[0].args.sourceSha256);
  assert.deepEqual(manifest.artifact.map((artifact) => artifact.kind), ["source"]);
  assert.deepEqual(manifest.artifacts.map((artifact) => artifact.kind), ["source"]);
  assert.equal(manifest.artifact[0].path, sourcePath);
  assert.equal(manifest.artifact[0].sha256, requests[0].args.sourceSha256);
  assert.match(manifest.artifact[0].relativePath, /hello\.csx$/);
  assert.equal(manifest.artifact[0].sizeBytes, Buffer.byteLength("Console.WriteLine(\"hi\"); return 7;\n", "utf8"));
  assert.deepEqual(manifest.artifacts, manifest.artifact);
});

test("code exec maps live timeout and busy bridge responses to structured envelopes", async () => {
  const runtime = await makeRuntime();
  const running = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--timeout-ms", "1000", "--yes"], {
    ...runtime,
    bridgeExecute: async () => ({
      ok: true,
      data: { status: "running", requestId: "req_bridge", sideEffectSemantics: "started-before-http-timeout", logPath: "Logs/UnityCliBridge.log" }
    })
  });
  const busy = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--timeout-ms", "2000", "--yes"], {
    ...runtime,
    bridgeExecute: async () => ({
      ok: false,
      error: { code: "E_UNITY_BUSY", message: "Unity Editor is compiling or importing assets." },
      nextActions: ["Wait for Unity to finish compiling/importing and retry."]
    })
  });

  assert.equal(running.ok, false);
  assert.equal(running.error?.code, "E_EXECUTION_TIMEOUT");
  assert.equal((running.error?.details as { command: string }).command, "code.exec");
  assert.equal((running.error?.details as { channel: string }).channel, "live");
  assert.equal((running.error?.details as { timeoutMs: number }).timeoutMs, 1000);
  assert.equal((running.error?.details as { logPath: string }).logPath, "Logs/UnityCliBridge.log");
  assert.deepEqual((running.error?.details as { audit: { command: string; channel: string; timeoutMs: number } }).audit, { requestId: running.requestId, command: "code.exec", channel: "live", timeoutMs: 1000 });
  assert.deepEqual((running.error?.details as { logs: { bridge: string } }).logs, { bridge: "Logs/UnityCliBridge.log" });
  assert.match(running.error?.next_actions?.join("\n") ?? "", /UnityCliBridge\.log/);
  assert.equal(busy.ok, false);
  assert.equal(busy.error?.code, "E_UNITY_BUSY");
  assert.equal((busy.error?.details as { requestId: string }).requestId, busy.requestId);
  assert.equal((busy.error?.details as { command: string }).command, "code.exec");
  assert.equal((busy.error?.details as { channel: string }).channel, "live");
  assert.equal((busy.error?.details as { timeoutMs: number }).timeoutMs, 2000);
  assert.deepEqual((busy.error?.details as { audit: { command: string; channel: string; timeoutMs: number } }).audit, { requestId: busy.requestId, command: "code.exec", channel: "live", timeoutMs: 2000 });
  assert.deepEqual((busy.error?.details as { logs: { bridge: string } }).logs, { bridge: "Unity project Logs/UnityCliBridge.log" });
});

test("code exec preserves compile diagnostics from bridge errors", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "code.exec", "--code", "return missing;", "--yes"], {
    ...runtime,
    bridgeExecute: async () => ({
      ok: false,
      error: { code: "E_CODE_COMPILE_FAILED", message: "Runtime Code compilation failed.", details: { diagnostics: [{ id: "CS0103", line: 1, column: 8 }] } },
      nextActions: ["Fix compiler diagnostics and rerun code exec."]
    })
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CODE_COMPILE_FAILED");
  assert.deepEqual(result.error?.details, { diagnostics: [{ id: "CS0103", line: 1, column: 8 }] });
});

test("code exec validates source selection, args json, channel and refs", async () => {
  const runtime = await makeRuntime();

  const missing = await execute(["tools", "run", "code.exec"], runtime);
  const both = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--stdin"], runtime);
  const args = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--args", "[]", "--yes"], runtime);
  const channel = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--channel", "raw", "--yes"], runtime);
  const refs = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--refs", join(runtime.dataDir, "missing.dll"), "--yes"], runtime);

  assert.equal(missing.error?.code, "E_CODE_SOURCE_INVALID");
  assert.equal(both.error?.code, "E_CODE_SOURCE_INVALID");
  assert.equal(args.error?.code, "E_SCHEMA_INVALID");
  assert.equal(channel.error?.code, "E_SCHEMA_INVALID");
  assert.equal(refs.error?.code, "E_REFERENCE_RESOLUTION_FAILED");
});

test("code exec --channel batchmode creates Unity job and deploys bridge package", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const unity = await makeFakeUnity(runtime.dataDir, "process.exit(0);");
  const sourcePath = join(runtime.dataDir, "job.csx");
  await writeFile(sourcePath, "return 42;", "utf8");

  const created = await execute(["tools", "run", "code.exec", "--file", sourcePath, "--channel", "batchmode", "--unity", unity, "--project", project, "--timeout-ms", "1234", "--yes"], runtime);

  assert.equal(created.ok, true);
  const data = created.data as { jobId: string; kind: string; command: string[]; result: { command: string; execution: { channel: string; timeoutMs: number }; bridge: { dependencyName: string } } };
  assert.equal(data.kind, "code");
  assert.equal(data.result.command, "code.exec");
  assert.equal(data.result.execution.channel, "batchmode");
  assert.equal(data.result.execution.timeoutMs, 1234);
  assert.equal(data.result.bridge.dependencyName, "com.unity_cli");
  assert.ok(data.command.includes("UnityCli.UnityCliBatch.ExecuteCode"));
  assert.ok(data.command.includes("-unityCliCodeSource"));
  assert.ok(data.command.includes("-unityCliTimeoutMs"));
  assert.ok(data.command.includes("-unityCliProjectPath"));
  assert.equal(data.command[data.command.indexOf("-unityCliCodeRefs") + 1], "__UNITY_CLI_EMPTY__");

  const manifest = JSON.parse(await readFile(join(runtime.dataDir, "artifacts", created.requestId, "manifest.json"), "utf8")) as { jobId: string; artifact: Array<{ relativePath: string; sha256: string }> };
  assert.equal(manifest.jobId, data.jobId);
  assert.equal(manifest.artifact[0].sha256.length, 64);
});

test("code job reads batchmode result artifact into job summary", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const unity = await makeFakeUnity(runtime.dataDir, `
const fs = require("node:fs");
const resultIndex = process.argv.indexOf("-unityCliCodeResult");
if (resultIndex >= 0) fs.writeFileSync(process.argv[resultIndex + 1], JSON.stringify({ ok: true, requestId: "req_fake", data: { compile: { ok: true, diagnostics: [] }, runtime: { ok: true, returnValue: 42, stdout: "", stderr: "" } } }));
process.exit(0);
`);

  const created = await execute(["tools", "run", "code.exec", "--code", "return 42;", "--channel", "batchmode", "--unity", unity, "--project", project, "--yes"], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const waited = await runtime.runner.wait(jobId, 3000);

  assert.equal(waited.status, "succeeded");
  const artifacts = waited.result?.artifacts as Array<{ kind: string; relativePath: string; sha256: string; sizeBytes: number }>;
  assert.deepEqual(waited.result?.data, { compile: { ok: true, diagnostics: [] }, runtime: { ok: true, returnValue: 42, stdout: "", stderr: "" }, artifacts });
  assert.deepEqual(artifacts.map((artifact) => artifact.kind), ["source", "result"]);
  assert.equal(artifacts[0].relativePath, "source.csx");
  assert.equal(artifacts[1].relativePath, "result.json");
  assert.match(artifacts[1].sha256, /^[a-f0-9]{64}$/);
  assert.ok(artifacts[1].sizeBytes > 0);

  const artifactDir = waited.result?.artifactDir as string;
  const manifest = JSON.parse(await readFile(join(artifactDir, "manifest.json"), "utf8")) as { artifact: Array<{ kind: string; relativePath: string; sha256: string; sizeBytes: number }>; artifacts: Array<{ kind: string; relativePath: string; sha256: string; sizeBytes: number }> };
  assert.deepEqual(manifest.artifact, artifacts);
  assert.deepEqual(manifest.artifacts, artifacts);
});

test("code exec maps artifact manifest write failures", async () => {
  const runtime = await makeRuntime();
  const artifactFile = join(runtime.dataDir, "not-a-directory");
  await writeFile(artifactFile, "occupied", "utf8");

  const result = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--artifact-dir", artifactFile, "--yes"], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_ARTIFACT_WRITE_FAILED");
  assert.equal((result.error?.details as { artifactDir: string }).artifactDir, artifactFile);
});

test("code wait timeout returns execution timeout details while job continues", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const unity = await makeFakeUnity(runtime.dataDir, `setTimeout(() => process.exit(0), 1000);`);

  const result = await execute(["tools", "run", "code.exec", "--code", "return 42;", "--channel", "batchmode", "--unity", unity, "--project", project, "--wait", "--wait-timeout-ms", "10", "--yes"], runtime);

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

test("Unity bridge and batch source expose Runtime Code diagnostics", async () => {
  const bridge = await readUnityBridgeSources();
  const batch = await readFile(new URL("../../unitypkg/Editor/UnityCliBatch.cs", import.meta.url), "utf8");

  assert.match(bridge, /private static HttpListener listener/);
  assert.match(bridge, /using System\.Linq/);
  assert.match(bridge, /RegisterCommand\(registry, "code\.exec", "code", true/);
  assert.match(bridge, /E_CODE_COMPILE_FAILED/);
  assert.match(bridge, /E_CODE_RUNTIME_EXCEPTION/);
  assert.match(bridge, /E_REFERENCE_RESOLUTION_FAILED/);
  assert.match(bridge, /requestId/);
  assert.match(bridge, /projectPath/);
  assert.match(bridge, /instanceId/);
  assert.match(bridge, /unityVersion/);
  assert.match(bridge, /bridgeVersion/);
  assert.match(bridge, /initiator/);
  assert.match(bridge, /timeoutMs/);
  assert.match(bridge, /CompilerDiagnosticsJson/);
  assert.match(bridge, /ValidateCodeReferences/);
  assert.match(bridge, /Runtime Code reference was not found/);
  assert.match(bridge, /EditorApplication\.applicationContentsPath, "Managed", "netstandard\.dll"/);
  assert.match(bridge, /name\.Equals\("netstandard"/);
  assert.match(bridge, /ProjectRoot\(\), "Library", "ScriptAssemblies"/);
  assert.match(bridge, /Directory\.GetFiles\(scriptAssemblies, "\*\.dll"\)/);
  assert.match(bridge, /builder\.Append\('\\\\'\)/);
  assert.match(batch, /public static void ExecuteCode\(\)/);
  assert.match(batch, /public static void Refresh\(\)/);
  assert.match(batch, /__UNITY_CLI_EMPTY__/);
  assert.match(batch, /netstandard\.dll/);
  assert.match(batch, /Library", "ScriptAssemblies/);
  assert.match(batch, /-unityCliCodeResult/);
  assert.match(batch, /-unityCliTimeoutMs/);
  assert.match(batch, /-unityCliProjectPath/);
  assert.match(batch, /E_CODE_SOURCE_INVALID/);
  assert.match(batch, /E_REFERENCE_RESOLUTION_FAILED/);
  assert.match(batch, /nextActions/);
  assert.match(batch, /requestId/);
  assert.match(batch, /projectPath/);
  assert.match(batch, /instanceId/);
  assert.match(batch, /unityVersion/);
  assert.match(batch, /bridgeVersion/);
  assert.match(batch, /initiator/);
  assert.match(batch, /timeoutMs/);
  assert.match(batch, /ValidateReferences/);
  assert.match(batch, /Runtime Code reference was not found/);
});
