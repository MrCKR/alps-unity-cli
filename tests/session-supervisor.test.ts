import assert from "node:assert/strict";
import test from "node:test";
import {
  SessionSupervisor,
  SessionSupervisorError,
  bridgeHttpTimeoutMs,
  computeRequestDigestV1,
  freezeRequest,
} from "../src/bridge/session-supervisor.js";
import { EditorBlockerObserver } from "../src/editor-blocker-observer.js";
import type { InstanceSelection, UnityInstance, UnityInstanceStatus } from "../src/instances.js";

const INSTANCE_ID = `unity-${"a".repeat(64)}`;
const SESSION_ID = `session-${"b".repeat(64)}`;
const OBSERVED_BLOCKER = {
  type: "dialog" as const, dialogId: "dialog_test", title: "Third Party Dialog", message: "Third Party Dialog",
  buttons: ["OK"], requiresUserAction: true as const, source: "windows-ui" as const, confidence: "exact" as const,
};

function observer(outcome: "observed-empty" | "observed-blocked" | "unproven" | "conflict" | "unsupported" | "offline", calls?: { value: number }): EditorBlockerObserver {
  return { observe: async () => {
    if (calls) calls.value += 1;
    return { outcome, blockers: outcome === "observed-blocked" ? [OBSERVED_BLOCKER] : [], ...(outcome === "conflict" ? { reason: "multiple-process-claims" } : {}) };
  } } as unknown as EditorBlockerObserver;
}

function selection(status: UnityInstanceStatus, generation = 1, target: "editor" | "runtime" = "editor", sessionId = SESSION_ID): InstanceSelection {
  const session = {
    target,
    sessionId,
    ...(target === "editor" ? { editorSessionId: sessionId } : { runtimeSessionId: sessionId, executablePath: process.execPath, buildId: "a".repeat(32) }),
    processId: 101,
    processStartedAt: "2026-07-16T00:00:00.000Z",
    publishedAt: "2026-07-16T00:00:01.000Z",
    publicationSequence: generation,
    lifecycle: status === "blocked" ? "blocked" as const : status === "ready" ? "ready" as const : "transitioning" as const,
    lastUnitySnapshot: {},
    listener: {
      listenerId: `listener-${generation.toString(16).padStart(32, "0")}`,
      generation,
      startedAt: "2026-07-16T00:00:01.000Z",
      endpoint: {
        host: "127.0.0.1" as const,
        preferredPort: 50000,
        actualPort: 50000 + generation,
        fallback: generation !== 1,
        url: `http://127.0.0.1:${50000 + generation}`,
      },
    },
    progress: status === "transitioning" ? [{ type: "compile" }] : [],
    blockers: status === "blocked" ? [{ kind: "modal", title: "Known blocker" }] : [],
    buildStamp: "test-build",
  };
  const instance: UnityInstance = {
    instanceId: INSTANCE_ID,
    canonicalizationVersion: 1,
    canonicalProjectPath: "c:\\repo\\project",
    displayProjectPath: "C:\\repo\\project",
    projectPath: "C:\\repo\\project",
    projectName: "project",
    status,
    target,
    revision: generation,
    updatedAt: "2026-07-16T00:00:01.000Z",
    sessions: [session],
    activeSession: session,
    allSessions: [session],
    sessionId,
    ...(target === "editor" ? { editorSessionId: sessionId } : { runtimeSessionId: sessionId }),
    processId: 101,
    publishedAt: session.publishedAt,
    lastUnitySnapshot: {},
    progress: session.progress,
    blockers: session.blockers,
    buildStamp: "test-build",
    claimVerification: "verified",
  };
  return { instance, matchedBy: "instanceId" };
}

function attestation(generation: number, target: "editor" | "runtime" = "editor") {
  return {
    target,
    instanceId: INSTANCE_ID,
    sessionId: SESSION_ID,
    listenerId: `listener-${generation.toString(16).padStart(32, "0")}`,
    listenerGeneration: generation,
  };
}

