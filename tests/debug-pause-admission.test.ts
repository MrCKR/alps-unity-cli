import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { SessionSupervisor, SessionSupervisorError } from "../src/bridge/session-supervisor.js";
import type { BridgeRequest, BridgeResponse } from "../src/bridge/types.js";
import type { EditorBlockerObserver, EditorDebugPauseBlocker } from "../src/editor-blocker-observer.js";
import { ERROR_CODES } from "../src/output.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { findDefineTool, type ToolContext } from "../src/tools/define.js";
import { StructuredToolError, blockedEditorStateError } from "../src/tools/errors.js";
import type { InstanceSelection, RegistrySession, UnityInstance, UnityInstanceStatus } from "../src/instances.js";
import { createUnityProject, makeRegistry, makeRegistrySession, registryIdentity, writeRegistry } from "./helpers/instance-registry-v3.js";

const INSTANCE_ID = `unity-${"a".repeat(64)}`;
const SESSION_ID = `session-${"b".repeat(64)}`;

/** 当前 live pause publication 发布的公开 debug-pause blocker；字段与 C# DTO 逐项对应。 */
const DEBUG_PAUSE_BLOCKER: EditorDebugPauseBlocker = {
  type: "debug-pause",
  pauseId: "pause-1",
  threadId: 1,
  mainThread: true,
  methodIdentity: "UnityCliDebugFixture.DebugCounterModel.IncrementBy(Int32)",
  targetKey: "Assembly-CSharp|UnityCliDebugFixture.DebugCounterModel|IncrementBy|System.Int32|System.Void|instance",
  position: "prefix",
  sessionId: "dbg-3",
  timeoutKind: "bounded",
  remainingMs: 21_310,
  pausedForMs: 8_689,
  resumeTool: "debug.pause-resume",
  infoTool: "debug.pause-list",
  requiresUserAction: false,
  source: "unity-debug-pause",
  confidence: "exact",
};

const DIALOG_BLOCKER = {
  type: "dialog" as const,
  dialogId: "dialog_admission",
  title: "Third Party Dialog",
  message: "Third Party Dialog",
  buttons: ["OK"],
  requiresUserAction: true as const,
  source: "windows-ui" as const,
  confidence: "exact" as const,
};

/** registry 归约结果：故意暂停只能由 live pause publication 证明，modal 只能由 Windows 观察证明。 */
function instance(status: UnityInstanceStatus, mainThreadDebugPause?: boolean): InstanceSelection {
  const paused = mainThreadDebugPause === true;
  const session: RegistrySession = {
    target: "editor",
    sessionId: SESSION_ID,
    editorSessionId: SESSION_ID,
    processId: 202,
    processStartedAt: "2026-07-16T00:00:00.000Z",
    publishedAt: "2026-07-16T00:00:01.000Z",
    publicationSequence: 1,
    lifecycle: status === "blocked" ? "blocked" : "ready",
    lastUnitySnapshot: {},
    listener: {
      listenerId: `listener-${"2".repeat(32)}`,
      generation: 1,
      startedAt: "2026-07-16T00:00:01.000Z",
      endpoint: { host: "127.0.0.1", preferredPort: 49_321, actualPort: 49_321, fallback: false, url: "http://127.0.0.1:49321" },
    },
    progress: [],
    blockers: status === "blocked" && !paused ? [{ kind: "modal", title: "Third Party Dialog" }] : [],
    buildStamp: "test-build",
    toolCatalogFingerprint: "c".repeat(64),
  };
  const value: UnityInstance = {
    instanceId: INSTANCE_ID,
    canonicalizationVersion: 1,
    canonicalProjectPath: "c:\\repo\\project",
    displayProjectPath: "C:\\repo\\project",
    projectPath: "C:\\repo\\project",
    projectName: "project",
    status,
    target: "editor",
    revision: 1,
    updatedAt: "2026-07-16T00:00:01.000Z",
    sessions: [session],
    activeSession: session,
    allSessions: [session],
    sessionId: SESSION_ID,
    editorSessionId: SESSION_ID,
    processId: session.processId,
    publishedAt: session.publishedAt,
    lastUnitySnapshot: {},
    progress: [],
    blockers: session.blockers,
    buildStamp: "test-build",
    toolCatalogFingerprint: session.toolCatalogFingerprint,
    ...(paused ? { mainThreadDebugPause: { pauseId: DEBUG_PAUSE_BLOCKER.pauseId, threadId: 1, publishedSequence: 1 } } : {}),
    claimVerification: "verified",
  };
  return { instance: value, matchedBy: "instanceId" };
}

/** 本地 refusal 路径不得再探 Windows UI：观察结果固定为未证明。 */
function unprovenObserver(): EditorBlockerObserver {
  return { observe: async () => ({ outcome: "unproven", blockers: [] }) } as unknown as EditorBlockerObserver;
}

