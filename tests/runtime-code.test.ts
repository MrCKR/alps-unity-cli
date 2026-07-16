import assert from "node:assert/strict";
import { mkdir, mkdtemp, readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { createServer } from "node:http";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { writeInstances, type UnityInstance } from "../src/instances.js";
import type { BridgeRequest } from "../src/bridge/index.js";
import type { CliResult } from "../src/types.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

function dataOf(result: CliResult): unknown {
  assert.ok(result.ok);
  return result.data;
}

function errorOf(result: CliResult) {
  assert.ok(!result.ok);
  return result.error;
}

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

  const result = await execute(["tools", "run", "code.exec", "--input", JSON.stringify({ file: sourcePath, args: { name: "UnityCli" }, artifactDir }), "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { compile: { ok: true, diagnostics: [] }, runtime: { ok: true, returnValue: 7, stdout: "hi\n", stderr: "" } } };
    }
  });

  assert.ok(result.ok);
  assert.match(requests[0].requestId, /^req_/);
  assert.equal(requests[0].command, "code.exec");
  assert.equal(requests[0].security?.confirmed, true);
  assert.equal(requests[0].security?.toolName, "code.exec");
  assert.equal(requests[0].input!.sourceKind, "file");
  assert.equal(requests[0].input!.sourcePath, sourcePath);
  assert.equal(requests[0].input!.channel, "live");
  assert.equal(requests[0].execution?.timeoutMs, 60000);
  assert.match(String(requests[0].input!.sourceSha256), /^[a-f0-9]{64}$/);
  const manifest = JSON.parse(await readFile(join(artifactDir, "manifest.json"), "utf8")) as { requestId: string; jobId: string | null; command: string; artifact: Array<{ kind: string; path: string; relativePath: string; sha256: string; sizeBytes: number }>; artifacts: Array<{ kind: string; path: string; relativePath: string; sha256: string; sizeBytes: number }>; source: { kind: string; sha256: string } };
  assert.equal(manifest.command, "code.exec");
  assert.equal(manifest.jobId, null);
  assert.equal(manifest.source.kind, "file");
  assert.equal(manifest.source.sha256, requests[0].input!.sourceSha256);
  assert.deepEqual(manifest.artifact.map((artifact) => artifact.kind), ["source"]);
  assert.deepEqual(manifest.artifacts.map((artifact) => artifact.kind), ["source"]);
  assert.equal(manifest.artifact[0].path, sourcePath);
  assert.equal(manifest.artifact[0].sha256, requests[0].input!.sourceSha256);
  assert.match(manifest.artifact[0].relativePath, /hello\.csx$/);
  assert.equal(manifest.artifact[0].sizeBytes, Buffer.byteLength("Console.WriteLine(\"hi\"); return 7;\n", "utf8"));
  assert.deepEqual(manifest.artifacts, manifest.artifact);
});

test("code exec forwards the bridge endpoint execution flag outside business input", async () => {
  const runtime = await makeRuntime();
  let received: BridgeRequest | undefined;
  const server = createServer((request, response) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => { body += chunk; });
    request.on("end", () => {
      received = JSON.parse(body) as BridgeRequest;
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, requestId: received.requestId, data: { status: "completed" } }));
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  const endpoint = `http://127.0.0.1:${address.port}`;

  try {
    const result = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--bridge-endpoint", endpoint, "--yes"], runtime);
    assert.ok(result.ok);
    assert.equal(received?.command, "code.exec");
    assert.equal(received?.security?.confirmed, true);
  } finally {
    await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});

test("code exec resolves the selected instance when no explicit bridge endpoint is passed", async () => {
  const runtime = await makeRuntime();
  let received: BridgeRequest | undefined;
  const server = createServer((request, response) => {
    if (request.method === "GET" && request.url === "/health") {
      const expectedTargetId = request.headers["x-unity-cli-expected-target-id"];
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, requestId: "", data: { bridge: "unity_cli", target: { id: expectedTargetId } } }));
      return;
    }
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => { body += chunk; });
    request.on("end", () => {
      received = JSON.parse(body) as BridgeRequest;
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, requestId: received.requestId, data: { status: "completed" } }));
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  const endpoint = `http://127.0.0.1:${address.port}`;
  const now = new Date().toISOString();
  const instance: UnityInstance = {
    instanceId: "inst_code_exec",
    projectPath: runtime.dataDir,
    projectHash: "code-exec-project",
    bridgeEndpoint: endpoint,
    status: "healthy",
    registeredAt: now,
    lastHeartbeatAt: now
  };
  await writeInstances(runtime.dataDir, [instance]);

  try {
    const result = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--instance", instance.instanceId, "--yes"], runtime);
    assert.ok(result.ok);
    assert.equal(received?.command, "code.exec");
    assert.equal(received?.security?.confirmed, true);
    assert.equal((result.data as { bridgeEndpoint: string }).bridgeEndpoint, endpoint);
  } finally {
    await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});