test("supervisor consumes one absolute deadline and redelivers identical read bytes after transition", async () => {
  let now = 1_000;
  let selectCount = 0;
  let generation = 1;
  const bodies: string[] = [];
  const remaining: number[] = [];
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => {
      selectCount += 1;
      if (selectCount <= 2) return selection("transitioning", generation);
      return selection("ready", generation);
    },
    post: async (_binding, request, remainingMs) => {
      bodies.push(request.body);
      remaining.push(remainingMs);
      if (bodies.length === 1) {
        generation = 2;
        return {
          response: {
            ok: false,
            requestId: request.payload.requestId,
            error: {
              code: "E_UNITY_DOMAIN_RELOAD",
              message: "reloading",
              nextActions: [],
              transient: true,
              retryAfterMs: 250,
            },
          },
          attestation: attestation(1),
        };
      }
      return {
        response: { ok: true, requestId: request.payload.requestId, data: { snapshot: "complete" } },
        attestation: attestation(2),
      };
    },
  });

  const response = await supervisor.read({ requestId: "req_read", command: "project-tool.run", input: { name: "scene.hierarchy" }, timeoutMs: 5_000 });
  assert.equal(response.ok, true);
  assert.equal(bodies.length, 2);
  assert.equal(bodies[0], bodies[1]);
  assert.match(bodies[0], /"absoluteDeadlineUtc":"1970-01-01T00:00:06.000Z"/);
  assert.ok(remaining[1] < remaining[0]);
  assert.ok(now < 6_000);
});

test("runtime-tool commands select only the development Player target", async () => {
  const selectedTargets: string[] = [];
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 10_000,
    sleep: async () => undefined,
    select: async (_flags, _dataDir, target) => {
      selectedTargets.push(target);
      return selection("ready", 1, target);
    },
    post: async (binding, request) => {
      assert.equal(binding.target, "runtime");
      assert.equal(binding.sessionId, SESSION_ID);
      return { response: { ok: true, requestId: request.payload.requestId, data: { tools: [] } }, attestation: attestation(1, "runtime") };
    },
  });

  const response = await supervisor.read({ requestId: "req_runtime_catalog", command: "runtime-tool.list", input: {} });
  assert.equal(response.ok, true);
  assert.deepEqual(selectedTargets, ["runtime"]);
});

test("caller absolute deadline is not truncated by the implicit execution default", async () => {
  const callerDeadlineMs = 130_000;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 10_000,
    sleep: async () => undefined,
    select: async () => selection("ready", 1),
    post: async (_binding, request, remainingMs) => {
      assert.equal(remainingMs, 120_000);
      assert.equal(request.absoluteDeadlineMs, callerDeadlineMs);
      assert.equal(request.payload.execution.absoluteDeadlineUtc, "1970-01-01T00:02:10.000Z");
      return { response: { ok: true, requestId: request.payload.requestId, data: {} }, attestation: attestation(1) };
    },
  });

  const response = await supervisor.execute({
    requestId: "req_caller_deadline",
    command: "project-tool.run",
    input: {},
    mutation: true,
    absoluteDeadlineMs: callerDeadlineMs,
  });
  assert.equal(response.ok, true);
});

test("supervisor retains its execution default when no caller deadline exists", async () => {
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 10_000,
    sleep: async () => undefined,
    select: async () => selection("ready", 1),
    post: async (_binding, request, remainingMs) => {
      assert.equal(remainingMs, 60_000);
      assert.equal(request.absoluteDeadlineMs, 70_000);
      return { response: { ok: true, requestId: request.payload.requestId, data: {} }, attestation: attestation(1) };
    },
  });

  const response = await supervisor.read({ requestId: "req_default_deadline", command: "project-tool.run", input: {} });
  assert.equal(response.ok, true);
});