function attestation() {
  return {
    target: "editor" as const,
    instanceId: INSTANCE_ID,
    sessionId: SESSION_ID,
    listenerId: `listener-${"2".repeat(32)}`,
    listenerGeneration: 1,
  };
}

/** Bridge 暂停闸门的 canonical 拒绝；路由字段与 C# DebugPauseControl 一致。 */
function pausedGateRejection(request: BridgeRequest): BridgeResponse {
  return {
    ok: false,
    requestId: request.requestId,
    error: {
      code: "E_DEBUG_MAIN_THREAD_PAUSED",
      message: "The Unity Editor main thread is deliberately paused at a debug hook breakpoint, so this request was refused before any effect instead of waiting behind the paused main-thread queue.",
      nextActions: [
        "Run tools run debug.pause-list to read the pause site that is holding the main thread.",
        "Run tools run debug.pause-resume with pauseId pause-1 to release the site and let the main thread continue.",
      ],
      transient: false,
      retryAfterMs: 0,
      details: { pauseId: "pause-1", threadId: 1, mainThread: true, timeoutKind: "bounded", remainingMs: 21_310, pauseTool: "debug.pause-list", resumeTool: "debug.pause-resume" },
    },
  };
}

test("a proven main-thread pause reaches the single Bridge gate instead of the local modal refusal", async () => {
  const commands: string[] = [];
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 50_000,
    sleep: async () => undefined,
    select: async () => instance("blocked", true),
    post: async (_binding, request) => {
      commands.push(request.payload.command);
      // 冻结的可达集合由 Bridge 控制通道在自己的 HTTP 线程上服务；不可达的普通变更在同一闸门被拒。
      const nested = (request.payload.input as { name?: string }).name;
      const response = nested === "debug.code-execute" ? pausedGateRejection(request.payload) : { ok: true as const, requestId: request.payload.requestId, data: { sites: [] } };
      return { response, attestation: attestation() };
    },
  });

  const describe = await supervisor.read({ requestId: "req_paused_describe", command: "catalog.editor.describe", input: { name: "debug.pause-list" }, timeoutMs: 500 });
  assert.equal(describe.ok, true);
  const list = await supervisor.execute({ requestId: "req_paused_list", command: "catalog.editor.run", input: { name: "debug.pause-list" }, timeoutMs: 500, mutation: false });
  assert.equal(list.ok, true);
  const events = await supervisor.execute({ requestId: "req_paused_events", command: "catalog.editor.run", input: { name: "debug.events-read", sourceId: "pause" }, timeoutMs: 500, mutation: false });
  assert.equal(events.ok, true);

  const mutation = await supervisor.execute({ requestId: "req_paused_mutation", command: "catalog.editor.run", input: { name: "debug.code-execute" }, timeoutMs: 500, mutation: true });
  assert.equal(mutation.ok, false);
  if (mutation.ok) throw new Error("unreachable");
  assert.equal(mutation.error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
  assert.match(mutation.error.nextActions.join(" "), /debug\.pause-list/);
  assert.match(mutation.error.nextActions.join(" "), /debug\.pause-resume/);
  assert.equal(mutation.error.details?.pauseId, "pause-1");
  assert.deepEqual(commands, ["catalog.editor.describe", "catalog.editor.run", "catalog.editor.run", "catalog.editor.run"]);
});

test("without a pause publication the modal refusal stays local, and a worker-only pause never becomes a main-thread block", async () => {
  let modalPosts = 0;
  const modal = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 51_000,
    sleep: async () => undefined,
    select: async () => instance("blocked"),
    observer: unprovenObserver(),
    post: async () => { modalPosts += 1; throw new Error("must not post"); },
  });
  await assert.rejects(
    modal.execute({ requestId: "req_modal_blocked", command: "catalog.editor.run", input: { name: "debug.pause-list" }, timeoutMs: 200, mutation: false }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_UNITY_BLOCKED",
  );
  assert.equal(modalPosts, 0);

  let workerPosts = 0;
  let now = 52_000;
  const workerOnly = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => instance("unresponsive"),
    observer: unprovenObserver(),
    post: async () => { workerPosts += 1; throw new Error("must not post"); },
  });
  // 只暂停 worker 时主线程仍会推进，registry 不会发布主线程暂停证据；
  // 因此这里只能是 transport 层的不可用，绝不能报成 debug-pause block。
  await assert.rejects(
    workerOnly.execute({ requestId: "req_worker_only", command: "catalog.editor.run", input: { name: "debug.code-execute" }, timeoutMs: 200, mutation: true }),
    (error: unknown) => error instanceof SessionSupervisorError
      && (error.code === "E_UNITY_UNRESPONSIVE" || error.code === "E_UNITY_TRANSITION_TIMEOUT"),
  );
  assert.equal(workerPosts, 0);
});

