import assert from "node:assert/strict";
import test from "node:test";
import {
  SessionSupervisor,
  SessionSupervisorError,
  bridgeHttpTimeoutMs,
  computeRequestDigestV1,
  freezeRequest,
} from "../src/bridge/session-supervisor.js";
import type { InstanceSelection, UnityInstance, UnityInstanceStatus } from "../src/instances.js";

const INSTANCE_ID = `unity-${"a".repeat(64)}`;
const SESSION_ID = `session-${"b".repeat(64)}`;

function selection(status: UnityInstanceStatus, generation = 1): InstanceSelection {
  const session = {
    editorSessionId: SESSION_ID,
    processId: 101,
    processStartedAt: "2026-07-16T00:00:00.000Z",
    lastHeartbeatAt: "2026-07-16T00:00:01.000Z",
    claimVerification: "verified" as const,
    state: status === "blocked" ? "blocked" as const : status === "ready" ? "ready" as const : "transitioning" as const,
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
    revision: generation,
    updatedAt: "2026-07-16T00:00:01.000Z",
    sessions: [session],
    activeSession: session,
    editorSessionId: SESSION_ID,
    processId: 101,
    lastHeartbeatAt: session.lastHeartbeatAt,
    lastUnitySnapshot: {},
    progress: session.progress,
    blockers: session.blockers,
    buildStamp: "test-build",
    claimVerification: "verified",
  };
  return { instance, matchedBy: "instanceId" };
}

function attestation(generation: number) {
  return {
    instanceId: INSTANCE_ID,
    editorSessionId: SESSION_ID,
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

  const response = await supervisor.read({ requestId: "req_read", command: "context.read", input: { name: "scene.hierarchy" }, timeoutMs: 5_000 });
  assert.equal(response.ok, true);
  assert.equal(bodies.length, 2);
  assert.equal(bodies[0], bodies[1]);
  assert.match(bodies[0], /"absoluteDeadlineUtc":"1970-01-01T00:00:06.000Z"/);
  assert.ok(remaining[1] < remaining[0]);
  assert.ok(now < 6_000);
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

  const response = await supervisor.read({ requestId: "req_rebind", command: "context.read", input: {}, timeoutMs: 2_000 });
  assert.equal(response.ok, true);
  assert.deepEqual(bodies, [bodies[0], bodies[0]]);
  assert.equal(now, 10_100);
});

test("journal-backed mutation redelivery reuses exact bytes and recovers terminal result", async () => {
  let now = 20_000;
  const bodies: string[] = [];
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => now,
    sleep: async (ms) => { now += ms; },
    select: async () => selection("ready", 1),
    post: async (_binding, request) => {
      bodies.push(request.body);
      if (bodies.length === 1) throw new Error("response lost after request upload");
      return {
        response: { ok: true, requestId: request.payload.requestId, data: { completed: true } },
        attestation: attestation(1),
      };
    },
  });

  const response = await supervisor.execute({ requestId: "req_mutation", command: "project-tool.run", input: {}, timeoutMs: 5_000, mutation: true });
  assert.equal(response.ok, true);
  assert.deepEqual(bodies, [bodies[0], bodies[0]]);
});

test("mutation without recoverable terminal becomes outcome unknown at the one deadline", async () => {
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
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_REQUEST_OUTCOME_UNKNOWN",
  );
  assert.ok(posts >= 2);
  assert.equal(now, 25_250);
});

test("post-dispatch rebind failure becomes outcome unknown", async () => {
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
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_REQUEST_OUTCOME_UNKNOWN",
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
    supervisor.execute({ requestId: "req_selector_timeout", command: "context.read", input: {}, timeoutMs: 30, mutation: false }),
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

test("observeNow binds and posts once during a transitioning session without waiting or rebinding", async () => {
  let sleeps = 0;
  let posts = 0;
  const supervisor = new SessionSupervisor({ instance: INSTANCE_ID }, { dataDir: "test" }, {
    now: () => 40_000,
    sleep: async () => { sleeps += 1; },
    select: async () => selection("transitioning", 1),
    selectObservation: async (_flags, _dataDir, remainingMs) => {
      assert.equal(remainingMs, 1_000);
      return selection("transitioning", 1);
    },
    post: async (_binding, request, remainingMs) => {
      posts += 1;
      assert.equal(remainingMs, 300);
      return { response: { ok: true, requestId: request.payload.requestId, data: { schemaVersion: 1 } }, attestation: attestation(1) };
    },
  });

  const result = await supervisor.observeNow({ requestId: "req_observe", command: "context.read", input: { name: "editor.state" }, timeoutMs: 99_000 });
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
    command: "context.read",
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
    supervisor.observeNow({ requestId: "req_observe_expired", command: "context.read", input: {}, timeoutMs: 50 }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_CONTEXT_READ_TIMEOUT",
  );
  assert.equal(posts, 0);
  assert.ok(Date.now() - startedAt < 150);
});

test("injected observeNow freezes the selected real instance and caps a suspended probe at 300ms", async () => {
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
    supervisor.observeNow({ requestId: "req_injected_timeout", command: "context.read", input: {} }),
    (error: unknown) => error instanceof SessionSupervisorError && error.code === "E_CONTEXT_READ_TIMEOUT",
  );
  assert.equal(selected, 1);
  assert.ok(Date.now() - startedAt < 500);
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
    await assert.rejects(() => supervisor.observeNow({ requestId: `req_${status}`, command: "context.read", input: {} }), SessionSupervisorError);
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
