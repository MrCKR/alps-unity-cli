import assert from "node:assert/strict";
import { mkdir, mkdtemp, readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { createServer } from "node:http";
import test from "node:test";
import { execute as executeCli } from "../src/cli.js";
import { withInvocationOrigin } from "../src/invocation-origin.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { clearProjectIdentityCache } from "../src/project-identity.js";
import { createUnityProject, makeRegistry, makeRegistrySession, registryIdentity, writeRegistry } from "./helpers/instance-registry-v2.js";
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

/** 本文件验证 frozen batch/deferred Runtime Code 实现；普通 direct 拒绝由 invocation-origin contract tests 覆盖。 */
function execute(argv: string[], runtime: Parameters<typeof executeCli>[1]): Promise<CliResult> {
  return withInvocationOrigin("batch-frozen", () => executeCli(argv, runtime));
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
  await createUnityProject(project);
  await writeFile(join(project, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }, null, 2), "utf8");
  clearProjectIdentityCache();
  return project;
}

/** 为注入 transport 的测试保留真实 strict identity selection 边界。 */
async function registerReadyInstance(dataDir: string, projectPath: string): Promise<{ instanceId: string; close: () => Promise<void> }> {
  const identity = registryIdentity(projectPath);
  let session = await makeRegistrySession("http://127.0.0.1:1");
  const server = createServer((request, response) => {
    if (request.method !== "GET" || request.url !== "/health") {
      response.writeHead(404);
      response.end();
      return;
    }
    const address = server.address();
    assert.ok(address && typeof address === "object");
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ ok: true, requestId: "", data: {
      version: 2,
      instanceId: identity.instanceId,
      canonicalProjectPath: identity.canonicalProjectPath,
      canonicalizationVersion: 1,
      editorSessionId: session.editorSessionId,
      listenerId: session.listener.listenerId,
      actualPort: address.port,
      buildStamp: session.buildStamp,
    } }));
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  const endpoint = `http://127.0.0.1:${address.port}`;
  session = await makeRegistrySession(endpoint, {
    editorSessionId: session.editorSessionId,
    listener: { ...session.listener, endpoint: { ...session.listener.endpoint, actualPort: address.port, preferredPort: address.port, url: endpoint } },
  });
  await writeRegistry(dataDir, makeRegistry(identity, [session]));
  return {
    instanceId: identity.instanceId,
    close: () => new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve())),
  };
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

test("frozen code exec --file forwards bridge payload with source hash and artifact manifest", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const selection = await registerReadyInstance(runtime.dataDir, project);
  const sourcePath = join(runtime.dataDir, "hello.csx");
  const artifactDir = join(runtime.dataDir, "artifacts", "manual");
  await writeFile(sourcePath, "Console.WriteLine(\"hi\"); return 7;\n", "utf8");
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "code.exec", "--input", JSON.stringify({ file: sourcePath, args: { name: "UnityCli" }, artifactDir }), "--instance", selection.instanceId, "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { execution: { exitState: "completed" }, compile: { ok: true, diagnostics: [] }, runtime: { ok: true, returnValue: 7, stdout: "hi\n", stderr: "" } } };
    }
  });

  assert.ok(result.ok);
  await selection.close();
  assert.match(requests[0].requestId, /^req_/);
  assert.equal(requests[0].command, "code.exec");
  assert.equal(requests[0].execution.securityProof?.confirmed, true);
  assert.equal(requests[0].execution.securityProof?.toolName, "code.exec");
  assert.equal(requests[0].input!.sourceKind, "file");
  assert.equal(requests[0].input!.sourcePath, sourcePath);
  assert.equal(requests[0].input!.channel, "live");
  const deadlineMs = Date.parse(requests[0].execution.absoluteDeadlineUtc);
  assert.ok(deadlineMs > Date.now() && deadlineMs <= Date.now() + 60_000);
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

test("code exec rejects the removed public bridge endpoint bypass", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--bridge-endpoint", "http://127.0.0.1:47392", "--yes"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error.code, "E_OBSOLETE_OPTION");
});

