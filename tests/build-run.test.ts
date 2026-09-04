import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest, BridgeResponse } from "../src/bridge/types.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { findDefineTool, type ToolContext } from "../src/tools/define.js";
import { dispatchTool } from "../src/tools/dispatcher.js";
import { StructuredToolError } from "../src/tools/errors.js";
import { createUnityProject, makeRegistry, makeRegistrySession, registryIdentity, writeRegistry } from "./helpers/instance-registry-v3.js";

const tool = findDefineTool("build.run");
if (!tool) throw new Error("build.run definition is unavailable");
const blocker = { type: "dialog" as const, dialogId: "dialog_build", title: "Third Party Dialog", message: "Third Party Dialog", buttons: ["OK"], requiresUserAction: true as const, source: "windows-ui" as const, confidence: "exact" as const };

test("build.run reserves the coordinator build deadline", () => {
  assert.equal(tool.timeoutMs, 600_000);
});

test("build.run maps a null EditorBuildSettings scene array to the stable empty-scenes error", async () => {
  const source = await readFile("unitypkg/Editor/Infrastructure/UnityCliLiveBuildCoordinator.cs", "utf8");
  assert.match(source, /EditorBuildSettings\.scenes \?\? Array\.Empty<EditorBuildSettingsScene>\(\)/);
  assert.match(source, /scenes\.Length == 0[\s\S]*E_BUILD_SCENES_EMPTY/);
  assert.match(source, /build_entry_failed[\s\S]*E_EXECUTION_FAILED/);
  assert.match(source, /build_unhandled_exception[\s\S]*E_EXECUTION_FAILED/);
  assert.match(source, /report\.steps \?\? Array\.Empty<BuildStep>\(\)/);
  assert.doesNotMatch(source, /BridgeCommandException\("E_BRIDGE_COMMAND_FAILED", ex\.Message/);
});

async function fixture(playMode: "edit" | "play" = "edit") {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-build-"));
  const project = join(root, "Project");
  const dataDir = join(root, "data");
  await createUnityProject(project);
  const session = await makeRegistrySession("http://127.0.0.1:49322");
  session.lastUnitySnapshot = snapshot(session, playMode);
  await writeRegistry(dataDir, makeRegistry(registryIdentity(project), [session]));
  return { root, project, dataDir, session };
}

function snapshot(session: Awaited<ReturnType<typeof makeRegistrySession>>, playMode: "edit" | "play" = "edit") {
  return { schemaVersion: 1, observedAt: new Date().toISOString(), sourceEditorSessionId: session.editorSessionId, sourceListenerGeneration: session.listener.generation, fields: {
    compile: { known: true, value: false }, import: { known: true, value: false }, build: { known: true, value: false }, playMode: { known: true, value: playMode }, prefabStage: { known: true, value: null }, activeScene: { known: true, value: { name: "Main", path: "Assets/Main.unity", isDirty: false } }, runtime: { known: true, value: { ready: false, sessionId: "" } },
  } };
}

function context(project: string, dataDir: string, bridgeExecute: ToolContext["bridgeExecute"]): ToolContext {
  return { requestId: "req_build_behavior", projectPath: project, absoluteDeadlineMs: Date.now() + 5_000, execution: { timeoutMs: 5_000 }, dataDir, artifactDir: dataDir, flags: { instance: project, yes: true }, bridgeExecute, commitEffect: async () => { throw new Error("TS build must not own the effect barrier"); } };
}

function target(result: "Succeeded" | "Failed" = "Succeeded", artifact = validArtifact()) {
  return { target: "StandaloneWindows64", result, outputPath: "Builds/App.exe", totalSize: 42, totalErrors: result === "Succeeded" ? 0 : 1, totalWarnings: 0, startedAt: "2026-07-16T00:00:00.000Z", finishedAt: "2026-07-16T00:00:02.000Z", durationMs: 2000, messages: [], artifact };
}

function validArtifact(): { outputPath: string; exists: boolean; hash: string | null; treeHash: string | null; fileCount: number; size: number } { return { outputPath: "Builds/App.exe", exists: true, hash: "a".repeat(64), treeHash: null, fileCount: 1, size: 42 }; }

function bridge(f: Awaited<ReturnType<typeof fixture>>, rootEnvelope: unknown): ToolContext["bridgeExecute"] {
  return async (payload: BridgeRequest): Promise<BridgeResponse> => {
    if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session), progress: [], blockers: [] } };
    if (payload.command === "internal.build.run.start") return { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope } };
    throw new Error(`unexpected command ${payload.command}`);
  };
}