test("code exec maps live timeout and busy bridge responses to structured envelopes", async () => {
  const runtime = await makeRuntime();
  const running = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--timeout-ms", "1000", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload) => ({
      ok: true,
      requestId: payload.requestId,
      data: { status: "running", requestId: "req_bridge", sideEffectSemantics: "started-before-http-timeout", logPath: "Logs/UnityCliBridge.log" }
    })
  });
  const busy = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--timeout-ms", "2000", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload) => ({
      ok: false,
      requestId: payload.requestId,
      error: {
        code: "E_UNITY_BUSY",
        message: "Unity Editor is compiling or importing assets.",
        nextActions: ["Wait for Unity to finish compiling/importing and retry."],
        transient: false,
        retryAfterMs: 0 }
    })
  });

  assert.ok(!running.ok);
  assert.equal(running.error?.code, "E_EXECUTION_TIMEOUT");
  assert.equal((running.error?.details as { command: string }).command, "code.exec");
  assert.equal((running.error?.details as { channel: string }).channel, "live");
  assert.equal((running.error?.details as { timeoutMs: number }).timeoutMs, 1000);
  assert.equal((running.error?.details as { logPath: string }).logPath, "Logs/UnityCliBridge.log");
  assert.deepEqual((running.error?.details as { audit: { command: string; channel: string; timeoutMs: number } }).audit, { requestId: running.requestId, command: "code.exec", channel: "live", timeoutMs: 1000 });
  assert.deepEqual((running.error?.details as { logs: { bridge: string } }).logs, { bridge: "Logs/UnityCliBridge.log" });
  assert.match(running.error?.nextActions?.join("\n") ?? "", /UnityCliBridge\.log/);
  assert.ok(!busy.ok);
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
    bridgeExecute: async (payload) => ({
      ok: false,
      requestId: payload.requestId,
      error: {
        code: "E_CODE_COMPILE_FAILED",
        message: "Runtime Code compilation failed.",
        details: { diagnostics: [{ id: "CS0103", line: 1, column: 8 }] },
        nextActions: ["Fix compiler diagnostics and rerun code exec."],
          transient: false,
          retryAfterMs: 0
      }
    })
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_CODE_COMPILE_FAILED");
  assert.deepEqual(result.error?.details, { diagnostics: [{ id: "CS0103", line: 1, column: 8 }] });
  assert.deepEqual(result.error?.nextActions, ["Fix compiler diagnostics and rerun code exec."]);
  assert.equal(result.error?.transient, false);
  assert.equal(result.error?.retryAfterMs, 0);
});

test("code exec validates source selection, args json, channel and refs", async () => {
  const runtime = await makeRuntime();

  const missing = await execute(["tools", "run", "code.exec", "--yes"], runtime);
  const both = await execute(["tools", "run", "code.exec", "--input", JSON.stringify({ code: "return 1;", stdin: true }), "--yes"], runtime);
  const args = await execute(["tools", "run", "code.exec", "--input", JSON.stringify({ code: "return 1;", args: [] }), "--yes"], runtime);
  const channel = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--channel", "raw", "--yes"], runtime);
  const refs = await execute(["tools", "run", "code.exec", "--input", JSON.stringify({ code: "return 1;", refs: [join(runtime.dataDir, "missing.dll")] }), "--yes"], runtime);

  assert.equal(errorOf(missing).code, "E_CODE_SOURCE_INVALID");
  assert.equal(errorOf(both).code, "E_CODE_SOURCE_INVALID");
  assert.equal(errorOf(args).code, "E_SCHEMA_INVALID");
  assert.equal(errorOf(channel).code, "E_SCHEMA_INVALID");
  assert.equal(errorOf(refs).code, "E_REFERENCE_RESOLUTION_FAILED");
});

