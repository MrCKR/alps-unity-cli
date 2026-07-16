import assert from "node:assert/strict";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { bridgeCommandRegistry, type BridgeRequest } from "../src/bridge/index.js";
import { destructiveSecurityProof } from "../src/security.js";
import { mkdtemp, readFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-security-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("bridge registry distinguishes persistent writes from scene/session-only tools", () => {
  assert.equal(bridgeCommandRegistry.lookup("asset", "create")?.destructiveHint, true);
  assert.equal(bridgeCommandRegistry.lookup("scene", "save")?.destructiveHint, true);
  assert.equal(bridgeCommandRegistry.lookup("graphics", "bake-start")?.destructiveHint, true);
  assert.equal(bridgeCommandRegistry.lookup("go", "delete")?.destructiveHint ?? false, false);
  assert.equal(bridgeCommandRegistry.lookup("component", "remove")?.destructiveHint ?? false, false);
  assert.equal(bridgeCommandRegistry.lookup("scene", "unload")?.destructiveHint, true, "save=true can persist dirty scene data");
});

test("security policy exactly covers every destructive business tool in the full matrix", async () => {
  const { destructiveCommands } = await import("../src/security-policy.js");
  const matrix = JSON.parse(await readFile(join(process.cwd(), ".plan/evidence/final-cleanup/security-matrix.json"), "utf8")) as {
    counts: { total: number; builtIn: number; extension: number; tsLocal: number; destructive: number; mismatches: number };
    entries: Array<{ name: string; source: string; owner: string; destructiveHint: boolean }>;
  };
  const expected = [...new Set(matrix.entries.filter((entry) => entry.destructiveHint).map((entry) => entry.name))].sort();
  assert.deepEqual(destructiveCommands().map((entry) => entry.command).sort(), expected);
  assert.deepEqual(matrix.counts, { total: 370, builtIn: 233, extension: 100, tsLocal: 37, destructive: 99, nonDestructive: 271, mismatches: 0 });

  for (const entry of matrix.entries) {
    const source = await readFile(join(process.cwd(), entry.source), "utf8");
    if (entry.owner === "ts-local") {
      assert.equal(/destructiveHint:\s*true/.test(source), entry.destructiveHint, `${entry.name} TS descriptor must match the audited policy`);
      continue;
    }
    assert.equal(/DestructiveHint\s*=\s*true/.test(source), entry.destructiveHint, `${entry.name} C# descriptor must match the audited policy`);
    const spec = bridgeCommandRegistry.specs().find((candidate) => candidate.toolName === entry.name);
    assert.equal(spec?.destructiveHint === true, entry.destructiveHint, `${entry.name} bridge spec must match its C# descriptor`);
  }
});

test("Unity bridge keeps wrappers statically non-destructive and gates live descriptors dynamically", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/UnityCliBridge.cs", import.meta.url), "utf8");
  const registry = await readFile(new URL("../unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", import.meta.url), "utf8");
  const runtime = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs", import.meta.url), "utf8");
  const projectCommands = await readFile(new URL("../unitypkg/Editor/Commands/ProjectToolCommands.cs", import.meta.url), "utf8");
  const runtimeCommands = await readFile(new URL("../unitypkg/Editor/Commands/RuntimeToolCommands.cs", import.meta.url), "utf8");
  assert.match(source, /RequiresDestructiveSecurityProof\(request\.Command\)/);
  assert.match(source, /return string\.Equals\(command, "code\.exec"/);
  assert.match(projectCommands, /"project-tool\.run",\s*"project-tool",\s*false,\s*60000,\s*ProjectToolRun/s);
  assert.match(runtimeCommands, /"runtime-tool\.run",\s*"runtime-tool",\s*false,\s*60000,\s*RuntimeToolRun/s);
  assert.match(registry, /RefreshProjectToolFingerprint[\s\S]*request\.SecurityConfirmed\(name, true, definition\.SupportsDryRun, "project-tool\.run", definition\.Fingerprint\)/);
  assert.match(runtime, /RefreshProjectToolFingerprint[\s\S]*request\.SecurityConfirmed\(name, true, definition\.SupportsDryRun, "runtime-tool\.run", definition\.Fingerprint\)/);
  assert.match(source, /private static bool RequiresDestructiveSecurityProof\(string command\)\s*\{\s*return string\.Equals\(command, "code\.exec"/s);
});

test("destructive proof is bound to actual tool, wrapper descriptor and complete input", () => {
  const flags = { yes: true };
  const descriptor = { name: "asset.delete", destructiveHint: true, supportsDryRun: true, bridgeCommand: "project-tool.run", liveDescriptorFingerprint: "live-v1" };
  const proof = destructiveSecurityProof(flags, "asset.delete", descriptor, { name: "asset.delete", input: { path: "Assets/A.asset" } });
  const wrongName = destructiveSecurityProof(flags, "asset.move", { ...descriptor, name: "asset.move" }, { name: "asset.delete", input: { path: "Assets/A.asset" } });
  const wrongWrapper = destructiveSecurityProof(flags, "asset.delete", { ...descriptor, bridgeCommand: "runtime-tool.run" }, { name: "asset.delete", input: { path: "Assets/A.asset" } });
  const staleDescriptor = destructiveSecurityProof(flags, "asset.delete", { ...descriptor, liveDescriptorFingerprint: "live-v0" }, { name: "asset.delete", input: { path: "Assets/A.asset" } });
  const wrongInput = destructiveSecurityProof(flags, "asset.delete", descriptor, { name: "asset.delete", input: { path: "Assets/B.asset" } });
  assert.notEqual(proof.descriptorFingerprint, wrongName.descriptorFingerprint);
  assert.notEqual(proof.descriptorFingerprint, wrongWrapper.descriptorFingerprint);
  assert.notEqual(proof.descriptorFingerprint, staleDescriptor.descriptorFingerprint);
  assert.notEqual(proof.inputSha256, wrongInput.inputSha256);
  assert.equal(proof.toolName, "asset.delete");
});

test("destructive job tools require explicit --yes before starting work", async () => {
  const runtime = await makeRuntime();

  for (const [name, input] of [["build.run", {}], ["package.resolve", { project: runtime.dataDir }]] as const) {
    const result = await execute(["tools", "run", name, "--input", JSON.stringify(input)], runtime);
    assert.ok(!result.ok);
    assert.equal(result.error?.code, "E_DESTRUCTIVE_CONFIRMATION_REQUIRED");
  }
});

test("destructive dry-run forwards envelope preview without --yes", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "asset.delete", "Assets/DeleteMe.asset", "--dry-run"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { dryRun: payload.execution?.dryRun, plannedEffects: [{ type: "asset.delete", path: payload.input!.path }] } };
    }
  });

  assert.ok(result.ok);
  assert.equal(requests[0].command, "project-tool.run");
  assert.equal(requests[0].input!.name, "asset.delete");
  assert.equal(requests[0].execution?.dryRun, true);
  assert.equal((requests[0].input!.input as Record<string, unknown>).dryRun, true);
  assert.equal(requests[0].security?.confirmed, false);
  assert.equal(requests[0].security?.toolName, "asset.delete");
  assert.match(requests[0].security?.descriptorFingerprint ?? "", /^[a-f0-9]{64}$/);
  assert.match(requests[0].security?.inputSha256 ?? "", /^[a-f0-9]{64}$/);
});