test("frozen code exec resolves strict v2 instance identity without leaking endpoint", async () => {
  const runtime = await makeRuntime();
  await createUnityProject(runtime.dataDir);
  clearProjectIdentityCache();
  const identity = registryIdentity(runtime.dataDir);
  let received: BridgeRequest | undefined;
  let session = await makeRegistrySession("http://127.0.0.1:1");
  const server = createServer((request, response) => {
    if (request.method === "GET" && request.url === "/health") {
      const address = server.address();
      assert.ok(address && typeof address === "object");
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, requestId: "", data: {
        version: 2,
        instanceId: identity.instanceId,
        canonicalProjectPath: identity.canonicalProjectPath,
        canonicalizationVersion: 1,
        editorSessionId: session.editorSessionId,
        listenerId: session.listener.listenerId,
        actualPort: address.port,
        buildStamp: session.buildStamp,
      } }));
      return;
    }
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => { body += chunk; });
    request.on("end", () => {
      received = JSON.parse(body) as BridgeRequest;
      response.writeHead(200, {
        "content-type": "application/json",
        "x-unity-cli-instance-id": identity.instanceId,
        "x-unity-cli-editor-session-id": session.editorSessionId,
        "x-unity-cli-listener-id": session.listener.listenerId,
        "x-unity-cli-listener-generation": String(session.listener.generation),
      });
      response.end(JSON.stringify({ ok: true, requestId: received.requestId, data: { execution: { exitState: "completed" }, compile: { ok: true, diagnostics: [] }, runtime: { ok: true, returnValue: 1, stdout: "", stderr: "" } } }));
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  const endpoint = `http://127.0.0.1:${address.port}`;
  session = await makeRegistrySession(endpoint, { editorSessionId: session.editorSessionId, listener: { ...session.listener, endpoint: { ...session.listener.endpoint, actualPort: address.port, preferredPort: address.port, url: endpoint } } });
  await writeRegistry(runtime.dataDir, makeRegistry(identity, [session]));

  try {
    const result = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--instance", identity.instanceId, "--yes"], runtime);
    assert.ok(result.ok);
    assert.equal(received?.command, "code.exec");
    assert.equal(received?.execution.securityProof?.confirmed, true);
    assert.doesNotMatch(JSON.stringify(result.data), /bridgeEndpoint|listenerId|actualPort/);
  } finally {
    await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});

test("frozen code exec maps busy bridge responses to structured envelopes", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const selection = await registerReadyInstance(runtime.dataDir, project);
  const intermediate = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--timeout-ms", "1000", "--instance", selection.instanceId, "--yes"], {
    ...runtime,
    bridgeExecute: async (payload) => ({ ok: true, requestId: payload.requestId, data: { status: "running" } })
  });
  const busy = await execute(["tools", "run", "code.exec", "--code", "return 1;", "--timeout-ms", "2000", "--instance", selection.instanceId, "--yes"], {
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

  assert.ok(!intermediate.ok);
  assert.equal(intermediate.error?.code, "E_TOOL_COMPLETION_PROOF_INVALID");
  assert.ok(!busy.ok);
  assert.equal(busy.error?.code, "E_UNITY_BUSY");
  assert.equal((busy.error?.details as { requestId: string }).requestId, busy.requestId);
  assert.equal((busy.error?.details as { command: string }).command, "code.exec");
  assert.equal((busy.error?.details as { channel: string }).channel, "live");
  assert.equal((busy.error?.details as { timeoutMs: number }).timeoutMs, 2000);
  assert.deepEqual((busy.error?.details as { audit: { command: string; channel: string; timeoutMs: number } }).audit, { requestId: busy.requestId, command: "code.exec", channel: "live", timeoutMs: 2000 });
  assert.deepEqual((busy.error?.details as { logs: { bridge: string } }).logs, { bridge: "Unity project Logs/UnityCliBridge.log" });
  await selection.close();
});

test("frozen code exec preserves compile diagnostics from bridge errors", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const selection = await registerReadyInstance(runtime.dataDir, project);
  const result = await execute(["tools", "run", "code.exec", "--code", "return missing;", "--instance", selection.instanceId, "--yes"], {
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
  await selection.close();
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
  const protocolRequest = await readFile(new URL("../../unitypkg/Editor/Infrastructure/UnityCliProtocolRequest.cs", import.meta.url), "utf8");
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
  assert.doesNotMatch(codeExec, /Execution\.TimeoutMs/);
  assert.match(protocolRequest, /Execution\.AbsoluteDeadlineUtc/);
  assert.match(protocolRequest, /TryDeadlineRemainingMs/);
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