test("code exec strict object input accepts arrays and dry-runs through defineTool", async () => {
  const runtime = await makeRuntime();
  const refPath = join(runtime.dataDir, "Extra.dll");
  const artifactDir = join(runtime.dataDir, "artifacts", "structured");
  await writeFile(refPath, "placeholder", "utf8");

  const result = await execute(["tools", "run", "code.exec", "--input", JSON.stringify({
    code: "return Args[\"answer\"];",
    refs: [refPath],
    define: ["UNITY_CLI_TEST"],
    args: { answer: 42 },
    artifactDir
  }), "--timeout-ms", "123", "--dry-run"], runtime);

  assert.ok(result.ok);
  const data = dataOf(result) as { dryRun: boolean; preview: { sourceKind: string; timeoutMs: number; artifactDir: string; args: { answer: number } }; plannedEffects: Array<{ type: string; channel: string }> };
  assert.equal(data.dryRun, true);
  assert.equal(data.preview.sourceKind, "inline");
  assert.equal(data.preview.timeoutMs, 123);
  assert.equal(data.preview.artifactDir, artifactDir);
  assert.deepEqual(data.preview.args, { answer: 42 });
  assert.deepEqual(data.plannedEffects, [{ type: "code.exec", channel: "live", artifactDir, sideEffects: "user-code-not-executed" }]);
});

test("code exec --channel batchmode creates Unity job and deploys bridge package", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const unity = await makeFakeUnity(runtime.dataDir, "process.exit(0);");
  const sourcePath = join(runtime.dataDir, "job.csx");
  await writeFile(sourcePath, "return 42;", "utf8");

  const created = await execute(["tools", "run", "code.exec", "--file", sourcePath, "--channel", "batchmode", "--unity", unity, "--project", project, "--timeout-ms", "1234", "--yes"], runtime);

  assert.ok(created.ok);
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
  const jobId = (dataOf(created) as { jobId: string }).jobId;
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

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_ARTIFACT_WRITE_FAILED");
  assert.equal((result.error?.details as { artifactDir: string }).artifactDir, artifactFile);
});

test("code wait timeout returns execution timeout details while job continues", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const unity = await makeFakeUnity(runtime.dataDir, `setTimeout(() => process.exit(0), 1000);`);

  const result = await execute(["tools", "run", "code.exec", "--code", "return 42;", "--channel", "batchmode", "--unity", unity, "--project", project, "--wait", "--wait-timeout-ms", "10", "--yes"], runtime);

  assert.ok(!result.ok);
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

test("Runtime Code source exposes diagnostics from its current Infrastructure file", async () => {
  const codeExec = await readFile(new URL("../../unitypkg/Editor/Infrastructure/CodeExecSupport.cs", import.meta.url), "utf8");
  const bridge = await readFile(new URL("../../unitypkg/Editor/UnityCliBridge.cs", import.meta.url), "utf8");
  const packageRefresh = await readFile(new URL("../../unitypkg/Editor/Infrastructure/UnityCliPackageRefresh.cs", import.meta.url), "utf8");
  const batch = await readFile(new URL("../../unitypkg/Editor/UnityCliBatch.cs", import.meta.url), "utf8");

  assert.match(codeExec, /using System\.Linq/);
  assert.match(codeExec, /RegisterCommand\(registry, "code\.exec", "code", true/);
  assert.match(bridge, /SecurityConfirmed\(request\.Command, true, true, request\.Command\)/);
  assert.match(packageRefresh, /"bridge\.package\.refresh"/);
  assert.match(packageRefresh, /AssetDatabase\.Refresh\(ImportAssetOptions\.ForceUpdate\)/);
  assert.match(codeExec, /E_CODE_COMPILE_FAILED/);
  assert.match(codeExec, /E_CODE_RUNTIME_EXCEPTION/);
  assert.match(codeExec, /E_REFERENCE_RESOLUTION_FAILED/);
  assert.match(codeExec, /requestId/);
  assert.match(codeExec, /projectPath/);
  assert.match(codeExec, /instanceId/);
  assert.match(codeExec, /unityVersion/);
  assert.match(codeExec, /bridgeVersion/);
  assert.match(codeExec, /initiator/);
  assert.match(codeExec, /request\.Execution == null \? null : request\.Execution\.TimeoutMs/);
  assert.match(codeExec, /CompilerDiagnostics\(/);
  assert.match(codeExec, /ValidateCodeReferences/);
  assert.match(codeExec, /Runtime Code reference was not found/);
  assert.match(codeExec, /string\.Equals\(Path\.GetFileNameWithoutExtension\(p\), "netstandard"/);
  assert.match(codeExec, /ProjectRoot\(\), "Library", "ScriptAssemblies"/);
  assert.match(codeExec, /Directory\.GetFiles\(scriptAssemblies, "\*\.dll"\)/);
  assert.match(batch, /public static void ExecuteCode\(\)/);
  assert.match(batch, /public static void Refresh\(\)/);
  assert.match(batch, /__UNITY_CLI_EMPTY__/);
  assert.doesNotMatch(batch, /AddReferenceIfExists\([^\n]*netstandard\.dll/);
  assert.doesNotMatch(batch, /name\.Equals\("netstandard"/);
  assert.match(batch, /classic Mono profile/);
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
