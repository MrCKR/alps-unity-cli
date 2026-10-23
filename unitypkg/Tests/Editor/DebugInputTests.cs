using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// 可选设备后端与通用输入的定向逻辑测试。
    ///
    /// 覆盖：缺少依赖时的 unavailable 拒绝且不创建任何设备；会话拥有的虚拟设备与显式结束边界；
    /// 到期边界与待主线程释放在唯一中央 tick 上补完；只有读回确认才报 released；
    /// 暂停（主线程当场 / worker 交给中央调度）与 session stop 的释放语义；
    /// 冲突在任何 effect 之前拒绝；目标坐标空间的解析与拒绝；通用输入路径与 wrapper 现场验证。
    /// </summary>
    public sealed class DebugInputTests
    {
        /// <summary>可拦截的 managed 输入 wrapper 目标；Legacy 路径的现场验证用它做真实 detour。</summary>
        private sealed class InputWrapperProbe
        {
            public static int Axis;

            /// <summary>可被 detour 的 managed wrapper。禁止内联，否则现场验证读不到真实调用。</summary>
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public static float ReadMoveAxis()
            {
                return Axis;
            }
        }

        /// <summary>
        /// 测试用设备后端：只实现设备后端契约，用来确定性地驱动 owner 语义。
        /// 它模拟“提交可能尚未生效”与“读回可能仍报告按下”两种真实环境差异。
        /// </summary>
        private sealed class FakeDeviceBackend : IUnityCliDebugInputDeviceBackend
        {
            private readonly List<string> knownControls = new List<string> { "Space", "W", "LeftShift" };
            private readonly List<object> devices = new List<object>();
            private readonly List<string> foreignNames = new List<string>();
            private readonly Dictionary<object, string> names = new Dictionary<object, string>();
            private readonly Dictionary<object, List<string>> applied = new Dictionary<object, List<string>>();
            private readonly Dictionary<object, List<string>> queued = new Dictionary<object, List<string>>();

            internal bool AvailableFlag = true;
            internal bool CreateFails;
            internal bool RemoveFails;
            internal bool ReadFails;
            internal bool ListFails;
            internal int CreateCount;
            internal int RemoveCount;
            internal int QueueCount;

            /// <summary>模拟仍报告为按下的控制：设备尚未处理已提交的清除状态。</summary>
            internal List<string> ForcedHeld = new List<string>();

            /// <summary>提交是否立即生效；false 时状态保持为未生效，直到测试显式应用。</summary>
            internal bool ApplyImmediately = true;

            public string Name { get { return UnityCliDebugInputBackends.InputSystem; } }

            public bool Available { get { return AvailableFlag; } }

            public string UnavailableReason { get { return UnityCliDebugInputReasons.BackendNotLoaded; } }

            public string UnavailableDetail { get { return "the fake backend is declared unavailable"; } }

            public string ApiIdentity { get { return "fake-device-backend/1"; } }

            public IList<string> MissingApiMembers { get { return new List<string>(); } }

            internal void AddForeignName(string name) { foreignNames.Add(name); }

            internal IList<string> DeviceNames { get { return names.Values.ToList(); } }

            /// <summary>让未生效的提交生效，模拟设备的真实输入更新。</summary>
            internal void ApplyQueued()
            {
                foreach (var pair in queued) applied[pair.Key] = new List<string>(pair.Value);
                queued.Clear();
            }

            public bool TryCreateDevice(string layout, string deviceName, out object device, out string reason)
            {
                reason = string.Empty;
                if (!Available) { device = null; reason = UnavailableReason; return false; }
                if (CreateFails) { device = null; reason = "fake-create-refusal"; return false; }
                var handle = new object();
                devices.Add(handle);
                names[handle] = deviceName;
                applied[handle] = new List<string>();
                CreateCount++;
                device = handle;
                return true;
            }

            public bool TryRemoveDevice(object device, out string reason)
            {
                reason = string.Empty;
                if (!Available) { reason = UnavailableReason; return false; }
                if (RemoveFails) { reason = "fake-remove-refusal"; return false; }
                if (device == null || !devices.Contains(device)) { reason = "device-not-found"; return false; }
                devices.Remove(device);
                names.Remove(device);
                applied.Remove(device);
                queued.Remove(device);
                RemoveCount++;
                return true;
            }

            public bool TryQueueKeys(object device, IList<string> keys, out string reason)
            {
                reason = string.Empty;
                if (!Available) { reason = UnavailableReason; return false; }
                if (device == null || !devices.Contains(device)) { reason = "device-not-found"; return false; }
                var state = keys == null ? new List<string>() : new List<string>(keys);
                QueueCount++;
                if (ApplyImmediately) applied[device] = state;
                else queued[device] = state;
                return true;
            }

            public bool TryQueuePointer(object device, IList<string> buttons, double x, double y, out string reason)
            {
                return TryQueueKeys(device, buttons, out reason);
            }

            public bool TryReadHeldControls(object device, out IList<string> held, out string reason)
            {
                var merged = new List<string>(ForcedHeld);
                reason = string.Empty;
                if (!Available) { held = merged; reason = UnavailableReason; return false; }
                if (ReadFails) { held = merged; reason = "fake-read-refusal"; return false; }
                if (device != null)
                {
                    List<string> state;
                    if (applied.TryGetValue(device, out state)) merged.AddRange(state);
                    if (queued.TryGetValue(device, out state)) merged.AddRange(state);
                }
                held = merged.Distinct(StringComparer.Ordinal).ToList();
                return true;
            }

            public bool TryReadDeviceNames(out IList<string> namesOut, out string reason)
            {
                var merged = new List<string>(foreignNames);
                namesOut = merged;
                reason = string.Empty;
                if (!Available) { reason = UnavailableReason; return false; }
                if (ListFails) { reason = "fake-list-refusal"; return false; }
                foreach (var name in names.Values) merged.Add(name);
                return true;
            }

            public bool IsKnownControl(string name)
            {
                return !string.IsNullOrEmpty(name) && knownControls.Contains(name, StringComparer.Ordinal);
            }

            /// <summary>恢复所有拒绝开关与设备状态，供清理使用。</summary>
            internal void Reset()
            {
                AvailableFlag = true;
                CreateFails = false;
                RemoveFails = false;
                ReadFails = false;
                ListFails = false;
                ApplyImmediately = true;
                ForcedHeld.Clear();
                foreignNames.Clear();
            }
        }

        private readonly List<string> sessions = new List<string>();
        private readonly List<MethodHookInstallResult> hooks = new List<MethodHookInstallResult>();
        private FakeDeviceBackend backend;
        private string sessionId;

        [SetUp]
        public void SetUp()
        {
            DebugScheduler.Reset();
            DebugTriggerBudget.Reset();
            DebugEventStore.Reset();
            DebugInputHost.Reset();
            DebugInputDeviceBackends.Reset();
            MethodHookHost.InitializeAfterReload();
            sessions.Clear();
            hooks.Clear();
            backend = new FakeDeviceBackend();
            DebugInputDeviceBackends.UseProvider(backend);
            DebugEventClocks.BindOrObserveMainThread();
            sessionId = CreateSession();
        }

        [TearDown]
        public void TearDown()
        {
            // 先恢复一个宽松的后端，让清理管线真的能完成释放，不把待清理状态留给后续用例。
            backend.Reset();
            foreach (var hook in hooks)
            {
                try
                {
                    var plan = MethodHookHost.PrepareRemove(new MethodHookRemoveRequest
                    {
                        HookId = hook.HookId,
                        EditorSessionId = UnityCliBridge.EditorSessionId(),
                        ExpectedRevision = hook.Revision
                    });
                    MethodHookHost.ApplyRemove(plan);
                }
                catch (Exception)
                {
                    // 撤除失败不掩盖用例断言。
                }
            }
            foreach (var id in sessions) ReleaseSession(id);
            DebugInputHost.Reset();
            DebugInputDeviceBackends.Reset();
            DebugEventStore.Reset();
            DebugScheduler.Reset();
            DebugTriggerBudget.Reset();
            sessions.Clear();
            hooks.Clear();
        }

        // ------------------------------------------------------------------
        // 缺少依赖
        // ------------------------------------------------------------------

        [Test]
        public void UnavailableBackendRefusesKeyAndPointerBeforeAnyEffect()
        {
            backend.AvailableFlag = false;

            var key = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PrepareKey(KeyRequest("Space", "hold-ms", 250)));
            Assert.That(key.Code, Is.EqualTo("E_DEBUG_BINDING_UNSUPPORTED"), "a missing device backend must be refused, not half-served");
            Assert.That(Detail(key, "status"), Is.EqualTo(UnityCliDebugInputBackendStates.Unavailable));
            Assert.That(Detail(key, "backend"), Is.EqualTo(UnityCliDebugInputBackends.InputSystem));
            Assert.That(Detail(key, "reason"), Is.EqualTo(UnityCliDebugInputReasons.BackendNotLoaded));
            Assert.That(Convert.ToBoolean(Detail(key, "installedByThisOwner"), CultureInfo.InvariantCulture), Is.False, "this owner never installs a package");

            var pointer = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PreparePointer(new DebugInputPointerRequest
            {
                SessionId = sessionId,
                TargetSpace = "screen-pixels",
                X = 10d,
                Y = 20d
            }));
            Assert.That(pointer.Code, Is.EqualTo("E_DEBUG_BINDING_UNSUPPORTED"));

            // 拒绝路径不得留下任何设备、记录或活动资源。
            Assert.That(backend.CreateCount, Is.EqualTo(0), "a refused dispatch creates no device");
            var info = DebugInputHost.Info(new DebugInputInfoRequest { SessionId = sessionId });
            Assert.That(info.Backend.Status, Is.EqualTo(UnityCliDebugInputBackendStates.Unavailable));
            Assert.That(info.Backend.Reason, Is.EqualTo(UnityCliDebugInputReasons.BackendNotLoaded));
            Assert.That(info.Backend.InstalledByThisOwner, Is.False);
            Assert.That(info.Devices, Is.Empty);
            Assert.That(info.Holds, Is.Empty);
            Assert.That(DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId }).Budget.ActiveResourcesUsed, Is.EqualTo(0L));
        }

        // ------------------------------------------------------------------
        // 设备拥有权与结束边界
        // ------------------------------------------------------------------

        [Test]
        public void PressCreatesOneSessionOwnedDeviceAndRecordsItsOwnEndBoundary()
        {
            var result = DebugInputHost.ApplyKey(DebugInputHost.PrepareKey(KeyRequest("Space", "hold-ms", 250)));

            Assert.That(result.DeviceKind, Is.EqualTo(UnityCliDebugInputDeviceKinds.Keyboard));
            Assert.That(result.DeviceName, Is.EqualTo("unitycli-" + sessionId + "-keyboard"), "the session owns its own virtual device");
            Assert.That(result.EndBoundary, Is.EqualTo(UnityCliDebugInputEndBoundaries.HoldMs));
            Assert.That(result.HoldMs, Is.EqualTo(250L));
            Assert.That(result.ExpiresAtMonotonicMs, Is.GreaterThan(result.DispatchedAtMonotonicMs), "the dispatch carries its own end boundary");
            Assert.That(result.State, Is.EqualTo(UnityCliDebugInputHoldStates.Held));
            Assert.That(result.ReleaseState, Is.EqualTo(UnityCliDebugInputHoldStates.Held), "a pressed record is not released yet");
            Assert.That(result.ConfirmedControls, Does.Contain("Space"), "the fake backend applies the submitted state immediately");
            Assert.That(backend.CreateCount, Is.EqualTo(1), "one press creates exactly one session-owned device");
            Assert.That(backend.RemoveCount, Is.EqualTo(0), "ending the lifetime is explicit");

            var info = DebugInputHost.Info(new DebugInputInfoRequest { SessionId = sessionId });
            Assert.That(info.Devices.Count, Is.EqualTo(1));
            Assert.That(info.Devices[0].Kind, Is.EqualTo(UnityCliDebugInputDeviceKinds.Keyboard));
            Assert.That(info.Devices[0].State, Is.EqualTo(UnityCliDebugInputDeviceStates.Active));
            Assert.That(info.Holds.Count, Is.EqualTo(1));
            Assert.That(info.Budget.DevicesUsed, Is.EqualTo(1L));
            Assert.That(info.Budget.HoldsUsed, Is.EqualTo(1L));

            // 同一控制已有自己的结束边界：第二次按下在 effect 之前被拒绝。
            var conflict = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PrepareKey(KeyRequest("Space", "until-release", null)));
            Assert.That(conflict.Code, Is.EqualTo("E_DEBUG_RESOURCE_CONFLICT"));
            Assert.That(Detail(conflict, "reason"), Is.EqualTo(UnityCliDebugInputReasons.HoldConflict));
            Assert.That(backend.CreateCount, Is.EqualTo(1));
            Assert.That(backend.QueueCount, Is.EqualTo(1), "a refused press submits nothing");
        }

        [Test]
        public void HoldBoundaryIsReleasedOnTheCentralTickAndReleasesThePlanSlot()
        {
            DebugInputHost.ApplyKey(DebugInputHost.PrepareKey(KeyRequest("Space", "hold-ms", 1)));
            Assert.That(DebugTriggerBudget.CountOfKind(sessionId, DebugTriggerBudget.KindInput), Is.EqualTo(1), "a held input occupies one planned input slot");

            System.Threading.Thread.Sleep(5);
            DebugInputHost.Advance();

            var info = DebugInputHost.Info(new DebugInputInfoRequest { SessionId = sessionId });
            Assert.That(info.Holds[0].State, Is.EqualTo(UnityCliDebugInputHoldStates.Released));
            Assert.That(info.Holds[0].ReleaseReason, Is.EqualTo(UnityCliDebugInputReasons.HoldBoundaryReached));
            Assert.That(info.Holds[0].ReleaseConfirmed, Is.True);
            Assert.That(DebugTriggerBudget.CountOfKind(sessionId, DebugTriggerBudget.KindInput), Is.EqualTo(0), "a released input returns its planned slot");

            var released = DebugInputHost.ApplyRelease(DebugInputHost.PrepareRelease(new DebugInputReleaseRequest { SessionId = sessionId, Scope = "all" }));
            Assert.That(released.ReleaseState, Is.EqualTo(UnityCliDebugInputHoldStates.Released));
            Assert.That(released.Devices, Is.Empty, "scope all ends the device lifetime when the removal is confirmed");
            Assert.That(backend.RemoveCount, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------
        // 释放确认
        // ------------------------------------------------------------------

        [Test]
        public void ReleaseStaysPendingUntilTheDeviceReadbackConfirmsIt()
        {
            DebugInputHost.ApplyKey(DebugInputHost.PrepareKey(KeyRequest("Space", "until-release", null)));

            // 设备仍报告按下：释放只能报 pending，并给出精确原因。
            backend.ForcedHeld.Add("Space");
            var pending = DebugInputHost.ApplyRelease(DebugInputHost.PrepareRelease(new DebugInputReleaseRequest { SessionId = sessionId, Scope = "keys" }));
            Assert.That(pending.ReleaseState, Is.EqualTo(UnityCliDebugInputHoldStates.Pending), "an unconfirmed release is never reported as released");
            Assert.That(string.Join(";", pending.PendingReasons), Does.Contain(UnityCliDebugInputReasons.ReleaseNotApplied));
            Assert.That(pending.DeferredTo, Is.Not.Empty, "a pending release names the main-thread path that completes it");
            Assert.That(pending.DeferredTo, Is.Not.EqualTo("none"), "a registered release request has a real completion path");
            Assert.That(pending.Holds[0].ReleaseConfirmed, Is.False);

            // 中央 tick 在主线程重试确认；设备真正清除后同一记录变为 released。
            backend.ForcedHeld.Clear();
            DebugInputHost.Advance();
            var info = DebugInputHost.Info(new DebugInputInfoRequest { SessionId = sessionId });
            Assert.That(info.Holds[0].State, Is.EqualTo(UnityCliDebugInputHoldStates.Released));
            Assert.That(info.Holds[0].ReleaseConfirmed, Is.True);
            Assert.That(info.Budget.PendingReleaseRequests, Is.EqualTo(0L), "the completed release leaves no deferred request");

            var confirmed = DebugInputHost.ApplyRelease(DebugInputHost.PrepareRelease(new DebugInputReleaseRequest { SessionId = sessionId, Scope = "keys" }));
            Assert.That(confirmed.ReleaseState, Is.EqualTo(UnityCliDebugInputHoldStates.Released));
        }

        [Test]
        public void WorkerPauseDefersReleaseToTheCentralSchedulerWhileMainThreadPauseReleasesAtOnce()
        {
            DebugInputHost.ApplyKey(DebugInputHost.PrepareKey(KeyRequest("Space", "until-release", null)));

            // worker 命中：释放请求交给中央调度，记录保持 pending。
            DebugInputHost.OnPauseHit(false);
            var deferred = DebugInputHost.Info(new DebugInputInfoRequest { SessionId = sessionId });
            Assert.That(deferred.Holds[0].State, Is.EqualTo(UnityCliDebugInputHoldStates.Pending));
            Assert.That(deferred.Holds[0].ReleaseReason, Is.EqualTo(UnityCliDebugInputReasons.PauseWorkerDeferred));
            Assert.That(deferred.Budget.PendingReleaseRequests, Is.EqualTo(1L));

            DebugInputHost.Advance();
            var completed = DebugInputHost.Info(new DebugInputInfoRequest { SessionId = sessionId });
            Assert.That(completed.Holds[0].State, Is.EqualTo(UnityCliDebugInputHoldStates.Released));
            Assert.That(completed.Holds[0].ReleaseConfirmed, Is.True);

            // 主线程命中：当场释放可达的按下状态，因此暂停之前就没有残留保持。
            DebugInputHost.ApplyKey(DebugInputHost.PrepareKey(KeyRequest("W", "until-release", null)));
            DebugInputHost.OnPauseHit(true);
            Assert.That(DebugInputHost.HasUnreleasedInput(sessionId), Is.False, "a main-thread pause releases reachable holds before the thread stops");
            var afterPause = DebugInputHost.Info(new DebugInputInfoRequest { SessionId = sessionId });
            Assert.That(afterPause.Holds.Single(hold => hold.Controls.Contains("W")).ReleaseReason, Is.EqualTo(UnityCliDebugInputReasons.PauseMainThread));
        }

        [Test]
        public void SessionStopReleasesConfirmedInputAndReportsPendingWhenUnconfirmed()
        {
            DebugInputHost.ApplyKey(DebugInputHost.PrepareKey(KeyRequest("Space", "until-release", null)));
            var report = DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId })).Cleanup;
            Assert.That(report.CleanupPending, Is.False);
            Assert.That(report.ReleasedInput, Is.GreaterThanOrEqualTo(1L), "the input owner reports released input");
            Assert.That(DebugInputHost.HasUnreleasedInput(sessionId), Is.False);

            // 第二个 session：设备拒绝移除时 stop 必须如实保持待清理，而不是写成已完成。
            var second = CreateSession();
            DebugInputHost.ApplyKey(DebugInputHost.PrepareKey(KeyRequest(second, "Space", "until-release", null)));
            backend.RemoveFails = true;
            backend.ForcedHeld.Add("Space");
            var pending = DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = second })).Cleanup;
            Assert.That(pending.CleanupPending, Is.True, "an unconfirmed input release keeps the cleanup pending");
            Assert.That(DebugInputHost.HasUnreleasedInput(second), Is.True);

            backend.RemoveFails = false;
            backend.ForcedHeld.Clear();
            var completed = DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = second })).Cleanup;
            Assert.That(completed.CleanupPending, Is.False, "a repeated stop completes the same pending cleanup");
            Assert.That(DebugInputHost.HasUnreleasedInput(second), Is.False);
        }

        // ------------------------------------------------------------------
        // 冲突与坐标空间
        // ------------------------------------------------------------------

        [Test]
        public void ForeignDeviceNameAndUnreadableDeviceListAreRefusedBeforeActivation()
        {
            backend.AddForeignName("unitycli-" + sessionId + "-keyboard");
            var foreign = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PrepareKey(KeyRequest("Space", "until-release", null)));
            Assert.That(foreign.Code, Is.EqualTo("E_DEBUG_RESOURCE_CONFLICT"));
            Assert.That(Detail(foreign, "reason"), Is.EqualTo(UnityCliDebugInputReasons.DeviceNameOwnedElsewhere));
            Assert.That(backend.CreateCount, Is.EqualTo(0), "this owner never takes over another owner's device");
            Assert.That(backend.RemoveCount, Is.EqualTo(0), "this owner never removes another owner's device");

            backend.Reset();
            backend.ListFails = true;
            var unreadable = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PrepareKey(KeyRequest("Space", "until-release", null)));
            Assert.That(unreadable.Code, Is.EqualTo("E_DEBUG_BINDING_UNSUPPORTED"));
            Assert.That(Detail(unreadable, "reason"), Is.EqualTo(UnityCliDebugInputReasons.DeviceListUnreadable));
            Assert.That(Detail(unreadable, "backendReason"), Is.EqualTo("fake-list-refusal"), "the backend's raw text is diagnostics, never the public reason");
            Assert.That(backend.CreateCount, Is.EqualTo(0));
        }

        [Test]
        public void PointerResolvesItsDeclaredCoordinateSpaceAndRejectsMalformedInput()
        {
            var missingBoundary = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PreparePointer(new DebugInputPointerRequest
            {
                SessionId = sessionId,
                TargetSpace = "screen-pixels",
                X = 10d,
                Y = 10d,
                Buttons = new List<string> { "left" }
            }));
            Assert.That(missingBoundary.Code, Is.EqualTo("E_SCHEMA_INVALID"), "a press must declare its own end boundary; there is no default");
            Assert.That(Convert.ToString(Detail(missingBoundary, "path"), CultureInfo.InvariantCulture), Is.EqualTo("$.endBoundary"));

            var badSpace = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PreparePointer(new DebugInputPointerRequest
            {
                SessionId = sessionId,
                TargetSpace = "world-space",
                X = 1d,
                Y = 1d
            }));
            Assert.That(badSpace.Code, Is.EqualTo("E_SCHEMA_INVALID"));

            var outsideRange = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PreparePointer(new DebugInputPointerRequest
            {
                SessionId = sessionId,
                TargetSpace = "game-view-normalized",
                X = 2d,
                Y = 0.5d
            }));
            Assert.That(outsideRange.Code, Is.EqualTo("E_SCHEMA_INVALID"));

            var moveWithBoundary = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PreparePointer(new DebugInputPointerRequest
            {
                SessionId = sessionId,
                TargetSpace = "screen-pixels",
                X = 10d,
                Y = 10d,
                EndBoundary = "hold-ms",
                HoldMs = 50
            }));
            Assert.That(moveWithBoundary.Code, Is.EqualTo("E_SCHEMA_INVALID"), "a move holds nothing, so it must not declare an end boundary");

            var request = new DebugInputPointerRequest
            {
                SessionId = sessionId,
                TargetSpace = "game-view-normalized",
                X = 0.5d,
                Y = 0.25d,
                Buttons = new List<string> { "left" },
                EndBoundary = "until-release"
            };
            if (UnityEngine.Screen.width <= 0 || UnityEngine.Screen.height <= 0)
            {
                // 本机没有可读 Game 视图/屏幕尺寸时，产品必须在 effect 之前拒绝，绝不按假设尺寸下发。
                var refused = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugInputHost.PreparePointer(request));
                Assert.That(refused.Code, Is.EqualTo("E_DEBUG_SCOPE_UNSUPPORTED"));
                Assert.That(Detail(refused, "rule"), Is.EqualTo(UnityCliDebugInputReasons.TargetSpaceUnresolved));
                return;
            }

            var result = DebugInputHost.ApplyPointer(DebugInputHost.PreparePointer(request));
            Assert.That(result.PointerSpace, Is.EqualTo(UnityCliDebugInputPointerSpaces.GameViewNormalized));
            Assert.That(result.ResolvedWidth, Is.EqualTo((double)UnityEngine.Screen.width), "the observed size is reported instead of assumed");
            Assert.That(result.ResolvedHeight, Is.EqualTo((double)UnityEngine.Screen.height));
            Assert.That(result.PointerX, Is.EqualTo(0.5d * UnityEngine.Screen.width).Within(0.001d));
            Assert.That(result.PointerY, Is.EqualTo(0.25d * UnityEngine.Screen.height).Within(0.001d));
            Assert.That(result.ResolutionSource, Is.EqualTo("Screen.width/height"));
            Assert.That(result.ReleaseState, Is.EqualTo(UnityCliDebugInputHoldStates.Held));
            Assert.That(backend.DeviceNames.Count, Is.EqualTo(1), "the session owns exactly one pointer device");
        }

        // ------------------------------------------------------------------
        // 通用输入路径与 wrapper 现场验证
        // ------------------------------------------------------------------

        [Test]
        public void InfoReportsGenericInputRoutesWithoutLockingToALogicalFrame()
        {
            var info = DebugInputHost.Info(new DebugInputInfoRequest());
            var kinds = info.GenericRoutes.Select(route => route.Kind).ToList();
            Assert.That(kinds, Is.EquivalentTo(new[]
            {
                UnityCliDebugInputRouteKinds.BusinessEntry,
                UnityCliDebugInputRouteKinds.ManagedWrapperInjection,
                UnityCliDebugInputRouteKinds.ArbitraryCode,
                UnityCliDebugInputRouteKinds.ProgramEntry,
                UnityCliDebugInputRouteKinds.ThreadSafePointDelivery
            }));
            Assert.That(info.GenericRoutes.Select(route => route.Tool).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList(),
                Is.EquivalentTo(new[] { "debug.code-execute", "debug.hook-install", "debug.method-invoke", "debug.program-run" }));
            foreach (var route in info.GenericRoutes)
            {
                Assert.That(route.Thread, Is.Not.Empty, "every general input path must state its real thread");
                Assert.That(route.LogicalFrame, Is.Not.Empty, "every general input path must state its relation to a logical frame");
                Assert.That(route.Proof, Is.Not.Empty, "every general input path must state what proves the business read the input");
            }
            Assert.That(info.OwnershipStatement, Does.Contain("never"), "the ownership statement must state what this owner never does");
        }

        [Test]
        public void LegacyWrapperSupportRequiresAnArmedDetourAndARealInvocation()
        {
            var spec = WrapperSpec();

            var undeclared = DebugInputHost.Info(new DebugInputInfoRequest()).Legacy;
            Assert.That(undeclared.Supported, Is.False);
            Assert.That(undeclared.Reason, Is.EqualTo(UnityCliDebugInputReasons.NoWrapperDeclared));
            Assert.That(undeclared.Statement, Does.Contain("cannot drive"), "no verified path means the input cannot be driven here");

            var unresolved = DebugInputHost.Info(new DebugInputInfoRequest
            {
                Wrapper = new MethodHookTargetSpec
                {
                    AssemblyName = typeof(DebugInputTests).Assembly.GetName().Name,
                    TypeName = "UnityCli.Tests.NoSuchInputWrapper",
                    MethodName = "ReadMoveAxis",
                    ParameterTypes = new List<string>(),
                    ReturnType = "System.Single",
                    IsStatic = true
                }
            }).Legacy;
            Assert.That(unresolved.Supported, Is.False);
            Assert.That(unresolved.Reason, Is.EqualTo(UnityCliDebugInputReasons.WrapperUnresolved));
            Assert.That(unresolved.ResolutionError, Is.Not.Empty, "a failed resolution must be reported, not hidden");

            var noDetour = DebugInputHost.Info(new DebugInputInfoRequest { Wrapper = spec }).Legacy;
            Assert.That(noDetour.Supported, Is.False);
            Assert.That(noDetour.Reason, Is.EqualTo(UnityCliDebugInputReasons.NoLiveDetour));
            Assert.That(noDetour.MethodIdentity, Is.Not.Empty, "the resolved wrapper identity is reported even when it is not supported");

            // 真实 detour：安装一个 capture-only 观察，然后真的调用该 wrapper。
            MethodHookInstallResult installed;
            try
            {
                installed = MethodHookHost.ApplyInstall(new MethodHookInstallPlan
                {
                    Resolved = MethodHookTargetResolver.Resolve(spec),
                    Capture = new MethodHookCapture { Arguments = true },
                    Activation = "editor-and-play",
                    EmitHitEvents = true,
                    Label = "input-legacy-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                }, null);
            }
            catch (Exception exception)
            {
                Assert.Ignore("the method hook backend is not available in this domain: " + exception.GetType().Name);
                return;
            }
            hooks.Add(installed);

            var armedOnly = DebugInputHost.Info(new DebugInputInfoRequest { Wrapper = spec }).Legacy;
            Assert.That(armedOnly.Supported, Is.False, "an armed detour without an observed invocation is not support");
            Assert.That(armedOnly.Reason, Is.EqualTo(UnityCliDebugInputReasons.NoObservedInvocation));
            Assert.That(armedOnly.Route, Is.Not.Null, "the armed definition is matched by its exact target, not by a display identity");
            Assert.That(armedOnly.Route.HookId, Is.EqualTo(installed.HookId));
            Assert.That(armedOnly.Route.State, Is.EqualTo(MethodHookStateText.Of(MethodHookState.Armed)));

            InputWrapperProbe.ReadMoveAxis();

            var verified = DebugInputHost.Info(new DebugInputInfoRequest { Wrapper = spec }).Legacy;
            Assert.That(verified.Supported, Is.True, "an armed detour plus a real invocation verifies that wrapper on site");
            Assert.That(verified.Route.DetourVerified, Is.True);
            Assert.That(verified.Route.InvocationVerified, Is.True);
            Assert.That(verified.Route.HitCount, Is.GreaterThanOrEqualTo(1L));
            Assert.That(verified.Route.HookId, Is.EqualTo(installed.HookId));
        }

        // ------------------------------------------------------------------
        // 用例工具
        // ------------------------------------------------------------------

        /// <summary>读取稳定错误码的 details 字段；details 由实现以字典形式提供。</summary>
        private static object Detail(UnityCliBridge.BridgeCommandException exception, string key)
        {
            var details = exception.Details as Dictionary<string, object>;
            return details != null && details.TryGetValue(key, out var value) ? value : null;
        }

        private static string CreateSession()
        {
            return DebugSessionHost.ApplyCreate(DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
            {
                Label = "input-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay,
                ClockKind = UnityCliDebugClocks.Monotonic
            })).SessionId;
        }

        private static void ReleaseSession(string sessionId)
        {
            try
            {
                DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
            }
            catch
            {
                // 已停止或已删除都无需再停止。
            }
            try
            {
                DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
            }
            catch
            {
                // 定义已被删除，或仍有未完成清理时保持现状。
            }
        }

        /// <summary>本用例 session 上的一次键按下声明。</summary>
        private DebugInputKeyRequest KeyRequest(string key, string endBoundary, long? holdMs)
        {
            return KeyRequest(sessionId, key, endBoundary, holdMs);
        }

        /// <summary>指定 session 上的一次键按下声明。</summary>
        private static DebugInputKeyRequest KeyRequest(string sessionId, string key, string endBoundary, long? holdMs)
        {
            return new DebugInputKeyRequest
            {
                SessionId = sessionId,
                Keys = new List<string> { key },
                EndBoundary = endBoundary,
                HoldMs = holdMs
            };
        }

        private static MethodHookTargetSpec WrapperSpec()
        {
            return new MethodHookTargetSpec
            {
                AssemblyName = typeof(DebugInputTests).Assembly.GetName().Name,
                TypeName = typeof(InputWrapperProbe).FullName,
                MethodName = "ReadMoveAxis",
                ParameterTypes = new List<string>(),
                ReturnType = "System.Single",
                IsStatic = true
            };
        }
    }
}