test("query transport recovery rebinds generation without rebuilding request", async () => {
  let now = 10_000;
  let generation = 1;
  const bodies: string[] = [];
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection("ready", generation),
    post: async (_binding, request) => {
      bodies.push(request.body);
      if (bodies.length === 1) {
        generation = 2;
        throw new Error("listener closed during reload");
      }
      return {
        response: { ok: true, requestId: request.payload.requestId, data: { items: [] } },
        attestation: attestation(2),
      };
    },
  });

  const response = await supervisor.read({ requestId: "req_rebind", command: "project-tool.run", input: {}, timeoutMs: 2_000 });
  assert.equal(response.ok, true);
  assert.deepEqual(bodies, [bodies[0], bodies[0]]);
  assert.equal(now, 10_100);
});

test("same-session mutation redelivery reuses exact bytes, deadline, and durable effect", async () => {
  let now = 20_000;
  let generation = 1;
  let effects = 0;
  const posts: Array<{ sessionId: string; generation: number; body: string; deadline: number; remainingMs: number }> = [];
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection("ready", generation),
    post: async (binding, request, remainingMs) => {
      posts.push({ sessionId: binding.sessionId, generation: binding.listenerGeneration, body: request.body, deadline: request.absoluteDeadlineMs, remainingMs });
      if (posts.length === 1) {
        effects += 1;
        generation = 2;
        throw new Error("response lost after request upload");
      }
      return {
        response: { ok: true, requestId: request.payload.requestId, data: { completed: true } },
        attestation: attestation(2),
      };
    },
  });

  const response = await supervisor.execute({ requestId: "req_mutation", command: "project-tool.run", input: {}, timeoutMs: 5_000, mutation: true });
  assert.equal(response.ok, true);
  assert.deepEqual(posts.map((post) => post.sessionId), [SESSION_ID, SESSION_ID]);
  assert.deepEqual(posts.map((post) => post.generation), [1, 2]);
  assert.equal(posts[0].body, posts[1].body);
  assert.equal(posts[0].deadline, 25_000);
  assert.equal(posts[1].deadline, posts[0].deadline);
  assert.ok(posts[1].remainingMs < posts[0].remainingMs);
  assert.equal(effects, 1);
});

test("cross-session mutation stops after one POST and returns bounded outcome unknown", async () => {
  let now = 24_000;
  let selections = 0;
  let effects = 0;
  const posts: Array<{ sessionId: string; generation: number; body: string; deadline: number }> = [];
  const replacementSessionId = `session-${"c".repeat(64)}`;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection("ready", selections++ === 0 ? 1 : 2, "editor", selections === 1 ? SESSION_ID : replacementSessionId),
    observer: observer("unproven"),
    post: async (binding, request) => {
      posts.push({ sessionId: binding.sessionId, generation: binding.listenerGeneration, body: request.body, deadline: request.absoluteDeadlineMs });
      effects += 1;
      throw new Error("response lost after request upload");
    },
  });

  await assert.rejects(
    supervisor.execute({ requestId: "req_cross_session_fence", command: "project-tool.run", input: {}, timeoutMs: 1_000, mutation: true }),
    (error: unknown) => {
      assert.ok(error instanceof SessionSupervisorError);
      assert.equal(error.code, "E_EXECUTION_TIMEOUT");
      assert.deepEqual(Object.keys(error.details ?? {}).sort(), ["dispatch", "instanceId", "outcome", "reason", "replay"]);
      assert.equal(error.details?.outcome, "unknown");
      assert.equal(error.details?.dispatch, "may-have-started");
      assert.equal(error.details?.replay, "new-decision-required");
      assert.match(String(error.details?.reason), /session changed/);
      assert.deepEqual(error.nextActions, [
        "Run unity_cli tools run editor.state to observe the selected Editor before making a new submission decision.",
        "Use the affected capability's read-only Tool or artifact readback to determine whether the prior mutation took effect.",
      ]);
      assert.doesNotMatch(error.nextActions.join(" "), /dialog|retry|repeat|replay/i);
      return true;
    },
  );
  assert.deepEqual(posts.map((post) => post.sessionId), [SESSION_ID]);
  assert.deepEqual(posts.map((post) => post.generation), [1]);
  assert.equal(posts[0].deadline, 25_000);
  assert.equal(effects, 1);
  assert.ok(now < 25_000);
});

