import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest, BridgeResponse } from "../src/bridge/types.js";
import type { ToolContext } from "../src/tools/define.js";
import { StructuredToolError } from "../src/tools/errors.js";
import { runLivePackageResolve, sha256CanonicalJson, type LivePackageBinding } from "../src/tools/package/live-resolve.js";

const mockRootRequestDigest = "d".repeat(64);

const blocker = {
  type: "dialog" as const,
  dialogId: "dialog_package",
  title: "Third Party Dialog",
  message: "Third Party Dialog",
  buttons: ["OK"],
  requiresUserAction: true as const,
  source: "windows-ui" as const,
  confidence: "exact" as const,
};

/** 构造已绑定的 Live Package root，专门验证 effect 后 status 错误分类。 */
async function fixture(code: string, details?: Record<string, unknown>) {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-package-live-"));
  const project = join(root, "Project");
  const dataDir = join(root, "data");
  await mkdir(join(project, "Packages"), { recursive: true });
  await writeFile(join(project, "Packages", "manifest.json"), "{}", "utf8");
  const deadline = Date.now() + 5_000;
  const bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.package.resolve.start") {
      return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "files_proven", rootRequestDigest: mockRootRequestDigest } };
    }
    if (payload.command === "internal.package.resolve.status") {
      return { ok: false, requestId: payload.requestId, error: { code, message: "status failed", nextActions: [], transient: true, retryAfterMs: 5000, ...(details ? { details } : {}) } };
    }
    throw new Error(`unexpected ${payload.command}`);
  };
  const context: ToolContext = {
    requestId: `req_package_${code.toLowerCase()}`,
    projectPath: project,
    dataDir,
    artifactDir: dataDir,
    absoluteDeadlineMs: deadline,
    execution: {},
    flags: { instance: project, yes: true },
    bridgeExecute,
    commitEffect: async () => undefined,
  };
  const binding: LivePackageBinding = {
    instanceId: `unity-${"a".repeat(64)}`,
    expectedEditorSessionId: `session-${"b".repeat(64)}`,
  };
  return { root, context, binding };
}

