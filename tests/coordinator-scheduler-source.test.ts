import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

test("Bridge main-thread scheduler advances every durable coordinator before queue selection", async () => {
  const [bridgeSource, waitSource] = await Promise.all([
    readFile(join(process.cwd(), "unitypkg", "Editor", "Bridge", "BridgeWorkQueue.cs"), "utf8"),
    readFile(join(process.cwd(), "unitypkg", "Editor", "Infrastructure", "UnityCliWaiter.cs"), "utf8"),
  ]);
  const bridgeStart = bridgeSource.indexOf("private static void ProcessQueuedWork()");
  assert.ok(bridgeStart >= 0, "missing Bridge queue scheduler anchor");
  const bridgeEnd = bridgeSource.indexOf("private static async Task ExecuteWorkItemAsync", bridgeStart);
  assert.ok(bridgeEnd > bridgeStart, "missing Bridge queue scheduler boundary");
  const schedulerStart = waitSource.indexOf("public static void Tick()");
  assert.ok(schedulerStart >= 0, "missing durable scheduler anchor");
  const schedulerEnd = waitSource.indexOf("private static void Register", schedulerStart);
  assert.ok(schedulerEnd > schedulerStart, "missing durable scheduler boundary");
  const bridgeScheduler = bridgeSource.slice(bridgeStart, bridgeEnd);
  const durableScheduler = waitSource.slice(schedulerStart, schedulerEnd);
  const ticks = [
    'TickCoordinator("compilation-events", UnityCliCompilationEventBroker.Tick);',
    'TickCoordinator("editor.compile", UnityCliBridge.UnityCliCompileCoordinator.Tick);',
    'TickCoordinator("build.run", UnityCliBridge.UnityCliLiveBuildCoordinator.Tick);',
    'TickCoordinator("editor.refresh", UnityCliBridge.UnityCliLiveRefreshCoordinator.Tick);',
    'TickCoordinator("asset.refresh", UnityCliBridge.UnityCliAssetRefreshCoordinator.Tick);',
    'TickCoordinator("package.embed", UnityCliBridge.UnityCliPackageEmbedCoordinator.Tick);',
    'TickCoordinator("package.resolve", UnityCliBridge.UnityCliPackageResolveCoordinator.Tick);',
    'TickCoordinator("test.run", UnityCliBridge.UnityCliTestRunCoordinator.Tick);',
    'TickCoordinator("navmesh.bake", UnityCliBridge.UnityCliNavMeshBakeCoordinator.Tick);',
    'TickCoordinator("occlusion.bake", UnityCliBridge.UnityCliOcclusionBakeCoordinator.Tick);',
  ];
  const productionCoordinatorTicks = [...durableScheduler.matchAll(/TickCoordinator\("[^"]+", [^;]+\);/g)]
    .map((match) => match[0])
    .filter((tick) => !tick.startsWith('TickCoordinator("editor.autotick",'));
  assert.deepEqual(productionCoordinatorTicks, ticks, "the assertion list must equal every production durable coordinator tick");
  let previous = -1;
  for (const tick of ticks) {
    const index = durableScheduler.indexOf(tick);
    assert.ok(index >= 0, `${tick} must have a production scheduler anchor`);
    assert.ok(index > previous, `${tick} must be called once in scheduler order`);
    assert.equal(durableScheduler.indexOf(tick, index + 1), -1, `${tick} must not have a duplicate scheduler owner`);
    assert.equal(bridgeScheduler.includes(tick), false, `${tick} must not be called again by BridgeWorkQueue`);
    previous = index;
  }
  assert.match(waitSource, /private static void TickCoordinator\(string owner, Action tick\)[\s\S]*catch \(Exception exception\)[\s\S]*CoordinatorFailures/);
  const schedulerCall = bridgeScheduler.indexOf("UnityCliWaitScheduler.Tick();");
  assert.ok(schedulerCall >= 0, "missing durable scheduler call");
  const queueSelection = bridgeScheduler.indexOf("SelectQueuedStarts(state", schedulerCall);
  assert.ok(queueSelection > schedulerCall, "durable roots must progress before queued HTTP work is selected");
  // Continuation 由显式队列驱动，不能覆盖 Unity 主线程原生 SynchronizationContext。
  assert.doesNotMatch(waitSource, /SetSynchronizationContext|:\s*SynchronizationContext/);
  assert.doesNotMatch(bridgeSource, /EnsureMainThreadSynchronizationContext/);
  assert.match(waitSource, /class UnityCliMainThreadContinuationQueue[\s\S]*Queue<Action>[\s\S]*MainThreadContinuations\.Drain\(\)/);
});