test("read transport recovery may rerun on a replacement Editor session", async () => {
  let now = 26_000;
  let currentSessionId = SESSION_ID;
  let generation = 1;
  const posts: Array<{ sessionId: string; generation: number; body: string; deadline: number }> = [];
  const replacementSessionId = `session-${"d".repeat(64)}`;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection("ready", generation, "editor", currentSessionId),
    post: async (binding, request) => {
      posts.push({ sessionId: binding.sessionId, generation: binding.listenerGeneration, body: request.body, deadline: request.absoluteDeadlineMs });
      if (posts.length === 1) {
        currentSessionId = replacementSessionId;
        generation = 2;
        throw new Error("read response lost before session replacement");
      }
      return { response: { ok: true, requestId: request.payload.requestId, data: { items: ["fresh"] } }, attestation: { ...attestation(2), sessionId: replacementSessionId } };
    },
  });

  const response = await supervisor.read({ requestId: "req_cross_session_read", command: "project-tool.run", input: {}, timeoutMs: 2_000 });
  assert.equal(response.ok, true);
  assert.deepEqual(posts.map((post) => post.sessionId), [SESSION_ID, replacementSessionId]);
  assert.deepEqual(posts.map((post) => post.generation), [1, 2]);
  assert.equal(posts[0].body, posts[1].body);
  assert.equal(posts[0].deadline, posts[1].deadline);
  assert.ok(now < 28_000);
});

test("mutation without recoverable terminal becomes execution timeout at the one deadline", async () => {
  let now = 25_000;
  let posts = 0;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection("ready", 1),
    post: async () => {
      posts += 1;
      throw new Error("response remains unavailable");
    },
  });

  await assert.rejects(
    supervisor.execute({ requestId: "req_mutation_timeout", command: "project-tool.run", input: {}, timeoutMs: 250, mutation: true }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_EXECUTION_TIMEOUT",
  );
  assert.ok(posts >= 2);
  assert.equal(now, 25_250);
});

test("post-dispatch rebind failure becomes execution timeout", async () => {
  let now = 28_000;
  let selections = 0;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection(selections++ === 0 ? "ready" : "blocked", 1),
    post: async () => { throw new Error("response lost after upload"); },
  });

  await assert.rejects(
    supervisor.execute({ requestId: "req_post_bind_failure", command: "project-tool.run", input: {}, timeoutMs: 500, mutation: true }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_EXECUTION_TIMEOUT",
  );
});

test("initial selector is bounded by the immutable deadline", async () => {
  let posts = 0;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: Date.now,
    select: async () => await new Promise<InstanceSelection>(() => undefined),
    post: async () => { posts += 1; throw new Error("must not post"); },
  });
  const startedAt = Date.now();
  await assert.rejects(
    supervisor.execute({ requestId: "req_selector_timeout", command: "project-tool.run", input: {}, timeoutMs: 30, mutation: false }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_EXECUTION_TIMEOUT",
  );
  assert.ok(Date.now() - startedAt < 250);
  assert.equal(posts, 0);
});

test("blocked session fails before POST and does not expose endpoint details", async () => {
  let posts = 0;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 30_000,
    sleep: async () => undefined,
    select: async () => selection("blocked", 1),
    post: async () => {
      posts += 1;
      throw new Error("must not post");
    },
  });

  await assert.rejects(
    supervisor.execute({ requestId: "req_blocked", command: "project-tool.run", input: {}, mutation: true }),
    (error: unknown) => {
      assert.ok(error instanceof SessionSupervisorError);
      assert.equal(error.code, "E_UNITY_BLOCKED");
      assert.doesNotMatch(JSON.stringify(error.details), /endpoint|port|listener|editorSessionId/i);
      return true;
    },
  );
  assert.equal(posts, 0);
});

