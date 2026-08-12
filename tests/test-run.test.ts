import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest, BridgeResponse } from "../src/bridge/types.js";
import { dispatchTool } from "../src/tools/dispatcher.js";
import { findDefineTool, type ToolContext } from "../src/tools/define.js";
import { StructuredToolError } from "../src/tools/errors.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { createUnityProject, makeRegistry, makeRegistrySession, registryIdentity, writeRegistry } from "./helpers/instance-registry-v2.js";

const tool = findDefineTool("test.run");
const cancelTool = findDefineTool("test.cancel");
if (!tool) throw new Error("test.run is unavailable");
if (!cancelTool) throw new Error("test.cancel is unavailable");
const blocker = { type: "dialog" as const, dialogId: "dialog_test", title: "Third Party Dialog", message: "Third Party Dialog", buttons: ["OK"], requiresUserAction: true as const, source: "windows-ui" as const, confidence: "exact" as const };

test("test.run reserves the coordinator test deadline", () => {
  assert.equal(tool.timeoutMs, 600_000);
});

async function fixture() {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-test-run-")); const project = join(root, "Project"); const dataDir = join(root, "data");
  await createUnityProject(project); const session = await makeRegistrySession("http://127.0.0.1:49324");
  session.lastUnitySnapshot = { schemaVersion: 1, observedAt: new Date().toISOString(), sourceEditorSessionId: session.editorSessionId, sourceListenerGeneration: session.listener.generation, fields: { compile: { known: true, value: false }, import: { known: true, value: false }, build: { known: true, value: false }, playMode: { known: true, value: "edit" }, prefabStage: { known: true, value: null }, activeScene: { known: true, value: { name: "Main", path: "Assets/Main.unity", isDirty: false } }, runtime: { known: true, value: { ready: false, sessionId: "" } } } };
  await writeRegistry(dataDir, makeRegistry(registryIdentity(project), [session])); return { root, project, dataDir, session };
}
function terminal(requestId: string, failed = false) { const summary = { platform: "EditMode", status: failed ? "failed" : "passed", testCount: 1, passed: failed ? 0 : 1, failed: failed ? 1 : 0, skipped: 0, inconclusive: 0, duration: 0.1, startedAt: "2026-07-16T00:00:00.000Z", finishedAt: "2026-07-16T00:00:01.000Z" }; const data = { summary, failures: failed ? [{ fullName: "Suite.Fails", message: "expected", stackTrace: "stack", duration: 0.1 }] : [], xml: { path: "Results/test.xml", kind: "nunit-xml", hash: "a".repeat(64), size: 7 } }; return failed ? { ok: false, requestId, error: { code: "E_TESTS_FAILED", message: "One or more Unity tests failed.", nextActions: [], details: data } } : { ok: true, requestId, data }; }
function bridge(f: Awaited<ReturnType<typeof fixture>>, rootEnvelope: unknown): ToolContext["bridgeExecute"] { return async (payload: BridgeRequest): Promise<BridgeResponse> => { if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: f.session.lastUnitySnapshot, progress: [], blockers: [] } }; if (payload.command === "internal.test.run.start") return { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope } }; throw new Error(`unexpected ${payload.command}`); }; }

test("test.run schema excludes project and unity", async () => { const f = await fixture(); try { const result = await dispatchTool("test.run", undefined, { input: "{\"project\":\".\"}", instance: f.project }, { dataDir: f.dataDir, bridgeExecute: bridge(f, terminal("unused")) }, "req_schema"); assert.equal(result.ok, false); if (!result.ok) assert.equal(result.error.code, "E_SCHEMA_INVALID"); } finally { await rm(f.root, { recursive: true, force: true }); } });
test("test.run projects a RunFinished success with XML proof", async () => { const f = await fixture(); try { const result = await dispatchTool("test.run", undefined, { input: "{\"platform\":\"EditMode\",\"xml\":true}", instance: f.project }, { dataDir: f.dataDir, bridgeExecute: bridge(f, terminal("req_test")) }, "req_test"); assert.equal(result.ok, true); } finally { await rm(f.root, { recursive: true, force: true }); } });
test("test.run exposes RunFinished failures as E_TESTS_FAILED", async () => { const f = await fixture(); try { await assert.rejects(async () => await tool.execute({}, { requestId: "req_test", projectPath: f.project, dataDir: f.dataDir, artifactDir: f.dataDir, flags: { instance: f.project }, absoluteDeadlineMs: Date.now() + 5000, execution: {}, bridgeExecute: bridge(f, terminal("req_test", true)), commitEffect: async () => undefined }), (error: unknown) => error instanceof StructuredToolError && error.code === "E_TESTS_FAILED"); } finally { await rm(f.root, { recursive: true, force: true }); } });

