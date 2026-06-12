import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "./cli.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import type { BridgeRequest } from "./bridge-commands.js";

process.env.ALPS_UNITY_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "alps-unity-code-"));
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

  const result = await execute(["code", "exec", "--file", sourcePath, "--args", "{\"name\":\"Alps\"}", "--artifact-dir", artifactDir], {
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
  const manifest = JSON.parse(await readFile(join(artifactDir, "manifest.json"), "utf8")) as { requestId: string; jobId: string | null; command: string; artifact: Array<{ relativePath: string; sha256: string }>; artifacts: Array<{ relativePath: string; sha256: string }>; source: { kind: string; sha256: string } };
  assert.equal(manifest.command, "code.exec");
  assert.equal(manifest.jobId, null);
  assert.equal(manifest.source.kind, "file");
  assert.equal(manifest.source.sha256, requests[0].args.sourceSha256);
  assert.equal(manifest.artifact[0].sha256, requests[0].args.sourceSha256);
  assert.match(manifest.artifact[0].relativePath, /hello\.csx$/);
  assert.equal(manifest.artifacts[0].sha256, requests[0].args.sourceSha256);
});

test("code exec preserves compile diagnostics from bridge errors", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["code", "exec", "--code", "return missing;"], {
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

test("code exec validates source selection, args json and channel", async () => {
  const runtime = await makeRuntime();

  const missing = await execute(["code", "exec"], runtime);
  const both = await execute(["code", "exec", "--code", "return 1;", "--stdin"], runtime);
  const args = await execute(["code", "exec", "--code", "return 1;", "--args", "[]"], runtime);
  const channel = await execute(["code", "exec", "--code", "return 1;", "--channel", "raw"], runtime);

  assert.equal(missing.error?.code, "E_CODE_SOURCE_INVALID");
  assert.equal(both.error?.code, "E_CODE_SOURCE_INVALID");
  assert.equal(args.error?.code, "E_SCHEMA_INVALID");
  assert.equal(channel.error?.code, "E_SCHEMA_INVALID");
});

test("code exec --channel batchmode creates Unity job and deploys bridge package", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const unity = await makeFakeUnity(runtime.dataDir, "process.exit(0);");
  const sourcePath = join(runtime.dataDir, "job.csx");
  await writeFile(sourcePath, "return 42;", "utf8");

  const created = await execute(["code", "exec", "--file", sourcePath, "--channel", "batchmode", "--unity", unity, "--project", project, "--timeout-ms", "1234"], runtime);

  assert.equal(created.ok, true);
  const data = created.data as { jobId: string; kind: string; command: string[]; result: { command: string; execution: { channel: string; timeoutMs: number }; bridge: { dependencyName: string } } };
  assert.equal(data.kind, "code");
  assert.equal(data.result.command, "code.exec");
  assert.equal(data.result.execution.channel, "batchmode");
  assert.equal(data.result.execution.timeoutMs, 1234);
  assert.equal(data.result.bridge.dependencyName, "com.alps.unity-cli.bridge");
  assert.ok(data.command.includes("AlpsUnityCli.Batch.ExecuteCode"));
  assert.ok(data.command.includes("-alpsCodeSource"));
  assert.ok(data.command.includes("-alpsTimeoutMs"));
  assert.ok(data.command.includes("-alpsProjectPath"));

  const manifest = JSON.parse(await readFile(join(runtime.dataDir, "artifacts", created.requestId, "manifest.json"), "utf8")) as { jobId: string; artifact: Array<{ relativePath: string; sha256: string }> };
  assert.equal(manifest.jobId, data.jobId);
  assert.equal(manifest.artifact[0].sha256.length, 64);
});

test("code job reads batchmode result artifact into job summary", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const unity = await makeFakeUnity(runtime.dataDir, `
const fs = require("node:fs");
const resultIndex = process.argv.indexOf("-alpsCodeResult");
if (resultIndex >= 0) fs.writeFileSync(process.argv[resultIndex + 1], JSON.stringify({ ok: true, requestId: "req_fake", data: { compile: { ok: true, diagnostics: [] }, runtime: { ok: true, returnValue: 42, stdout: "", stderr: "" } } }));
process.exit(0);
`);

  const created = await execute(["code", "exec", "--code", "return 42;", "--channel", "batchmode", "--unity", unity, "--project", project], runtime);
  const jobId = (created.data as { jobId: string }).jobId;
  const waited = await runtime.runner.wait(jobId, 3000);

  assert.equal(waited.status, "succeeded");
  assert.deepEqual(waited.result?.data, { compile: { ok: true, diagnostics: [] }, runtime: { ok: true, returnValue: 42, stdout: "", stderr: "" } });
});

test("code exec maps artifact manifest write failures", async () => {
  const runtime = await makeRuntime();
  const artifactFile = join(runtime.dataDir, "not-a-directory");
  await writeFile(artifactFile, "occupied", "utf8");

  const result = await execute(["code", "exec", "--code", "return 1;", "--artifact-dir", artifactFile], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_ARTIFACT_WRITE_FAILED");
  assert.equal((result.error?.details as { artifactDir: string }).artifactDir, artifactFile);
});

test("code wait timeout returns execution timeout details while job continues", async () => {
  const runtime = await makeRuntime();
  const project = await makeUnityProject(runtime.dataDir);
  const unity = await makeFakeUnity(runtime.dataDir, `setTimeout(() => process.exit(0), 1000);`);

  const result = await execute(["code", "exec", "--code", "return 42;", "--channel", "batchmode", "--unity", unity, "--project", project, "--wait", "--wait-timeout-ms", "10"], runtime);

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
  const bridge = await readFile(new URL("../unity-package/Editor/AlpsUnityBridge.cs", import.meta.url), "utf8");
  const batch = await readFile(new URL("../unity-package/Editor/AlpsUnityBatch.cs", import.meta.url), "utf8");

  assert.match(bridge, /private static readonly string\[\] CodeCommands = \{ "code\.exec" \}/);
  assert.match(bridge, /case "code\.exec"/);
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
  assert.match(batch, /public static void ExecuteCode\(\)/);
  assert.match(batch, /-alpsCodeResult/);
  assert.match(batch, /-alpsTimeoutMs/);
  assert.match(batch, /-alpsProjectPath/);
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
});
