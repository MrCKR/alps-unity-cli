#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.LowLevel;

namespace UnityCli.Tests
{
    /// <summary>
    /// Editor Play 的 PlayerLoop phase 现场定向测试。
    ///
    /// 覆盖：phase 集合封闭与插入位置解析、安装幂等与真实子系统计数（跨反复 Play 段不叠加）、
    /// 只移除本框架自己的子系统、phase 投递只由自己的执行点取得且单赢家、
    /// driver 只在自己声明的 phase 上推进、Edit 模式拒绝码稳定、schema enum 与 owner 集合一致。
    /// </summary>
    public sealed class DebugPlayerLoopPhaseTests
    {
        private static readonly string[] ExpectedMarkers =
        {
            "BeforeUpdateSubsystem", "AfterUpdateSubsystem", "FixedUpdateSubsystem", "LateUpdateSubsystem"
        };

        [TearDown]
        public void RemovePhaseSites()
        {
            DebugPlayerLoopHost.ReleaseAll("test-teardown");
            DebugScheduler.Reset();
        }

        /// <summary>直接数真实 PlayerLoop 里的标记子系统，证明反复安装不会叠加重复条目。</summary>
        private static int SubsystemCount(string markerName)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var subsystems = loop.subSystemList ?? new PlayerLoopSystem[0];
            var full = typeof(DebugPlayerLoopHost).FullName + "+" + markerName;
            return subsystems.Count(subsystem => subsystem.type != null && string.Equals(subsystem.type.FullName, full, StringComparison.Ordinal));
        }

        /// <summary>叠加计数：本框架在顶层循环里当前一共留下几个标记子系统，用于证明没有孤儿残留。</summary>
        private static int FrameworkSubsystemCount()
        {
            return ExpectedMarkers.Sum(SubsystemCount);
        }

        /// <summary>一个 owner 只声明一个 phase：同一 Play 段里每个声明的 phase 都有自己的 owner。</summary>
        private static string SegmentOwner(int playSegment, string phase)
        {
            return "segment-owner-" + playSegment.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + phase;
        }

        private static string MarkerOf(string phase)
        {
            if (string.Equals(phase, DebugPlayerLoopPhases.BeforeUpdate, StringComparison.Ordinal)) return ExpectedMarkers[0];
            if (string.Equals(phase, DebugPlayerLoopPhases.AfterUpdate, StringComparison.Ordinal)) return ExpectedMarkers[1];
            if (string.Equals(phase, DebugPlayerLoopPhases.FixedUpdate, StringComparison.Ordinal)) return ExpectedMarkers[2];
            return ExpectedMarkers[3];
        }

        private static long LongOf(Dictionary<string, object> report, string key)
        {
            return Convert.ToInt64(report[key], System.Globalization.CultureInfo.InvariantCulture);
        }

        [Test]
        public void ThePhaseSetIsClosedAndEveryPlacementResolves()
        {
            Assert.That(DebugPlayerLoopPhases.All(), Is.EqualTo(new[] { "before-update", "after-update", "fixed-update", "late-update" }), "the declarable phase set is closed and stable");
            Assert.That(DebugPlayerLoopPhases.IsDefined("editor-tick"), Is.False, "the editor tick is not a PlayerLoop phase");
            Assert.That(DebugPlayerLoopPhases.IsDefined("frame"), Is.False);

            var roots = new List<string> { "Initialization", "EarlyUpdate", "FixedUpdate", "PreUpdate", "Update", "PreLateUpdate", "PostLateUpdate" };
            int index;
            Assert.That(DebugPlayerLoopLayout.TryResolve("before-update", roots, out index), Is.True);
            Assert.That(index, Is.EqualTo(4), "before-update inserts before the Update phase");
            Assert.That(DebugPlayerLoopLayout.TryResolve("after-update", roots, out index), Is.True);
            Assert.That(index, Is.EqualTo(5), "after-update inserts after the Update phase");
            Assert.That(DebugPlayerLoopLayout.TryResolve("fixed-update", roots, out index), Is.True);
            Assert.That(index, Is.EqualTo(3), "fixed-update inserts after the FixedUpdate phase");
            Assert.That(DebugPlayerLoopLayout.TryResolve("late-update", roots, out index), Is.True);
            Assert.That(index, Is.EqualTo(6), "late-update inserts after the LateUpdate scripts and before PostLateUpdate");

            Assert.That(DebugPlayerLoopLayout.TryResolve("frame", roots, out index), Is.False, "an undeclared phase never resolves to a position");
            Assert.That(DebugPlayerLoopLayout.TryResolve("before-update", new List<string> { "Initialization" }, out index), Is.False, "a missing anchor must be reported instead of a guessed position");
        }