test("successful direct response performs zero Windows blocker observations", async () => {
  const calls = { value: 0 };
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 35_000,
    select: async () => selection("ready", 1),
    observer: observer("observed-empty", calls),
    post: async (_binding, request) => ({ response: { ok: true, requestId: request.payload.requestId, data: { done: true } }, attestation: attestation(1) }),
  });
  const result = await supervisor.read({ requestId: "req_zero_observer", command: "project-tool.run", input: {}, timeoutMs: 500 });
  assert.equal(result.ok, true);
  assert.equal(calls.value, 0);
});

test("read transport failure returns observed blocker in the same error", async () => {
  let now = 36_000;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection("ready", 1),
    observer: observer("observed-blocked"),
    post: async () => { throw new Error("modal blocked main thread"); },
  });
  await assert.rejects(
    supervisor.read({ requestId: "req_read_blocker", command: "project-tool.run", input: {}, timeoutMs: 500 }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_UNITY_BLOCKED"
      && Array.isArray(error.details?.blockers) && error.details.blockers.length === 1,
  );
});

test("post-dispatch mutation keeps execution timeout and attaches observed blockers", async () => {
  let now = 37_000;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection("ready", 1),
    observer: observer("observed-blocked"),
    post: async () => { throw new Error("response lost behind modal"); },
  });
  await assert.rejects(
    supervisor.execute({ requestId: "req_mutation_blocker", command: "project-tool.run", input: {}, timeoutMs: 500, mutation: true }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_EXECUTION_TIMEOUT"
      && Array.isArray(error.details?.blockers) && error.details.blockers.length === 1,
  );
});

test("Bridge execution timeout terminal is returned without an additional observation", async () => {
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 38_000,
    select: async () => selection("ready", 1),
    observer: observer("observed-blocked"),
    post: async (_binding, request) => ({
      response: { ok: false, requestId: request.payload.requestId, error: { code: "E_EXECUTION_TIMEOUT", message: "unknown", nextActions: [], transient: false, retryAfterMs: 0, details: { phase: "effect_started" } } },
      attestation: attestation(1),
    }),
  });
  const result = await supervisor.execute({ requestId: "req_bridge_unknown", command: "project-tool.run", input: {}, timeoutMs: 500, mutation: true });
  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.equal(result.error.code, "E_EXECUTION_TIMEOUT");
    assert.equal((result.error.details as { phase?: string }).phase, "effect_started");
    assert.equal(((result.error.details as { blockers?: unknown[] }).blockers ?? []).length, 0);
  }
});

test("pre-registry selector failure reports unique process modal before dispatch", async () => {
  let posts = 0;
  const offline = selection("offline", 1);
  offline.instance.sessions = [];
  offline.instance.activeSession = undefined;
  const supervisor = new SessionSupervisor({ instance: "C:\\repo\\project" }, { dataDir: "test" }, {
    now: () => 39_000,
    select: async () => { throw new Error("registry not written"); },
    selectObservation: async () => offline,
    observer: observer("observed-blocked"),
    post: async () => { posts += 1; throw new Error("must not post"); },
  });
  await assert.rejects(
    supervisor.execute({ requestId: "req_pre_registry_blocker", command: "project-tool.run", input: {}, timeoutMs: 500, mutation: true }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_UNITY_BLOCKED",
  );
  assert.equal(posts, 0);
});

test("observeNow binds and posts once during a transitioning session without waiting or rebinding", async () => {
  let sleeps = 0;
  let posts = 0;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 40_000,
    sleep: async () => { sleeps += 1; },
    select: async () => selection("transitioning", 1),
    selectObservation: async (_flags, _dataDir, remainingMs) => {
      assert.equal(remainingMs, 10_000);
      return selection("transitioning", 1);
    },
    post: async (_binding, request, remainingMs) => {
      posts += 1;
      assert.equal(remainingMs, 3_500);
      return { response: { ok: true, requestId: request.payload.requestId, data: { schemaVersion: 1 } }, attestation: attestation(1) };
    },
  });

  const result = await supervisor.observeNow({ requestId: "req_observe", command: "project-tool.run", input: { name: "editor.state" }, timeoutMs: 99_000 });
  assert.equal(result.ok, true);
  assert.equal(sleeps, 0);
  assert.equal(posts, 1);
});