test("Package redelivery waits for a live file executor instead of racing read-only recovery", async () => {
  const f = await fixture("E_EXECUTION_TIMEOUT");
  let recovered = 0;
  f.context.absoluteDeadlineMs = Date.now() + 100;
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.package.resolve.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "files_committing", rootRequestDigest: mockRootRequestDigest, fileExecutorAlive: true, filePlan: { name: "com.acme.live" } } };
    if (payload.command === "internal.package.resolve.status") return { ok: false, requestId: payload.requestId, error: { code: "E_EXECUTION_TIMEOUT", message: "status deadline", nextActions: [], transient: true, retryAfterMs: 250 } };
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    await assert.rejects(() => runLivePackageResolve(f.context, "install", { name: "com.acme.live" }, {
      async prepare() { throw new Error("prepare must not run"); },
      async apply() { throw new Error("apply must not run"); },
      async recover() { recovered += 1; throw new Error("recovery must wait"); },
    }, f.binding), (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT");
    assert.equal(recovered, 0);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("Package redelivery recovers files only after the original executor exited", async () => {
  const f = await fixture("E_EXECUTION_TIMEOUT");
  let recovered = 0;
  const controls: string[] = [];
  const terminal = { ok: true, requestId: f.context.requestId, data: { recovered: true } };
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.package.resolve.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "files_committing", rootRequestDigest: mockRootRequestDigest, fileExecutorAlive: false, filePlan: { name: "com.acme.dead" } } };
    if (payload.command === "internal.package.files.commit") { controls.push(payload.command); assert.equal((payload.input as Record<string, unknown>).executorPid, process.pid); return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "files_committing", fileExecutorAlive: true, fileExecutorOwnedByCaller: true, filePlan: { name: "com.acme.dead" } } }; }
    if (payload.command === "internal.package.files.prove") { controls.push(payload.command); assert.equal((payload.input as Record<string, unknown>).executorPid, process.pid); return { ok: true, requestId: payload.requestId, data: { terminal: true, accepted: true, phase: "list_succeeded", fileExecutorAlive: false, fileExecutorOwnedByCaller: true, terminalEnvelope: terminal } }; }
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    const result = await runLivePackageResolve<{ recovered: boolean }>(f.context, "install", { name: "com.acme.dead" }, {
      async prepare() { throw new Error("prepare must not run"); },
      async apply() { throw new Error("apply must not run"); },
      async recover() {
        recovered += 1;
        return { expectedManifestSha256: "a".repeat(64), predicates: [], filesProof: { proven: true }, publicResultSeed: { files: { proven: true } } };
      },
    }, f.binding);
    assert.deepEqual(result, { recovered: true });
    assert.equal(recovered, 1);
    assert.deepEqual(controls, ["internal.package.files.commit", "internal.package.files.prove"]);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("Package first execution claims one file owner before prepare and crosses one atomic commit barrier", async () => {
  const f = await fixture("E_EXECUTION_TIMEOUT");
  const controls: string[] = [];
  let executorCapability: string | undefined;
  const terminal = { ok: true, requestId: f.context.requestId, data: { installed: true } };
  const rootRequestDigest = "d".repeat(64);
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    const input = payload.input as Record<string, unknown>;
    if (payload.command === "internal.package.resolve.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", rootRequestDigest, fileExecutorAlive: false, fileExecutorOwnedByCaller: false } };
    if (payload.command === "internal.package.files.claim") {
      controls.push(payload.command);
      assert.equal(input.executorPid, process.pid);
      assert.match(input.executorCapability as string, /^[0-9a-f]{64}$/);
      executorCapability = input.executorCapability as string;
      return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", fileExecutorAlive: true, fileExecutorOwnedByCaller: true } };
    }
    if (payload.command === "internal.package.files.commit") {
      controls.push(payload.command);
      assert.equal(input.executorCapability, executorCapability);
      const leasePath = join(f.context.dataDir!, ".temp", "requests", f.binding.instanceId, ".instance-resource.lock");
      await mkdir(join(f.context.dataDir!, ".temp", "requests", f.binding.instanceId), { recursive: true });
      await writeFile(leasePath, `${JSON.stringify({ requestId: f.context.requestId, requestDigest: rootRequestDigest })}\n`, "utf8");
      const stateDir = join(f.context.projectPath!, "Library", "UnityCli");
      await mkdir(stateDir, { recursive: true });
      await writeFile(join(stateDir, "active-package-resolve.json"), JSON.stringify({ requestId: f.context.requestId, requestDigest: rootRequestDigest, editorSessionId: f.binding.expectedEditorSessionId, phase: "effect_started", packageOperation: { phase: "files_committing", fileExecutorPid: process.pid, fileExecutorCapabilitySha256: createHash("sha256").update(executorCapability!).digest("hex") } }), "utf8");
      await writeFile(join(stateDir, "active-package-reload-epoch.json"), JSON.stringify({ editorSessionId: f.binding.expectedEditorSessionId, epoch: "epoch-first" }), "utf8");
      return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "files_committing", rootRequestDigest, fileExecutorAlive: true, fileExecutorOwnedByCaller: true, filePlan: input.filePlan } };
    }
    if (payload.command === "internal.package.files.prove") {
      controls.push(payload.command);
      assert.equal(input.executorCapability, executorCapability);
      return { ok: true, requestId: payload.requestId, data: { terminal: true, accepted: true, phase: "files_proven", fileExecutorAlive: true, fileExecutorOwnedByCaller: true, terminalEnvelope: terminal } };
    }
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    const result = await runLivePackageResolve<{ installed: boolean }>(f.context, "install", { name: "com.acme.claimed" }, {
      async prepare(signal) { signal.throwIfAborted(); return { name: "com.acme.claimed" }; },
      async apply(_plan, signal) {
        signal.throwIfAborted();
        return { expectedManifestSha256: "a".repeat(64), predicates: [], filesProof: { proven: true }, publicResultSeed: { files: { proven: true } } };
      },
      async recover() { throw new Error("recover must not run"); },
    }, f.binding);
    assert.deepEqual(result, { installed: true });
    assert.deepEqual(controls, ["internal.package.files.claim", "internal.package.files.commit", "internal.package.files.prove"]);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("Package reload revocation aborts the guarded Worker before proof", async () => {
  const f = await fixture("E_EXECUTION_TIMEOUT");
  const rootRequestDigest = "e".repeat(64);
  let proved = 0;
  let aborted = false;
  let executorCapability = "";
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    const input = payload.input as Record<string, unknown>;
    if (payload.command === "internal.package.resolve.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", rootRequestDigest } };
    if (payload.command === "internal.package.files.claim") { executorCapability = input.executorCapability as string; return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", fileExecutorAlive: true, fileExecutorOwnedByCaller: true } }; }
    if (payload.command === "internal.package.files.commit") {
      const leaseDir = join(f.context.dataDir!, ".temp", "requests", f.binding.instanceId);
      await mkdir(leaseDir, { recursive: true });
      await writeFile(join(leaseDir, ".instance-resource.lock"), `${JSON.stringify({ requestId: f.context.requestId, requestDigest: rootRequestDigest })}\n`, "utf8");
      const stateDir = join(f.context.projectPath!, "Library", "UnityCli");
      await mkdir(stateDir, { recursive: true });
      await writeFile(join(stateDir, "active-package-resolve.json"), JSON.stringify({ requestId: f.context.requestId, requestDigest: rootRequestDigest, editorSessionId: f.binding.expectedEditorSessionId, phase: "effect_started", packageOperation: { phase: "files_committing", fileExecutorPid: process.pid, fileExecutorCapabilitySha256: createHash("sha256").update(executorCapability).digest("hex") } }), "utf8");
      await writeFile(join(stateDir, "active-package-reload-epoch.json"), JSON.stringify({ editorSessionId: f.binding.expectedEditorSessionId, epoch: "epoch-revoke" }), "utf8");
      return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "files_committing", rootRequestDigest, fileExecutorAlive: true, fileExecutorOwnedByCaller: true, filePlan: input.filePlan } };
    }
    if (payload.command === "internal.package.files.prove") { proved += 1; throw new Error("proof must not run"); }
    if (payload.command === "internal.package.files.fail") {
      const terminalEnvelope = { ok: false, requestId: f.context.requestId, error: { code: "E_EXECUTION_TIMEOUT", message: "reload revoked file effect", nextActions: [] } };
      return { ok: true, requestId: payload.requestId, data: { terminal: true, accepted: true, phase: "result_persisted", terminalEnvelope } };
    }
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    await assert.rejects(() => runLivePackageResolve(f.context, "install", { name: "com.acme.reload" }, {
      async prepare() { return { name: "com.acme.reload" }; },
      async apply(_plan, signal) {
        const revokeDir = join(f.context.projectPath!, "Library", "UnityCli");
        await mkdir(revokeDir, { recursive: true });
        await writeFile(join(revokeDir, "active-package-file-revoke.json"), JSON.stringify({
          requestId: f.context.requestId,
          requestDigest: rootRequestDigest,
          editorSessionId: f.binding.expectedEditorSessionId,
        }), "utf8");
        await new Promise<void>((resolve, reject) => signal.addEventListener("abort", () => { aborted = true; reject(signal.reason); }, { once: true }));
        throw new Error("unreachable");
      },
      async recover() { throw new Error("recover must not run"); },
    }, f.binding), (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT");
    assert.equal(aborted, true);
    assert.equal(proved, 0);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("Package root status recovers ambiguous start responses with the shared canonical input hash", async () => {
  for (const entry of [
    { operation: "install", input: { name: "same-package", version: "1.0.0" } },
    { operation: "remove", input: { name: "same-package", force: false } },
    { operation: "deploy", input: { source: "D:/packages/source", packageName: "com.acme.source" } },
  ]) {
    const f = await fixture("E_EXECUTION_TIMEOUT");
    const terminal = { ok: true, requestId: f.context.requestId, data: { recovered: true } };
    f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
      if (payload.command === "internal.package.resolve.start") return { ok: false, requestId: payload.requestId, error: { code: "E_EXECUTION_TIMEOUT", message: "response lost", nextActions: [], transient: false, retryAfterMs: 0 } };
      if (payload.command === "internal.package.resolve.status") return { ok: true, requestId: payload.requestId, data: { terminal: true, accepted: true, phase: "files_proven", operation: entry.operation, rootInputSha256: sha256CanonicalJson(entry.input), fileExecutorAlive: false, fileExecutorOwnedByCaller: false, terminalEnvelope: terminal } };
      throw new Error(`unexpected ${payload.command}`);
    };
    try {
      const result = await runLivePackageResolve<{ recovered: boolean }>(f.context, entry.operation, entry.input, undefined, f.binding);
      assert.deepEqual(result, { recovered: true });
    } finally { await rm(f.root, { recursive: true, force: true }); }
  }
});

test("Package root status cannot downgrade a reused requestId with different public input", async () => {
  const f = await fixture("E_EXECUTION_TIMEOUT");
  let claimed = 0;
  let statusCalls = 0;
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.package.resolve.start") return { ok: false, requestId: payload.requestId, error: { code: "E_REQUEST_ID_REUSED", message: "root bytes changed", nextActions: [], transient: false, retryAfterMs: 0 } };
    if (payload.command === "internal.package.resolve.status") { statusCalls += 1; return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", operation: "install", rootInputSha256: sha256CanonicalJson({ name: "old-package" }), fileExecutorAlive: false, fileExecutorOwnedByCaller: false } }; }
    if (payload.command === "internal.package.files.claim") { claimed += 1; throw new Error("claim must not run"); }
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    await assert.rejects(() => runLivePackageResolve(f.context, "install", { name: "new-package" }, {
      async prepare() { throw new Error("prepare must not run"); },
      async apply() { throw new Error("apply must not run"); },
      async recover() { throw new Error("recover must not run"); },
    }, f.binding), (error: unknown) => error instanceof StructuredToolError && error.code === "E_REQUEST_ID_REUSED");
    assert.equal(claimed, 0);
    assert.equal(statusCalls, 0);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("Package control status cannot downgrade a rejected exact plan into a successful commit", async () => {
  const f = await fixture("E_EXECUTION_TIMEOUT");
  let applied = 0;
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.package.resolve.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", rootRequestDigest: mockRootRequestDigest, fileExecutorAlive: false, fileExecutorOwnedByCaller: false } };
    if (payload.command === "internal.package.files.claim") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", fileExecutorAlive: true, fileExecutorOwnedByCaller: true } };
    if (payload.command === "internal.package.files.commit") return { ok: false, requestId: payload.requestId, error: { code: "E_REQUEST_ID_REUSED", message: "plan changed", nextActions: [], transient: false, retryAfterMs: 0 } };
    if (payload.command === "internal.package.resolve.status") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "files_committing", fileExecutorAlive: true, fileExecutorOwnedByCaller: true, filePlan: { name: "old-plan" } } };
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    await assert.rejects(() => runLivePackageResolve(f.context, "install", { name: "new-plan" }, {
      async prepare() { return { name: "new-plan" }; },
      async apply() { applied += 1; throw new Error("must not run"); },
      async recover() { throw new Error("must not run"); },
    }, f.binding), (error: unknown) => error instanceof StructuredToolError && error.code === "E_REQUEST_ID_REUSED");
    assert.equal(applied, 0);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("Package selector conflict returns execution timeout without applying an unaccepted plan", async () => {
  const f = await fixture("E_EXECUTION_TIMEOUT");
  let applied = 0;
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.package.resolve.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", rootRequestDigest: mockRootRequestDigest, fileExecutorAlive: false, fileExecutorOwnedByCaller: false } };
    if (payload.command === "internal.package.files.claim") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", fileExecutorAlive: true, fileExecutorOwnedByCaller: true } };
    if (payload.command === "internal.package.files.commit" || payload.command === "internal.package.resolve.status") return { ok: false, requestId: payload.requestId, error: { code: "E_INSTANCE_CONFLICT", message: "process claims changed", nextActions: [], transient: false, retryAfterMs: 0 } };
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    await assert.rejects(() => runLivePackageResolve(f.context, "install", { name: "conflict-package" }, {
      async prepare() { return { name: "conflict-package" }; },
      async apply() { applied += 1; throw new Error("must not run"); },
      async recover() { throw new Error("must not run"); },
    }, f.binding), (error: unknown) => {
      assert.ok(error instanceof StructuredToolError);
      assert.equal(error.code, "E_EXECUTION_TIMEOUT");
      assert.equal(error.details?.causeCode, "E_INSTANCE_CONFLICT");
      return true;
    });
    assert.equal(applied, 0);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("Package file claim transition timeout returns execution timeout before prepare", async () => {
  const f = await fixture("E_UNITY_TRANSITION_TIMEOUT");
  let prepared = 0;
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.package.resolve.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "awaiting_files", rootRequestDigest: mockRootRequestDigest, fileExecutorAlive: false, fileExecutorOwnedByCaller: false } };
    if (payload.command === "internal.package.files.claim" || payload.command === "internal.package.resolve.status") return { ok: false, requestId: payload.requestId, error: { code: "E_UNITY_TRANSITION_TIMEOUT", message: "listener generation did not converge", nextActions: [], transient: false, retryAfterMs: 0 } };
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    await assert.rejects(() => runLivePackageResolve(f.context, "install", { name: "transition-package" }, {
      async prepare() { prepared += 1; return {}; },
      async apply() { throw new Error("apply must not run"); },
      async recover() { throw new Error("recover must not run"); },
    }, f.binding), (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT" && error.details?.causeCode === "E_UNITY_TRANSITION_TIMEOUT");
    assert.equal(prepared, 0);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("Package resolve survives a transient reload observation and returns the durable terminal", async () => {
  const f = await fixture("E_EXECUTION_TIMEOUT");
  const terminal = { ok: true, requestId: f.context.requestId, data: { resolved: true } };
  let statusCalls = 0;
  f.context.absoluteDeadlineMs = Date.now() + 500;
  f.context.bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.package.resolve.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "files_proven", rootRequestDigest: mockRootRequestDigest } };
    if (payload.command === "internal.package.resolve.status") {
      statusCalls += 1;
      if (statusCalls === 1) return { ok: false, requestId: payload.requestId, error: { code: "E_EXECUTION_TIMEOUT", message: "reload in progress", nextActions: [], transient: true, retryAfterMs: 0 } };
      return { ok: true, requestId: payload.requestId, data: { terminal: true, accepted: true, phase: "list_succeeded", operation: "resolve", rootInputSha256: sha256CanonicalJson({}), fileExecutorAlive: false, fileExecutorOwnedByCaller: false, terminalEnvelope: terminal } };
    }
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    assert.deepEqual(await runLivePackageResolve<{ resolved: boolean }>(f.context, "resolve", {}, undefined, f.binding), { resolved: true });
    assert.equal(statusCalls, 2);
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test("Package resolve converts blockers and exhausted reload observation to execution timeout", async () => {
  for (const entry of [
    { code: "E_UNITY_BLOCKED", details: { blockers: [blocker] }, blockers: [blocker], retryable: false },
    { code: "E_EXECUTION_TIMEOUT", details: undefined, blockers: undefined, retryable: true },
    { code: "E_UNITY_TRANSITION_TIMEOUT", details: undefined, blockers: undefined, retryable: true },
  ]) {
    const f = await fixture(entry.code, entry.details);
    if (entry.retryable) f.context.absoluteDeadlineMs = Date.now() + 80;
    try {
      await assert.rejects(
        async () => await runLivePackageResolve(f.context, "resolve", {}, undefined, f.binding),
        (error: unknown) => {
          assert.ok(error instanceof StructuredToolError);
          assert.equal(error.code, "E_EXECUTION_TIMEOUT");
          assert.equal(error.transient, false);
          assert.equal(error.retryAfterMs, 0);
          assert.equal(error.details?.causeCode, entry.code);
          assert.deepEqual(error.details?.blockers, entry.blockers);
          if (entry.blockers) assert.match(error.message, /Third Party Dialog/);
          return true;
        },
      );
    } finally {
      await rm(f.root, { recursive: true, force: true });
    }
  }
});
