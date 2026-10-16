#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 输入域的读取投影与校验：后端可用性、通用输入路径、候选 wrapper 的现场验证、设备与记录视图，
    /// 以及全部稳定错误码的映射。写入路径的真实 effect 只在 DebugInputHost 里。
    /// </summary>
    internal static partial class DebugInputHost
    {
        /// <summary>读取后端可用性、通用输入路径、候选 wrapper 的现场验证结果与本 owner 的设备与记录。</summary>
        internal static DebugInputInfoResult Info(DebugInputInfoRequest request)
        {
            var backend = DebugInputDeviceBackends.Resolve();
            var sessionId = request == null ? null : request.SessionId;
            if (!string.IsNullOrEmpty(sessionId) && DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId }).Session == null)
                throw MissingSession(sessionId);
            return new DebugInputInfoResult
            {
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                RuntimeId = DebugSessionHost.RuntimeId(),
                DomainGeneration = DebugSessionHost.DomainGeneration(),
                Backend = BackendView(backend),
                GenericRoutes = GenericRoutes(),
                Legacy = DescribeLegacy(request == null ? null : request.Wrapper),
                Devices = DevicesFor(sessionId).Select(DeviceView).ToList(),
                Holds = RecordsFor(sessionId).Select(HoldView).ToList(),
                Budget = BudgetView(sessionId),
                OwnershipStatement = "This owner only drives virtual devices it created for the requested session. It never lists, resets, rewrites or takes over a real device or another owner's device, and it never installs a package."
            };
        }

        /// <summary>
        /// 通用输入路径：它们不依赖任何输入包，也不受单一逻辑帧约束。
        /// key/pointer 只是可选设备后端，不替代这些路径。
        /// </summary>
        private static List<DebugInputRouteView> GenericRoutes()
        {
            return new List<DebugInputRouteView>
            {
                new DebugInputRouteView
                {
                    Kind = UnityCliDebugInputRouteKinds.BusinessEntry,
                    Tool = "debug.method.invoke",
                    Thread = "the entry runs synchronously on the calling Unity main thread",
                    LogicalFrame = "no logical frame is required: the entry is called between frames",
                    Proof = "the declared completionProbe plus a fresh read Tool readback"
                },
                new DebugInputRouteView
                {
                    Kind = UnityCliDebugInputRouteKinds.ManagedWrapperInjection,
                    Tool = "debug.hook.install",
                    Thread = "the callback runs synchronously on the thread that calls the intercepted wrapper",
                    LogicalFrame = "the wrapper call itself is the boundary, so no logical frame is required",
                    Proof = "the recorded hook event proves the wrapper really ran; parameter replacement, result replacement and skipping the original method are reported by the hook itself"
                },
                new DebugInputRouteView
                {
                    Kind = UnityCliDebugInputRouteKinds.ArbitraryCode,
                    Tool = "debug.code.execute",
                    Thread = "the code runs synchronously on the Unity main thread",
                    LogicalFrame = "no logical frame is required",
                    Proof = "the returned value plus a later readback; a return value is never acceptance proof by itself"
                },
                new DebugInputRouteView
                {
                    Kind = UnityCliDebugInputRouteKinds.ProgramEntry,
                    Tool = "debug.program.run",
                    Thread = "the declared execution site decides the thread: the central editor tick or a named PlayerLoop phase",
                    LogicalFrame = "the step driver can wait on frames, time, a signal or a condition instead of being tied to one logical frame",
                    Proof = "named context.Check results read back by debug.run.begin and debug.run.end"
                },
                new DebugInputRouteView
                {
                    Kind = UnityCliDebugInputRouteKinds.ThreadSafePointDelivery,
                    Tool = "debug.program.run",
                    Thread = "the delivery executes on the bound exact target or the declared framework phase, never on a different thread",
                    LogicalFrame = "deliveries are bounded, single-winner and expire instead of being replayed in a later frame",
                    Proof = "the delivery fact plus the program check observed at that execution point"
                }
            };
        }

        /// <summary>
        /// 候选 managed wrapper 的现场验证：只有真实 detour 加真实调用才算支持。
        /// 匹配按 exact target key 进行；没有可信路径时明确说明这个输入无法驱动，不用项目特例补成支持。
        /// </summary>
        private static DebugInputLegacyView DescribeLegacy(MethodHookTargetSpec spec)
        {
            var view = new DebugInputLegacyView
            {
                Supported = false,
                DriveWindows = new List<string>
                {
                    "debug.hook.install with a prefix callback can replace arguments or skip the original method on the hit thread",
                    "debug.hook.install with a postfix callback can replace the returned value",
                    "debug.method.invoke can call the business input entry directly",
                    "debug.program.run can deliver a bounded signal to that exact target"
                }
            };
            if (spec == null)
            {
                view.Reason = UnityCliDebugInputReasons.NoWrapperDeclared;
                view.Statement = "No candidate managed wrapper was declared, so this owner declares no Legacy input support: without a real wrapper it cannot drive that input, and it does not use project-specific shortcuts to pretend otherwise.";
                return view;
            }
            MethodHookResolvedTarget resolved;
            try
            {
                resolved = MethodHookTargetResolver.Resolve(spec);
            }
            catch (BridgeCommandException exception)
            {
                view.Reason = UnityCliDebugInputReasons.WrapperUnresolved;
                view.ResolutionError = exception.Code + ": " + UnityCliBridge.BoundedText(exception.Message, DebugLimits.MaxDiagnosticTextBytes);
                view.Statement = "The declared wrapper does not resolve to an interceptable managed method in this domain, so this owner cannot drive that input and declares no Legacy input support.";
                return view;
            }
            var identity = MethodHookMethodText.Describe(resolved.Method);
            view.MethodIdentity = identity;
            MethodHookListItem best = null;
            try
            {
                // 只按 exact target key 匹配：公开 identity 文本不区分重载，用它匹配会挑错现场。
                var list = MethodHookHost.ListByTargetKey(MethodHookTargetKey.From(resolved.Method).Text);
                foreach (var item in list ?? new List<MethodHookListItem>())
                {
                    if (best == null || item.HitCount > best.HitCount) best = item;
                }
            }
            catch (BridgeCommandException exception)
            {
                view.ResolutionError = exception.Code + ": " + UnityCliBridge.BoundedText(exception.Message, DebugLimits.MaxDiagnosticTextBytes);
            }
            if (best == null)
            {
                view.Reason = UnityCliDebugInputReasons.NoLiveDetour;
                view.Statement = "No live detour exists on the declared wrapper, so this owner cannot prove that wrapper is interceptable and declares no Legacy input support.";
                return view;
            }
            ApplyLegacyVerdict(best, view);
            return view;
        }

        /// <summary>
        /// 把一个 exact target 上的现场事实判定为 Legacy 支持、或者记下缺失的那个条件。
        /// 冻结规则：只有 armed detour 加一次真实观察到的调用才算支持；
        /// 两个条件缺一不可，缺哪个就报哪个。
        /// </summary>
        private static void ApplyLegacyVerdict(MethodHookListItem best, DebugInputLegacyView view)
        {
            var armed = string.Equals(best.State, MethodHookStateText.Of(MethodHookState.Armed), StringComparison.Ordinal);
            var invoked = best.HitCount > 0L;
            view.Route = new DebugInputLegacyRouteView
            {
                HookId = best.HookId,
                SessionId = best.EditorSessionId,
                State = best.State,
                HitCount = best.HitCount,
                MethodIdentity = best.MethodIdentity,
                DetourVerified = armed,
                InvocationVerified = invoked
            };
            if (!armed)
            {
                view.Reason = UnityCliDebugInputReasons.NoLiveDetour;
                view.Statement = "The declared wrapper has a definition but no armed detour in this domain, so this owner declares no Legacy input support.";
                return;
            }
            if (!invoked)
            {
                view.Reason = UnityCliDebugInputReasons.NoObservedInvocation;
                view.Statement = "The declared wrapper has an armed detour but no observed invocation, so this owner does not claim that this input can be driven: a wrapper that is inlined or never called cannot be proven to work.";
                return;
            }
            view.Supported = true;
            view.Reason = string.Empty;
            view.Statement = "The declared managed wrapper was verified on site: the detour is armed on that exact target and at least one real invocation was observed, so this owner declares that wrapper path supported.";
        }

        // ------------------------------------------------------------------
        // 视图投影
        // ------------------------------------------------------------------

        private static DebugInputBackendView BackendView(IUnityCliDebugInputDeviceBackend backend)
        {
            return new DebugInputBackendView
            {
                Name = string.IsNullOrEmpty(backend.Name) ? UnityCliDebugInputBackends.None : backend.Name,
                Status = backend.Available ? UnityCliDebugInputBackendStates.Available : UnityCliDebugInputBackendStates.Unavailable,
                Reason = backend.Available ? string.Empty : backend.UnavailableReason,
                Detail = backend.Available ? string.Empty : backend.UnavailableDetail,
                ApiIdentity = backend.ApiIdentity,
                MissingApiMembers = backend.MissingApiMembers.ToList(),
                InstalledByThisOwner = false,
                Layouts = backend.Available ? "Keyboard, Mouse" : string.Empty
            };
        }

        private static DebugInputDeviceView DeviceView(DeviceRecord device)
        {
            return new DebugInputDeviceView
            {
                DeviceId = device.DeviceId,
                Kind = device.Kind,
                Name = device.Name,
                State = device.State,
                Backend = device.Backend,
                ApiIdentity = device.ApiIdentity,
                ResourceId = device.ResourceId,
                PlaySegmentId = device.PlaySegmentId,
                CreatedAtMonotonicMs = device.CreatedAtMonotonicMs,
                CreatedThreadId = device.CreatedThreadId,
                RemovalReason = device.RemovalReason
            };
        }

        /// <summary>记录视图；是否已被设备真实报告为按下由现场读回决定，绝不由下发事实推断。</summary>
        private static DebugInputHoldView HoldView(InputRecord record)
        {
            var backend = DebugInputDeviceBackends.Resolve();
            DeviceRecord device;
            lock (Sync) device = FindDeviceLocked(record.SessionId, record.DeviceKind);
            var confirmed = new List<string>();
            var confirmReason = string.Empty;
            if (device != null && backend.Available)
            {
                IList<string> held;
                if (!backend.TryReadHeldControls(device.Handle, out held, out confirmReason)) held = new List<string>();
                confirmed = held.ToList();
            }
            else if (!backend.Available)
            {
                confirmReason = backend.UnavailableReason;
            }
            return new DebugInputHoldView
            {
                DispatchId = record.DispatchId,
                SessionId = record.SessionId,
                DeviceId = record.DeviceId,
                DeviceKind = record.DeviceKind,
                Action = record.Action,
                Controls = new List<string>(record.Controls),
                EndBoundary = record.EndBoundary,
                HoldMs = record.HoldMs,
                ExpiresAtMonotonicMs = record.ExpiresAtMonotonicMs,
                DispatchedAtMonotonicMs = record.DispatchedAtMonotonicMs,
                DispatchedThreadId = record.DispatchedThreadId,
                State = record.State,
                ReleaseReason = record.ReleaseReason,
                ReleasedAtMonotonicMs = record.ReleasedAtMonotonicMs,
                ReleaseConfirmed = record.ReleaseConfirmed,
                PointerSpace = record.PointerSpace,
                PointerX = record.PointerX,
                PointerY = record.PointerY,
                ResolvedWidth = record.ResolvedWidth,
                ResolvedHeight = record.ResolvedHeight,
                ConfirmedControls = confirmed,
                ConfirmReason = StableReason(confirmReason, UnityCliDebugInputReasons.HoldReadRefused)
            };
        }

        private static DebugInputBudgetView BudgetView(string sessionId)
        {
            var scope = string.IsNullOrEmpty(sessionId) ? null : sessionId;
            long holds;
            long devices;
            long pending;
            lock (Sync)
            {
                holds = Records.Count(record => scope == null || string.Equals(record.SessionId, scope, StringComparison.Ordinal));
                devices = Devices.Values.Count(device => scope == null || string.Equals(device.SessionId, scope, StringComparison.Ordinal));
                pending = PendingReleases.Count(entry => scope == null || string.Equals(entry.Key, scope, StringComparison.Ordinal));
            }
            return new DebugInputBudgetView
            {
                DevicesUsed = devices,
                MaxDevicesPerSession = DebugLimits.MaxInputDevicesPerSession,
                HoldsUsed = holds,
                MaxHoldsPerSession = DebugLimits.MaxInputHoldsPerSession,
                KeysPerDispatchMax = DebugLimits.MaxInputKeysPerDispatch,
                MaxHoldMs = DebugLimits.MaxInputHoldMs,
                PlannedInputs = DebugTriggerBudget.CountOfKind(sessionId, DebugTriggerBudget.KindInput),
                MaxPlannedInputsPerSession = DebugLimits.MaxPlannedInjectionsPerSession,
                PendingReleaseRequests = pending,
                MaxPendingReleaseRequests = DebugLimits.MaxInputPendingReleases
            };
        }

        private static List<DeviceRecord> DevicesFor(string sessionId)
        {
            lock (Sync)
            {
                return Devices.Values.Where(device => string.IsNullOrEmpty(sessionId) || string.Equals(device.SessionId, sessionId, StringComparison.Ordinal))
                    .OrderBy(device => device.DeviceId, StringComparer.Ordinal).ToList();
            }
        }

        private static List<InputRecord> RecordsFor(string sessionId)
        {
            lock (Sync)
            {
                return Records.Where(record => string.IsNullOrEmpty(sessionId) || string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)).ToList();
            }
        }

        /// <summary>下发结果：结束边界、坐标空间解析、当场读回确认与共同预算都在这里给出。</summary>
        private static DebugInputDispatchResult DispatchResult(DispatchSpec spec, InputRecord record, double now, int threadId, IUnityCliDebugInputDeviceBackend backend)
        {
            DeviceRecord device;
            lock (Sync) device = FindDeviceLocked(spec.SessionId, spec.DeviceKind);
            var confirmed = new List<string>();
            var confirmReason = string.Empty;
            if (device != null && backend.Available)
            {
                IList<string> held;
                if (!backend.TryReadHeldControls(device.Handle, out held, out confirmReason)) held = new List<string>();
                confirmed = held.ToList();
            }
            if (record != null)
                // 一次下发始终记录一条下发事件；释放确认由 ReleaseRecord 单独追加。
            AppendEvent(EventKindDispatch,
                    record, record.ReleaseReason, record.ReleaseConfirmed, device);
            var released = record == null || string.Equals(record.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal);
            return new DebugInputDispatchResult
            {
                DispatchId = record == null ? string.Empty : record.DispatchId,
                SessionId = spec.SessionId,
                DeviceId = device == null ? spec.DeviceId : device.DeviceId,
                DeviceKind = spec.DeviceKind,
                DeviceName = device == null ? spec.DeviceName : device.Name,
                Backend = backend.Name,
                ApiIdentity = backend.ApiIdentity,
                Action = spec.Action,
                Controls = new List<string>(spec.Controls),
                EndBoundary = spec.EndBoundary,
                HoldMs = spec.HoldMs,
                ExpiresAtMonotonicMs = record == null ? 0d : record.ExpiresAtMonotonicMs,
                State = record == null ? UnityCliDebugInputHoldStates.Released : record.State,
                ReleaseState = released ? UnityCliDebugInputHoldStates.Released : record.State,
                ReleaseReason = record == null ? UnityCliDebugInputReasons.ExplicitRelease : record.ReleaseReason,
                ReleaseConfirmed = released,
                PointerSpace = spec.PointerSpace,
                PointerX = spec.PointerX,
                PointerY = spec.PointerY,
                ResolvedWidth = spec.ResolvedWidth,
                ResolvedHeight = spec.ResolvedHeight,
                ResolutionSource = spec.PointerSpace.Length == 0 ? string.Empty : "Screen.width/height",
                ConfirmedControls = confirmed,
                ConfirmReason = StableReason(confirmReason, UnityCliDebugInputReasons.HoldReadRefused),
                DispatchedAtMonotonicMs = now,
                DispatchedThreadId = threadId,
                ExecutionPoint = DispatchExecutionPoint,
                Budget = BudgetView(spec.SessionId),
                Statement = "A submitted device event only proves this owner submitted it. It never proves that the business read the input: prove acceptance with a hook capture, a program check, a value read or a sealed debug.run window."
            };
        }

        /// <summary>释放结果：只有全部目标都确认释放才报 released，否则报 pending 与精确原因。</summary>
        private static DebugInputReleaseResult BuildReleaseResult(string sessionId, string scope, string reason)
        {
            var holds = RecordsFor(sessionId).Select(HoldView).ToList();
            var devices = DevicesFor(sessionId).Select(DeviceView).ToList();
            var targeted = holds.Where(hold => ScopeMatches(scope, hold.DeviceKind)).ToList();
            var pendingReasons = new List<string>();
            long pending = 0;
            long confirmed = 0;
            foreach (var hold in targeted)
            {
                if (hold.ReleaseConfirmed && string.Equals(hold.State, UnityCliDebugInputHoldStates.Released, StringComparison.Ordinal))
                {
                    confirmed++;
                    continue;
                }
                pending++;
                if (!string.IsNullOrEmpty(hold.ReleaseReason) && !pendingReasons.Contains(hold.ReleaseReason)) pendingReasons.Add(hold.ReleaseReason);
            }
            var devicePending = false;
            if (string.Equals(scope, UnityCliDebugInputReleaseScopes.All, StringComparison.Ordinal))
            {
                foreach (var device in devices)
                {
                    if (string.Equals(device.State, UnityCliDebugInputDeviceStates.Released, StringComparison.Ordinal)) continue;
                    devicePending = true;
                    if (!string.IsNullOrEmpty(device.RemovalReason) && !pendingReasons.Contains(device.RemovalReason)) pendingReasons.Add(device.RemovalReason);
                }
            }
            var released = pending == 0 && !devicePending;
            if (released) confirmed += devices.Count;
            long pendingRequests;
            lock (Sync) pendingRequests = PendingReleases.Count(entry => string.Equals(entry.Key, sessionId, StringComparison.Ordinal));
            return new DebugInputReleaseResult
            {
                SessionId = sessionId,
                Scope = scope,
                ReleaseState = released ? UnityCliDebugInputHoldStates.Released : UnityCliDebugInputHoldStates.Pending,
                ReleasedAtMonotonicMs = released ? DebugEventClocks.MonotonicMilliseconds : 0d,
                ReleasedThreadId = released ? Thread.CurrentThread.ManagedThreadId : 0,
                ConfirmedCount = confirmed,
                PendingCount = pending + (devicePending ? 1 : 0),
                PendingReasons = pendingReasons,
                DeferredTo = released ? string.Empty : pendingRequests == 0 ? "none" : "the central scheduler tick on the Unity main thread",
                Holds = holds,
                Devices = devices,
                Budget = BudgetView(sessionId),
                Statement = released
                    ? "Every targeted record was confirmed released by the device readback, so this owner reports released."
                    : "At least one targeted record is not confirmed released, so this owner reports pending with the exact reason instead of claiming the hold ended. The last release request reason was " + (reason ?? string.Empty) + "."
            };
        }

        // ------------------------------------------------------------------
        // 参数校验与错误映射
        // ------------------------------------------------------------------

        /// <summary>session 必须存在且 open：已停止的 session 不再接受新的输入计划。</summary>
        private static void RequireOpenSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw InvalidField("sessionId", "the id of an open debug session", "missing");
            DebugSessionInfoResult info;
            try
            {
                info = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId });
            }
            catch (BridgeCommandException)
            {
                throw MissingSession(sessionId);
            }
            var state = info.Session == null ? string.Empty : info.Session.State;
            if (string.Equals(state, UnityCliDebugSessionStates.Open, StringComparison.Ordinal)) return;
            throw new BridgeCommandException(
                "E_DEBUG_SESSION_NOT_OPEN",
                "The session is not open, so it cannot accept a new input plan.",
                new Dictionary<string, object>
                {
                    { "sessionId", sessionId },
                    { "state", state },
                    { "cleanupPending", info.Session != null && info.Session.CleanupPending }
                },
                new[]
                {
                    "Run tools run debug.session.info to read the current session state and cleanup detail.",
                    "Run tools run debug.session.create to start a new session for this investigation."
                });
        }

        /// <summary>设备后端必须现场可用；缺失依赖只报 unavailable，且绝不安装。</summary>
        private static IUnityCliDebugInputDeviceBackend RequireBackend(string deviceKind)
        {
            var backend = DebugInputDeviceBackends.Resolve();
            if (backend.Available) return backend;
            throw BackendRejection(backend,
                "No device backend is available for " + deviceKind + " input, so nothing was created and no package was installed.",
                backend.UnavailableReason);
        }

        private static BridgeCommandException BackendRejection(IUnityCliDebugInputDeviceBackend backend, string message, string reason, string backendReason = null)
        {
            // 公开 reason 永远是本 owner 的稳定分类：后端返回的错误原因先过闭集，原始文本只作诊断。
            var reported = string.IsNullOrEmpty(reason) ? backend.UnavailableReason : reason;
            var classification = StableReason(reported, UnityCliDebugInputReasons.BackendUnsupported);
            var diagnostics = string.IsNullOrEmpty(backendReason) ? reported : backendReason;
            var details = new Dictionary<string, object>
            {
                { "status", UnityCliDebugInputBackendStates.Unavailable },
                { "backend", string.IsNullOrEmpty(backend.Name) ? UnityCliDebugInputBackends.None : backend.Name },
                { "reason", classification },
                { "detail", backend.UnavailableDetail },
                { "missingApiMembers", backend.MissingApiMembers.ToList() },
                { "installedByThisOwner", false }
            };
            if (!string.IsNullOrEmpty(diagnostics) && !string.Equals(diagnostics, classification, StringComparison.Ordinal))
                details["backendReason"] = UnityCliBridge.BoundedText(diagnostics, DebugLimits.MaxDiagnosticTextBytes);
            return new BridgeCommandException(
                "E_DEBUG_BINDING_UNSUPPORTED",
                message + " Its status is unavailable.",
                details,
                new[]
                {
                    "Run tools run debug.input.info to read the backend status this Editor reports.",
                    "Drive this input through a general path instead: debug.method.invoke for a business entry, debug.hook.install for an interceptable managed wrapper, debug.code.execute for explicit code, or debug.program.run for a bounded cross-thread delivery.",
                    "Install an input package yourself only if the project really needs it; this owner never installs or changes packages."
                });
        }

        /// <summary>
        /// 后端 out reason 转公开原因的唯一收敛点。
        /// 后端契约要求返回稳定原因，但异常路径会返回自由文本；只有闭集内的稳定码可以直接公开，
        /// 其余一律回落到本 owner 对该操作声明的稳定分类，原始文本只作诊断，绝不进入公开 reason。
        /// </summary>
        private static string StableReason(string backendReason, string fallback)
        {
            return UnityCliDebugInputReasons.IsDefined(backendReason) ? backendReason : fallback;
        }

        /// <summary>设备建立失败：资源或清理条目预算不足与后端拒绝分开报告。</summary>
        private static BridgeCommandException DeviceCreationRejection(string failure, DispatchSpec spec, IUnityCliDebugInputDeviceBackend backend)
        {
            var detail = failure ?? string.Empty;
            if (detail.StartsWith("resource:", StringComparison.Ordinal) || detail.StartsWith("cleanup-step:", StringComparison.Ordinal))
            {
                long used = 0L;
                try
                {
                    used = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = spec.SessionId }).Budget.ActiveResourcesUsed;
                }
                catch (BridgeCommandException)
                {
                    // 读取占用失败不改变拒绝结论；占用按 0 报告而不是猜测。
                }
                return new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "This session cannot register another active resource for the requested virtual device, so no device was created.",
                    new Dictionary<string, object>
                    {
                        { "sessionId", spec.SessionId },
                        { "kind", UnityCliDebugResourceKinds.Input },
                        { "reason", detail },
                        { "activeResourcesUsed", used },
                        { "maxActiveResourcesPerSession", (long)DebugLimits.MaxActiveResourcesPerSession }
                    },
                    new[]
                    {
                        "Run tools run debug.input.release with scope all to end this owner's device lifetime for that session.",
                        "Run tools run debug.session.info to read the active resource usage of that session."
                    });
            }
            return BackendRejection(backend, "The device backend refused to create the session-owned virtual device.",
                UnityCliDebugInputReasons.DeviceCreationRefused,
                detail.StartsWith("backend:", StringComparison.Ordinal) ? detail.Substring("backend:".Length) : detail);
        }

        private static BridgeCommandException OffMainThread(string tool, string message)
        {
            return new BridgeCommandException(
                "E_DEBUG_SCOPE_UNSUPPORTED",
                message + " So " + tool + " was refused before any effect.",
                new Dictionary<string, object>
                {
                    { "tool", tool },
                    { "scope", UnityCliDebugInputReasons.OffMainThread },
                    { "rule", UnityCliDebugInputReasons.OffMainThread }
                },
                new[]
                {
                    "Submit this call from the Unity main thread; the device backend and the declared coordinate space both need it.",
                    "Run tools run debug.input.info to read the backend status and the records this owner reports."
                });
        }

        private static BridgeCommandException MissingSession(string sessionId)
        {
            return new BridgeCommandException(
                "E_DEBUG_SESSION_NOT_FOUND",
                "No loaded session definition has that sessionId in the current Editor session.",
                new Dictionary<string, object> { { "sessionId", sessionId ?? string.Empty } },
                new[] { "Run tools run debug.session.list to read the currently loaded session definitions." });
        }

        private static BridgeCommandException InvalidField(string field, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "Field " + field + " is invalid: expected " + expected + "; received " + (actual ?? "null") + ".",
                new Dictionary<string, object> { { "path", "$." + field }, { "expected", expected }, { "actual", actual ?? string.Empty } },
                new[] { "Run tools describe the matching debug.input Tool to read the field contract, then submit the corrected input." });
        }

        /// <summary>闭集字段校验；取值必须与声明的取值完全一致。</summary>
        private static string NormalizeString(string value, string field, string first, string second)
        {
            return NormalizeString(value, field, first, second, null);
        }

        /// <summary>三值闭集校验；释放范围有三个取值，复用同一份错误形状。</summary>
        private static string NormalizeString(string value, string field, string first, string second, string third)
        {
            if (string.Equals(value, first, StringComparison.Ordinal)) return value;
            if (string.Equals(value, second, StringComparison.Ordinal)) return value;
            if (third != null && string.Equals(value, third, StringComparison.Ordinal)) return value;
            throw InvalidField(field, first + " or " + second + (third == null ? string.Empty : " or " + third), value);
        }

        private static List<string> NormalizeControls(List<string> controls, string field, bool required)
        {
            var normalized = new List<string>();
            if (controls != null)
            {
                foreach (var control in controls)
                {
                    if (string.IsNullOrWhiteSpace(control)) throw InvalidField(field, "non-empty control names", "empty entry");
                    var name = control.Trim();
                    if (name.Length > DebugLimits.MaxInputNameBytes) throw InvalidField(field, "control names inside the declared byte budget", name);
                    if (!normalized.Contains(name, StringComparer.Ordinal)) normalized.Add(name);
                }
            }
            if (required && normalized.Count == 0) throw InvalidField(field, "at least one control name", "empty");
            if (normalized.Count > DebugLimits.MaxInputKeysPerDispatch)
                throw InvalidField(field, "at most " + DebugLimits.MaxInputKeysPerDispatch.ToString(CultureInfo.InvariantCulture) + " control names", normalized.Count.ToString(CultureInfo.InvariantCulture));
            return normalized;
        }

        private static List<string> NormalizeButtons(List<string> buttons)
        {
            var normalized = new List<string>();
            if (buttons != null)
            {
                foreach (var button in buttons)
                {
                    if (!UnityCliDebugInputButtons.IsDefined(button)) throw InvalidField("buttons", "left, right or middle", button);
                    if (!normalized.Contains(button, StringComparer.Ordinal)) normalized.Add(button);
                }
            }
            return normalized;
        }

        /// <summary>
        /// 结束边界的唯一校验点：按下控制必须声明显式边界且没有默认值，纯移动不得声明边界。
        /// </summary>
        private static void ValidateBoundary(bool press, string endBoundary, long? holdMs, out string normalized, out long normalizedMs)
        {
            normalized = string.Empty;
            normalizedMs = 0L;
            if (!press)
            {
                if (!string.IsNullOrEmpty(endBoundary) || holdMs != null)
                    throw InvalidField("endBoundary", "no end boundary when no control is pressed, because nothing is held", endBoundary ?? "holdMs");
                return;
            }
            if (!UnityCliDebugInputEndBoundaries.IsDefined(endBoundary))
                throw InvalidField("endBoundary", "hold-ms or until-release, with no default", endBoundary);
            normalized = endBoundary;
            if (string.Equals(endBoundary, UnityCliDebugInputEndBoundaries.HoldMs, StringComparison.Ordinal) == false)
            {
                if (holdMs != null) throw InvalidField("holdMs", "no holdMs for the until-release boundary", holdMs.Value.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (holdMs == null) throw InvalidField("holdMs", "the millisecond boundary required by endBoundary hold-ms", "missing");
            if (holdMs.Value < 1L || holdMs.Value > DebugLimits.MaxInputHoldMs)
                throw InvalidField("holdMs", "1 to " + DebugLimits.MaxInputHoldMs.ToString(CultureInfo.InvariantCulture) + " milliseconds", holdMs.Value.ToString(CultureInfo.InvariantCulture));
            normalizedMs = holdMs.Value;
        }
    }
}
#endif
