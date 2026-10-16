import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

import { failure } from "../src/output.js";
import { execute } from "../src/cli.js";
import { selectObservationInstanceForFlags, type MainThreadDebugPause } from "../src/instances.js";
import { debugPausePartialOutcome } from "../src/tools/errors.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { DISPATCH_PAUSE_DIAGNOSIS_BUDGET_MS, enrichTargetFailureWithObservedBlocker } from "../src/tools/dispatcher.js";
import type { EditorBlockerObserver } from "../src/editor-blocker-observer.js";
import {
  createUnityProject,
  makeRegistry,
  makeRegistrySession,
  registryIdentity,
  startStrictHealthServer,
  writeRegistry,
} from "./helpers/instance-registry-v3.js";

const INSTANCE_ID = `unity-${"c".repeat(64)}`;
const PAUSE_ID = "pause-9";

/** 失败归因只读同一 selector；这里给出的现场就是 live publication 投影出来的暂停事实。 */
function selectorReturning(pause: Record<string, unknown> | undefined, calls: { count: number; remainingMs: number[] }) {
  return (async (_flags: unknown, _dataDir: string, remainingMs: number) => {
    calls.count += 1;
    calls.remainingMs.push(remainingMs);
    return {
      instance: {
        instanceId: INSTANCE_ID,
        canonicalProjectPath: "c:\\repo\\project",
        displayProjectPath: "C:\\repo\\project",
        activeSession: undefined,
        status: "blocked",
        ...(pause ? { mainThreadDebugPause: pause } : {}),
      },
      matchedBy: "instanceId",
    };
  }) as never;
}

/** dialog 分支在这些用例里不得运行：暂停归因先于 modal 诊断。 */
function unprovenObserver(calls: { count: number }): EditorBlockerObserver {
  return { observe: async () => { calls.count += 1; return { outcome: "unproven", blockers: [] }; } } as unknown as EditorBlockerObserver;
}

/** supervisor 对已发出 mutation 的统一失败：dispatch 可能已开始，outcome 原本是 unknown。 */
function dispatchedTimeoutFailure() {
  return failure("req_dispatched", "E_EXECUTION_TIMEOUT", "The request did not return a terminal result before its deadline.", [
    "Run unitycli tools run editor.state to observe the selected Editor before making a new submission decision.",
    "Use the affected capability's read-only Tool or artifact readback to determine whether the prior mutation took effect.",
  ], {
    instanceId: INSTANCE_ID,
    reason: "Post-dispatch listener rebind failed with E_UNITY_TRANSITION_TIMEOUT.",
    outcome: "unknown",
    dispatch: "may-have-started",
    replay: "new-decision-required",
  });
}