test("observeNow freezes the caller absolute deadline after selector time is consumed", async () => {
  let now = 50_000;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async () => undefined,
    selectObservation: async (_flags, _dataDir, remainingMs) => {
      assert.equal(remainingMs, 500);
      now += 200;
      return selection("ready", 1);
    },
    post: async (_binding, request, remainingMs) => {
      assert.equal(remainingMs, 300);
      assert.equal(request.absoluteDeadlineMs, 50_500);
      assert.equal(request.payload.execution.absoluteDeadlineUtc, "1970-01-01T00:00:50.500Z");
      return { response: { ok: true, requestId: request.payload.requestId, data: {} }, attestation: attestation(1) };
    },
  });

  const result = await supervisor.observeNow({
    requestId: "req_observe_deadline",
    command: "project-tool.run",
    input: {},
    observationAbsoluteDeadlineMs: 50_500,
  });
  assert.equal(result.ok, true);
});

test("observeNow preserves an already-consumed supplied budget and does not POST after expiry", async () => {
  let posts = 0;
  const startedAt = Date.now();
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: Date.now,
    sleep: async () => undefined,
    select: async () => selection("ready", 1),
    selectObservation: async (_flags, _dataDir, remainingMs) => {
      assert.ok(remainingMs <= 50);
      await new Promise((complete) => setTimeout(complete, 70));
      return selection("ready", 1);
    },
    post: async () => {
      posts += 1;
      throw new Error("must not post");
    },
  });

  await assert.rejects(
    supervisor.observeNow({ requestId: "req_observe_expired", command: "project-tool.run", input: {}, timeoutMs: 50 }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_EXECUTION_TIMEOUT",
  );
  assert.equal(posts, 0);
  assert.ok(Date.now() - startedAt < 150);
});

test("injected observeNow freezes the selected real instance and caps a suspended probe at 3500ms", async () => {
  const startedAt = Date.now();
  let selected = 0;
  const supervisor = new SessionSupervisor({}, {
    bridgeExecute: async () => new Promise(() => undefined),
  }, {
    selectObservation: async () => {
      selected += 1;
      return selection("ready", 1);
    },
  });
  await assert.rejects(
    supervisor.observeNow({ requestId: "req_injected_timeout", command: "project-tool.run", input: {} }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_EXECUTION_TIMEOUT",
  );
  assert.equal(selected, 1);
  assert.ok(Date.now() - startedAt < 4_000);
});

test("observeNow does not POST for offline or conflict observations", async () => {
  for (const status of ["offline", "conflict"] as const) {
    let posts = 0;
    const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
      now: () => 40_000,
      sleep: async () => undefined,
      select: async () => selection(status, 1),
      selectObservation: async () => selection(status, 1),
      post: async () => { posts += 1; throw new Error("must not post"); },
    });
    await assert.rejects(() => supervisor.observeNow({ requestId: `req_${status}`, command: "project-tool.run", input: {} }), SessionSupervisorError);
    assert.equal(posts, 0);
  }
});

test("RequestDigest v1 binds instance identity and exact UTF-8 request bytes", () => {
  const body = "{\"protocolVersion\":2,\"requestId\":\"req_digest\",\"instanceId\":\"unity-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"command\":\"project-tool.run\",\"input\":{},\"execution\":{\"absoluteDeadlineUtc\":\"2026-07-16T00:00:00.000Z\",\"completionContractVersion\":1,\"requestDigestVersion\":1}}";
  const digest = computeRequestDigestV1(INSTANCE_ID, Buffer.from(body, "utf8"));
  assert.equal(digest, "0c8c90d9afafe0a4d3357249f7e4e739c9952e37f417550d44655b50652866eb");
  assert.notEqual(computeRequestDigestV1(INSTANCE_ID, Buffer.from(`${body} `, "utf8")), digest);
  assert.notEqual(computeRequestDigestV1(`unity-${"c".repeat(64)}`, Buffer.from(body, "utf8")), digest);
});

