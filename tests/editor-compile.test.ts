import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest, BridgeResponse } from "../src/bridge/types.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { defineToolMetadata, findDefineTool, type ToolContext } from "../src/tools/define.js";
import { compileChildRequestId } from "../src/tools/editor/compile.js";
import { StructuredToolError } from "../src/tools/errors.js";
import { createUnityProject, makeRegistry, makeRegistrySession, registryIdentity, writeRegistry } from "./helpers/instance-registry-v2.js";

const tool = findDefineTool("editor.compile");
if (!tool) throw new Error("editor.compile definition is unavailable");
const mockRootRequestDigest = "d".repeat(64);
const blocker = { type: "dialog" as const, dialogId: "dialog_compile", title: "Third Party Dialog", message: "Third Party Dialog", buttons: ["OK"], requiresUserAction: true as const, source: "windows-ui" as const, confidence: "exact" as const };

async function fixture(playMode: "edit" | "play" = "edit") {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-compile-"));
  const project = join(root, "Project");
  const dataDir = join(root, "data");
  await createUnityProject(project);
  const session = await makeRegistrySession("http://127.0.0.1:49321");
  session.lastUnitySnapshot = snapshot(session, playMode);
  await writeRegistry(dataDir, makeRegistry(registryIdentity(project), [session]));
  return { root, project, dataDir, session };
}

function snapshot(session: Awaited<ReturnType<typeof makeRegistrySession>>, playMode: "edit" | "play") {
  return {
    schemaVersion: 1,
    observedAt: new Date().toISOString(),
    sourceEditorSessionId: session.editorSessionId,
    sourceListenerGeneration: session.listener.generation,
    fields: {
      compile: { known: true, value: false },
      import: { known: true, value: false },
      build: { known: true, value: false },
      playMode: { known: true, value: playMode },
      prefabStage: { known: true, value: null },
      activeScene: { known: true, value: { name: "Main", path: "Assets/Main.unity", isDirty: false } },
      runtime: { known: true, value: { ready: false, sessionId: "" } },
    },
  };
}

function terminalData() {
  return {
    compile: {
      completed: true,
      refresh: true,
      path: null,
      startedAt: "2026-07-16T00:00:00.000Z",
      finishedAt: "2026-07-16T00:00:02.000Z",
      durationMs: 2000,
      assemblyCount: 2,
      errorCount: 0,
      warningCount: 1,
      status: "warnings",
    },
    diagnostics: [{ assembly: "Assembly-CSharp.dll", severity: "warning", code: "CS0168", message: "Variable is declared but never used", file: "Assets/Test.cs", line: 4, column: 9 }],
    editorState: {
      status: "ready",
      compile: { known: true, value: false },
      import: { known: true, value: false },
      playMode: { known: true, value: "edit" },
    },
    plannedEffects: [{ type: "asset-refresh" }, { type: "script-compile" }, { type: "domain-reload" }],
  };
}

function context(project: string, dataDir: string, bridgeExecute?: ToolContext["bridgeExecute"], overrides: Partial<ToolContext["execution"]> = {}): ToolContext {
  return {
    requestId: "req_compile_behavior",
    projectPath: project,
    absoluteDeadlineMs: Date.now() + 15_000,
    execution: { timeoutMs: 15_000, ...overrides },
    dataDir,
    artifactDir: dataDir,
    flags: { instance: project },
    bridgeExecute,
    commitEffect: async () => { throw new Error("TS compile must not own the effect barrier"); },
  };
}

test("editor.compile publishes a non-empty path schema", () => {
  const metadata = defineToolMetadata().find((entry) => entry.name === "editor.compile");
  assert.ok(metadata);
  assert.equal(metadata.timeoutMs, 120_000);
  const properties = (metadata.inputSchema.properties ?? {}) as Record<string, Record<string, unknown>>;
  assert.equal(properties.path?.minLength, 1);
});

test("editor.compile child IDs hash the complete long root", () => {
  const left = `${"a".repeat(199)}x`;
  const right = `${"a".repeat(199)}y`;
  const leftId = compileChildRequestId(left, "_compile_status");
  const rightId = compileChildRequestId(right, "_compile_status");
  assert.notEqual(leftId, rightId);
  assert.ok(leftId.length <= 200);
  assert.ok(rightId.length <= 200);
});

