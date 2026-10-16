using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// 监听式触发、监听条件、跨执行点投递与中央调度观测的定向逻辑测试。
    ///
    /// 覆盖：条件比较、有限命中消费、触发预算上限与释放、player-loop-phase 拒绝、
    /// 真实 phase 到达上的监听式触发与投递事实、hook.list 自足、投递的单一赢家与过期记账、
    /// 每-tick 预算与观测字段、host signal 递归上限、listener 快照往返，
    /// 以及 program-owned binding 的所有权守卫。
    /// </summary>
    public sealed class DebugListenerTriggerTests
    {
        private sealed class ListenerProbe
        {
            public int PublicField = 7;
            public int Flags = 3;

            /// <summary>phase 到达现场没有实例，因此那里的条件与捕获只能声明静态字段。</summary>
            public static int StaticCounter = 4242;

            public void Fire(int amount, string name) { }

            /// <summary>phase 现场测试专用目标；与 Fire 分开，避免两个用例共享同一个 exact target。</summary>
            public void Pulse(int amount) { }
        }

        [SetUp]
        public void SetUp()
        {
            DebugScheduler.Reset();
            DebugTriggerBudget.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            DebugScheduler.Reset();
            DebugTriggerBudget.Reset();
            // 用例失败也不能把框架的 PlayerLoop 子系统留给后续用例。
            DebugPlayerLoopHost.ReleaseAll("test-teardown");
        }

        /// <summary>建立一个 open session，作为 listener 声明的计划注入 owner。</summary>
        private static string CreateSession()
        {
            var plan = DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
            {
                Label = "phase-listener-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                ClockKind = UnityCliDebugClocks.Monotonic
            });
            return DebugSessionHost.ApplyCreate(plan).SessionId;
        }

        /// <summary>关闭用例建立的 session；停用失败不掩盖用例断言。</summary>
        private static void StopSession(string sessionId)
        {
            try
            {
                if (!string.IsNullOrEmpty(sessionId)) DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
            }
            catch
            {
                // 清理失败不影响产品事实断言。
            }
            try
            {
                // 定义预算只在 remove 后归还：只 stop 会把定义留给同一个 Editor session 里的后续用例。
                if (!string.IsNullOrEmpty(sessionId)) DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
            }
            catch
            {
                // 定义已被删除，或仍有未完成清理时保持现状。
            }
        }

        /// <summary>EventStore 快照里匹配来源、种类、名称与可选 hook 身份的条目。</summary>
        private static List<Dictionary<string, object>> Events(string sourceId, string kind, string name, string hookId = null)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var entry in DebugEventStore.Snapshot())
            {
                object value;
                if (!entry.TryGetValue("sourceId", out value) || !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), sourceId, StringComparison.Ordinal)) continue;
                if (!entry.TryGetValue("kind", out value) || !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), kind, StringComparison.Ordinal)) continue;
                if (name != null && (!entry.TryGetValue("name", out value) || !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), name, StringComparison.Ordinal))) continue;
                if (hookId != null && (!entry.TryGetValue("hookId", out value) || !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), hookId, StringComparison.Ordinal))) continue;
                result.Add(entry);
            }
            return result;
        }

        private static Dictionary<string, object> ValueOf(Dictionary<string, object> entry)
        {
            object value;
            return entry.TryGetValue("value", out value) ? value as Dictionary<string, object> : null;
        }

        private static MethodHookTargetSpec Spec(string methodName, params string[] parameterTypes)
        {
            return new MethodHookTargetSpec
            {
                AssemblyName = typeof(ListenerProbe).Assembly.GetName().Name,
                TypeName = typeof(ListenerProbe).FullName,
                MethodName = methodName,
                ParameterTypes = parameterTypes.ToList(),
                ReturnType = "System.Void",
                IsStatic = false
            };
        }

        private static MethodHookListenerState Listener(long maxHits, int threadId, params MethodHookConditionSpec[] conditions)
        {
            return MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-1",
                Trigger = "method-hit",
                MaxHits = maxHits,
                ThreadId = threadId == 0 ? null : (int?)threadId,
                Conditions = conditions.ToList()
            });
        }

        [Test]
        public void ConditionsCompareProjectedArgumentAndFieldValues()
        {
            var instance = new ListenerProbe();
            var resolved = MethodHookTargetResolver.Resolve(Spec("Fire", "System.Int32", "System.String"));
            var listener = Listener(0L, 0,
                new MethodHookConditionSpec { Kind = "argument", Name = "0", Operator = "equals", Value = 5 },
                new MethodHookConditionSpec { Kind = "field", Name = "PublicField", Operator = "not-equals", Value = 8 });
            MethodHookHost.ResolveListenerFields(listener, resolved.Method);

            var arguments = new object[] { 5, "go" };
            Assert.That(listener.Matches(Thread.CurrentThread.ManagedThreadId, "static", arguments, instance, true), Is.True, "both conditions hold for this call");
            Assert.That(Interlocked.Read(ref listener.Filtered), Is.EqualTo(0L), "a matching call must not be counted as filtered");

            Assert.That(listener.Matches(Thread.CurrentThread.ManagedThreadId, "static", new object[] { 6, "go" }, instance, true), Is.False, "argument equality is enforced");
            // 参数条件成立、字段等于字面量（8 not-equals 8 为假）时，同一次调用必须被字段条件拒绝。
            instance.PublicField = 8;
            Assert.That(listener.Matches(Thread.CurrentThread.ManagedThreadId, "static", arguments, instance, true), Is.False, "the field condition is enforced by the same call");
            // 字段恢复后同一参数必须再次通过，证明比较读取的是命中时刻的实时字段值而不是发布时的快照。
            instance.PublicField = 7;
            Assert.That(listener.Matches(Thread.CurrentThread.ManagedThreadId, "static", arguments, instance, true), Is.True, "not-equals reads the live field value instead of a published snapshot");
            Assert.That(Interlocked.Read(ref listener.Filtered), Is.EqualTo(2L), "every rejected call is counted as filtered");
        }

        [Test]
        public void ThreadAndInstanceFiltersRejectEveryOtherHit()
        {
            var listener = Listener(0L, Thread.CurrentThread.ManagedThreadId);
            listener.InstanceIdentity = "ListenerProbe:probe-a";
            Assert.That(listener.Matches(Thread.CurrentThread.ManagedThreadId, "ListenerProbe:probe-a", new object[0], null, true), Is.True);
            Assert.That(listener.Matches(Thread.CurrentThread.ManagedThreadId, "ListenerProbe:probe-b", new object[0], null, true), Is.False, "instance identity must match exactly");
            Assert.That(listener.Matches(Thread.CurrentThread.ManagedThreadId + 1, "ListenerProbe:probe-a", new object[0], null, true), Is.False, "the declared hit thread is enforced");
            Assert.That(Interlocked.Read(ref listener.Filtered), Is.EqualTo(2L));
        }

        [Test]
        public void FiniteListenerStopsAfterItsDeclaredHits()
        {
            var oneShot = Listener(1L, 0);
            Assert.That(oneShot.TryCountHit(), Is.True, "one-shot consumes its single hit");
            Assert.That(oneShot.TryCountHit(), Is.False, "the second hit is refused");
            Assert.That(oneShot.Exhausted, Is.True);
            Assert.That(Interlocked.Read(ref oneShot.Hits), Is.EqualTo(1L), "consumed hits never exceed the budget");
            Assert.That(Interlocked.Read(ref oneShot.Filtered), Is.EqualTo(1L), "the refused hit is reported as filtered, not as a hit");

            var unbounded = Listener(0L, 0);
            for (var index = 0; index < 5; index++) Assert.That(unbounded.TryCountHit(), Is.True);
            Assert.That(unbounded.Exhausted, Is.False, "an omitted budget is unbounded");
        }

        [Test]
        public void PlayerLoopPhaseTriggerIsRefusedWithTheSharedSiteCode()
        {
            var refused = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-1",
                Trigger = "player-loop-phase",
                Phase = "before-update"
            }));
            Assert.That(refused.Code, Is.EqualTo("E_DEBUG_PROGRAM_SITE_UNAVAILABLE"), "Edit mode refuses a Player frame phase with the same code as program execution sites");
            Assert.That(refused.NextActions, Is.Not.Empty, "the refusal must carry executable next actions");
            Assert.That(DebugTriggerSources.All(), Does.Contain("player-loop-phase"), "the vocabulary still names the phase source");
            // 拒绝必须发生在安装之前：Edit 模式下不得留下任何 phase 子系统。
            Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(0), "a refused declaration installs no phase subsystem");
        }

        [Test]
        public void UnknownTriggerAndConditionShapesAreRefusedBeforeAnyEffect()
        {
            var trigger = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec { SessionId = "dbg-1", Trigger = "frame" }));
            Assert.That(trigger.Code, Is.EqualTo("E_SCHEMA_INVALID"));
            var kind = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-1",
                Conditions = new List<MethodHookConditionSpec> { new MethodHookConditionSpec { Kind = "property", Name = "X", Operator = "equals" } }
            }));
            Assert.That(kind.Code, Is.EqualTo("E_SCHEMA_INVALID"));
            var index = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-1",
                Conditions = new List<MethodHookConditionSpec> { new MethodHookConditionSpec { Kind = "argument", Name = "first", Operator = "equals" } }
            }));
            Assert.That(index.Code, Is.EqualTo("E_SCHEMA_INVALID"), "an argument condition must use a zero-based index");
        }

        [Test]
        public void TriggerBudgetIsBoundedPerSessionAndReleasedByOwner()
        {
            var used = 0;
            for (var index = 0; index < DebugLimits.MaxPlannedInjectionsPerSession; index++)
            {
                Assert.That(DebugTriggerBudget.TryPlan("dbg-1", DebugTriggerBudget.KindListener, "hook-plan:" + index.ToString(CultureInfo.InvariantCulture), out used), Is.Null);
            }
            Assert.That(used, Is.EqualTo(DebugLimits.MaxPlannedInjectionsPerSession));
            Assert.That(DebugTriggerBudget.TryPlan("dbg-1", DebugTriggerBudget.KindInput, "input-plan:1", out used), Is.EqualTo("limit-exceeded"), "listeners and inputs share one budget");
            Assert.That(DebugTriggerBudget.TryPlan("dbg-2", DebugTriggerBudget.KindListener, "hook-plan:other", out used), Is.Null, "the budget is per session");

            Assert.That(DebugTriggerBudget.Release("hook-plan:0"), Is.True);
            Assert.That(DebugTriggerBudget.TryPlan("dbg-1", DebugTriggerBudget.KindListener, "hook-plan:new", out used), Is.Null, "a released slot is reusable");
            Assert.That(DebugTriggerBudget.CountOfKind("dbg-1", DebugTriggerBudget.KindListener), Is.EqualTo(DebugLimits.MaxPlannedInjectionsPerSession));
            var report = DebugTriggerBudget.Describe("dbg-1");
            Assert.That(Convert.ToInt64(report["maxPlannedPerSession"], CultureInfo.InvariantCulture), Is.EqualTo((long)DebugLimits.MaxPlannedInjectionsPerSession));
            DebugTriggerBudget.Reset();
            Assert.That(DebugTriggerBudget.PlannedFor("dbg-1"), Is.EqualTo(0), "domain reset discards this Editor startup's plans");
        }

        [Test]
        public void DeliveryClaimHasExactlyOneWinnerAndRecordsItsFullFact()
        {
            var deliveryId = DebugScheduler.EnqueueDelivery("prog-1", "prun-1", "Listener", "sig-a", "{}", 7L, 5000d, "target-key-1", UnityCliDebugProgramExecutionSites.Signal);
            Assert.That(deliveryId, Is.Not.Null.And.Not.Empty);
            Assert.That(DebugScheduler.PendingDeliveryCount, Is.EqualTo(1));

            var first = DebugScheduler.TryClaimTargetDeliveries("target-key-1", 42, DebugLimits.MaxDeliveriesPerTick);
            Assert.That(first.Count, Is.EqualTo(1), "the target execution point takes the delivery exactly once");
            var claimed = first[0];
            Assert.That(claimed.Revision, Is.EqualTo(7L));
            Assert.That(claimed.TriggeredThreadId, Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
            Assert.That(claimed.ExecutionThreadId, Is.EqualTo(42), "the executing thread is the thread that reached the execution point");
            Assert.That(claimed.TriggeredAtMonotonicMs, Is.GreaterThan(0d));
            Assert.That(claimed.QueuedAtMonotonicMs, Is.GreaterThanOrEqualTo(claimed.TriggeredAtMonotonicMs));
            Assert.That(claimed.StartedAtMonotonicMs, Is.GreaterThanOrEqualTo(claimed.QueuedAtMonotonicMs));
            Assert.That(claimed.Expired, Is.False);

            Assert.That(DebugScheduler.TryClaimTargetDeliveries("target-key-1", 42, DebugLimits.MaxDeliveriesPerTick), Is.Empty, "a claimed delivery is never handed out twice");
            Assert.That(DebugScheduler.PendingDeliveryCount, Is.EqualTo(0));
            Assert.That(DebugScheduler.CompletedDeliveries, Is.EqualTo(1L));
            DebugScheduler.Reset();
        }

        [Test]
        public void TargetThatNeverArrivesExpiresWithoutExecuting()
        {
            // 未过期时：另一个 target 到达执行点既不能取得这条投递，也不能让它离开队列。
            DebugScheduler.EnqueueDelivery("prog-1", "prun-1", "Listener", "sig-b", null, 1L, 5000d, "target-key-2", UnityCliDebugProgramExecutionSites.Signal);
            Assert.That(DebugScheduler.TryClaimTargetDeliveries("target-key-3", 1, DebugLimits.MaxDeliveriesPerTick), Is.Empty, "an unrelated target must never take another target's delivery");
            Assert.That(DebugScheduler.PendingDeliveryCount, Is.EqualTo(1), "an unrelated target must not consume the pending delivery");
            Assert.That(DebugScheduler.CompletedDeliveries, Is.EqualTo(0L), "an unrelated target records no execution");
            Assert.That(DebugScheduler.ExpiredDeliveries, Is.EqualTo(0L), "the delivery is still inside its declared timeout");
            DebugScheduler.Reset();

            // 目标始终不到达时只允许过期：过期是终态，既不重放也不换线程。
            DebugScheduler.EnqueueDelivery("prog-1", "prun-1", "Listener", "sig-b", null, 1L, 0.5d, "target-key-2", UnityCliDebugProgramExecutionSites.Signal);
            Thread.Sleep(30);
            Assert.That(DebugScheduler.ExpirePendingDeliveries(), Is.EqualTo(1L), "the pending delivery expires once its declared timeout passed");
            Assert.That(DebugScheduler.PendingDeliveryCount, Is.EqualTo(0), "an expired delivery is closed, never left pending");
            Assert.That(DebugScheduler.CompletedDeliveries, Is.EqualTo(0L), "an expiry never records an execution");
            Assert.That(DebugScheduler.ExpiredDeliveries, Is.EqualTo(1L), "expiry is accounted, never replayed");
            Assert.That(DebugScheduler.ExpirePendingDeliveries(), Is.EqualTo(0L), "expiry is terminal and is never counted twice");
            Assert.That(DebugScheduler.TryClaimTargetDeliveries("target-key-2", 1, DebugLimits.MaxDeliveriesPerTick), Is.Empty, "an expired delivery is never executed later");
            Assert.That(DebugScheduler.ExpiredDeliveries, Is.EqualTo(1L), "a claim attempt must not re-count the closed delivery");
            DebugScheduler.Reset();
        }

        [Test]
        public void FrameworkPhaseDeliveriesHonorThePerTickBudget()
        {
            for (var index = 0; index < 3; index++)
                DebugScheduler.EnqueueDelivery("prog-1", "prun-1", "Listener", "sig-c" + index.ToString(CultureInfo.InvariantCulture), null, 1L, 5000d, null, UnityCliDebugProgramExecutionSites.EditorTick);
            Assert.That(DebugScheduler.ClaimFrameworkPhaseDeliveries(UnityCliDebugProgramExecutionSites.EditorTick, 7, 2).Count, Is.EqualTo(2), "one tick claims at most the declared per-tick budget");
            Assert.That(DebugScheduler.ClaimFrameworkPhaseDeliveries(UnityCliDebugProgramExecutionSites.EditorTick, 7, 2).Count, Is.EqualTo(1));
            Assert.That(DebugScheduler.ClaimFrameworkPhaseDeliveries(UnityCliDebugProgramExecutionSites.EditorTick, 7, 2), Is.Empty);
            var report = DebugScheduler.Describe();
            Assert.That(Convert.ToInt64(report["maxDeliveriesPerTick"], CultureInfo.InvariantCulture), Is.EqualTo((long)DebugLimits.MaxDeliveriesPerTick));
            Assert.That(Convert.ToInt64(report["frameworkDeliveriesClaimed"], CultureInfo.InvariantCulture), Is.EqualTo(3L));
            Assert.That(Convert.ToInt64(report["queueHighWaterMark"], CultureInfo.InvariantCulture), Is.EqualTo(3L), "the queue high-water mark is observable");
            Assert.That(Convert.ToInt64(report["maxDeliveries"], CultureInfo.InvariantCulture), Is.EqualTo((long)DebugLimits.MaxProgramDeliveries));
            DebugScheduler.Reset();
        }

        [Test]
        public void SchedulerExposesPerTickCostBacklogOverrunAndDispatchCounts()
        {
            DebugScheduler.EnqueueDelivery("prog-1", "prun-1", "Listener", "sig-d", null, 1L, 5000d, null, UnityCliDebugProgramExecutionSites.EditorTick);
            DebugScheduler.Tick();
            var report = DebugScheduler.Describe();
            foreach (var key in new[] { "tickCount", "lastTickMilliseconds", "maxTickMilliseconds", "meanTickMilliseconds", "overrunTicks", "overrunThresholdMs", "tickSamples", "maxTickSamples", "backlog", "pendingDeliveries", "dispatchHits", "dispatchFiltered", "dispatchSamples", "dispatchDrops", "signalRecursionRefusals", "maxSignalRecursionDepth" })
            {
                Assert.That(report.ContainsKey(key), Is.True, "the scheduler report must expose " + key);
            }
            Assert.That(Convert.ToInt64(report["tickCount"], CultureInfo.InvariantCulture), Is.GreaterThanOrEqualTo(1L));
            Assert.That(Convert.ToInt64(report["tickSamples"], CultureInfo.InvariantCulture), Is.LessThanOrEqualTo((long)DebugLimits.SchedulerTickSamples), "tick samples stay bounded");
            Assert.That(Convert.ToInt64(report["overrunThresholdMs"], CultureInfo.InvariantCulture), Is.EqualTo((long)DebugLimits.SchedulerTickOverrunMs));
            DebugScheduler.Reset();
        }

        [Test]
        public void SignalRecursionStopsAtTheDeclaredDepth()
        {
            for (var depth = 0; depth < DebugLimits.MaxSignalRecursionDepth; depth++)
                Assert.That(DebugScheduler.TryEnterSignalScope(), Is.True, "depth " + depth.ToString(CultureInfo.InvariantCulture) + " is inside the declared limit");
            Assert.That(DebugScheduler.TryEnterSignalScope(), Is.False, "the dispatch chain is refused at the declared recursion depth");
            Assert.That(DebugScheduler.SignalRecursionRefusals, Is.EqualTo(1L));
            for (var depth = 0; depth < DebugLimits.MaxSignalRecursionDepth; depth++) DebugScheduler.ExitSignalScope();
            Assert.That(DebugScheduler.TryEnterSignalScope(), Is.True, "leaving the chain releases the scope");
            DebugScheduler.ExitSignalScope();
            DebugScheduler.Reset();
        }

        [Test]
        public void ListenerConfigurationSurvivesSnapshotWithoutItsHitCounters()
        {
            var definition = new MethodHookDefinition
            {
                HookId = "hook-aaaa",
                Revision = 3L,
                TargetSpec = Spec("Fire", "System.Int32", "System.String"),
                TargetKey = "key",
                Listener = Listener(2L, 9, new MethodHookConditionSpec { Kind = "argument", Name = "1", Operator = "equals", Value = "go" })
            };
            definition.Listener.TryCountHit();
            var restored = MethodHookDefinition.FromData(definition.ToSnapshotData());
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored.Listener, Is.Not.Null, "the declared listener must be restored, never silently dropped");
            Assert.That(restored.Listener.SessionId, Is.EqualTo("dbg-1"));
            Assert.That(restored.Listener.Trigger, Is.EqualTo("method-hit"));
            Assert.That(restored.Listener.MaxHits, Is.EqualTo(2L));
            Assert.That(restored.Listener.ThreadId, Is.EqualTo(9));
            Assert.That(restored.Listener.Conditions.Length, Is.EqualTo(1));
            Assert.That(restored.Listener.Conditions[0].ArgumentIndex, Is.EqualTo(1));
            Assert.That(restored.Listener.Conditions[0].Operator, Is.EqualTo("equals"));
            Assert.That(Interlocked.Read(ref restored.Listener.Hits), Is.EqualTo(0L), "hit counters belong to one Editor startup and are not restored");
            Assert.That(restored.Owner, Is.Empty, "program ownership is never persisted across a domain reload");

            // 两个投影的分界：同一次 Editor 启动的快照带现场线程身份，可跨启动复用的声明投影绝不带。
            var declaration = definition.ToData();
            var declarationListener = declaration["listener"] as IDictionary<string, object>;
            Assert.That(declarationListener, Is.Not.Null, "the declaration projection must still carry the declared listener");
            Assert.That(declarationListener.ContainsKey("threadId"), Is.False, "one Editor startup's thread identity never enters a reusable declaration projection");
            Assert.That(declarationListener["sessionId"], Is.EqualTo("dbg-1"), "the declaration projection keeps the session reference that import re-establishes");
            Assert.That(declarationListener["maxHits"], Is.EqualTo(2L), "the declared hit budget is a declaration, not live state");
        }

        [Test]
        public void ProgramOwnedBindingRefusesDirectHookMutation()
        {
            MethodHookHost.InitializeAfterReload();
            var plan = MethodHookHost.PrepareInstall(new MethodHookInstallRequest
            {
                Label = "listener-owner-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Target = Spec("Fire", "System.Int32", "System.String"),
                Activation = "editor-and-play",
                Capture = new MethodHookCaptureSpec { Arguments = true }
            }, "program:prog-1");
            var installed = MethodHookHost.ApplyInstall(plan, null);
            try
            {
                var remove = new MethodHookRemoveRequest
                {
                    HookId = installed.HookId,
                    EditorSessionId = installed.EditorSessionId,
                    ExpectedRevision = installed.Revision
                };
                var refused = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareRemove(remove));
                Assert.That(refused.Code, Is.EqualTo("E_HOOK_OWNED_BY_PROGRAM"), "a direct debug.hook.* call must not change a program-owned binding");
                Assert.That(refused.NextActions.Any(action => action.Contains("debug.program.replace")), Is.True, "the refusal must point at the owning program Tools");
                Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareDisable(new MethodHookDisableRequest
                {
                    HookId = installed.HookId,
                    EditorSessionId = installed.EditorSessionId,
                    ExpectedRevision = installed.Revision
                }));
                // 只有拥有它的 program 自己可以解除该 binding。
                Assert.That(MethodHookHost.PrepareRemove(remove, "program:prog-1"), Is.Not.Null);
            }
            finally
            {
                // 释放本次测试建立的绑定；既使上面的断言失败也不把 patch 留在测试目标上。
                try
                {
                    var cleanup = MethodHookHost.PrepareRemove(new MethodHookRemoveRequest { HookId = installed.HookId, EditorSessionId = installed.EditorSessionId, ExpectedRevision = installed.Revision }, "program:prog-1");
                    MethodHookHost.ApplyRemove(cleanup);
                }
                catch (Exception exception)
                {
                    Assert.Ignore("the probe binding could not be released in this environment: " + exception.GetType().Name);
                }
            }
            Assert.That(MethodHookHost.IsTargetBound(MethodHookTargetKey.From(MethodHookTargetResolver.Resolve(Spec("Fire", "System.Int32", "System.String")).Method).Text), Is.False, "the test must not leave a bound target behind");
        }

        [Test]
        public void PhaseTriggerRefusesFiltersItCouldNeverEvaluateBeforeAnySiteIsInstalled()
        {
            var argument = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-1",
                Trigger = DebugTriggerSources.PlayerLoopPhase,
                Phase = "after-update",
                Conditions = new List<MethodHookConditionSpec> { new MethodHookConditionSpec { Kind = "argument", Name = "0", Operator = "equals", Value = 1 } }
            }));
            Assert.That(argument.Code, Is.EqualTo("E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION"), "an argument condition can never be evaluated at a phase arrival");
            Assert.That(argument.NextActions, Is.Not.Empty, "the refusal must carry executable next actions");
            Assert.That(argument.NextActions.Any(action => action.Contains("method-hit")), Is.True, "the recovery path names the trigger that can evaluate an argument");

            var instance = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-1",
                Trigger = DebugTriggerSources.PlayerLoopPhase,
                Phase = "after-update",
                InstanceIdentity = "ListenerProbe:probe-a"
            }));
            Assert.That(instance.Code, Is.EqualTo("E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION"), "an instance identity filter can never match at a phase arrival");

            // 拒绝发生在安装之前：既没有 phase 子系统，也没有计划注入预算占用。
            Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(0), "a refused declaration installs no phase subsystem");
            Assert.That(DebugTriggerBudget.PlannedFor("dbg-1"), Is.EqualTo(0), "a refused declaration reserves no planned injection");

            // 合法声明仍得到同一条 Edit 模式不可用拒绝：新增校验不得覆盖执行域事实。
            var unavailable = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-1",
                Trigger = DebugTriggerSources.PlayerLoopPhase,
                Phase = "after-update"
            }));
            Assert.That(unavailable.Code, Is.EqualTo("E_DEBUG_PROGRAM_SITE_UNAVAILABLE"), "a declarable phase is still refused for the Edit-mode domain, not for its filters");
        }

        /// <summary>
        /// 构造一个声明 player-loop-phase 的 install plan。公开入口只在 Editor Play 下接受该触发源，
        /// 而 EditMode 用例必须能覆盖真实 phase 到达路径，因此这里用生产内部类型构造同一 plan。
        /// </summary>
        private static MethodHookInstallPlan PhaseInstallPlan(string sessionId, string phase, long maxHits, out MethodHookListenerState listener)
        {
            var resolved = MethodHookTargetResolver.Resolve(Spec("Pulse", "System.Int32"));
            listener = new MethodHookListenerState
            {
                SessionId = sessionId,
                Trigger = DebugTriggerSources.PlayerLoopPhase,
                Phase = phase,
                MaxHits = maxHits,
                Conditions = new[]
                {
                    new MethodHookCondition
                    {
                        Kind = MethodHookConditionKinds.Field,
                        Name = "StaticCounter",
                        Operator = MethodHookConditionOperators.Equals,
                        DeclaringType = typeof(ListenerProbe).FullName,
                        LiteralCanonical = UnityCliSerializer.Canonicalize(ListenerProbe.StaticCounter)
                    }
                }
            };
            MethodHookHost.ResolveListenerFields(listener, resolved.Method);
            var planId = "hook-plan:" + Guid.NewGuid().ToString("N").Substring(0, 12);
            int used;
            Assert.That(DebugTriggerBudget.TryPlan(sessionId, DebugTriggerBudget.KindListener, planId, out used), Is.Null, "the plan must reserve its planned injection before the effect");
            return new MethodHookInstallPlan
            {
                Resolved = resolved,
                Capture = new MethodHookCapture
                {
                    Arguments = true,
                    StaticFields = new List<MethodHookCaptureField> { new MethodHookCaptureField { DeclaringType = typeof(ListenerProbe).FullName, FieldName = "StaticCounter" } }
                },
                Activation = "editor-and-play",
                EmitHitEvents = true,
                Label = "phase-listener-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Listener = listener,
                TriggerPlanId = planId
            };
        }

        [Test]
        public void ASatisfiedPhaseListenerFiresOnItsOwnPhaseWithFullDeliveryFacts()
        {
            MethodHookHost.InitializeAfterReload();
            var sessionId = CreateSession();
            MethodHookListenerState listener;
            var plan = PhaseInstallPlan(sessionId, "before-update", 1L, out listener);
            var installed = MethodHookHost.ApplyInstall(plan, null);
            try
            {
                Assert.That(DebugPlayerLoopHost.InstalledPhases(), Does.Contain("before-update"), "declaring the trigger installs that framework-owned phase site");
                var completedBaseline = DebugScheduler.CompletedDeliveries;

                // 另一个 phase 既不得求值该 listener，也不得取得投递。
                DebugScheduler.AdvancePhase("after-update");
                Assert.That(Events(UnityCliDebugSources.Trigger, DebugTriggerSources.PlayerLoopPhase, installed.HookId), Is.Empty, "a different phase never fires this listener");
                Assert.That(Interlocked.Read(ref listener.Hits), Is.EqualTo(0L), "no hit is consumed outside the declared phase");
                Assert.That(DebugScheduler.CompletedDeliveries, Is.EqualTo(completedBaseline), "no delivery is executed outside the declared phase");

                // 真实 phase 到达：同一执行点段内投递并执行。
                DebugScheduler.AdvancePhase("before-update");
                var triggers = Events(UnityCliDebugSources.Trigger, DebugTriggerSources.PlayerLoopPhase, installed.HookId);
                Assert.That(triggers.Count, Is.EqualTo(1), "a satisfied listener triggers exactly once at its own phase");
                Assert.That(Interlocked.Read(ref listener.Hits), Is.EqualTo(1L), "the trigger consumes one declared hit");
                Assert.That(DebugScheduler.PendingDeliveryCount, Is.EqualTo(0), "the same phase segment claims the delivery it queued");
                Assert.That(DebugScheduler.CompletedDeliveries, Is.EqualTo(completedBaseline + 1L), "exactly this trigger's delivery executed, never left pending or replayed");

                var trigger = triggers[0];
                Assert.That(Convert.ToString(trigger["kind"], CultureInfo.InvariantCulture), Is.EqualTo(DebugTriggerSources.PlayerLoopPhase));
                Assert.That(Convert.ToString(trigger["executionPoint"], CultureInfo.InvariantCulture), Is.EqualTo("trigger:player-loop-phase:before-update"), "the event must name the real phase it came from");
                Assert.That(Convert.ToString(trigger["hookId"], CultureInfo.InvariantCulture), Is.EqualTo(installed.HookId));
                Assert.That(Convert.ToInt64(trigger["revision"], CultureInfo.InvariantCulture), Is.EqualTo(installed.Revision));
                var value = ValueOf(trigger);
                Assert.That(value, Is.Not.Null, "the trigger fact must carry the delivery facts");
                var triggeredAt = Convert.ToDouble(value["triggeredAtMonotonicMs"], CultureInfo.InvariantCulture);
                var queuedAt = Convert.ToDouble(value["queuedAtMonotonicMs"], CultureInfo.InvariantCulture);
                var startedAt = Convert.ToDouble(value["startedAtMonotonicMs"], CultureInfo.InvariantCulture);
                Assert.That(triggeredAt, Is.GreaterThan(0d));
                Assert.That(queuedAt, Is.GreaterThanOrEqualTo(triggeredAt));
                Assert.That(startedAt, Is.GreaterThanOrEqualTo(queuedAt), "the delivery records all three edges in order");
                Assert.That(Convert.ToInt64(value["triggerThreadId"], CultureInfo.InvariantCulture), Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
                Assert.That(Convert.ToInt64(value["executionThreadId"], CultureInfo.InvariantCulture), Is.EqualTo(Thread.CurrentThread.ManagedThreadId), "the phase executes on the thread that reached it");
                Assert.That(Convert.ToBoolean(value["expired"], CultureInfo.InvariantCulture), Is.False);
                Assert.That(Convert.ToString(value["detail"], CultureInfo.InvariantCulture), Is.EqualTo("player-loop-phase:before-update"));
                var fields = value["fields"] as Dictionary<string, object>;
                Assert.That(fields, Is.Not.Null, "declared static fields are captured at the phase");
                Assert.That(Convert.ToInt32(fields[typeof(ListenerProbe).FullName + ".StaticCounter"], CultureInfo.InvariantCulture), Is.EqualTo(ListenerProbe.StaticCounter));

                // 需要方法实例或参数的动作留下稳定覆盖缺口，绝不静默跳过。
                var gaps = Events(UnityCliDebugSources.Hook, "coverage", MethodHookHost.PhaseActionCoverageName, installed.HookId);
                Assert.That(gaps.Count, Is.EqualTo(1), "the declared capture that needs a call is reported once per satisfied trigger");
                var declared = ValueOf(gaps[0])["declared"] as List<object>;
                Assert.That(declared, Is.Not.Null);
                Assert.That(declared.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)), Does.Contain("capture.arguments"));

                // 有限命中用尽后现场与计划预算立即归还：不会留下一个永远不会触发的声明。
                Assert.That(listener.Exhausted, Is.True);
                Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(0), "an exhausted listener releases its own phase site");
                Assert.That(DebugTriggerBudget.PlannedFor(sessionId), Is.EqualTo(0), "an exhausted listener releases its planned injection");
                DebugScheduler.AdvancePhase("before-update");
                Assert.That(Events(UnityCliDebugSources.Trigger, DebugTriggerSources.PlayerLoopPhase, installed.HookId).Count, Is.EqualTo(1), "an exhausted listener never fires again");
            }
            finally
            {
                MethodHookHost.ApplyRemove(MethodHookHost.PrepareRemove(new MethodHookRemoveRequest
                {
                    HookId = installed.HookId,
                    EditorSessionId = installed.EditorSessionId,
                    ExpectedRevision = installed.Revision
                }));
                StopSession(sessionId);
            }
        }

        [Test]
        public void ListReportsTheListenerTriggerPhaseAndTheIdentifiersRemovalRequires()
        {
            MethodHookHost.InitializeAfterReload();
            var sessionId = CreateSession();
            MethodHookListenerState listener;
            var plan = PhaseInstallPlan(sessionId, "after-update", 4L, out listener);
            var label = plan.Label;
            var installed = MethodHookHost.ApplyInstall(plan, null);
            try
            {
                var page = MethodHookHost.List(new MethodHookListRequest { Label = label });
                Assert.That(page.Items.Count, Is.EqualTo(1));
                var item = page.Items[0];
                Assert.That(item.Listener, Is.Not.Null, "a listed definition must report its declared listener");
                Assert.That(item.Listener.Trigger, Is.EqualTo(DebugTriggerSources.PlayerLoopPhase), "the list must name the trigger source, never report it empty");
                Assert.That(item.Listener.Phase, Is.EqualTo("after-update"), "the list must name the declared phase");
                Assert.That(item.Listener.MaxHits, Is.EqualTo(4L));
                Assert.That(item.EditorSessionId, Is.EqualTo(installed.EditorSessionId), "the list must report the session that the mutation calls require");
                Assert.That(item.Revision, Is.EqualTo(installed.Revision), "the listed revision is the expectedRevision those calls compare against");

                // 只持有列表输出的客户端必须能直接完成清理：同一 identity 与 revision 被接受。
                var removal = MethodHookHost.PrepareRemove(new MethodHookRemoveRequest
                {
                    HookId = item.HookId,
                    EditorSessionId = item.EditorSessionId,
                    ExpectedRevision = item.Revision
                });
                Assert.That(removal, Is.Not.Null);
                MethodHookHost.ApplyRemove(removal);
                Assert.That(MethodHookHost.List(new MethodHookListRequest { Label = label }).Total, Is.EqualTo(0));
                Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(0), "removing the definition releases its own phase site");
                Assert.That(DebugTriggerBudget.PlannedFor(sessionId), Is.EqualTo(0), "removing the definition releases its planned injection");
            }
            finally
            {
                try
                {
                    var remaining = MethodHookHost.List(new MethodHookListRequest { Label = label });
                    if (remaining.Total > 0)
                    {
                        var definition = remaining.Items[0];
                        MethodHookHost.ApplyRemove(MethodHookHost.PrepareRemove(new MethodHookRemoveRequest
                        {
                            HookId = definition.HookId,
                            EditorSessionId = definition.EditorSessionId,
                            ExpectedRevision = definition.Revision
                        }));
                    }
                }
                catch
                {
                    // 清理失败不掩盖断言结果。
                }
                StopSession(sessionId);
            }
        }
    }
}