test("editor.compile refuses a deliberate pause before any effect with its own code and resume/info routing", async () => {
  const tool = findDefineTool("editor.compile");
  if (!tool) throw new Error("editor.compile definition is unavailable");
  const root = await mkdtemp(join(tmpdir(), "unity-cli-pause-admission-"));
  const project = join(root, "Project");
  const dataDir = join(root, "data");
  try {
    await createUnityProject(project);
    const session = await makeRegistrySession("http://127.0.0.1:49321");
    session.lastUnitySnapshot = {
      schemaVersion: 1,
      observedAt: new Date().toISOString(),
      sourceEditorSessionId: session.editorSessionId,
      sourceListenerGeneration: session.listener.generation,
      fields: {
        compile: { known: true, value: false }, import: { known: true, value: false }, build: { known: true, value: false },
        playMode: { known: true, value: "edit" }, prefabStage: { known: true, value: null },
        activeScene: { known: true, value: { name: "Main", path: "Assets/Main.unity", isDirty: false } },
        runtime: { known: true, value: { ready: false, sessionId: "" } },
      },
    };
    await writeRegistry(dataDir, makeRegistry(registryIdentity(project), [session]));
    const commands: string[] = [];
    const context: ToolContext = {
      requestId: "req_pause_compile",
      projectPath: project,
      absoluteDeadlineMs: Date.now() + 15_000,
      execution: { timeoutMs: 15_000 },
      dataDir,
      artifactDir: dataDir,
      flags: { instance: project },
      bridgeExecute: async (payload) => {
        commands.push(payload.command);
        // 主线程停住时只有 editor.state 快照可由控制通道服务，业务变更绝不能到达 Unity。
        return { ok: true, requestId: payload.requestId, data: { snapshot: session.lastUnitySnapshot, progress: [], blockers: [DEBUG_PAUSE_BLOCKER] } };
      },
      commitEffect: async () => { throw new Error("TS compile must not own the effect barrier"); },
    };
    await assert.rejects(async () => await tool.execute({}, context), (error: unknown) => {
      assert.ok(error instanceof StructuredToolError);
      assert.equal(error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
      assert.equal(error.transient, false);
      assert.equal(error.details?.pauseId, "pause-1");
      assert.equal(error.details?.resumeTool, "debug.pause-resume");
      assert.match(error.nextActions.join(" "), /debug\.pause-list/);
      assert.match(error.nextActions.join(" "), /debug\.pause-resume/);
      return true;
    });
    assert.deepEqual(commands, ["internal.editor.state.snapshot"]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("a Windows modal is still reported as E_UNITY_BLOCKED with its dialog blocker", () => {
  const modal = blockedEditorStateError("Unity Editor is blocked and cannot start compilation.", [DIALOG_BLOCKER]);
  assert.equal(modal.code, "E_UNITY_BLOCKED");
  assert.equal(modal.message, "Unity Editor is blocked and cannot start compilation.");
  assert.deepEqual(modal.details?.blockers, [DIALOG_BLOCKER]);
  const paused = blockedEditorStateError("Unity Editor is blocked and cannot start compilation.", [DEBUG_PAUSE_BLOCKER]);
  assert.equal(paused.code, "E_DEBUG_MAIN_THREAD_PAUSED");
  assert.equal(paused.details?.blockers, undefined);
});

test("the main-thread pause rejection code is registered in the public CLI taxonomy", () => {
  assert.equal(ERROR_CODES.E_DEBUG_MAIN_THREAD_PAUSED, "E_DEBUG_MAIN_THREAD_PAUSED");
});

test("an accepted test run keeps observing a deliberate pause instead of failing on the first refusal", async () => {
  const tool = findDefineTool("test.run");
  if (!tool) throw new Error("test.run definition is unavailable");
  const root = await mkdtemp(join(tmpdir(), "unity-cli-pause-run-"));
  const project = join(root, "Project");
  const dataDir = join(root, "data");
  try {
    await createUnityProject(project);
    const session = await makeRegistrySession("http://127.0.0.1:49321");
    session.lastUnitySnapshot = {
      schemaVersion: 1,
      observedAt: new Date().toISOString(),
      sourceEditorSessionId: session.editorSessionId,
      sourceListenerGeneration: session.listener.generation,
      fields: {
        compile: { known: true, value: false }, import: { known: true, value: false }, build: { known: true, value: false },
        playMode: { known: true, value: "edit" }, prefabStage: { known: true, value: null },
        activeScene: { known: true, value: { name: "Main", path: "Assets/Main.unity", isDirty: false } },
        runtime: { known: true, value: { ready: false, sessionId: "" } },
      },
    };
    await writeRegistry(dataDir, makeRegistry(registryIdentity(project), [session]));
    const digest = "d".repeat(64);
    let refusals = 0;
    let statusReads = 0;
    const bridgeExecute = async (payload: BridgeRequest): Promise<BridgeResponse> => {
      if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: session.lastUnitySnapshot, progress: [], blockers: [] } };
      if (payload.command === "internal.test.run.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: digest } };
      if (payload.command === "internal.test.run.status") {
        statusReads += 1;
        // 暂停在 run 被接受之后才发布：前两次只读 status 都在效果前被同一冻结闸门拒绝。
        if (refusals < 2) { refusals += 1; return pausedGateRejection(payload); }
        return { ok: true, requestId: payload.requestId, data: { terminal: true, rootEnvelope: { ok: true, requestId: "req_pause_accepted_run", data: { summary: { platform: "EditMode", status: "passed", testCount: 1, passed: 1, failed: 0, skipped: 0, inconclusive: 0, duration: 0.1, startedAt: "2026-07-16T00:00:00.000Z", finishedAt: "2026-07-16T00:00:01.000Z" }, failures: [] } } } };
      }
      throw new Error(`unexpected ${payload.command}`);
    };
    const result = await tool.execute({ platform: "EditMode" }, {
      requestId: "req_pause_accepted_run", projectPath: project, dataDir, artifactDir: dataDir, flags: { instance: project },
      absoluteDeadlineMs: Date.now() + 15_000, execution: { timeoutMs: 15_000 }, commitEffect: async () => undefined, bridgeExecute,
    });
    assert.equal((result as { summary: { status: string; passed: number } }).summary.status, "passed");
    assert.equal((result as { summary: { passed: number } }).summary.passed, 1);
    assert.equal(refusals, 2);
    assert.equal(statusReads, 3, "the accepted run must keep polling across the pause and then read its terminal");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("an accepted test run reports the deliberate pause verbatim once the request deadline ends", async () => {
  const tool = findDefineTool("test.run");
  if (!tool) throw new Error("test.run definition is unavailable");
  const root = await mkdtemp(join(tmpdir(), "unity-cli-pause-deadline-"));
  const project = join(root, "Project");
  const dataDir = join(root, "data");
  try {
    await createUnityProject(project);
    const session = await makeRegistrySession("http://127.0.0.1:49322");
    session.lastUnitySnapshot = {
      schemaVersion: 1,
      observedAt: new Date().toISOString(),
      sourceEditorSessionId: session.editorSessionId,
      sourceListenerGeneration: session.listener.generation,
      fields: {
        compile: { known: true, value: false }, import: { known: true, value: false }, build: { known: true, value: false },
        playMode: { known: true, value: "edit" }, prefabStage: { known: true, value: null },
        activeScene: { known: true, value: { name: "Main", path: "Assets/Main.unity", isDirty: false } },
        runtime: { known: true, value: { ready: false, sessionId: "" } },
      },
    };
    await writeRegistry(dataDir, makeRegistry(registryIdentity(project), [session]));
    await assert.rejects(async () => await tool.execute({ platform: "EditMode" }, {
      requestId: "req_pause_deadline_run", projectPath: project, dataDir, artifactDir: dataDir, flags: { instance: project },
      absoluteDeadlineMs: Date.now() + 5_000, execution: { timeoutMs: 5_000 }, commitEffect: async () => undefined,
      bridgeExecute: async (payload: BridgeRequest): Promise<BridgeResponse> => {
        if (payload.command === "internal.editor.state.snapshot") return { ok: true, requestId: payload.requestId, data: { snapshot: session.lastUnitySnapshot, progress: [], blockers: [] } };
        if (payload.command === "internal.test.run.start") return { ok: true, requestId: payload.requestId, data: { terminal: false, accepted: true, rootRequestDigest: "e".repeat(64) } };
        if (payload.command === "internal.test.run.status") return pausedGateRejection(payload);
        throw new Error(`unexpected ${payload.command}`);
      },
    }), (error: unknown) => {
      assert.ok(error instanceof StructuredToolError);
      // 现场仍未被解除时必须给出暂停事实本身，而不是一个没有原因的 deadline 超时。
      assert.equal(error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
      assert.equal(error.details?.pauseId, "pause-1");
      assert.equal(error.details?.resumeTool, "debug.pause-resume");
      assert.equal(error.details?.remainingMs, 21_310);
      assert.match(error.nextActions.join(" "), /debug\.pause-list/);
      assert.match(error.nextActions.join(" "), /debug\.pause-resume/);
      return true;
    });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