test("Unity wait lifecycle has thread-safe callback terminal state and public PlayMode primitive", async () => {
  const waiter = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliWaiter.cs", import.meta.url), "utf8");
  assert.match(waiter, /UntilPlayModeState\(PlayModeStateChange state, TimeSpan timeout/);
  assert.match(waiter, /private readonly object callbackLock = new object\(\)/);
  assert.match(waiter, /lock \(callbackLock\)[\s\S]*if \(callbackTerminal \|\| IsCompleted\) return/);
  assert.match(waiter, /cancellationToken\.Register\(\(\) => UnityCliWaitScheduler\.QueueOnMainThread/);
  assert.match(waiter, /UnityCliWaitScheduler\.QueueOnMainThread\(toRun\)/);
  assert.doesNotMatch(waiter, /toRun\?\.Invoke\(\)/);
  assert.match(waiter, /case PlayModeStateChange\.EnteredPlayMode: return playing && !changing/);
});

test("package.embed uses its one-capability source with cancellable scheduler wait and failure handling", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/package/embed.cs", import.meta.url), "utf8");
  assert.match(source, /public async System\.Threading\.Tasks\.ValueTask<Result> ExecuteAsync/);
  assert.match(source, /await ctx\.Wait\.Until\([\s\S]*TimeSpan\.FromSeconds\(120\), ct\)/);
  assert.match(source, /E_PACKAGE_EMBED_FAILED/);
  assert.match(source, /E_EXECUTION_TIMEOUT/);
  assert.doesNotMatch(source, /Thread\.Sleep/);
});

test("Bridge work lifecycle separates queue deadline, execution timeout and client disconnect cancellation", async () => {
  const http = await readFile(new URL("../unitypkg/Editor/Bridge/BridgeHttpServer.cs", import.meta.url), "utf8");
  const queue = await readFile(new URL("../unitypkg/Editor/Bridge/BridgeWorkQueue.cs", import.meta.url), "utf8");
  assert.match(http, /WatchClientDisconnect[\s\S]*OutputStream\.CanWrite[\s\S]*CancelClientDisconnected/);
  assert.match(http, /WaitHandle\.WaitAny\(new\[\] \{ item\.Started\.WaitHandle, item\.Completed\.WaitHandle \}, 60000\)/);
  assert.match(http, /E_QUEUE_START_TIMEOUT/);
  assert.match(queue, /Started \{ get; private set; \}/);
  assert.match(queue, /TryStart\(\)[\s\S]*cancellation\.CancelAfter[\s\S]*Started\.Set\(\)/);
  assert.match(queue, /MaxWorkStartsPerTick = 16/);
  assert.match(queue, /RunningWork\.Add\(item\)[\s\S]*ExecuteWorkItemAsync\(item\)/);
  assert.match(queue, /RunningWork\.Remove\(item\)[\s\S]*item\.Completed\.Set\(\)/);
  assert.doesNotMatch(queue, /runningWork != null/);
});

test("code exec dry-run returns local preview and does not call bridge", async () => {
  const runtime = await makeRuntime();
  let called = false;

  const result = await execute(["tools", "run", "code.exec", "--code", "Debug.Log(1);", "--dry-run"], {
    ...runtime,
    bridgeExecute: async (payload) => {
      called = true;
      return { ok: true as const, requestId: payload.requestId, data: {} };
    }
  });

  assert.ok(result.ok);
  assert.equal(called, false);
  assert.equal((result.data as { dryRun: boolean }).dryRun, true);
  assert.equal((result.data as { preview: { sourceSha256: string } }).preview.sourceSha256.length, 64);
});