test("editor.compile dry-run is local and returns only planned effects", async () => {
  let calls = 0;
  const result = await tool.execute({ refresh: false }, context(".", ".", async () => { calls += 1; throw new Error("must not dispatch"); }, { dryRun: true }));
  assert.deepEqual(result, {
    dryRun: true,
    plannedEffects: [{ type: "script-compile" }, { type: "domain-reload" }],
  });
  assert.equal(calls, 0);
});

test("editor.compile rejects an empty path before dry-run or Bridge dispatch", async () => {
  let calls = 0;
  await assert.rejects(async () => await tool.execute({ path: "" }, context(".", ".", async () => { calls += 1; throw new Error("must not dispatch"); }, { dryRun: true })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_SCHEMA_INVALID");
  assert.equal(calls, 0);
});

test("editor.compile consumes private acknowledgement and returns only the root warning terminal", async () => {
  const f = await fixture();
  const payloads: BridgeRequest[] = [];
  let statusReads = 0;
  const bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
    payloads.push(payload);
    if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "edit"), progress: [], blockers: [] } };
    if (payload.command === "internal.editor.compile.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, phase: "queued", runId: "private-id", rootRequestDigest: mockRootRequestDigest } };
    if (payload.command === "internal.editor.compile.status") {
      statusReads += 1;
      return statusReads === 1
        ? { ok: true, requestId: payload.requestId, data: { terminal: false, phase: "compiling", runId: "private-id" } }
        : { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope: { ok: true, requestId: "req_compile_behavior", data: terminalData() } } };
    }
    throw new Error(`unexpected command ${payload.command}`);
  };
  try {
    const ctx = context(f.project, f.dataDir, bridgeExecute);
    const result = await tool.execute({}, ctx);
    assert.deepEqual(result, {
      ...terminalData(),
      plannedEffects: [{ type: "asset-refresh" }, { type: "script-compile" }, { type: "domain-reload" }],
    });
    assert.doesNotMatch(JSON.stringify(result), /private-id|queued|compiling/);
    const compilePayloads = payloads.filter((payload) => payload.command.startsWith("internal.editor.compile"));
    assert.deepEqual(compilePayloads.map((payload) => payload.command), ["internal.editor.compile.start", "internal.editor.compile.status", "internal.editor.compile.status"]);
    assert.equal(new Set(compilePayloads.map((payload) => payload.execution.absoluteDeadlineUtc)).size, 1);
    assert.equal(new Set(compilePayloads.map((payload) => payload.instanceId)).size, 1);
    assert.equal((compilePayloads[0].input as Record<string, unknown>).expectedEditorSessionId, f.session.editorSessionId);
    assert.equal(compilePayloads[0].requestId, "req_compile_behavior");
    assert.match(compilePayloads[1].requestId, /_compile_status$/);
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test("editor.compile returns a recovered start terminal without polling status", async () => {
  const f = await fixture();
  const commands: string[] = [];
  try {
    const result = await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      commands.push(payload.command);
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "edit"), progress: [], blockers: [] } };
      return { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope: { ok: true, requestId: "req_compile_behavior", data: terminalData() } } };
    }));
    assert.deepEqual(result, terminalData());
    assert.deepEqual(commands, ["internal.editor.state.snapshot", "internal.editor.compile.start"]);
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test("editor.compile rejects malformed or transitioning success proof", async () => {
  const cases = [
    { name: "fractional diagnostic", mutate: (data: ReturnType<typeof terminalData>) => { data.diagnostics[0].line = 1.5; } },
    { name: "transitioning editor", mutate: (data: ReturnType<typeof terminalData>) => { data.editorState.status = "transitioning"; } },
  ];
  for (const entry of cases) {
    const f = await fixture();
    const data = terminalData();
    entry.mutate(data);
    try {
      await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
        if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "edit"), progress: [], blockers: [] } };
        if (payload.command === "internal.editor.compile.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: mockRootRequestDigest } };
        return { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope: { ok: true, requestId: "req_compile_behavior", data } } };
      })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_TOOL_COMPLETION_PROOF_INVALID", entry.name);
    } finally {
      await rm(f.root, { recursive: true, force: true });
    }
  }
});