test("test.cancel dry-run reads one exact coordinator plan without cancellation", async () => {
  const f = await fixture();
  const runId = "71dc3a77-98cf-4dfc-9b31-c4b2dc67ff85";
  try {
    const result = await cancelTool.execute({}, {
      requestId: "req_cancel_plan", projectPath: f.project, dataDir: f.dataDir, artifactDir: f.dataDir,
      flags: { instance: f.project }, absoluteDeadlineMs: Date.now() + 5000, execution: { dryRun: true }, commitEffect: async () => undefined,
      bridgeExecute: async (payload: BridgeRequest): Promise<BridgeResponse> => {
        if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: f.session.lastUnitySnapshot, progress: [], blockers: [] } };
        if (payload.command === "internal.test.run.cancel.plan") return { ok: true, requestId: payload.requestId, data: cancelProof(runId, true, [runId]) };
        throw new Error(`unexpected ${payload.command}`);
      },
    });
    assert.equal((result as { dryRun: boolean }).dryRun, true);
    assert.equal((result as { runId: string }).runId, runId);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("test.cancel accepts only an inactive exact GUID terminal", async () => {
  const f = await fixture();
  const runId = "71dc3a77-98cf-4dfc-9b31-c4b2dc67ff85";
  const rootRequestDigest = "c".repeat(64);
  try {
    let calls = 0;
    const result = await cancelTool.execute({}, {
      requestId: "req_cancel", projectPath: f.project, dataDir: f.dataDir, artifactDir: f.dataDir,
      flags: { instance: f.project }, absoluteDeadlineMs: Date.now() + 5000, execution: {}, commitEffect: async () => undefined,
      bridgeExecute: async (payload: BridgeRequest): Promise<BridgeResponse> => {
        if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: f.session.lastUnitySnapshot, progress: [], blockers: [] } };
        if (payload.command === "internal.test.run.cancel") { calls++; return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestId: "req_root", rootRequestDigest, runId } }; }
        if (payload.command === "internal.test.run.cancel.status") { calls++; assert.equal((payload.input as Record<string, unknown>).rootRequestDigest, rootRequestDigest); return { ok: true, requestId: payload.requestId, data: { terminal: true, result: cancelProof(runId, false, []) } }; }
        throw new Error(`unexpected ${payload.command}`);
      },
    });
    assert.equal(calls, 2);
    assert.equal((result as { cancelled: boolean }).cancelled, true);

    await assert.rejects(async () => cancelTool.execute({}, {
      requestId: "req_cancel_invalid", projectPath: f.project, dataDir: f.dataDir, artifactDir: f.dataDir,
      flags: { instance: f.project }, absoluteDeadlineMs: Date.now() + 5000, execution: {}, commitEffect: async () => undefined,
      bridgeExecute: async (payload: BridgeRequest): Promise<BridgeResponse> => {
        if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: f.session.lastUnitySnapshot, progress: [], blockers: [] } };
        if (payload.command === "internal.test.run.cancel") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestId: "req_root", rootRequestDigest, runId } };
        if (payload.command === "internal.test.run.cancel.status") return { ok: true, requestId: payload.requestId, data: { terminal: true, result: cancelProof(runId, false, [runId]) } };
        throw new Error(`unexpected ${payload.command}`);
      },
    }), (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT" && error.details?.causeCode === "E_TOOL_COMPLETION_PROOF_INVALID");
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

function cancelProof(runId: string, dryRun: boolean, activeRunIds: string[]) {
  return { cancelled: !dryRun, dryRun, rootRequestId: "req_root", runId, platform: "EditMode", activeRunIds, editorSessionId: "session-test", plannedEffects: [{ type: "test-run-cancel", rootRequestId: "req_root", runId }] };
}

test("test.run converts a blocker observed after acceptance to execution timeout", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, {
      requestId: "req_test_blocked", projectPath: f.project, dataDir: f.dataDir, artifactDir: f.dataDir,
      flags: { instance: f.project }, absoluteDeadlineMs: Date.now() + 5000, execution: {}, commitEffect: async () => undefined,
      bridgeExecute: async (payload: BridgeRequest): Promise<BridgeResponse> => {
        if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: f.session.lastUnitySnapshot, progress: [], blockers: [] } };
        if (payload.command === "internal.test.run.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: "d".repeat(64) } };
        if (payload.command === "internal.test.run.status") return { ok: false, requestId: payload.requestId, error: { code: "E_UNITY_BLOCKED", message: "blocked", nextActions: [], transient: false, retryAfterMs: 0, details: { blockers: [blocker] } } };
        throw new Error(`unexpected ${payload.command}`);
      },
    }), (error: unknown) => {
      assert.ok(error instanceof StructuredToolError);
      assert.equal(error.code, "E_EXECUTION_TIMEOUT");
      assert.equal(error.transient, false);
      assert.equal(error.retryAfterMs, 0);
      assert.deepEqual(error.details?.blockers, [blocker]);
      assert.match(error.message, /Third Party Dialog/);
      assert.deepEqual(error.nextActions, ["Run unity_cli tools run editor.dialogs to inspect the current modal dialog.", "Run unity_cli tools run editor.dialog.click with a current dialogId and exact button label."]);
      return true;
    });
  } finally { await rm(f.root, { recursive: true, force: true }); }
});
