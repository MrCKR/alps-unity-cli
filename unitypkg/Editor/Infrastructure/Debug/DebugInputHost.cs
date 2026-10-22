#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEngine;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>key 的 effect 前提炼结果：验证全部通过后才允许跨过效果屏障。</summary>
    internal sealed class DebugInputKeyPlan
    {
        internal string SessionId;
        internal string Action;
        internal List<string> Keys;
        internal string EndBoundary;
        internal long HoldMs;
        internal string DeviceId;
        internal string DeviceName;
        internal string DeviceKind;
        internal string PlanId;
    }

    /// <summary>pointer 的 effect 前提炼结果；目标坐标空间的实际解析尺寸也在 effect 之前取得。</summary>
    internal sealed class DebugInputPointerPlan
    {
        internal string SessionId;
        internal string Action;
        internal List<string> Buttons;
        internal string EndBoundary;
        internal long HoldMs;
        internal string DeviceId;
        internal string DeviceName;
        internal string DeviceKind;
        internal string PlanId;
        internal string PointerSpace;
        internal double PointerX;
        internal double PointerY;
        internal double ResolvedWidth;
        internal double ResolvedHeight;
        internal string ResolutionSource;
    }

    /// <summary>release 的 effect 前提炼结果。</summary>
    internal sealed class DebugInputReleasePlan
    {
        internal string SessionId;
        internal string Scope;
    }

    /// <summary>
    /// 可选设备后端的唯一 owner：会话拥有的虚拟设备、按下记录、结束边界与释放确认。
    ///
    /// 职责：
    /// - 现场探测设备后端可用性；缺失依赖只报 unavailable，从不安装任何包。
    /// - 每个 session 最多拥有一个键设备与一个指针设备，并作为 session 资源与清理条目的一部分。
    /// - 每次下发都带独立结束边界；释放请求在主线程当场提交并读回确认，否则登记给中央 tick 补完。
    /// - 暂停、目标失效、session stop 与 Play 退出都请求释放，且只有实际确认才报 released。
    ///
    /// 规则：
    /// - 只操作本 owner 建立的设备；从不列出、reset、接管或改写真实设备与其他 owner 的设备。
    /// - 冲突（同名设备属于别人、同名控制已有不同结束边界）一律在 effect 之前拒绝。
    /// - 不注册任何 update 回调：到期边界与待主线程释放只由唯一中央 scheduler 的 tick 推进。
    /// - 机制状态只对本次 Unity 启动有效；域重载或 Play 段变化后一律作废。
    /// </summary>
    internal static partial class DebugInputHost
    {
        private const string DispatchExecutionPoint = "input-dispatch";
        private const string EventKindDispatch = "input-dispatch";
        private const string EventKindRelease = "input-release";

        /// <summary>一个会话拥有的虚拟设备及其资源槽位。</summary>
        internal sealed class DeviceRecord
        {
            internal string DeviceId;
            internal string SessionId;
            internal string Kind;
            internal string Name;
            internal string State = UnityCliDebugInputDeviceStates.Active;
            internal string ResourceId;
            internal string PlaySegmentId;
            internal string Backend;
            internal string ApiIdentity;
            internal object Handle;
            internal double CreatedAtMonotonicMs;
            internal int CreatedThreadId;
            internal string RemovalReason = string.Empty;
        }

        /// <summary>一条输入记录：动作、控制、结束边界与释放确认状态。</summary>
        internal sealed class InputRecord
        {
            internal string DispatchId;
            internal string SessionId;
            internal string DeviceId;
            internal string DeviceKind;
            internal string Action;
            internal List<string> Controls = new List<string>();
            internal string EndBoundary;
            internal long HoldMs;
            internal double ExpiresAtMonotonicMs;
            internal double DispatchedAtMonotonicMs;
            internal int DispatchedThreadId;
            internal string State = UnityCliDebugInputHoldStates.Held;
            internal string ReleaseReason = string.Empty;
            internal double ReleasedAtMonotonicMs;
            internal bool ReleaseConfirmed;
            internal string PointerSpace = string.Empty;
            internal double PointerX;
            internal double PointerY;
            internal double ResolvedWidth;
            internal double ResolvedHeight;
            internal string PlanId;
        }

        /// <summary>
        /// 等待主线程补完的释放请求；每个 session 至多一条，按位记录仍然待确认的范围。
        /// 释放是幂等的，因此同一 session 的多个范围合并到同一条请求里。
        /// </summary>
        private sealed class PendingRelease
        {
            internal string SessionId;
            internal int Scopes;
        }

        private const int ScopeBitKeys = 1;
        private const int ScopeBitPointer = 2;
        private const int ScopeBitAll = 4;

        /// <summary>一次下发的完整参数；key 与 pointer 共用同一 effect，避免两套设备与记录语义。</summary>
        private sealed class DispatchSpec
        {
            internal string SessionId;
            internal string Action;
            internal List<string> Controls;
            internal string EndBoundary;
            internal long HoldMs;
            internal string DeviceId;
            internal string DeviceName;
            internal string DeviceKind;
            internal string PlanId;
            internal string PointerSpace = string.Empty;
            internal double PointerX;
            internal double PointerY;
            internal double ResolvedWidth;
            internal double ResolvedHeight;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DeviceRecord> Devices = new Dictionary<string, DeviceRecord>(StringComparer.Ordinal);
        private static readonly List<InputRecord> Records = new List<InputRecord>();
        private static readonly Dictionary<string, PendingRelease> PendingReleases = new Dictionary<string, PendingRelease>(StringComparer.Ordinal);

        /// <summary>
        /// 已经注册过 release-input 清理条目的 session。
        /// 一个 session 的设备生命周期可以多次开始与结束，但清理条目只注册一次：它释放该 owner 在此 session 的全部输入。
        /// </summary>
        private static readonly HashSet<string> cleanupRegistered = new HashSet<string>(StringComparer.Ordinal);
        private static long nextDispatchIndex = 1L;

        /// <summary>仍处于 held 或 pending 的记录数量；命中热路径与中央 tick 用它跳过无事可做的调用。</summary>
        private static volatile int liveRecords;

        // ------------------------------------------------------------------
        // key / pointer：effect 前校验与唯一 effect
        // ------------------------------------------------------------------

        /// <summary>key 的效果前校验：session 状态、后端可用性、结束边界、键名与冲突都在这里判定。</summary>
        internal static DebugInputKeyPlan PrepareKey(DebugInputKeyRequest request)
        {
            var sessionId = request == null ? null : request.SessionId;
            if (!DebugEventClocks.BindOrObserveMainThread())
                throw OffMainThread("debug.input.key", "The device backend must be driven from the Unity main thread.");
            RequireOpenSession(sessionId);
            var keys = NormalizeControls(request == null ? null : request.Keys, "keys", true);
            var backend = RequireBackend(UnityCliDebugInputDeviceKinds.Keyboard);
            foreach (var key in keys)
            {
                if (!backend.IsKnownControl(key))
                    throw InvalidField("keys", "names of the key controls this device backend defines", key);
            }
            string endBoundary;
            long holdMs;
            ValidateBoundary(true, request == null ? null : request.EndBoundary, request == null ? null : request.HoldMs, out endBoundary, out holdMs);
            var kind = UnityCliDebugInputDeviceKinds.Keyboard;
            var deviceName = DeviceName(sessionId, kind);
            RequireNoForeignDevice(deviceName, backend);
            lock (Sync)
            {
                RequireNoHoldConflictLocked(sessionId, kind, keys);
                RequireRecordBudgetLocked(sessionId);
            }
            var planId = PlanId();
            // 计划槽位与输入共用同一 session 预算，因此在 effect 之前保留。
            RequirePlan(sessionId, planId);
            return new DebugInputKeyPlan
            {
                SessionId = sessionId,
                Action = UnityCliDebugInputActions.Press,
                Keys = keys,
                EndBoundary = endBoundary,
                HoldMs = holdMs,
                DeviceId = DeviceId(sessionId, kind),
                DeviceName = deviceName,
                DeviceKind = kind,
                PlanId = planId
            };
        }

        /// <summary>key 的唯一 effect：按需建立会话设备、提交键状态并记录结束边界。</summary>
        internal static DebugInputDispatchResult ApplyKey(DebugInputKeyPlan plan)
        {
            return Apply(new DispatchSpec
            {
                SessionId = plan.SessionId,
                Action = plan.Action,
                Controls = plan.Keys,
                EndBoundary = plan.EndBoundary,
                HoldMs = plan.HoldMs,
                DeviceId = plan.DeviceId,
                DeviceName = plan.DeviceName,
                DeviceKind = plan.DeviceKind,
                PlanId = plan.PlanId
            });
        }

        /// <summary>pointer 的效果前校验；目标坐标空间必须在 effect 之前解析成真实尺寸。</summary>
        internal static DebugInputPointerPlan PreparePointer(DebugInputPointerRequest request)
        {
            var sessionId = request == null ? null : request.SessionId;
            if (!DebugEventClocks.BindOrObserveMainThread())
                throw OffMainThread("debug.input.pointer", "The declared pointer coordinate space must be resolved on the Unity main thread.");
            RequireOpenSession(sessionId);
            var space = request == null ? null : request.TargetSpace;
            if (!UnityCliDebugInputPointerSpaces.IsDefined(space))
                throw InvalidField("targetSpace", "screen-pixels or game-view-normalized", space);
            var buttons = NormalizeButtons(request == null ? null : request.Buttons);
            string endBoundary;
            long holdMs;
            // 声明按键就是按下，必须带结束边界；不声明按键就是纯移动，不得声明边界。
            ValidateBoundary(buttons.Count != 0, request == null ? null : request.EndBoundary, request == null ? null : request.HoldMs, out endBoundary, out holdMs);
            double x;
            double y;
            double width;
            double height;
            var source = ResolvePointerPosition(space, request == null ? 0d : request.X, request == null ? 0d : request.Y, out x, out y, out width, out height);
            var kind = UnityCliDebugInputDeviceKinds.Pointer;
            var backend = RequireBackend(kind);
            var deviceName = DeviceName(sessionId, kind);
            RequireNoForeignDevice(deviceName, backend);
            lock (Sync)
            {
                RequireNoHoldConflictLocked(sessionId, kind, buttons);
                RequireRecordBudgetLocked(sessionId);
            }
            var planId = PlanId();
            // 计划槽位与输入共用同一 session 预算，因此在 effect 之前保留。
            RequirePlan(sessionId, planId);
            return new DebugInputPointerPlan
            {
                SessionId = sessionId,
                Action = buttons.Count == 0 ? UnityCliDebugInputActions.Move : UnityCliDebugInputActions.Press,
                Buttons = buttons,
                EndBoundary = endBoundary,
                HoldMs = holdMs,
                DeviceId = DeviceId(sessionId, kind),
                DeviceName = deviceName,
                DeviceKind = kind,
                PlanId = planId,
                PointerSpace = space,
                PointerX = x,
                PointerY = y,
                ResolvedWidth = width,
                ResolvedHeight = height,
                ResolutionSource = source
            };
        }

        /// <summary>pointer 的唯一 effect。</summary>
        internal static DebugInputDispatchResult ApplyPointer(DebugInputPointerPlan plan)
        {
            return Apply(new DispatchSpec
            {
                SessionId = plan.SessionId,
                Action = plan.Action,
                Controls = plan.Buttons,
                EndBoundary = plan.EndBoundary,
                HoldMs = plan.HoldMs,
                DeviceId = plan.DeviceId,
                DeviceName = plan.DeviceName,
                DeviceKind = plan.DeviceKind,
                PlanId = plan.PlanId,
                PointerSpace = plan.PointerSpace,
                PointerX = plan.PointerX,
                PointerY = plan.PointerY,
                ResolvedWidth = plan.ResolvedWidth,
                ResolvedHeight = plan.ResolvedHeight
            });
        }

        // ------------------------------------------------------------------
        // release
        // ------------------------------------------------------------------

        /// <summary>release 的 effect 前校验：session 必须存在，范围必须显式声明。</summary>
        internal static DebugInputReleasePlan PrepareRelease(DebugInputReleaseRequest request)
        {
            DebugEventClocks.BindOrObserveMainThread();
            var sessionId = request == null ? null : request.SessionId;
            if (string.IsNullOrEmpty(sessionId))
                throw InvalidField("sessionId", "the id of a debug session", "missing");
            if (DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId }).Session == null)
                throw MissingSession(sessionId);
            var scope = NormalizeString(request == null ? null : request.Scope, "scope", UnityCliDebugInputReleaseScopes.All, UnityCliDebugInputReleaseScopes.Keys, UnityCliDebugInputReleaseScopes.Pointer);
            return new DebugInputReleasePlan { SessionId = sessionId, Scope = scope };
        }

        /// <summary>
        /// 释放本 owner 在该 session 的输入。
        /// 主线程调用当场提交释放并读回确认；其他线程只能请求释放并登记给中央 tick，因此如实报 pending。
        /// </summary>
        internal static DebugInputReleaseResult ApplyRelease(DebugInputReleasePlan plan)
        {
            // 能证明在主线程就当场确认；否则只能请求释放并如实报 pending。
            var mainThread = DebugEventClocks.BindOrObserveMainThread();
            var reason = mainThread ? UnityCliDebugInputReasons.ExplicitRelease : UnityCliDebugInputReasons.RequiresMainThread;
            ReleaseSession(plan.SessionId, plan.Scope, reason, mainThread);
            return BuildReleaseResult(plan.SessionId, plan.Scope, reason);
        }

        // ------------------------------------------------------------------
        // effect 主体
        // ------------------------------------------------------------------

        private static DebugInputDispatchResult Apply(DispatchSpec spec)
        {
            var backend = DebugInputDeviceBackends.Resolve();
            var now = DebugEventClocks.MonotonicMilliseconds;
            var threadId = Thread.CurrentThread.ManagedThreadId;
            DeviceRecord device;
            string failure;
            lock (Sync) failure = EnsureDeviceLocked(spec.SessionId, spec.DeviceKind, spec.DeviceName, backend, now, threadId, out device);
            if (failure != null)
            {
                DebugTriggerBudget.Release(spec.PlanId);
                throw DeviceCreationRejection(failure, spec, backend);
            }

            var createdByThisCall = WasCreatedByThisCall(device, now);
            var queued = string.Equals(spec.DeviceKind, UnityCliDebugInputDeviceKinds.Keyboard, StringComparison.Ordinal)
                ? backend.TryQueueKeys(device.Handle, spec.Controls, out failure)
                : backend.TryQueuePointer(device.Handle, spec.Controls, spec.PointerX, spec.PointerY, out failure);
            if (!queued)
            {
                // 状态未提交：先撤掉本次刚建立的设备，再按实际残留诚实报告，绝不假装下发成功。
                string removeFailure = null;
                var removed = createdByThisCall && backend.TryRemoveDevice(device.Handle, out removeFailure);
                DebugTriggerBudget.Release(spec.PlanId);
                if (removed)
                {
                    lock (Sync) DropDeviceLocked(device);
                    throw BackendRejection(backend, "The device backend refused the submitted state, and the device created for this call was removed again.", UnityCliDebugInputReasons.DispatchRefused, failure);
                }
                if (!createdByThisCall) throw BackendRejection(backend, "The device backend refused the submitted state, so no input was dispatched.", UnityCliDebugInputReasons.DispatchRefused, failure);
                throw new BridgeCommandException(
                    "E_EXECUTION_TIMEOUT",
                    "The device backend accepted neither the submitted state nor the removal of the device created for this call, so the effect may be partial.",
                    new Dictionary<string, object>
                    {
                        { "sessionId", spec.SessionId }, { "deviceId", device.DeviceId }, { "backend", backend.Name },
                        { "reason", string.IsNullOrEmpty(failure) ? removeFailure : failure }, { "effectMayBePartial", true }
                    },
                    new[]
                    {
                        "Run tools run debug.input.info to read the devices and records this owner still reports.",
                        "Run tools run debug.input.release with scope all to request release again, then read the reported release state."
                    });
            }

            // 纯移动不保持任何控制，因此它自己就已经是已释放状态；按下记录保持直到确认释放。
            var moveOnly = spec.Controls.Count == 0;
            var record = new InputRecord
            {
                DispatchId = NextDispatchId(),
                SessionId = spec.SessionId,
                DeviceId = device.DeviceId,
                DeviceKind = spec.DeviceKind,
                Action = spec.Action,
                Controls = new List<string>(spec.Controls),
                EndBoundary = spec.EndBoundary,
                HoldMs = spec.HoldMs,
                ExpiresAtMonotonicMs = string.Equals(spec.EndBoundary, UnityCliDebugInputEndBoundaries.HoldMs, StringComparison.Ordinal) ? now + spec.HoldMs : 0d,
                DispatchedAtMonotonicMs = now,
                DispatchedThreadId = threadId,
                State = moveOnly ? UnityCliDebugInputHoldStates.Released : UnityCliDebugInputHoldStates.Held,
                ReleaseReason = moveOnly ? UnityCliDebugInputReasons.MoveOnly : string.Empty,
                ReleaseConfirmed = moveOnly,
                ReleasedAtMonotonicMs = moveOnly ? now : 0d,
                PointerSpace = spec.PointerSpace,
                PointerX = spec.PointerX,
                PointerY = spec.PointerY,
                ResolvedWidth = spec.ResolvedWidth,
                ResolvedHeight = spec.ResolvedHeight,
                PlanId = spec.PlanId
            };
            lock (Sync)
            {
                Records.Add(record);
                RefreshLiveRecordsLocked();
            }
            if (moveOnly) DebugTriggerBudget.Release(spec.PlanId);
            return DispatchResult(spec, record, now, threadId, backend);
        }

        /// <summary>
        /// 释放一个 session 的目标输入。
        /// 主线程执行提交与确认；非主线程只登记请求，记录保持 pending 直到中央 tick 在主线程确认。
        /// </summary>
        private static void ReleaseSession(string sessionId, string scope, string reason, bool mainThread)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            InputRecord[] targets;
            DeviceRecord[] devices;
            lock (Sync)
            {
                targets = Records.Where(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
                        && !string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal)
                        && ScopeMatches(scope, record.DeviceKind)).ToArray();
                devices = string.Equals(scope, UnityCliDebugInputReleaseScopes.All, StringComparison.Ordinal)
                    ? Devices.Values.Where(device => string.Equals(device.SessionId, sessionId, StringComparison.Ordinal)).ToArray()
                    : new DeviceRecord[0];
            }
            if (!mainThread)
            {
                lock (Sync)
                {
                    foreach (var record in targets)
                    {
                        if (string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal)) continue;
                        record.State = UnityCliDebugInputHoldStates.Pending;
                        record.ReleaseReason = reason;
                        record.ReleaseConfirmed = false;
                    }
                    RegisterPendingReleaseLocked(sessionId, scope);
                    RefreshLiveRecordsLocked();
                }
                return;
            }
            foreach (var record in targets) ReleaseRecord(record, reason, true);
            var backend = DebugInputDeviceBackends.Resolve();
            var now = DebugEventClocks.MonotonicMilliseconds;
            foreach (var device in devices) RemoveDevice(device, reason, now, backend);
            lock (Sync)
            {
                // 主线程已经确认过：只有这个范围确实没有未确认目标时才清除它的待补完请求。
                if (!HasUnreleasedLocked(sessionId, scope)) ClearPendingScopeLocked(sessionId, scope);
                RefreshLiveRecordsLocked();
            }
        }

        /// <summary>释放一条记录：先提交清除状态，再按设备真实读回确认；读不回就保持 pending。</summary>
        private static void ReleaseRecord(InputRecord record, string reason, bool mainThread)
        {
            if (record == null) return;
            var now = DebugEventClocks.MonotonicMilliseconds;
            if (!mainThread)
            {
                lock (Sync)
                {
                    if (string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal)) return;
                    record.State = UnityCliDebugInputHoldStates.Pending;
                    record.ReleaseReason = reason;
                    record.ReleaseConfirmed = false;
                    RegisterPendingReleaseLocked(record.SessionId, UnityCliDebugInputReleaseScopes.All);
                    RefreshLiveRecordsLocked();
                }
                return;
            }
            var backend = DebugInputDeviceBackends.Resolve();
            DeviceRecord device;
            lock (Sync) device = FindDeviceLocked(record.SessionId, record.DeviceKind);
            if (device == null)
            {
                // 设备已不存在：不可能仍保持任何按下状态。
                lock (Sync)
                {
                    ConfirmReleaseLocked(record, UnityCliDebugInputReasons.DeviceAbsent, now);
                    RefreshLiveRecordsLocked();
                }
                AppendEvent(EventKindRelease, record, UnityCliDebugInputReasons.DeviceAbsent, true, null);
                return;
            }
            string queueFailure;
            var queued = string.Equals(record.DeviceKind, UnityCliDebugInputDeviceKinds.Keyboard, StringComparison.Ordinal)
                ? backend.TryQueueKeys(device.Handle, new List<string>(), out queueFailure)
                : backend.TryQueuePointer(device.Handle, new List<string>(), record.PointerX, record.PointerY, out queueFailure);
            if (!queued)
            {
                RecordPending(record, reason, StableReason(queueFailure, UnityCliDebugInputReasons.DispatchRefused));
                return;
            }
            IList<string> held;
            string readReason;
            if (!backend.TryReadHeldControls(device.Handle, out held, out readReason))
            {
                RecordPending(record, reason, StableReason(readReason, UnityCliDebugInputReasons.HoldReadRefused));
                return;
            }
            if (held.Any(control => record.Controls.Contains(control, StringComparer.Ordinal)))
            {
                RecordPending(record, reason, UnityCliDebugInputReasons.ReleaseNotApplied);
                return;
            }
            lock (Sync) ConfirmReleaseLocked(record, reason, now);
            AppendEvent(EventKindRelease, record, reason, true, device);
        }

        /// <summary>设备级释放：移除本 owner 的设备并确认它真的不在后端设备列表里。</summary>
        private static void RemoveDevice(DeviceRecord device, string reason, double now, IUnityCliDebugInputDeviceBackend backend)
        {
            if (device == null || string.Equals(device.State, UnityCliDebugInputDeviceStates.Released, StringComparison.Ordinal)) return;
            var playExit = string.Equals(reason, UnityCliDebugInputReasons.PlayExit, StringComparison.Ordinal);
            lock (Sync)
            {
                var stillHeld = Records.Any(record => string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal)
                    && !string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal));
                if (stillHeld && !playExit) return;
            }
            string removeReason;
            if (!backend.TryRemoveDevice(device.Handle, out removeReason))
            {
                lock (Sync)
                {
                    device.State = UnityCliDebugInputDeviceStates.Active;
                    device.RemovalReason = StableReason(removeReason, UnityCliDebugInputReasons.DeviceRemovalRefused);
                    RegisterPendingReleaseLocked(device.SessionId, UnityCliDebugInputReleaseScopes.All);
                    RefreshLiveRecordsLocked();
                }
                return;
            }
            IList<string> names;
            string readReason;
            var readable = backend.TryReadDeviceNames(out names, out readReason);
            var absent = readable && !names.Contains(device.Name);
            lock (Sync)
            {
                device.RemovalReason = absent
                    ? reason
                    : readable ? UnityCliDebugInputReasons.DeviceRemovalRefused
                    : StableReason(readReason, UnityCliDebugInputReasons.DeviceListUnreadable);
                device.State = absent ? UnityCliDebugInputDeviceStates.Released : UnityCliDebugInputDeviceStates.Active;
                if (absent) Devices.Remove(device.DeviceId);
                if (!absent) RegisterPendingReleaseLocked(device.SessionId, UnityCliDebugInputReleaseScopes.All);
                RefreshLiveRecordsLocked();
            }
            if (absent && !string.IsNullOrEmpty(device.ResourceId)) DebugSessionHost.ReleaseResource(device.ResourceId);
        }

        // ------------------------------------------------------------------
        // 中央 scheduler 主线程段与生命周期
        // ------------------------------------------------------------------

        /// <summary>
        /// 中央 tick 的主线程段：补完等待主线程的释放请求，并关闭已到达结束边界的按下记录。
        /// 只在有活跃记录时才做任何工作；它不注册 update 回调，也不建立第二条队列。
        /// </summary>
        internal static void Advance()
        {
            if (liveRecords == 0 && PendingReleases.Count == 0) return;
            DebugEventClocks.BindOrObserveMainThread();
            string[] sessions;
            lock (Sync) sessions = PendingReleases.Keys.ToArray();
            foreach (var sessionId in sessions)
            {
                foreach (var scope in PendingScopesOf(sessionId))
                {
                    ReleaseSession(sessionId, scope, UnityCliDebugInputReasons.RequiresMainThread, true);
                }
            }
            var now = DebugEventClocks.MonotonicMilliseconds;
            InputRecord[] due;
            lock (Sync)
            {
                due = Records.Where(record => string.Equals(record.State, UnityCliDebugInputHoldStates.Held, StringComparison.Ordinal)
                    && string.Equals(record.EndBoundary, UnityCliDebugInputEndBoundaries.HoldMs, StringComparison.Ordinal)
                    && now >= record.ExpiresAtMonotonicMs).ToArray();
            }
            foreach (var record in due) ReleaseRecord(record, UnityCliDebugInputReasons.HoldBoundaryReached, true);
        }

        /// <summary>
        /// 一次暂停命中即将停住命中线程：主线程命中当场释放可达的按下状态，worker 命中把释放请求交给中央调度。
        /// </summary>
        internal static void OnPauseHit(bool mainThread)
        {
            if (liveRecords == 0) return;
            string[] sessions;
            lock (Sync)
            {
                sessions = Records.Where(record => !string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal))
                    .Select(record => record.SessionId).Distinct(StringComparer.Ordinal).ToArray();
            }
            foreach (var sessionId in sessions)
            {
                ReleaseSession(sessionId, UnityCliDebugInputReleaseScopes.All,
                    mainThread ? UnityCliDebugInputReasons.PauseMainThread : UnityCliDebugInputReasons.PauseWorkerDeferred, mainThread);
            }
        }

        /// <summary>一个 Play 段结束：该段建立的设备与按下状态一并请求释放；无法确认的部分保持 pending。</summary>
        internal static void OnPlayExit(string playSegmentId)
        {
            if (string.IsNullOrEmpty(playSegmentId)) return;
            string[] sessions;
            lock (Sync)
            {
                sessions = Devices.Values.Where(device => string.Equals(device.PlaySegmentId, playSegmentId, StringComparison.Ordinal))
                    .Select(device => device.SessionId).Distinct(StringComparer.Ordinal).ToArray();
            }
            foreach (var sessionId in sessions)
                ReleaseSession(sessionId, UnityCliDebugInputReleaseScopes.All, UnityCliDebugInputReasons.PlayExit, DebugEventClocks.BindOrObserveMainThread());
        }

        /// <summary>本次 Unity 启动的输入机制状态作废；域重载与测试复位共用这里。</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                Devices.Clear();
                Records.Clear();
                PendingReleases.Clear();
                cleanupRegistered.Clear();
                nextDispatchIndex = 1L;
                liveRecords = 0;
            }
        }

        /// <summary>该 session 是否仍有未确认释放的记录或设备；清理条目用它判断待清理。</summary>
        internal static bool HasUnreleasedInput(string sessionId)
        {
            lock (Sync)
            {
                return Records.Any(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
                        && !string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal))
                    || Devices.Values.Any(device => string.Equals(device.SessionId, sessionId, StringComparison.Ordinal));
            }
        }

        // ------------------------------------------------------------------
        // 设备建立、资源登记与内部小工具
        // ------------------------------------------------------------------

        /// <summary>按需建立本 session 的虚拟设备：先保留资源槽位与清理条目，再让后端建立设备。</summary>
        private static string EnsureDeviceLocked(string sessionId, string kind, string deviceName, IUnityCliDebugInputDeviceBackend backend, double now, int threadId, out DeviceRecord device)
        {
            device = FindDeviceLocked(sessionId, kind);
            if (device != null) return null;
            var reservation = DebugSessionHost.ReserveResource(sessionId, UnityCliDebugResourceKinds.Input, deviceName, report => ReleaseResourceCleanup(sessionId), true);
            if (!reservation.Success) return "resource:" + reservation.Failure;
            if (!cleanupRegistered.Contains(sessionId))
            {
                var cleanupFailure = DebugSessionHost.RegisterCleanupStep(sessionId, UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.ReleaseInput),
                    "release-input", UnityCliDebugFacilityOwners.Input, report => ReleaseStep(sessionId, report), true);
                if (cleanupFailure != null)
                {
                    DebugSessionHost.ReleaseResource(reservation.Resource.ResourceId);
                    return "cleanup-step:" + cleanupFailure;
                }
                cleanupRegistered.Add(sessionId);
            }
            object handle;
            string reason;
            if (!backend.TryCreateDevice(DebugInputBackendInputSystem.LayoutOf(kind), deviceName, out handle, out reason))
            {
                DebugSessionHost.ReleaseResource(reservation.Resource.ResourceId);
                return "backend:" + reason;
            }
            device = new DeviceRecord
            {
                DeviceId = DeviceId(sessionId, kind),
                SessionId = sessionId,
                Kind = kind,
                Name = deviceName,
                ResourceId = reservation.Resource.ResourceId,
                PlaySegmentId = DebugSessionHost.RuntimeId(),
                Backend = backend.Name,
                ApiIdentity = backend.ApiIdentity,
                Handle = handle,
                CreatedAtMonotonicMs = now,
                CreatedThreadId = threadId
            };
            Devices[device.DeviceId] = device;
            DebugSessionHost.AttachResource(device.ResourceId);
            return null;
        }

        /// <summary>
        /// session 资源槽位的清理委托：释放该 session 的输入，并在全部确认释放后返回 true 使槽位真正被释放。
        /// 未确认时返回 false，让清理管线如实保持 cleanupPending。
        /// </summary>
        private static bool ReleaseResourceCleanup(string sessionId)
        {
            ReleaseSession(sessionId, UnityCliDebugInputReleaseScopes.All, UnityCliDebugInputReasons.SessionStop, DebugEventClocks.BindOrObserveMainThread());
            return !HasUnreleasedInput(sessionId);
        }

        /// <summary>
        /// session 清理条目：释放该 owner 的全部按下状态与设备，并记录已释放输入数。
        /// 无法确认释放时返回 false，让清理管线如实保持 cleanupPending 而不是写成已完成。
        /// </summary>
        private static bool ReleaseStep(string sessionId, UnityCliDebugCleanupReport report)
        {
            ReleaseSession(sessionId, UnityCliDebugInputReleaseScopes.All, UnityCliDebugInputReasons.SessionStop, DebugEventClocks.BindOrObserveMainThread());
            if (report != null) report.ReleasedInput++;
            return !HasUnreleasedInput(sessionId);
        }

        private static void DropDeviceLocked(DeviceRecord device)
        {
            if (device == null) return;
            Devices.Remove(device.DeviceId);
            if (!string.IsNullOrEmpty(device.ResourceId)) DebugSessionHost.ReleaseResource(device.ResourceId);
        }

        private static DeviceRecord FindDeviceLocked(string sessionId, string kind)
        {
            DeviceRecord device;
            return Devices.TryGetValue(DeviceId(sessionId, kind), out device) ? device : null;
        }

        /// <summary>同名设备必须不存在，或者正是本 owner 自己在用的设备；绝不接管别人的设备。</summary>
        private static void RequireNoForeignDevice(string deviceName, IUnityCliDebugInputDeviceBackend backend)
        {
            IList<string> names;
            string reason;
            if (!backend.TryReadDeviceNames(out names, out reason))
                throw BackendRejection(backend, "The device list could not be read, so this owner cannot prove it would not collide with an existing device.",
                    UnityCliDebugInputReasons.DeviceListUnreadable, reason);
            if (!names.Contains(deviceName)) return;
            lock (Sync)
            {
                foreach (var device in Devices.Values)
                {
                    if (string.Equals(device.Name, deviceName, StringComparison.Ordinal)
                        && string.Equals(device.State, UnityCliDebugInputDeviceStates.Active, StringComparison.Ordinal)) return;
                }
            }
            throw new BridgeCommandException(
                "E_DEBUG_RESOURCE_CONFLICT",
                "A device with this owner's exact target name already exists and was not created by this debug owner, so the dispatch was refused before any effect.",
                new Dictionary<string, object>
                {
                    { "deviceName", deviceName }, { "backend", backend.Name },
                    { "reason", UnityCliDebugInputReasons.DeviceNameOwnedElsewhere }
                },
                new[]
                {
                    "Do not reset or remove that device: this owner never takes over another owner's device.",
                    "Run tools run debug.input.info to read the devices this debug owner really owns.",
                    "Release the conflicting device in the runtime that created it, then dispatch again."
                });
        }

        /// <summary>同名控制上已有未释放记录时拒绝按下：只允许一个结束边界决定何时释放。</summary>
        private static void RequireNoHoldConflictLocked(string sessionId, string kind, List<string> controls)
        {
            foreach (var record in Records)
            {
                if (!string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)) continue;
                if (!string.Equals(record.DeviceKind, kind, StringComparison.Ordinal)) continue;
                if (string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal)) continue;
                if (!record.Controls.Any(control => controls.Contains(control, StringComparer.Ordinal))) continue;
                throw new BridgeCommandException(
                    "E_DEBUG_RESOURCE_CONFLICT",
                    "The device already holds one of the requested controls under its own end boundary, so the dispatch was refused before any effect.",
                    new Dictionary<string, object>
                    {
                        { "sessionId", sessionId }, { "deviceKind", kind }, { "dispatchId", record.DispatchId },
                        { "heldControls", record.Controls }, { "existingEndBoundary", record.EndBoundary },
                        { "reason", UnityCliDebugInputReasons.HoldConflict }
                    },
                    new[]
                    {
                        "Run tools run debug.input.release with the matching scope to release the current hold, then dispatch again.",
                        "Run tools run debug.input.info to read the retained records and their declared end boundaries."
                    });
            }
        }

        /// <summary>记录预算：可以淘汰最旧的已释放记录，但绝不淘汰仍按下或待确认的记录。</summary>
        private static void RequireRecordBudgetLocked(string sessionId)
        {
            var mine = Records.Where(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)).ToList();
            if (mine.Count < DebugLimits.MaxInputHoldsPerSession) return;
            var evictable = mine.FirstOrDefault(record => string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal));
            if (evictable != null)
            {
                Records.Remove(evictable);
                RefreshLiveRecordsLocked();
                return;
            }
            throw new BridgeCommandException(
                "E_DEBUG_LIMIT_EXCEEDED",
                "The retained input record budget of this session is fully used by records that are still held or pending release.",
                new Dictionary<string, object>
                {
                    { "limit", "input-records" }, { "used", (long)mine.Count },
                    { "max", (long)DebugLimits.MaxInputHoldsPerSession }, { "sessionId", sessionId }
                },
                new[]
                {
                    "Run tools run debug.input.release to release the current holds, then dispatch again.",
                    "Run tools run debug.input.info to read the retained records and their release states."
                });
        }

        /// <summary>计划槽位：输入与 listener 共用同一 session 预算，超限在 effect 之前拒绝。</summary>
        private static void RequirePlan(string sessionId, string planId)
        {
            int used;
            var failure = DebugTriggerBudget.TryPlan(sessionId, DebugTriggerBudget.KindInput, planId, out used);
            if (failure == null) return;
            throw new BridgeCommandException(
                "E_DEBUG_TRIGGER_BUDGET_EXHAUSTED",
                "The planned injection and input budget of this session is exhausted, so the dispatch was refused before any effect.",
                new Dictionary<string, object>
                {
                    { "sessionId", sessionId }, { "reason", failure }, { "used", (long)used },
                    { "max", (long)DebugLimits.MaxPlannedInjectionsPerSession }
                },
                new[]
                {
                    "Run tools run debug.input.release to release inputs this owner no longer needs.",
                    "Run tools run debug.hook.remove to release listeners that are no longer needed.",
                    "Run tools run debug.session.info to read handles.listeners and handles.injections."
                });
        }

        private static void RegisterPendingReleaseLocked(string sessionId, string scope)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            var bit = ScopeBit(scope);
            PendingRelease existing;
            if (PendingReleases.TryGetValue(sessionId, out existing))
            {
                existing.Scopes |= bit;
                if ((existing.Scopes & ScopeBitAll) != 0) existing.Scopes = ScopeBitAll;
                return;
            }
            if (PendingReleases.Count >= DebugLimits.MaxInputPendingReleases) return;
            PendingReleases[sessionId] = new PendingRelease { SessionId = sessionId, Scopes = bit };
        }

        /// <summary>清除已经确认完成的范围位；没有剩余范围时删除整条请求。</summary>
        private static void ClearPendingScopeLocked(string sessionId, string scope)
        {
            PendingRelease existing;
            if (!PendingReleases.TryGetValue(sessionId, out existing)) return;
            existing.Scopes &= ~ScopeBit(scope);
            if (existing.Scopes == 0) PendingReleases.Remove(sessionId);
        }

        /// <summary>一个 session 当前待补完的范围；all 位覆盖其他位。</summary>
        private static List<string> PendingScopesOf(string sessionId)
        {
            lock (Sync)
            {
                var scopes = new List<string>();
                PendingRelease existing;
                if (!PendingReleases.TryGetValue(sessionId, out existing)) return scopes;
                if ((existing.Scopes & ScopeBitAll) != 0)
                {
                    scopes.Add(UnityCliDebugInputReleaseScopes.All);
                    return scopes;
                }
                if ((existing.Scopes & ScopeBitKeys) != 0) scopes.Add(UnityCliDebugInputReleaseScopes.Keys);
                if ((existing.Scopes & ScopeBitPointer) != 0) scopes.Add(UnityCliDebugInputReleaseScopes.Pointer);
                return scopes;
            }
        }

        private static int ScopeBit(string scope)
        {
            if (string.Equals(scope, UnityCliDebugInputReleaseScopes.All, StringComparison.Ordinal)) return ScopeBitAll;
            return string.Equals(scope, UnityCliDebugInputReleaseScopes.Keys, StringComparison.Ordinal) ? ScopeBitKeys : ScopeBitPointer;
        }

        private static void ConfirmReleaseLocked(InputRecord record, string reason, double now)
        {
            if (record == null || string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal)) return;
            record.State = UnityCliDebugInputHoldStates.Released;
            record.ReleaseReason = reason;
            record.ReleaseConfirmed = true;
            record.ReleasedAtMonotonicMs = now;
            DebugTriggerBudget.Release(record.PlanId);
        }

        private static void RecordPending(InputRecord record, string reason, string detail)
        {
            lock (Sync)
            {
                if (record == null || string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal)) return;
                record.State = UnityCliDebugInputHoldStates.Pending;
                record.ReleaseReason = string.IsNullOrEmpty(detail) ? reason : reason + ":" + detail;
                record.ReleaseConfirmed = false;
                // 未确认的释放必须由中央 tick 继续补完：登记请求而不是让它停在那里。
                RegisterPendingReleaseLocked(record.SessionId, ScopeOf(record.DeviceKind));
                RefreshLiveRecordsLocked();
            }
        }

        private static string ScopeOf(string deviceKind)
        {
            return string.Equals(deviceKind, UnityCliDebugInputDeviceKinds.Keyboard, StringComparison.Ordinal)
                ? UnityCliDebugInputReleaseScopes.Keys
                : UnityCliDebugInputReleaseScopes.Pointer;
        }

        private static void RefreshLiveRecordsLocked()
        {
            liveRecords = Records.Count(record => !string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal));
        }

        private static bool HasUnreleasedLocked(string sessionId, string scope)
        {
            return Records.Any(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
                    && !string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal)
                    && ScopeMatches(scope, record.DeviceKind))
                || string.Equals(scope, UnityCliDebugInputReleaseScopes.All, StringComparison.Ordinal)
                    && Devices.Values.Any(device => string.Equals(device.SessionId, sessionId, StringComparison.Ordinal));
        }

        private static bool ScopeMatches(string scope, string deviceKind)
        {
            if (string.Equals(scope, UnityCliDebugInputReleaseScopes.All, StringComparison.Ordinal)) return true;
            if (string.Equals(scope, UnityCliDebugInputReleaseScopes.Keys, StringComparison.Ordinal))
                return string.Equals(deviceKind, UnityCliDebugInputDeviceKinds.Keyboard, StringComparison.Ordinal);
            return string.Equals(deviceKind, UnityCliDebugInputDeviceKinds.Pointer, StringComparison.Ordinal);
        }

        private static bool WasCreatedByThisCall(DeviceRecord device, double now)
        {
            return device != null && Math.Abs(device.CreatedAtMonotonicMs - now) < 0.001d;
        }

        private static string DeviceId(string sessionId, string kind)
        {
            return (sessionId ?? string.Empty) + "/" + kind;
        }

        private static string DeviceName(string sessionId, string kind)
        {
            return UnityCliBridge.BoundedText("unitycli-" + (sessionId ?? string.Empty) + "-" + kind, DebugLimits.MaxInputNameBytes);
        }

        private static string PlanId()
        {
            return "input:" + Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        private static string NextDispatchId()
        {
            var index = nextDispatchIndex;
            nextDispatchIndex++;
            return "in-" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 解析目标坐标空间：两个空间都以当前 Game 视图/屏幕尺寸为基准，并在 effect 之前固定尺寸。
        /// 没有可读尺寸时明确拒绝，绝不用假设的尺寸下发。
        /// </summary>
        private static string ResolvePointerPosition(string space, double x, double y, out double resolvedX, out double resolvedY, out double width, out double height)
        {
            width = UnityEngine.Screen.width;
            height = UnityEngine.Screen.height;
            resolvedX = 0d;
            resolvedY = 0d;
            if (width <= 0d || height <= 0d)
                throw new BridgeCommandException(
                    "E_DEBUG_SCOPE_UNSUPPORTED",
                    "No Game view or screen size is readable, so the declared pointer coordinate space cannot be resolved and nothing was dispatched.",
                    new Dictionary<string, object> { { "rule", UnityCliDebugInputReasons.TargetSpaceUnresolved }, { "targetSpace", space }, { "screenWidth", width }, { "screenHeight", height } },
                    new[]
                    {
                        "Open a Game view that reports a size, then submit the pointer again.",
                        "Run tools run debug.input.info to read the backend status before retrying."
                    });
            resolvedX = x;
            resolvedY = y;
            if (string.Equals(space, UnityCliDebugInputPointerSpaces.GameViewNormalized, StringComparison.Ordinal))
            {
                if (resolvedX < 0d || resolvedX > 1d || resolvedY < 0d || resolvedY > 1d)
                    throw InvalidField("x", "a normalized position within 0 and 1 for game-view-normalized", resolvedX.ToString("R", CultureInfo.InvariantCulture) + "/" + resolvedY.ToString("R", CultureInfo.InvariantCulture));
                resolvedX *= width;
                resolvedY *= height;
            }
            return "Screen.width/height";
        }

        /// <summary>把下发与释放事实写入唯一调试事件存储；来源是 trigger，因此不需要第二套真值。</summary>
        private static void AppendEvent(string kind, InputRecord record, string reason, bool confirmed, DeviceRecord device)
        {
            try
            {
                var value = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "dispatchId", record.DispatchId },
                    { "deviceId", record.DeviceId },
                    { "deviceKind", record.DeviceKind },
                    { "action", record.Action },
                    { "controls", new List<object>(record.Controls.Cast<object>()) },
                    { "endBoundary", record.EndBoundary },
                    { "holdMs", record.HoldMs },
                    { "expiresAtMonotonicMs", record.ExpiresAtMonotonicMs },
                    { "dispatchedAtMonotonicMs", record.DispatchedAtMonotonicMs },
                    { "dispatchThreadId", (long)record.DispatchedThreadId },
                    { "executionThreadId", (long)Thread.CurrentThread.ManagedThreadId },
                    { "holdState", record.State },
                    { "releaseReason", reason ?? string.Empty },
                    { "releaseConfirmed", confirmed },
                    { "mainThread", DebugEventClocks.IsCurrentThreadMainThread() }
                };
                if (device != null)
                {
                    value["deviceName"] = device.Name;
                    value["backend"] = device.Backend;
                    value["apiIdentity"] = device.ApiIdentity;
                }
                if (!string.IsNullOrEmpty(record.PointerSpace))
                {
                    value["pointerSpace"] = record.PointerSpace;
                    value["pointerX"] = record.PointerX;
                    value["pointerY"] = record.PointerY;
                    value["resolvedWidth"] = record.ResolvedWidth;
                    value["resolvedHeight"] = record.ResolvedHeight;
                }
                DebugEventStore.Append(new UnityCliDebugEvent
                {
                    SourceId = UnityCliDebugSources.Trigger,
                    Kind = kind,
                    ExecutionPoint = DispatchExecutionPoint,
                    Name = record.Action,
                    Value = value,
                    ClockKind = UnityCliDebugClocks.Monotonic,
                    ClockValue = DebugEventClocks.MonotonicMilliseconds,
                    Frame = DebugEventClocks.LastObservedMainFrame,
                    ThreadId = Thread.CurrentThread.ManagedThreadId,
                    MainThread = DebugEventClocks.IsCurrentThreadMainThread()
                });
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_input_event_failed " + exception.GetType().Name);
            }
        }
    }
}
#endif