test("editor.compile rejects stable Play Mode before private start", async () => {
  const f = await fixture("play");
  const commands: string[] = [];
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      commands.push(payload.command);
      return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "play"), progress: [], blockers: [] } };
    })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_UNITY_COMPILE_REQUIRES_EDIT_MODE" && error.transient === false && error.retryAfterMs === 0);
    assert.deepEqual(commands, ["internal.editor.state.snapshot"]);
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test("editor.compile preflight cannot outlive the root deadline", async () => {
  const f = await fixture();
  const commands: string[] = [];
  const ctx = context(f.project, f.dataDir, async (payload) => {
    commands.push(payload.command);
    return await new Promise<BridgeResponse>(() => undefined);
  });
  ctx.absoluteDeadlineMs = Date.now() + 40;
  const startedAt = Date.now();
  try {
    await assert.rejects(async () => await tool.execute({}, ctx), (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT");
    assert.ok(Date.now() - startedAt < 300);
    assert.ok(commands.length <= 1);
    assert.equal(commands.includes("internal.editor.compile.start"), false);
    assert.ok(commands.every((command) => command === "internal.editor.state.snapshot"));
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test("editor.compile classifies lost status transport as execution timeout", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "edit"), progress: [], blockers: [] } };
      if (payload.command === "internal.editor.compile.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: mockRootRequestDigest } };
      return { ok: false, requestId: payload.requestId, error: { code: "E_EXECUTION_TIMEOUT", message: "status read expired", nextActions: [], transient: false, retryAfterMs: 0 } };
    })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT" && error.transient === false && error.retryAfterMs === 0);
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test("editor.compile converts a status blocker after acceptance to execution timeout", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "edit"), progress: [], blockers: [] } };
      if (payload.command === "internal.editor.compile.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: mockRootRequestDigest } };
      return { ok: false, requestId: payload.requestId, error: { code: "E_UNITY_BLOCKED", message: "blocked", nextActions: [], transient: false, retryAfterMs: 0, details: { blockers: [blocker] } } };
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

test("editor.compile rejects private operation fields in root error details", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "edit"), progress: [], blockers: [] } };
      if (payload.command === "internal.editor.compile.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: mockRootRequestDigest } };
      return { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope: { ok: false, requestId: "req_compile_behavior", error: { code: "E_EXECUTION_TIMEOUT", message: "unknown", nextActions: [], transient: false, retryAfterMs: 0, details: { phase: "compiling", listenerId: "private" } } } } };
    })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_TOOL_COMPLETION_PROOF_INVALID" && !String(JSON.stringify(error.details)).includes("private"));
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test("editor.compile rejects private paths in root error text", async () => {
  const f = await fixture();
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "edit"), progress: [], blockers: [] } };
      if (payload.command === "internal.editor.compile.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: mockRootRequestDigest } };
      return { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope: { ok: false, requestId: "req_compile_behavior", error: { code: "E_EXECUTION_TIMEOUT", message: "Failed at C:\\Users\\builder\\journal.json", nextActions: [], transient: false, retryAfterMs: 0 } } } };
    })), (error: unknown) => error instanceof StructuredToolError && error.code === "E_TOOL_COMPLETION_PROOF_INVALID");
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});

test("editor.compile preserves the attributed root compiler error", async () => {
  const f = await fixture();
  const compileError = {
    ok: false,
    requestId: "req_compile_behavior",
    error: {
      code: "E_UNITY_COMPILE_FAILED",
      message: "Unity reported compiler errors.",
      nextActions: ["Fix the listed compiler diagnostics."],
      transient: false,
      retryAfterMs: 0,
      details: {
        compile: { completed: true, errorCount: 1, warningCount: 0, status: "errors" },
        diagnostics: [{ assembly: "Assembly-CSharp.dll", severity: "error", code: "CS1002", message: "; expected", file: "Assets/Broken.cs", line: 3, column: 1 }],
        editorState: { status: "ready" },
      },
    },
  };
  try {
    await assert.rejects(async () => await tool.execute({}, context(f.project, f.dataDir, async (payload) => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(f.session, "edit"), progress: [], blockers: [] } };
      if (payload.command === "internal.editor.compile.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: mockRootRequestDigest } };
      return { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope: compileError } };
    })), (error: unknown) => {
      assert.ok(error instanceof StructuredToolError);
      assert.equal(error.code, "E_UNITY_COMPILE_FAILED");
      assert.deepEqual((error.details as { diagnostics: unknown[] }).diagnostics, compileError.error.details.diagnostics);
      return true;
    });
  } finally {
    await rm(f.root, { recursive: true, force: true });
  }
});
