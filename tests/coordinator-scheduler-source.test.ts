import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

test("Bridge main-thread scheduler advances every durable coordinator before queue early return", async () => {
  const [bridgeSource, waitSource] = await Promise.all([
    readFile(join(process.cwd(), "unitypkg", "Editor", "Bridge", "BridgeWorkQueue.cs"), "utf8"),
    readFile(join(process.cwd(), "unitypkg", "Editor", "Infrastructure", "UnityCliWaiter.cs"), "utf8"),
  ]);
  const bridgeScheduler = bridgeSource.slice(bridgeSource.indexOf("private static void ProcessQueuedWork()"), bridgeSource.indexOf("private static async Task ExecuteWorkItemAsync"));
  const durableScheduler = waitSource.slice(waitSource.indexOf("public static void Tick()"), waitSource.indexOf("private static void Register"));
  const ticks = [
    'TickCoordinator("compilation-events", UnityCliCompilationEventBroker.Tick);',
    'TickCoordinator("editor.compile", UnityCliBridge.UnityCliCompileCoordinator.Tick);',
    'TickCoordinator("editor.refresh", UnityCliBridge.UnityCliLiveRefreshCoordinator.Tick);',
    'TickCoordinator("package.resolve", UnityCliBridge.UnityCliPackageResolveCoordinator.Tick);',
    'TickCoordinator("test.run", UnityCliBridge.UnityCliTestRunCoordinator.Tick);',
  ];
  let previous = -1;
  for (const tick of ticks) {
    const index = durableScheduler.indexOf(tick);
    assert.ok(index > previous, `${tick} must be called once in scheduler order`);
    assert.equal(durableScheduler.indexOf(tick, index + 1), -1, `${tick} must not have a duplicate scheduler owner`);
    assert.equal(bridgeScheduler.includes(tick), false, `${tick} must not be called again by BridgeWorkQueue`);
    previous = index;
  }
  assert.match(waitSource, /private static void TickCoordinator\(string owner, Action tick\)[\s\S]*catch \(Exception exception\)[\s\S]*CoordinatorFailures/);
  const schedulerCall = bridgeScheduler.indexOf("UnityCliWaitScheduler.Tick();");
  const emptyQueueReturn = bridgeScheduler.indexOf("if (WorkQueue.Count == 0) return;");
  assert.ok(schedulerCall >= 0 && emptyQueueReturn > schedulerCall, "durable roots must progress even with no queued HTTP work");
  // Continuation 由显式队列驱动，不能覆盖 Unity 主线程原生 SynchronizationContext。
  assert.doesNotMatch(waitSource, /SetSynchronizationContext|:\s*SynchronizationContext/);
  assert.doesNotMatch(bridgeSource, /EnsureMainThreadSynchronizationContext/);
  assert.match(waitSource, /class UnityCliMainThreadContinuationQueue[\s\S]*Queue<Action>[\s\S]*MainThreadContinuations\.Drain\(\)/);
});
