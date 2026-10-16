#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// 主线程暂停控制通道的定向测试。
    ///
    /// 覆盖范围：可达集合与快速拒绝、现场操作有界等待、health/editor.state 的 blocker 投影、
    /// 解除路径先于 pause 资源安装、共享编译器的互斥 lane、F-16/F-17 的投影与所有权规则。
    /// 真实暂停与恢复由 Dev 功能矩阵验证，不在本夹具重复。
    /// </summary>
    public sealed class DebugPauseControlTests
    {
        private bool channelWasPublished;

        [SetUp]
        public void SetUp()
        {
            channelWasPublished = DebugPauseControl.Available;
        }

        [TearDown]
        public void TearDown()
        {
            DebugPauseHost.Reset();
            DebugCompilerOwner.EndOwnership(DebugCompilerLane.PauseService);
            DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick);
            // 控制通道是本次启动的真实机制状态：夹具只能恢复它，绝不能在桥仍运行时把它关掉。
            if (channelWasPublished && !DebugPauseControl.Available) DebugPauseControl.Activate();
        }

        [Test]
        public void ReachableSetIsAFrozenAllowlistOfReadsAndTheReleasePath()
        {
            Assert.That(DebugPauseControl.IsReachable("internal.editor.state.snapshot", string.Empty, null), Is.True, "editor.state must stay observable while the main thread is held");
            foreach (var command in new[] { "catalog.editor.health", "catalog.editor.groups", "catalog.editor.summary", "catalog.editor.describe", "catalog.editor.list" })
                Assert.That(DebugPauseControl.IsReachable(command, string.Empty, null), Is.True, command + " must be served from the live publication during a pause");
            foreach (var tool in new[]
            {
                "debug.pause.list", "debug.pause.wait", "debug.pause.inspect", "debug.pause.evaluate", "debug.pause.resume",
                "debug.events.read", "debug.thread.list",
                "debug.session.info", "debug.session.list", "debug.program.info", "debug.program.list",
                "debug.hook.info", "debug.hook.list"
            })
                Assert.That(DebugPauseControl.IsReachable("catalog.editor.run", tool, "{}"), Is.True, tool + " must be reachable during a main-thread pause");

            // 普通业务 mutation 与未命名 pauseId 的值操作都不在名单里，因此必须走统一拒绝路径。
            foreach (var tool in new[] { "debug.hook.install", "debug.hook.remove", "debug.session.create", "debug.session.stop", "debug.program.run", "debug.code.execute", "debug.console.clear", "editor.compile", "debug.value.read", "debug.value.write" })
                Assert.That(DebugPauseControl.IsReachable("catalog.editor.run", tool, "{}"), Is.False, tool + " must not be served during a main-thread pause");
            Assert.That(DebugPauseControl.IsReachable("catalog.editor.run", "debug.value.read", "{\"pauseId\":\"pause-1\"}"), Is.True, "a value operation that names a pauseId routes to that site");
            Assert.That(DebugPauseControl.IsReachable("catalog.editor.run", "debug.value.write", "{\"pauseId\":\"pause-1\"}"), Is.True);
            Assert.That(DebugPauseControl.IsReachable("catalog.editor.health", string.Empty, null), Is.True);
            Assert.That(DebugPauseControl.IsReachable("catalog.editor.run", string.Empty, null), Is.False, "an unreadable inner name is not a reachable capability");
        }

        [Test]
        public void NestedToolNameOnlyReadsTheDeclaredOuterName()
        {
            // CLI 提交的是 { name, input }：Tool 名只能来自外层 name 字段。
            Assert.That(DebugPauseControl.NestedToolName(WrappedRequest("debug.pause.list", "{}")), Is.EqualTo("debug.pause.list"));
            Assert.That(DebugPauseControl.NestedToolName(WrappedRequest("debug.pause.list", "{\"name\":\"debug.events.read\"}")), Is.EqualTo("debug.pause.list"),
                "the inner business args can never supply the Tool name");
            Assert.That(DebugPauseControl.NestedToolName(WrappedRequest(string.Empty, "{}")), Is.EqualTo(string.Empty), "an empty name has no inner capability");
            Assert.That(DebugPauseControl.NestedToolName(PlainRequest("catalog.editor.health", "{\"name\":\"debug.pause.list\"}")), Is.EqualTo(string.Empty), "only catalog.editor.run carries an inner Tool name");
            Assert.That(DebugPauseControl.NestedToolName(null), Is.EqualTo(string.Empty));
            Assert.That(DebugPauseControl.NestedInputJson(PlainRequest("catalog.editor.health", "{}")), Is.Null, "a command without a wrapped payload has no inner input");
        }

        [Test]
        public void PauseControlReachabilityIsDerivedFromTheRealRequestShape()
        {
            // F-20 回归覆盖：闸门不再从内层业务参数里找 Tool 名，因此这里必须从真实请求形状出发，
            // 走闸门实际使用的那一个入口，而不是手工把内层名称传给可达判定。
            foreach (var tool in new[] { "debug.pause.list", "debug.events.read" })
            {
                string nestedTool;
                Assert.That(DebugPauseControl.IsReachable(WrappedRequest(tool, "{}"), out nestedTool, out _), Is.True,
                    tool + " must stay reachable while the Editor main thread is paused");
                Assert.That(nestedTool, Is.EqualTo(tool), "the resolved inner Tool name must come from the outer name field");
            }

            foreach (var tool in new[] { "debug.hook.install", "debug.hook.remove", "debug.session.create", "debug.code.execute", "editor.compile" })
            {
                string nestedTool;
                Assert.That(DebugPauseControl.IsReachable(WrappedRequest(tool, "{\"pauseId\":\"pause-1\"}"), out nestedTool, out _), Is.False,
                    tool + " is outside the frozen allowlist and must stay refused even with a nested pauseId");
                Assert.That(nestedTool, Is.EqualTo(tool));
            }

            // 值操作只有内层 input 显式命名 pauseId 时才路由到该现场。
            foreach (var tool in new[] { "debug.value.read", "debug.value.write" })
            {
                string nestedInput;
                Assert.That(DebugPauseControl.IsReachable(WrappedRequest(tool, "{}"), out _, out nestedInput), Is.False,
                    tool + " without an explicit inner pauseId must stay on the normal path");
                Assert.That(DebugPauseControl.IsReachable(WrappedRequest(tool, "{\"pauseId\":\"pause-1\"}"), out _, out nestedInput), Is.True,
                    tool + " names a pauseId and must route to that site");
                Assert.That(nestedInput, Does.Contain("pause-1"), "only the outer input field supplies the inner business args");
            }

            // 名单外的命令即使携带 { name, input } 也不能变成可达能力。
            string otherTool;
            Assert.That(DebugPauseControl.IsReachable(WrappedRequest("debug.pause.list", "{}", "debug.code.execute"), out otherTool, out _), Is.False,
                "a command outside the allowlist must not become reachable through its payload");
            Assert.That(otherTool, Is.EqualTo(string.Empty));
            Assert.That(DebugPauseControl.IsReachable(WrappedRequest("debug.pause.list", "{}", "catalog.editor.health"), out otherTool, out _), Is.True,
                "a listed registry command stays reachable and needs no nested name");
            Assert.That(otherTool, Is.EqualTo(string.Empty));
        }

        [Test]
        public void MainThreadPausedRejectionNamesTheActualStateAndTheReleaseRoute()
        {
            var pause = SampleMainThreadPause();
            var failure = DebugPauseControl.MainThreadPaused("catalog.editor.run", "debug.hook.install", pause);

            Assert.That(failure.Code, Is.EqualTo(DebugPauseControl.RejectionCode));
            Assert.That(failure.Code, Is.EqualTo("E_DEBUG_MAIN_THREAD_PAUSED"));
            Assert.That(failure.Message, Does.Contain("main thread"));
            Assert.That(failure.Message, Does.Contain("before any effect"), "the rejection must state that nothing happened");
            Assert.That(failure.Message, Does.Not.Contain("compiling"), "a deliberate pause is not a compile gate");
            var details = (Dictionary<string, object>)failure.Details;
            Assert.That(details["pauseId"], Is.EqualTo("pause-7"));
            Assert.That(details["toolName"], Is.EqualTo("debug.hook.install"));
            Assert.That(details["mainThread"], Is.EqualTo(true));
            Assert.That(details["timeoutKind"], Is.EqualTo("bounded"));
            Assert.That(details["resumeTool"], Is.EqualTo("debug.pause.resume"));
            Assert.That(details["pauseTool"], Is.EqualTo("debug.pause.list"));
            Assert.That(failure.NextActions.Any(action => action.Contains("debug.pause.list", StringComparison.Ordinal)), Is.True);
            Assert.That(failure.NextActions.Any(action => action.Contains("debug.pause.resume", StringComparison.Ordinal) && action.Contains("pause-7", StringComparison.Ordinal)), Is.True,
                "the nextActions must route to the exact site");
        }

        [Test]
        public void PausedEditorStateSnapshotReportsUnknownFieldsAndTheSingleBlocker()
        {
            var data = DebugPauseControl.OffMainEditorStateSnapshot(SampleMainThreadPause());
            var snapshot = (Dictionary<string, object>)data["snapshot"];
            Assert.That(snapshot["schemaVersion"], Is.EqualTo(1L));
            Assert.That(snapshot["sourceEditorSessionId"], Is.EqualTo(UnityCliBridge.EditorSessionId()));
            Assert.That(snapshot["sourceListenerGeneration"], Is.Not.Null);
            var fields = (Dictionary<string, object>)snapshot["fields"];
            Assert.That(fields.Keys, Is.EquivalentTo(new[] { "compile", "import", "build", "playMode", "prefabStage", "activeScene", "runtime" }));
            foreach (var pair in fields)
            {
                var field = (Dictionary<string, object>)pair.Value;
                Assert.That(field["known"], Is.EqualTo(false), "a paused snapshot cannot claim any Unity field is known");
                Assert.That(field.ContainsKey("value"), Is.False, "an unknown field must not carry a value");
            }
            Assert.That(((List<object>)data["progress"]), Is.Empty);

            var blockers = (List<object>)data["blockers"];
            Assert.That(blockers.Count, Is.EqualTo(1));
            var blocker = (Dictionary<string, object>)blockers[0];
            Assert.That(blocker["type"], Is.EqualTo("debug-pause"));
            Assert.That(blocker["source"], Is.EqualTo("unity-debug-pause"));
            Assert.That(blocker["confidence"], Is.EqualTo("exact"));
            Assert.That(blocker["requiresUserAction"], Is.EqualTo(false), "a deliberate pause needs no user action");
            Assert.That(blocker["resumeTool"], Is.EqualTo("debug.pause.resume"));
            Assert.That(blocker["infoTool"], Is.EqualTo("debug.pause.list"));
            Assert.That(blocker["pauseId"], Is.EqualTo("pause-7"));
            Assert.That(blocker["mainThread"], Is.EqualTo(true));
            Assert.That(blocker["timeoutKind"], Is.EqualTo("bounded"));
            Assert.That(blocker.ContainsKey("endpoint"), Is.False, "no internal routing field may enter a public blocker");
        }

        [Test]
        public void HealthProjectionCarriesThePauseAsSeparateEvidence()
        {
            var pause = SampleMainThreadPause();
            var data = pause.ToData();
            Assert.That(data["pauseId"], Is.EqualTo("pause-7"));
            Assert.That(data["mainThread"], Is.EqualTo(true));
            Assert.That(data["publishedSequence"], Is.EqualTo(7L));
            // health 只发布暂停身份证据；blocker DTO 的判别字段与路由字段不得出现在这里（缺失而非 null）。
            foreach (var blockerOnly in new[] { "type", "resumeTool", "infoTool", "requiresUserAction", "source", "confidence" })
            {
                Assert.That(data.ContainsKey(blockerOnly), Is.False, $"the health projection must not carry the blocker-only field {blockerOnly}");
            }

            // pulse 与暂停是两份证据：投影里没有 pulse 字段，暂停也不会改写它。
            Assert.That(data.Keys.Any(key => key.IndexOf("sequence", StringComparison.Ordinal) >= 0 && !string.Equals(key, "publishedSequence", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public void PauseBreakpointIsRefusedUntilTheControlChannelIsPublished()
        {
            DebugPauseControl.Reset();
            Assert.That(DebugPauseControl.Available, Is.False);
            var spec = new MethodHookBreakpointSpec { Kind = DebugPauseBreakpointKinds.Pause, PauseTimeout = new MethodHookPauseTimeoutSpec { Kind = DebugPauseTimeoutKinds.Unbounded } };
            var refused = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugPauseHost.Prepare(spec, "breakpoint"));
            Assert.That(refused.Code, Is.EqualTo("E_DEBUG_CONTROL_PLANE_UNAVAILABLE"), "a site installed without a release route would be unreleasable");
            Assert.That(refused.NextActions.Count, Is.GreaterThan(0));

            // snapshot 断点不持有线程，因此不依赖该通道。
            var snapshot = DebugPauseHost.Prepare(new MethodHookBreakpointSpec { Kind = DebugPauseBreakpointKinds.Snapshot }, "breakpoint");
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot.Breakpoint.Kind, Is.EqualTo(DebugPauseBreakpointKinds.Snapshot));

            DebugPauseControl.Activate();
            var plan = DebugPauseHost.Prepare(spec, "breakpoint");
            Assert.That(plan.Breakpoint.Kind, Is.EqualTo(DebugPauseBreakpointKinds.Pause));
            Assert.That(plan.Breakpoint.Unbounded, Is.True);
            Assert.That(plan.Owner, Is.Null, "an unbounded pause without a sessionId reserves no session resource");
        }

        [Test]
        public void TheTwoCompilerLanesAreMutuallyExclusiveWithoutBlockingThePauseService()
        {
            Assert.That(DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.MainThreadTick), Is.True);
            Assert.That(DebugCompilerOwner.TryBeginOwnershipWithin(DebugCompilerLane.PauseService, TimeSpan.Zero), Is.False,
                "a busy compiler must return busy instead of waiting for the old owner, so the pause site stays inspectable");
            Assert.That(DebugCompilerOwner.HoldsOwnership(DebugCompilerLane.MainThreadTick), Is.True);
            Assert.That(DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick), Is.True);
            Assert.That(DebugCompilerOwner.TryBeginOwnershipWithin(DebugCompilerLane.PauseService, TimeSpan.Zero), Is.True,
                "once the other lane released, the pause service lane may advance csc while the main thread does not tick");
            Assert.That(DebugCompilerOwner.EndOwnership(DebugCompilerLane.PauseService), Is.True);
        }

        [Test]
        public void ValueOperationsRouteThroughTheSameOnSiteMailboxAndRefuseAnUnknownPauseId()
        {
            var stale = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugPauseHost.FindSite("pause-never-published"));
            Assert.That(stale.Code, Is.EqualTo("E_DEBUG_PAUSE_STALE"));
            Assert.That(((Dictionary<string, object>)stale.Details)["reason"], Is.EqualTo("unknown-pause-id"));
            Assert.That(typeof(DebugValueReadRequest).GetProperty("PauseId"), Is.Not.Null, "a value read must be able to name a pause site");
            Assert.That(typeof(DebugValueWriteRequest).GetProperty("PauseId"), Is.Not.Null, "a value write must be able to name a pause site");
            Assert.That(typeof(DebugPauseOperation).GetField("Site", BindingFlags.Instance | BindingFlags.NonPublic), Is.Not.Null,
                "an on-site operation must keep the site it belongs to so a pending report can name it");
            Assert.That(DebugPauseOperationKinds.ValueRead, Is.EqualTo("value.read"));
            Assert.That(DebugPauseOperationKinds.ValueWrite, Is.EqualTo("value.write"));
        }

        [Test]
        public void F16ListProjectionCarriesTheIdentityAndBreakpointRemoveNeeds()
        {
            // 列表必须自足：只持有列表输出的客户端必须能直接对同一 identity 执行 remove/replace/disable。
            var itemMembers = typeof(MethodHookListItem).GetProperties().Select(property => property.Name).ToArray();
            Assert.That(itemMembers, Does.Contain("EditorSessionId"));
            Assert.That(itemMembers, Does.Contain("Breakpoint"));
            Assert.That(typeof(MethodHookRemoveRequest).GetProperty("EditorSessionId").GetCustomAttributes(typeof(UnityCliRequiredAttribute), true), Is.Not.Empty,
                "remove still requires the editorSessionId, so the list is the only place a list-only client can learn it");

            var definition = new MethodHookDefinition
            {
                HookId = "hook-00000000000000ab",
                Breakpoint = new MethodHookBreakpoint { Kind = DebugPauseBreakpointKinds.Pause, Position = DebugPausePositions.Prefix, DurationMs = 1500L, SessionId = "dbg-1" },
                BreakpointOwner = new DebugPauseOwner { HookId = "hook-00000000000000ab", ResourceId = "res-1" }
            };
            var summary = MethodHookHost.SummarizeBreakpoint(definition);
            Assert.That(summary, Is.Not.Null, "a declared breakpoint must appear in the list projection");
            Assert.That(summary.Present, Is.True);
            Assert.That(summary.Kind, Is.EqualTo(DebugPauseBreakpointKinds.Pause));
            Assert.That(summary.Position, Is.EqualTo(DebugPausePositions.Prefix));
            Assert.That(summary.TimeoutKind, Is.EqualTo(DebugPauseTimeoutKinds.Bounded));
            Assert.That(summary.TimeoutDurationMs, Is.EqualTo(1500L));
            Assert.That(summary.SessionId, Is.EqualTo("dbg-1"));
            Assert.That(summary.ResourceRegistered, Is.True);
            Assert.That(MethodHookHost.SummarizeBreakpoint(new MethodHookDefinition()), Is.Null, "a definition without a breakpoint stays null");
        }

        [Test]
        public void F17PauseBreakpointIsAnArbitraryInterventionOwnerJustLikeAMutableCallback()
        {
            Assert.That(MethodHookHost.OwnsArbitraryIntervention(false, new MethodHookBreakpointSpec { Kind = DebugPauseBreakpointKinds.Pause }), Is.True,
                "a pause breakpoint lets pause.evaluate change arguments, skip the original and write the result, so it owns the same intervention right");
            Assert.That(MethodHookHost.OwnsArbitraryIntervention(false, new MethodHookBreakpointSpec { Kind = DebugPauseBreakpointKinds.Snapshot }), Is.False,
                "a snapshot breakpoint only captures values and may coexist");
            Assert.That(MethodHookHost.OwnsArbitraryIntervention(true, (MethodHookBreakpointSpec)null), Is.True);
            Assert.That(MethodHookHost.OwnsArbitraryIntervention(false, (MethodHookBreakpointSpec)null), Is.False);
            Assert.That(MethodHookHost.OwnsArbitraryIntervention(false, new MethodHookBreakpoint { Kind = DebugPauseBreakpointKinds.Pause }), Is.True);
            Assert.That(MethodHookHost.OwnsArbitraryIntervention(false, new MethodHookBreakpoint { Kind = DebugPauseBreakpointKinds.Snapshot }), Is.False);
            Assert.That(MethodHookHost.OwnsArbitraryIntervention(false, (MethodHookBreakpoint)null), Is.False);
        }

        /// <summary>
        /// 构造 CLI 实际提交的 wire payload 并交给桥的真实反序列化入口。
        /// 外层 args 是 { name, input }：name 是被包裹的 Tool 名，input 是该 Tool 的业务参数。
        /// 定向测试因此从真实请求形状出发，而不是手工拼一个内层名称。
        /// </summary>
        private static UnityCli.Protocol.BridgeRequest WrappedRequest(string toolName, string innerArgsJson, string command = "catalog.editor.run")
        {
            return PlainRequest(command, UnityCliSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", toolName },
                { "input", UnityCliSerializer.Parse(innerArgsJson) }
            }));
        }

        /// <summary>构造一个完整合法的 wire payload；缺失字段会让真实反序列化入口直接失败。</summary>
        private static UnityCli.Protocol.BridgeRequest PlainRequest(string command, string inputJson)
        {
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "protocolVersion", 2d },
                { "instanceId", "instance-under-test" },
                { "command", command },
                { "requestId", "req-under-test" },
                { "input", UnityCliSerializer.Parse(inputJson) },
                { "execution", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "absoluteDeadlineUtc", "2999-01-01T00:00:00.0000000Z" },
                        { "completionContractVersion", 1d },
                        { "requestDigestVersion", 1d }
                    }
                }
            };
            return UnityCli.Protocol.BridgeRequest.DeserializeStrict(UnityCliSerializer.Serialize(payload));
        }

        /// <summary>与现场无关的不可变投影样本；只用于断言公开形状，不声称存在真实现场。</summary>
        private static DebugMainThreadPauseView SampleMainThreadPause()
        {
            return new DebugMainThreadPauseView
            {
                PauseId = "pause-7",
                State = DebugPauseStates.Paused,
                ThreadId = 1,
                PublishedSequence = 7L,
                MethodIdentity = "UnityCli.Tests.Probe.Step()",
                TargetKey = "target-key",
                Position = DebugPausePositions.Prefix,
                SessionId = string.Empty,
                TimeoutKind = DebugPauseTimeoutKinds.Bounded,
                TimeoutDurationMs = 1500L,
                RemainingMs = 900L,
                PausedForMs = 600d,
                ServedOperations = 2L,
                RefusedRequests = 0L,
                EditorSessionId = UnityCliBridge.EditorSessionId()
            };
        }
    }
}
#endif