        [Test]
        public void InstallIsIdempotentAndOnlyRemovesItsOwnSubsystem()
        {
            Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(0), "no phase site is installed before the first declaration");
            foreach (var marker in ExpectedMarkers) Assert.That(SubsystemCount(marker), Is.EqualTo(0), "the Editor PlayerLoop must start without framework phase subsystems");

            var first = DebugPlayerLoopHost.Install("before-update", "owner-a");
            Assert.That(first, Is.Null, "the first declaration installs the phase site");
            Assert.That(SubsystemCount(MarkerOf("before-update")), Is.EqualTo(1), "exactly one named subsystem is inserted for that phase");
            Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(1));

            Assert.That(DebugPlayerLoopHost.Install("before-update", "owner-a"), Is.Null, "repeating the same declaration is idempotent");
            Assert.That(SubsystemCount(MarkerOf("before-update")), Is.EqualTo(1), "an idempotent install never stacks a duplicate subsystem");
            Assert.That(DebugPlayerLoopHost.IdempotentInstalls, Is.GreaterThanOrEqualTo(1L));

            Assert.That(DebugPlayerLoopHost.Install("before-update", "owner-b"), Is.Null, "a second owner on the same phase shares the one subsystem");
            Assert.That(SubsystemCount(MarkerOf("before-update")), Is.EqualTo(1));

            Assert.That(DebugPlayerLoopHost.Release("owner-a"), Is.True);
            Assert.That(SubsystemCount(MarkerOf("before-update")), Is.EqualTo(1), "the subsystem stays while any owner still declares that phase");
            Assert.That(DebugPlayerLoopHost.Release("owner-b"), Is.True);
            Assert.That(SubsystemCount(MarkerOf("before-update")), Is.EqualTo(0), "releasing the last owner removes exactly that subsystem");
            Assert.That(DebugPlayerLoopHost.Release("owner-b"), Is.False, "releasing an unknown owner is an idempotent no-op");
            Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(0));

            // 移动一个 owner 的声明：旧 phase 不再有 owner 时必须被移除，而不是留下孤儿子系统。
            Assert.That(DebugPlayerLoopHost.Install("before-update", "owner-c"), Is.Null);
            Assert.That(DebugPlayerLoopHost.Install("late-update", "owner-c"), Is.Null);
            Assert.That(SubsystemCount(MarkerOf("before-update")), Is.EqualTo(0), "a moved declaration removes the abandoned phase subsystem");
            Assert.That(SubsystemCount(MarkerOf("late-update")), Is.EqualTo(1));
        }

        [Test]
        public void RepeatedPlaySegmentsDoNotAccumulatePhaseSubsystems()
        {
            for (var playSegment = 0; playSegment < 3; playSegment++)
            {
                // 每个 Play 段都从零开始：上一个段结束时框架必须已经移除全部子系统。
                DebugPlayerLoopHost.ReleaseAll("play-exit");
                foreach (var marker in ExpectedMarkers) Assert.That(SubsystemCount(marker), Is.EqualTo(0), "a finished Play segment must leave no phase subsystem behind");

                // 一个 owner 只持有它声明的那一个 phase（program 与 listener 都是这个形状），
                // 因此“每个声明的 phase 各有一个子系统”必须由每个 phase 各自的 owner 建模。
                for (var position = 0; position < DebugPlayerLoopPhases.All().Length; position++)
                {
                    var phase = DebugPlayerLoopPhases.All()[position];
                    Assert.That(DebugPlayerLoopHost.Install(phase, SegmentOwner(playSegment, phase)), Is.Null);
                }

                Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(4), "one subsystem per declared phase, no matter how often Play is re-entered");
                Assert.That(FrameworkSubsystemCount(), Is.EqualTo(4), "the real PlayerLoop holds exactly four framework subsystems, never a copy per Play segment");
                foreach (var phase in DebugPlayerLoopPhases.All())
                {
                    Assert.That(SubsystemCount(MarkerOf(phase)), Is.EqualTo(1), phase + " must be installed exactly once in this Play segment");
                }
                var report = DebugPlayerLoopHost.Describe();
                Assert.That(LongOf(report, "installedCount"), Is.EqualTo(4L), "the report states the actual installed subsystem count");
                Assert.That(report["installedPhases"], Is.EqualTo(DebugPlayerLoopPhases.All()), "the report names the installed phases in the declared order");
                Assert.That(LongOf(report, "ownerCount"), Is.EqualTo(4L), "each declared phase is held by its own owner");
            }

            // 最后一个 Play 段结束后同样不得留下任何子系统。
            DebugPlayerLoopHost.ReleaseAll("play-exit");
            Assert.That(FrameworkSubsystemCount(), Is.EqualTo(0), "a finished Play segment leaves no phase subsystem behind");
        }

        /// <summary>
        /// 泄漏探针：同一个 owner 改声明另一个 phase 时，旧 phase 的子系统必须真的从 PlayerLoop 里消失，
        /// 而不是只改 Owners 记账。逐对 phase 组合读取真实循环计数，覆盖幂等（同 phase）与非幂等（换 phase）两类路径。
        /// </summary>
        [Test]
        public void MovingOneOwnerAcrossPhasesNeverLeavesAnOrphanSubsystem()
        {
            var phases = DebugPlayerLoopPhases.All();
            for (var from = 0; from < phases.Length; from++)
            {
                for (var to = 0; to < phases.Length; to++)
                {
                    DebugPlayerLoopHost.ReleaseAll("leak-probe");
                    Assert.That(FrameworkSubsystemCount(), Is.EqualTo(0), "the probe starts from a loop without framework subsystems");

                    Assert.That(DebugPlayerLoopHost.Install(phases[from], "moving-owner"), Is.Null);
                    Assert.That(FrameworkSubsystemCount(), Is.EqualTo(1), phases[from] + " is installed once for its single owner");

                    Assert.That(DebugPlayerLoopHost.Install(phases[to], "moving-owner"), Is.Null);
                    Assert.That(SubsystemCount(MarkerOf(phases[to])), Is.EqualTo(1), phases[to] + " is installed exactly once after the owner moved to it");
                    Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(1), "one owner holds exactly one phase");
                    if (!string.Equals(phases[from], phases[to], StringComparison.Ordinal))
                        Assert.That(SubsystemCount(MarkerOf(phases[from])), Is.EqualTo(0), phases[from] + " must be removed from the real PlayerLoop once its last owner moved away");
                    Assert.That(FrameworkSubsystemCount(), Is.EqualTo(1), "a moved declaration never stacks a second framework subsystem");

                    Assert.That(DebugPlayerLoopHost.Release("moving-owner"), Is.True);
                    Assert.That(FrameworkSubsystemCount(), Is.EqualTo(0), "releasing the moved owner removes exactly its one subsystem");
                }
            }
        }

        [Test]
        public void PhaseDeliveriesAreClaimedOnlyByTheirOwnExecutionPoint()
        {
            var deliveryId = DebugScheduler.EnqueueDelivery("prog-phase", "prun-phase", "Listener", "sig-phase", "{}", 3L, 5000d, null, "before-update");
            Assert.That(deliveryId, Is.Not.Null.And.Not.Empty);
            Assert.That(DebugScheduler.PendingDeliveryCount, Is.EqualTo(1));

            Assert.That(DebugScheduler.ClaimFrameworkPhaseDeliveries("editor-tick", 1, DebugLimits.MaxDeliveriesPerTick), Is.Empty, "the editor tick never takes a delivery addressed to a PlayerLoop phase");
            Assert.That(DebugScheduler.ClaimFrameworkPhaseDeliveries("after-update", 1, DebugLimits.MaxDeliveriesPerTick), Is.Empty, "another phase never takes it either");
            Assert.That(DebugScheduler.PendingDeliveryCount, Is.EqualTo(1), "a wrong execution point leaves the delivery pending");

            var claimed = DebugScheduler.ClaimFrameworkPhaseDeliveries("before-update", 1, DebugLimits.MaxDeliveriesPerTick);
            Assert.That(claimed.Count, Is.EqualTo(1), "the declared phase takes its delivery exactly once");
            Assert.That(claimed[0].DeliveryId, Is.EqualTo(deliveryId));
            Assert.That(claimed[0].Phase, Is.EqualTo("before-update"), "the delivery keeps the phase it was addressed to");
            Assert.That(claimed[0].StartedAtMonotonicMs, Is.GreaterThan(0d), "the claim records the start edge");
            Assert.That(claimed[0].ExecutionThreadId, Is.EqualTo(1), "the claim records the thread that reached the phase");
            Assert.That(DebugScheduler.ClaimFrameworkPhaseDeliveries("before-update", 1, DebugLimits.MaxDeliveriesPerTick), Is.Empty, "a claimed phase delivery is never handed out twice");

            // editor-tick 投递仍由同一个中央 tick 取得：phase 路由不改变既有语义。
            DebugScheduler.EnqueueDelivery("prog-phase", "prun-phase", "Listener", "sig-tick", null, 1L, 5000d, null, UnityCliDebugProgramExecutionSites.EditorTick);
            Assert.That(DebugScheduler.ClaimFrameworkPhaseDeliveries("before-update", 1, DebugLimits.MaxDeliveriesPerTick), Is.Empty, "a phase never takes an editor-tick delivery");
            Assert.That(DebugScheduler.ClaimFrameworkPhaseDeliveries("editor-tick", 1, DebugLimits.MaxDeliveriesPerTick).Count, Is.EqualTo(1));
        }

        [Test]
        public void ADriverIsAdvancedOnlyOnItsOwnDeclaredPhase()
        {
            var now = DebugEventClocks.MonotonicMilliseconds;
            Assert.That(DebugScheduler.PlantDriver("prog-driver-phase", "prun-driver-phase", "Step", 1L, now, now + 60000d, "before-update"), Is.Null);
            Assert.That(DebugScheduler.DescribeDriver("prog-driver-phase").Phase, Is.EqualTo("before-update"), "the driver reports the execution point it was planted on");

            // 中央 editor tick 不得推进声明了 PlayerLoop phase 的 driver。
            DebugScheduler.Tick();
            var untouched = DebugScheduler.DescribeDriver("prog-driver-phase");
            Assert.That(untouched, Is.Not.Null, "the editor tick must not advance or close another execution point's driver");
            Assert.That(untouched.StepIndex, Is.EqualTo(0L), "no step was claimed outside the declared phase");

            // 该 phase 的推进段才会真正推进它；这里没有对应运行，因此真实推进路径会关闭该 driver。
            DebugScheduler.AdvancePhase("before-update");
            Assert.That(DebugScheduler.DescribeDriver("prog-driver-phase"), Is.Null, "only the declared phase reaches the driver's real advance path");
        }

        [Test]
        public void EditModeRefusesAPhaseSiteWithTheStableSharedCode()
        {
            var refused = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugPlayerLoopHost.RequirePlayDomain("before-update", new Dictionary<string, object> { { "path", "$.phase" } }));
            Assert.That(refused.Code, Is.EqualTo("E_DEBUG_PROGRAM_SITE_UNAVAILABLE"), "Edit mode refuses a PlayerLoop phase with the shared stable code");
            Assert.That(refused.NextActions, Is.Not.Empty, "the refusal must carry executable next actions");
            Assert.That(refused.NextActions.Any(action => action.Contains("Play")), Is.True, "the recovery path names Editor Play");

            // listener 声明 phase 触发源时在 Edit 模式下使用同一错误码，并且不会安装任何现场。
            var listener = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-phase",
                Trigger = DebugTriggerSources.PlayerLoopPhase,
                Phase = "before-update"
            }));
            Assert.That(listener.Code, Is.EqualTo("E_DEBUG_PROGRAM_SITE_UNAVAILABLE"));
            Assert.That(DebugPlayerLoopHost.InstalledCount, Is.EqualTo(0), "a refused declaration installs nothing");
        }

        [Test]
        public void PhaseDeclarationsAreValidatedAndNeverHideBehindADefault()
        {
            var unknown = DebugPlayerLoopHost.PhaseUnknown("$.phase", "frame");
            Assert.That(unknown.Code, Is.EqualTo("E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN"), "an undeclared phase name has one stable code");
            Assert.That(unknown.NextActions, Is.Not.Empty);
            Assert.That(((IDictionary<string, object>)unknown.Details)["availablePhases"], Is.EqualTo(DebugPlayerLoopPhases.All()), "the refusal names the closed set");

            Assert.That(DebugPlayerLoopHost.Install("frame", "owner-x"), Is.EqualTo("phase-unknown"), "the mechanism validates the name at install time");
            Assert.That(DebugPlayerLoopHost.Install("before-update", string.Empty), Is.EqualTo("owner-required"), "a phase site always has an owner");

            // 只有声明 phase 触发源的 listener 才能带 phase；editor-tick 也不是 listener 触发源。
            var withoutPhase = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-phase",
                Trigger = DebugTriggerSources.MethodHit,
                Phase = "before-update"
            }));
            Assert.That(withoutPhase.Code, Is.EqualTo("E_SCHEMA_INVALID"));
            var tickTrigger = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => MethodHookHost.PrepareListener(new MethodHookListenerSpec
            {
                SessionId = "dbg-phase",
                Trigger = DebugTriggerSources.EditorTick
            }));
            Assert.That(tickTrigger.Code, Is.EqualTo("E_SCHEMA_INVALID"), "the editor tick is not a listener trigger source");
        }

        [Test]
        public void ThePublicSchemaDeclaresExactlyTheOwnerPhaseSet()
        {
            var runEnums = typeof(DebugProgramRunRequest).GetProperty("Phase").GetCustomAttributes(typeof(UnityCliStringEnumAttribute), true).Cast<UnityCliStringEnumAttribute>().FirstOrDefault();
            Assert.That(runEnums, Is.Not.Null, "debug.program.run must declare the phase field as a closed enum");
            Assert.That(runEnums.Values, Is.EqualTo(DebugPlayerLoopPhases.All()), "the run schema must not drift from the single phase owner");

            var listenerEnums = typeof(MethodHookListenerSpec).GetProperty("Phase").GetCustomAttributes(typeof(UnityCliStringEnumAttribute), true).Cast<UnityCliStringEnumAttribute>().FirstOrDefault();
            Assert.That(listenerEnums, Is.Not.Null, "the listener must declare the phase field as a closed enum");
            Assert.That(listenerEnums.Values, Is.EqualTo(DebugPlayerLoopPhases.All()), "the listener schema must not drift from the single phase owner");

            var triggers = typeof(MethodHookListenerSpec).GetProperty("Trigger").GetCustomAttributes(typeof(UnityCliStringEnumAttribute), true).Cast<UnityCliStringEnumAttribute>().FirstOrDefault();
            Assert.That(triggers, Is.Not.Null);
            Assert.That(triggers.Values, Does.Contain(DebugTriggerSources.PlayerLoopPhase), "a listener may subscribe the PlayerLoop phase trigger");
            Assert.That(triggers.Values, Does.Not.Contain(DebugTriggerSources.EditorTick), "the editor tick is not a listener trigger");

            // 触发事实必须能被证明来自真实 phase：事件里的 phase 名字与执行点字段。
            var triggersSource = DebugTriggerSources.All();
            Assert.That(triggersSource, Does.Contain(DebugTriggerSources.PlayerLoopPhase));
            Assert.That(triggersSource, Does.Contain(DebugTriggerSources.EditorTick));
        }
    }
}
#endif