test("build.run rejects stable Play Mode before private start", async () => {
  const f = await fixture("play");
  const commands: string[] = [];
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      commands.push(payload.command);
      return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "play"), progress: [], blockers: [] } };
    })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_TOOL_PLAYMODE_UNAVAILABLE" && error.transient === false && error.retryAfterMs === 0);
    assert.deepEqual(commands, ["internal.editor.state.snapshot"]);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("build.run preserves a C# Play Mode rejection that races the TS preflight", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session), progress: [], blockers: [] } };
      return { ok: false, requestId: payload.requestId, error: { code: "E_TOOL_PLAYMODE_UNAVAILABLE", message: "build.run requires Edit Mode", nextActions: ["Exit Play Mode."], transient: false, retryAfterMs: 0 } };
    })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_TOOL_PLAYMODE_UNAVAILABLE");
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("build.run returns only a terminal BuildReport and fresh artifact proof", async () => {
  const f = await fixture();
  try {
    const result = await tool.execute({ target: "StandaloneWindows64", output: "Builds/App.exe" }, context(f.project, f.dataDir, bridge(f, { ok: true, requestId: "req_build_behavior", data: { targets: [target()] } })));
    assert.deepEqual(result, { targets: [target()] });
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("dispatchTool preserves the build descriptor deadline through the private root", async () => {
  const f = await fixture();
  const startedAtMs = Date.now();
  let buildDeadlineMs = 0;
  const terminalBridge = bridge(f, { ok: true, requestId: "req_dispatch_build", data: { targets: [target()] } });
  try {
    const result = await dispatchTool("build.run", undefined, { input: JSON.stringify({ target: "StandaloneWindows64", output: "Builds/App.exe" }), yes: true, instance: f.project }, { dataDir: f.dataDir, bridgeExecute: async (payload) => {
      if (payload.command === "internal.build.run.start") buildDeadlineMs = Date.parse(payload.execution.absoluteDeadlineUtc);
      return terminalBridge!(payload);
    } }, "req_dispatch_build");
    assert.equal(result.ok, true);
    assert.ok(buildDeadlineMs >= startedAtMs + 590_000);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("build.run rejects a succeeded report without a non-empty SHA-256 artifact proof", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, bridge(f, { ok: true, requestId: "req_build_behavior", data: { targets: [target("Succeeded", { ...validArtifact(), size: 0, hash: null })] } }))), (error: unknown) => error instanceof StructuredToolError && error.code === "E_TOOL_COMPLETION_PROOF_INVALID");
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("build.run classifies a lost post-dispatch bridge response as execution timeout", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session), progress: [], blockers: [] } };
      return { ok: false, requestId: payload.requestId, error: { code: "BRIDGE_CONNECTION_FAILED", message: "response lost", nextActions: [], transient: false, retryAfterMs: 0 } };
    })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT");
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("build.run preserves observed blockers on execution timeout", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session), progress: [], blockers: [] } };
      return { ok: false, requestId: payload.requestId, error: { code: "E_EXECUTION_TIMEOUT", message: "response lost behind modal", nextActions: [], transient: true, retryAfterMs: 5000, details: { blockers: [blocker] } } };
    })), (error: unknown) => {
      assert.ok(error instanceof StructuredToolError);
      assert.equal(error.code, "E_EXECUTION_TIMEOUT");
      assert.equal(error.transient, false);
      assert.equal(error.retryAfterMs, 0);
      assert.deepEqual(error.details?.blockers, [blocker]);
      assert.match(error.message, /Third Party Dialog/);
      return true;
    });
  } finally { await rm(f.root, { recursive: true, force: true }); }
});