test("Bridge request v2 carries identity, immutable execution controls and remaining HTTP budget", () => {
  const frozen = freezeRequest({
    requestId: "req_contract",
    command: "project-tool.run",
    input: { name: "asset.info", input: {} },
    dryRun: true,
  }, INSTANCE_ID, 55_000);
  assert.equal(frozen.payload.protocolVersion, 2);
  assert.equal(frozen.payload.instanceId, INSTANCE_ID);
  assert.equal(frozen.payload.execution.completionContractVersion, 1);
  assert.equal(frozen.payload.execution.requestDigestVersion, 1);
  assert.equal(frozen.payload.execution.dryRun, true);
  assert.equal(bridgeHttpTimeoutMs(frozen.payload, 50_250), 4_750);
  assert.equal(frozen.requestDigest, computeRequestDigestV1(INSTANCE_ID, Buffer.from(frozen.body, "utf8")));
  assert.ok(Object.isFrozen(frozen.payload));
  assert.ok(Object.isFrozen(frozen.payload.execution));
});

test("durable transition status uses the health-independent selector", async () => {
  let normalSelections = 0;
  let transitionSelections = 0;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    select: async () => { normalSelections += 1; return selection("ready", 1); },
    selectTransition: async () => { transitionSelections += 1; return selection("transitioning", 2); },
    post: async (_binding, request) => ({
      response: { ok: true, requestId: request.payload.requestId, data: { terminal: true } },
      attestation: attestation(2),
    }),
  });
  const response = await supervisor.read({
    requestId: "req_transition_status",
    command: "internal.editor.compile.status",
    input: { rootRequestId: "req_root" },
    absoluteDeadlineMs: Date.now() + 5_000,
    allowTransitionBinding: true,
  });
  assert.equal(response.ok, true);
  assert.equal(normalSelections, 0);
  assert.equal(transitionSelections, 1);
});

test("durable roots and root-bound controls retain one immutable deadline and permit only the private allowlist", async () => {
  let now = 10_000;
  const commands = ["internal.editor.refresh.start", "internal.editor.refresh.status", "internal.package.files.claim", "internal.package.files.commit", "internal.package.files.prove", "internal.package.files.fail", "internal.navmesh.bake.start", "internal.navmesh.bake.status", "internal.navmesh.bake.plan", "internal.navmesh.bake.snapshot", "internal.navmesh.bake.cancel"];
  for (const command of commands) {
    const bodies: string[] = [];
    const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
      now: () => now,
      sleep: async () => { now += 1; },
      select: async () => selection("ready", 1),
      post: async (_binding, request) => {
        bodies.push(request.body);
        return { response: { ok: true, requestId: request.payload.requestId, data: {} }, attestation: attestation(1) };
      },
    });
    const readOnly = command.endsWith("status") || command.endsWith("plan") || command.endsWith("snapshot");
    const response = await supervisor.execute({ requestId: `req_${command}`, command, input: {}, mutation: !readOnly, absoluteDeadlineMs: 20_000, timeoutMs: 50_000, allowTransitionBinding: true });
    assert.equal(response.ok, true);
    assert.match(bodies[0]!, /"absoluteDeadlineUtc":"1970-01-01T00:00:20.000Z"/);
  }
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, { now: () => now, sleep: async () => undefined, select: async () => selection("ready", 1) });
  await assert.rejects(() => supervisor.execute({ requestId: "req_forbidden_transition", command: "project-tool.run", input: {}, allowTransitionBinding: true }), (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_INTERNAL_INVOCATION_INVALID");
});