test("a dispatched failure proven paused becomes the typed partial outcome with the release route", async () => {
  const calls = { count: 0, remainingMs: [] as number[] };
  const observerCalls = { count: 0 };
  const enriched = await enrichTargetFailureWithObservedBlocker(
    dispatchedTimeoutFailure(),
    {},
    { dataDir: "test" },
    Date.now(),
    unprovenObserver(observerCalls),
    selectorReturning({ pauseId: PAUSE_ID, threadId: 1, publishedSequence: 7, timeoutKind: "bounded", remainingMs: 21_310 }, calls),
    () => Date.now(),
  );
  assert.equal(enriched.ok, false);
  if (enriched.ok) throw new Error("unreachable");
  assert.equal(enriched.error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
  assert.equal(enriched.error.transient, false);
  assert.equal(enriched.error.retryAfterMs, 0);
  assert.match(enriched.error.message, /dispatched and may have started/);
  assert.match(enriched.error.message, new RegExp(PAUSE_ID));
  const details = enriched.error.details as Record<string, unknown>;
  // typed partial outcome：dispatch 可能已开始，永远不能报成 unknown。
  assert.equal(details.outcome, "may-have-started");
  assert.equal(details.dispatch, "may-have-started");
  assert.equal(details.replay, "new-decision-required");
  assert.equal(details.pauseId, PAUSE_ID);
  assert.equal(details.threadId, 1);
  assert.equal(details.mainThread, true);
  assert.equal(details.timeoutKind, "bounded");
  assert.equal(details.remainingMs, 21_310);
  assert.equal(details.pauseTool, "debug.pause.list");
  assert.equal(details.resumeTool, "debug.pause.resume");
  // 原失败原因保留：调用方仍能看到具体是哪一步没能拿到终态。
  assert.match(String(details.reason), /Post-dispatch listener rebind failed/);
  assert.equal(calls.count, 1);
  assert.deepEqual(calls.remainingMs, [DISPATCH_PAUSE_DIAGNOSIS_BUDGET_MS]);
  assert.equal(observerCalls.count, 0);
  const actions = enriched.error.nextActions.join(" ");
  assert.ok(actions.indexOf("debug.pause.list") >= 0 && actions.indexOf("debug.pause.list") < actions.indexOf("debug.pause.resume"), actions);
  assert.match(actions, /pauseId pause-9/);
  assert.match(actions, /never replayed automatically/);
});

test("without a live pause publication the dispatched timeout keeps its existing unknown outcome", async () => {
  const calls = { count: 0, remainingMs: [] as number[] };
  const original = dispatchedTimeoutFailure();
  const enriched = await enrichTargetFailureWithObservedBlocker(
    original,
    {},
    { dataDir: "test" },
    Date.now() + 1_000,
    unprovenObserver({ count: 0 }),
    selectorReturning(undefined, calls),
    () => Date.now(),
  );
  assert.deepEqual(enriched, original);
  // 第一次是暂停归因，第二次是原有的 modal 诊断；没有现场证据时两者都不改写失败。
  assert.equal(calls.count, 2);
  assert.equal(calls.remainingMs[0], DISPATCH_PAUSE_DIAGNOSIS_BUDGET_MS);
});

test("the pause attribution runs even after the caller deadline elapsed, and never fabricates missing facts", async () => {
  const calls = { count: 0, remainingMs: [] as number[] };
  const exhausted = Date.now() - 1_000;
  const enriched = await enrichTargetFailureWithObservedBlocker(
    dispatchedTimeoutFailure(),
    {},
    { dataDir: "test" },
    exhausted,
    unprovenObserver({ count: 0 }),
    selectorReturning({ pauseId: PAUSE_ID, threadId: 3, publishedSequence: 1 }, calls),
    () => Date.now(),
  );
  assert.equal(enriched.ok, false);
  if (enriched.ok) throw new Error("unreachable");
  assert.equal(enriched.error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
  const details = enriched.error.details as Record<string, unknown>;
  assert.equal(details.outcome, "may-have-started");
  assert.equal(details.pauseId, PAUSE_ID);
  assert.equal(details.threadId, 3);
  // publication 没带边界事实时省略该字段，绝不回填旧值。
  assert.equal("timeoutKind" in details, false);
  assert.equal("remainingMs" in details, false);
});

test("other routed failures and the pre-effect pause refusal are never rewritten", async () => {
  const cases = [
    failure("req_pre_effect", "E_DEBUG_MAIN_THREAD_PAUSED", "The Unity Editor main thread is deliberately paused at debug hook breakpoint.", [
      "Run unitycli tools run debug.pause.list to read the pause site that is holding the main thread.",
    ], { pauseId: PAUSE_ID, threadId: 1, mainThread: true, timeoutKind: "unbounded", remainingMs: 0 }),
    failure("req_connection", "BRIDGE_CONNECTION_FAILED", "Live bridge failed.", ["Run unitycli instances list."], { instanceId: INSTANCE_ID, dispatch: "may-have-started" }),
    failure("req_deadline", "E_EXECUTION_TIMEOUT", "The absolute request deadline elapsed before a terminal result was proven.", ["Inspect editor.state."], { instanceId: INSTANCE_ID }),
  ];
  for (const original of cases) {
    const calls = { count: 0, remainingMs: [] as number[] };
    const enriched = await enrichTargetFailureWithObservedBlocker(
      original,
      {},
      { dataDir: "test" },
      Date.now(),
      unprovenObserver({ count: 0 }),
      selectorReturning({ pauseId: PAUSE_ID, threadId: 1, publishedSequence: 1, timeoutKind: "bounded", remainingMs: 10 }, calls),
      () => Date.now(),
    );
    assert.deepEqual(enriched, original);
    assert.equal(calls.count, 0);
  }
});

test("the partial outcome builder keeps the pause facts and the caller-owned replay rule", () => {
  const envelope = debugPausePartialOutcome("req_builder", { pauseId: PAUSE_ID, threadId: 2, publishedSequence: 4, timeoutKind: "unbounded", remainingMs: 0 }, { outcome: "unknown", dispatch: "may-have-started", reason: "kept" });
  assert.equal(envelope.ok, false);
  assert.equal(envelope.error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
  const details = envelope.error.details as Record<string, unknown>;
  assert.equal(details.outcome, "may-have-started");
  assert.equal(details.reason, "kept");
  assert.equal(details.pauseId, PAUSE_ID);
  assert.equal(details.timeoutKind, "unbounded");
  assert.equal(details.remainingMs, 0);
  assert.ok(envelope.error.nextActions.some((action) => action.includes("debug.pause.list")));
  assert.ok(envelope.error.nextActions.some((action) => action.includes("debug.pause.resume")));
  assert.ok(envelope.error.nextActions.some((action) => action.includes("never replayed automatically")));
  assert.equal(envelope.error.transient, false);
  assert.equal(envelope.error.retryAfterMs, 0);
});

/**
 * 真实路径夹具：真实 registry v3 claim + 真实 /health publication。
 * 即时观察 selector 只归约 registry，因此它的结果本身永远不带暂停证据——
 * 这正是上一轮的缺陷：typed projection 拿不到证据，真实调用仍返回 unknown。
 */
async function livePauseFixture(debugPause: Record<string, unknown> | null) {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-dispatched-pause-data-"));
  const projectPath = await mkdtemp(join(tmpdir(), "unity-cli-dispatched-pause-project-"));
  await createUnityProject(projectPath);
  const identity = registryIdentity(projectPath);
  const sessionId = `session-${"1".repeat(64)}`;
  const listenerId = `listener-${"2".repeat(32)}`;
  // 暂停中的主线程不再推进 pulse：publication 仍是本次 live 证据，只是 ageMs 已经很大。
  const health = await startStrictHealthServer((_request, port) => ({
    version: 3,
    target: "editor",
    instanceId: identity.instanceId,
    canonicalProjectPath: identity.canonicalProjectPath,
    canonicalizationVersion: 1,
    sessionId,
    listenerId,
    listenerGeneration: 1,
    actualPort: port,
    buildStamp: "test-build",
    mainThread: { observed: true, sequence: 10, ageMs: 120_020, listenerGeneration: 1, lifecycle: "ready", debugPause },
  }));
  const session = await makeRegistrySession(health.endpoint, { sessionId, editorSessionId: sessionId });
  await writeRegistry(dataDir, makeRegistry(identity, [session]));
  return {
    dataDir,
    instanceId: identity.instanceId,
    requests: () => health.requests(),
    async close() {
      await health.close();
      await rm(dataDir, { recursive: true, force: true });
      await rm(projectPath, { recursive: true, force: true });
    },
  };
}

const LIVE_PAUSE: MainThreadDebugPause = { pauseId: "pause-41", threadId: 1, publishedSequence: 41, timeoutKind: "bounded", remainingMs: 119_998 };

/** 真实 post-dispatch 失败形状：supervisor 的 E_UNITY_TRANSITION_TIMEOUT 重绑失败。 */
test("the live post-dispatch rebind failure becomes the typed partial outcome from the real health publication", async () => {
  const fixture = await livePauseFixture({ ...LIVE_PAUSE, mainThread: true });
  try {
    // 即时观察 selector 不带暂停证据：证据只能来自当前 live health publication。
    const observation = await selectObservationInstanceForFlags({ instance: fixture.instanceId }, fixture.dataDir, DISPATCH_PAUSE_DIAGNOSIS_BUDGET_MS);
    assert.equal(observation.instance.mainThreadDebugPause, undefined);
    assert.ok(observation.instance.activeSession);
    const enriched = await enrichTargetFailureWithObservedBlocker(
      dispatchedTimeoutFailure(),
      { instance: fixture.instanceId },
      { dataDir: fixture.dataDir },
      Date.now(),
      unprovenObserver({ count: 0 }),
    );
    assert.equal(enriched.ok, false);
    if (enriched.ok) throw new Error("unreachable");
    assert.equal(enriched.error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
    assert.equal(enriched.error.transient, false);
    assert.equal(enriched.error.retryAfterMs, 0);
    assert.match(enriched.error.message, /may have started/);
    assert.match(enriched.error.message, new RegExp(LIVE_PAUSE.pauseId));
    const details = enriched.error.details as Record<string, unknown>;
    assert.equal(details.outcome, "may-have-started");
    assert.equal(details.dispatch, "may-have-started");
    assert.equal(details.replay, "new-decision-required");
    assert.equal(details.pauseId, LIVE_PAUSE.pauseId);
    assert.equal(details.threadId, LIVE_PAUSE.threadId);
    assert.equal(details.mainThread, true);
    assert.equal(details.timeoutKind, "bounded");
    assert.equal(details.remainingMs, LIVE_PAUSE.remainingMs);
    assert.equal(details.pauseFacts, "read");
    assert.equal(details.pauseTool, "debug.pause.list");
    assert.equal(details.resumeTool, "debug.pause.resume");
    assert.match(String(details.reason), /Post-dispatch listener rebind failed with E_UNITY_TRANSITION_TIMEOUT/);
    const actions = enriched.error.nextActions.join(" ");
    assert.ok(actions.includes("debug.pause.list") && actions.includes(`debug.pause.resume with pauseId ${LIVE_PAUSE.pauseId}`), actions);
  } finally {
    await fixture.close();
  }
});

test("a publication without boundary facts still yields the typed outcome and says the facts were not read", async () => {
  const fixture = await livePauseFixture({ pauseId: LIVE_PAUSE.pauseId, threadId: LIVE_PAUSE.threadId, publishedSequence: LIVE_PAUSE.publishedSequence, mainThread: true });
  try {
    const enriched = await enrichTargetFailureWithObservedBlocker(
      dispatchedTimeoutFailure(),
      { instance: fixture.instanceId },
      { dataDir: fixture.dataDir },
      Date.now(),
      unprovenObserver({ count: 0 }),
    );
    assert.equal(enriched.ok, false);
    if (enriched.ok) throw new Error("unreachable");
    assert.equal(enriched.error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
    const details = enriched.error.details as Record<string, unknown>;
    assert.equal(details.outcome, "may-have-started");
    assert.equal(details.pauseId, LIVE_PAUSE.pauseId);
    assert.equal(details.pauseFacts, "not-read-in-time");
    assert.match(enriched.error.message, /were not read from the current publication/);
    // 边界事实读不到时绝不回填。
    assert.equal("timeoutKind" in details, false);
    assert.equal("remainingMs" in details, false);
  } finally {
    await fixture.close();
  }
});

test("a publication without any pause keeps the unknown outcome on the real path", async () => {
  const fixture = await livePauseFixture(null);
  try {
    const enriched = await enrichTargetFailureWithObservedBlocker(
      dispatchedTimeoutFailure(),
      { instance: fixture.instanceId },
      { dataDir: fixture.dataDir },
      Date.now(),
      unprovenObserver({ count: 0 }),
    );
    assert.equal(enriched.ok, false);
    if (enriched.ok) throw new Error("unreachable");
    assert.equal(enriched.error.code, "E_EXECUTION_TIMEOUT");
    assert.equal((enriched.error.details as Record<string, unknown>).outcome, "unknown");
    // 归因确实读了一次当前 publication，只是它没有暂停证据。
    assert.ok(fixture.requests() > 0);
  } finally {
    await fixture.close();
  }
});

/**
 * 真实 `debug.code.execute` 调用路径夹具。
 *
 * 它走公开 CLI 入口，命中本地 definition + dispatcher 自己的 deadline race；业务 POST 由注入 transport 承载且永远不答复
 * （暂停中的主线程不可能回答它），因此失败信封是真实产生的，而不是合成信封。
 */
async function runCodeExecuteAgainstLivePublication(debugPause: Record<string, unknown> | null): Promise<{ result: Awaited<ReturnType<typeof execute>>; requests: number }> {
  const fixture = await livePauseFixture(debugPause);
  try {
    const result = await execute([
      "tools", "run", "debug.code.execute",
      "--instance", fixture.instanceId,
      "--timeout-ms", "600",
      "--input", JSON.stringify({ code: "return null;" }),
    ], {
      dataDir: fixture.dataDir,
      bridgeExecute: () => new Promise<never>(() => undefined),
    } as never);
    return { result, requests: fixture.requests() };
  } finally {
    await fixture.close();
  }
}

test("the real debug.code.execute caller-side timeout becomes the typed partial outcome from the live publication", async () => {
  const { result } = await runCodeExecuteAgainstLivePublication({ ...LIVE_PAUSE, mainThread: true });
  assert.equal(result.ok, false);
  if (result.ok) throw new Error("unreachable");
  assert.equal(result.error.code, "E_DEBUG_MAIN_THREAD_PAUSED");
  assert.equal(result.error.transient, false);
  assert.equal(result.error.retryAfterMs, 0);
  assert.match(result.error.message, /may have started/);
  assert.match(result.error.message, new RegExp(LIVE_PAUSE.pauseId));
  const details = result.error.details as Record<string, unknown>;
  // 调用方自己的 deadline 结束时暂停仍在：dispatch 事实必须保留，且不得报成 unknown。
  assert.equal(details.outcome, "may-have-started");
  assert.equal(details.dispatch, "may-have-started");
  assert.equal(details.replay, "new-decision-required");
  assert.equal(details.pauseId, LIVE_PAUSE.pauseId);
  assert.equal(details.threadId, LIVE_PAUSE.threadId);
  assert.equal(details.mainThread, true);
  assert.equal(details.timeoutKind, "bounded");
  assert.equal(details.remainingMs, LIVE_PAUSE.remainingMs);
  assert.equal(details.pauseFacts, "read");
  assert.equal(details.pauseTool, "debug.pause.list");
  assert.equal(details.resumeTool, "debug.pause.resume");
  const actions = result.error.nextActions.join(" ");
  assert.ok(actions.indexOf("debug.pause.list") >= 0 && actions.indexOf("debug.pause.list") < actions.indexOf("debug.pause.resume"), actions);
  assert.match(actions, new RegExp(`debug.pause.resume with pauseId ${LIVE_PAUSE.pauseId}`));
  assert.match(actions, /never replayed automatically/);
});

test("the same real debug.code.execute timeout without a live pause keeps unknown while still declaring the real dispatch fact", async () => {
  const { result, requests } = await runCodeExecuteAgainstLivePublication(null);
  assert.equal(result.ok, false);
  if (result.ok) throw new Error("unreachable");
  assert.equal(result.error.code, "E_EXECUTION_TIMEOUT");
  const details = result.error.details as Record<string, unknown>;
  assert.equal(details.outcome, "unknown");
  // dispatcher 自己的 deadline race 只在 effect barrier 提交后声明该事实：这里证明 code.execute 已提交。
  assert.equal(details.dispatch, "may-have-started");
  assert.equal(details.replay, "new-decision-required");
  // 没有暂停证据时归因不伪造现场事实，也不改写原失败。
  assert.equal("pauseId" in details, false);
  assert.equal("pauseTool" in details, false);
  assert.ok(requests > 0);
});
