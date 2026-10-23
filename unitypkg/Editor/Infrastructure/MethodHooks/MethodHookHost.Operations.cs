#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>method-hook 的公开操作：安装、替换、启停、删除、摘要、精查与事件续读。</summary>
    internal static partial class MethodHookHost
    {
        /// <summary>install 的 effect 前校验：语义、exact target、捕获字段、listener、容量与后端可用。</summary>
        internal static MethodHookInstallPlan PrepareInstall(MethodHookInstallRequest request, string owner = null)
        {
            EnsureReady();
            if (request == null) throw new BridgeCommandException("E_SCHEMA_INVALID", "debug.hook-install requires a request body.", "Pass target plus capture or callbacks.");
            var capture = NormalizeCapture(request.Capture);
            var callbacks = NormalizeCallbacks(request.Callbacks);
            if (!capture.HasBehaviour(callbacks))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "A hook requires at least one effective capture or callback behaviour.", "Enable capture.arguments, capture.result, an instance or static field, or provide prefixCode/postfixCode.");
            var listener = PrepareListener(request.Listener);
            if (listener != null) RequireListenerSession(listener);
            var activation = string.IsNullOrEmpty(request.Activation) ? "play" : request.Activation;
            if (!string.Equals(activation, "play", StringComparison.Ordinal) && !string.Equals(activation, "editor-and-play", StringComparison.Ordinal))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "activation must be play or editor-and-play.", "Pass activation: play or editor-and-play.");
            var resolved = MethodHookTargetResolver.Resolve(request.Target);
            EnsureBackend();
            lock (Sync)
            {
                if (Definitions.Count >= DebugLimits.MaxDefinitionsPerSession)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "The Editor session already holds " + DebugLimits.MaxDefinitionsPerSession.ToString(CultureInfo.InvariantCulture) + " hook definitions.", "Disable or remove a definition before installing another one.");
                var targetKey = MethodHookTargetKey.From(resolved.Method).Text;
                if (Definitions.Values.Count(definition => string.Equals(definition.TargetKey, targetKey, StringComparison.Ordinal)) >= DebugLimits.MaxDefinitionsPerTarget)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "This exact target already has " + DebugLimits.MaxDefinitionsPerTarget.ToString(CultureInfo.InvariantCulture) + " hook definitions.", "Remove one of the definitions on this target before installing another one.");
                if (!Targets.ContainsKey(MethodHookTargetKey.From(resolved.Method).Text) && AdapterIdentities.Count >= DebugLimits.MaxAdapterIdentities)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "This domain already loaded " + DebugLimits.MaxAdapterIdentities.ToString(CultureInfo.InvariantCulture) + " distinct adapter identities.", "Reuse existing targets or restart the Editor before targeting a new one.");
                EnsureNoConflictingIntervention(targetKey, null, OwnsArbitraryIntervention(callbacks != null && (!string.IsNullOrWhiteSpace(callbacks.PrefixCode) || !string.IsNullOrWhiteSpace(callbacks.PostfixCode)), request.Breakpoint));
                foreach (var field in capture.InstanceFields) field.Field = MethodHookTargetResolver.ResolveField(resolved.Method.DeclaringType, field.DeclaringType, field.FieldName);
                foreach (var field in capture.StaticFields) field.Field = MethodHookTargetResolver.ResolveField(resolved.Method.DeclaringType, field.DeclaringType, field.FieldName);
                if (callbacks != null && (!string.IsNullOrWhiteSpace(callbacks.PrefixCode) || !string.IsNullOrWhiteSpace(callbacks.PostfixCode)))
                    RequireCompileAdmission(CompileKey(callbacks.PrefixCode ?? string.Empty, callbacks.PostfixCode ?? string.Empty), 0L);
            }
            // 触发预算预留发生在 effect 之前：拒绝时不会产生定义，也不会占用任何真实资源。
            string triggerPlanId = null;
            if (listener != null)
            {
                triggerPlanId = "hook-plan:" + Guid.NewGuid().ToString("N").Substring(0, 16);
                int used;
                var failure = DebugTriggerBudget.TryPlan(listener.SessionId, DebugTriggerBudget.KindListener, triggerPlanId, out used);
                if (failure != null) throw TriggerBudgetRejection(listener, failure, used);
            }
            // 断点配置与其 session 资源槽位必须在 effect 之前保留；失败时归还未跨过 effect 的触发预算预留。
            DebugPauseBreakpointPlan breakpointPlan = null;
            if (request.Breakpoint != null)
            {
                try
                {
                    breakpointPlan = DebugPauseHost.Prepare(request.Breakpoint, "breakpoint");
                }
                catch
                {
                    ReleaseTriggerPlan(triggerPlanId);
                    throw;
                }
            }
            return new MethodHookInstallPlan
            {
                Resolved = resolved,
                TargetSpec = request.Target,
                Capture = capture,
                Callbacks = callbacks,
                Activation = activation,
                Priority = request.Priority ?? 0,
                EmitHitEvents = request.EmitHitEvents ?? true,
                Label = request.Label ?? string.Empty,
                Listener = listener,
                TriggerPlanId = triggerPlanId,
                Owner = owner ?? string.Empty,
                BreakpointPlan = breakpointPlan
            };
        }

        /// <summary>
        /// 声明 listener 时必须指定一个存在且处于 open 的 session：
        /// 已停止的 session 不再接收新的计划注入，也不允许往不存在的 owner 上挂新观察。
        /// </summary>
        private static void RequireListenerSession(MethodHookListenerState listener)
        {
            DebugSessionInfoResult info;
            try
            {
                info = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = listener.SessionId });
            }
            catch (BridgeCommandException)
            {
                throw new BridgeCommandException(
                    "E_DEBUG_SESSION_NOT_FOUND",
                    "The session that should own this listener does not exist in the current Editor session.",
                    new Dictionary<string, object> { { "sessionId", listener.SessionId }, { "path", "$.listener.sessionId" } },
                    new[]
                    {
                        "Run tools run debug.session-list to read the loaded session definitions.",
                        "Run tools run debug.session-create to establish a new owner, then install the listener again."
                    });
            }
            var state = info.Session == null ? string.Empty : info.Session.State;
            if (string.Equals(state, UnityCliDebugSessionStates.Open, StringComparison.Ordinal)) return;
            throw new BridgeCommandException(
                "E_DEBUG_SESSION_NOT_OPEN",
                "A session that is not open cannot accept a new planned injection.",
                new Dictionary<string, object>
                {
                    { "sessionId", listener.SessionId },
                    { "state", state },
                    { "cleanupPending", info.Session != null && info.Session.CleanupPending },
                    { "path", "$.listener.sessionId" }
                },
                new[]
                {
                    "Run tools run debug.session-info to read the current state and cleanup detail.",
                    "Run tools run debug.session-create to start a new session for this investigation."
                });
        }

        /// <summary>触发预算拒绝的稳定错误；携带当前占用与剩余预算。</summary>
        private static BridgeCommandException TriggerBudgetRejection(MethodHookListenerState listener, string failure, int used)
        {
            return new BridgeCommandException(
                "E_DEBUG_TRIGGER_BUDGET_EXHAUSTED",
                "The session has no free planned-injection budget, so this listener was refused before any effect.",
                new Dictionary<string, object>
                {
                    { "reason", failure },
                    { "sessionId", listener.SessionId },
                    { "used", (long)used },
                    { "max", (long)DebugLimits.MaxPlannedInjectionsPerSession },
                    { "trigger", listener.Trigger }
                },
                new[]
                {
                    "Run tools run debug.hook-remove for a listener you no longer need, then install it again.",
                    "Run tools run debug.session-info on the owning session to read its planned injection budget."
                });
        }

        /// <summary>
        /// program 挂载的 binding 只能由该 program 管理。直接的 debug.hook.* 变更必须在这里被拒绝，
        /// 否则一次普通 replace/disable 会静默改掉 program-owned revision，使 program 与真实绑定脱节。
        /// </summary>
        private static void RequireHookOwner(string action, MethodHookDefinition definition, string callerOwner)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Owner)) return;
            if (string.Equals(definition.Owner, callerOwner ?? string.Empty, StringComparison.Ordinal)) return;
            throw new BridgeCommandException(
                "E_HOOK_OWNED_BY_PROGRAM",
                "This hook definition is owned by a debug program binding, so a direct debug.hook." + action + " call cannot change it.",
                new Dictionary<string, object>
                {
                    { "hookId", definition.HookId },
                    { "owner", definition.Owner },
                    { "action", action },
                    { "revision", definition.Revision },
                    { "targetKey", definition.TargetKey ?? string.Empty }
                },
                new[]
                {
                    "Run tools run debug.program-replace to change the program that owns this binding.",
                    "Run tools run debug.program-stop or debug.program-remove to release the program-owned binding.",
                    "Run tools run debug.program-list to read the programs of this Editor session."
                });
        }

        /// <summary>
        /// 冲突规则校验（effect 之前）：调用方回调体可以写任意状态，写范围无法静态证明，
        /// 而 pause 断点同样让调用方代码在命中现场自行干预（pause.evaluate 可以改参数、跳过原方法并写结果），
        /// 所以两者都是任意干预 owner：同一 exact target 上最多只能有一个活动 owner；
        /// 纯结构化观察（含 snapshot 断点）不写目标状态，可以共存。
        /// </summary>
        private static void EnsureNoConflictingIntervention(string targetKey, MethodHookDefinition self, bool intervention)
        {
            if (!intervention || string.IsNullOrEmpty(targetKey)) return;
            foreach (var definition in Definitions.Values)
            {
                if (ReferenceEquals(definition, self)) continue;
                if (!OwnsArbitraryIntervention(definition.IsIntervention, BreakpointOf(definition))) continue;
                if (!string.Equals(definition.TargetKey, targetKey, StringComparison.Ordinal)) continue;
                var state = definition.State;
                if (state != MethodHookState.Armed && state != MethodHookState.Dormant && state != MethodHookState.Restoring) continue;
                var kind = IsPauseBreakpoint(BreakpointOf(definition)) ? "pause-breakpoint" : "callback";
                throw new BridgeCommandException(
                    "E_DEBUG_RESOURCE_CONFLICT",
                    "This exact target already has an active " + (string.Equals(kind, "pause-breakpoint", StringComparison.Ordinal) ? "pause breakpoint" : "hook whose callback code owns an unprovable write scope") + ": " + definition.HookId + ".",
                    new Dictionary<string, object> { { "targetKey", targetKey }, { "ownerHookId", definition.HookId }, { "scope", "arbitrary-intervention" }, { "ownerKind", kind } },
                    new[] { "Remove, disable or replace that definition instead of adding a second arbitrary-intervention owner on the same exact target.", "Install capture-only observations on this target when independent observation is required." });
            }
        }

        /// <summary>一个定义/提交是否持有同一 target 上的任意干预权：回调体或 pause 断点都算。
        /// 定向测试与实现共用同一声明，不复制判定。</summary>
        internal static bool OwnsArbitraryIntervention(bool hasCallbacks, MethodHookBreakpoint breakpoint)
        {
            return hasCallbacks || IsPauseBreakpoint(breakpoint);
        }

        /// <summary>提交时只能拿到断点 spec，因此同一判定必须对两种形态各有一份且语义一致。</summary>
        internal static bool OwnsArbitraryIntervention(bool hasCallbacks, MethodHookBreakpointSpec breakpoint)
        {
            return hasCallbacks || IsPauseBreakpoint(breakpoint);
        }

        private static bool IsPauseBreakpoint(MethodHookBreakpoint breakpoint)
        {
            return breakpoint != null && string.Equals(breakpoint.Kind, DebugPauseBreakpointKinds.Pause, StringComparison.Ordinal);
        }

        private static bool IsPauseBreakpoint(MethodHookBreakpointSpec breakpoint)
        {
            return breakpoint != null && string.Equals(breakpoint.Kind, DebugPauseBreakpointKinds.Pause, StringComparison.Ordinal);
        }

        private static MethodHookBreakpoint BreakpointOf(MethodHookDefinition definition)
        {
            return definition == null ? null : definition.Breakpoint;
        }

        /// <summary>effect barrier 之后建立 Attachment、原子保存并读回，然后发布新的不可变派发快照。</summary>
        internal static MethodHookInstallResult ApplyInstall(MethodHookInstallPlan plan, MethodHookPreparedCallbacks prepared)
        {
            if (plan == null) throw new BridgeCommandException("E_SCHEMA_INVALID", "The install plan is unavailable.", "Submit debug.hook-install again.");
            var definition = new MethodHookDefinition
            {
                HookId = "hook-" + Guid.NewGuid().ToString("N").Substring(0, 16),
                Revision = 1,
                Label = plan.Label,
                Priority = plan.Priority,
                Activation = plan.Activation,
                TargetSpec = BuildTargetSpec(plan.Resolved.Method),
                TargetKey = MethodHookTargetKey.From(plan.Resolved.Method).Text,
                Capture = plan.Capture,
                PrefixCode = plan.Callbacks == null ? string.Empty : plan.Callbacks.PrefixCode ?? string.Empty,
                PostfixCode = plan.Callbacks == null ? string.Empty : plan.Callbacks.PostfixCode ?? string.Empty,
                EmitHitEvents = plan.EmitHitEvents,
                State = MethodHookState.Restoring,
                Resolved = plan.Resolved.Method,
                Callback = prepared == null ? null : prepared.Callback,
                CallbackKey = prepared == null ? string.Empty : prepared.CompileKey,
                PrefixHash = prepared == null ? string.Empty : prepared.PrefixHash,
                PostfixHash = prepared == null ? string.Empty : prepared.PostfixHash,
                Listener = plan.Listener,
                Owner = plan.Owner ?? string.Empty,
                TriggerOwnerId = plan.TriggerPlanId ?? string.Empty
            };
            definition.StateBag = new MethodHookStateBag(definition.HookId);
            if (plan.BreakpointPlan != null)
            {
                definition.Breakpoint = plan.BreakpointPlan.Breakpoint;
                definition.BreakpointOwner = plan.BreakpointPlan.Owner;
            }
            MethodHookTargetRuntime runtime;
            lock (Sync)
            {
                InstallSequence(definition);
                Definitions[definition.HookId] = definition;
                runtime = EnsureTargetRuntime(plan.Resolved, definition.TargetSpec);
                runtime.Definitions.Add(definition);
            }
            try
            {
                AttachIfNeeded(runtime);
                // listener 声明的 phase 现场与定义同生命周期：失败走同一 rollback，不留半个定义。
                var phaseFailure = InstallListenerPhaseSite(definition, definition.TriggerOwnerId, definition.Listener);
                if (phaseFailure != null) throw DebugPlayerLoopHost.Unavailable(definition.Listener.Phase, phaseFailure);
                lock (Sync)
                {
                    definition.State = definition.ActivationMatches(playScope) ? MethodHookState.Armed : MethodHookState.Dormant;
                    PersistDefinitions("install");
                    // 只有完整建立后一次发布：命中路径要么看到旧快照，要么看到新快照。
                    PublishDispatchSnapshot(runtime);
                }
                AppendLifecycle(definition, "installed", MethodHookStateText.Of(definition.State));
            }
            catch (Exception)
            {
                RollbackDefinition(definition, runtime);
                ReleaseTriggerPlan(plan.TriggerPlanId);
                DebugPauseHost.ReleaseUncommittedPlan(plan.BreakpointPlan);
                throw;
            }
            // 定义已建立且已发布：断点的 session 资源槽位现在才成为活动资源。
            DebugPauseHost.CommitPlan(plan.BreakpointPlan, definition.HookId);
            // 该 session 的第 1 阶段条目必须存在：stop 时它关闭未来入口并取消 pending 投递。
            RegisterSessionStopDispatch(definition);
            return new MethodHookInstallResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                Activation = definition.Activation,
                Priority = definition.Priority,
                EventCursor = DebugEventStore.BeginCursor(new UnityCliDebugEventFilter { HookId = definition.HookId }),
                Target = MethodHookTargetResolver.Summarize(plan.Resolved, definition.TargetSpec),
                Capture = definition.Capture.Summarize(),
                Callbacks = SummarizeCallbacks(definition),
                Listener = SummarizeListener(definition.Listener),
                Breakpoint = SummarizeBreakpoint(definition)
            };
        }

        /// <summary>把一个定义的断点配置投影为公开事实；未声明断点时整个字段为空。</summary>
        internal static MethodHookBreakpointSummary SummarizeBreakpoint(MethodHookDefinition definition)
        {
            if (definition == null || definition.Breakpoint == null) return null;
            var breakpoint = definition.Breakpoint;
            var owner = definition.BreakpointOwner;
            return new MethodHookBreakpointSummary
            {
                Present = true,
                Kind = breakpoint.Kind,
                Position = breakpoint.Position,
                TimeoutKind = string.Equals(breakpoint.Kind, DebugPauseBreakpointKinds.Pause, StringComparison.Ordinal)
                    ? (breakpoint.Unbounded ? DebugPauseTimeoutKinds.Unbounded : DebugPauseTimeoutKinds.Bounded)
                    : string.Empty,
                TimeoutDurationMs = breakpoint.Unbounded ? 0L : breakpoint.DurationMs,
                StackDepth = breakpoint.StackDepth,
                SessionId = breakpoint.SessionId ?? string.Empty,
                ResourceRegistered = owner != null && !string.IsNullOrEmpty(owner.ResourceId),
                ResourceId = owner == null || string.IsNullOrEmpty(owner.ResourceId) ? null : owner.ResourceId
            };
        }

        /// <summary>
        /// 释放一个定义占用的计划注入预算与 phase 现场。只释放本定义自己的预留，
        /// 因此移除一个 listener 不会影响同一 session 上其他 listener 或输入的计划。
        /// </summary>
        private static void ReleaseTriggerPlan(string triggerOwnerId)
        {
            if (string.IsNullOrEmpty(triggerOwnerId)) return;
            // 同一预留身份同时持有该 listener 的 phase 现场，因此在这里一并归还；未登记时是幂等 no-op。
            DebugPlayerLoopHost.Release(triggerOwnerId);
            DebugTriggerBudget.Release(triggerOwnerId);
        }

        /// <summary>
        /// 声明 player-loop-phase 触发源的 listener 必须建立对应 phase 现场：否则该声明只是一句话，
        /// 投递永远不会在声明的 phase 上取得执行权。返回非 null 表示稳定失败原因。
        /// 现场记在同一触发预留身份下，release、disable、remove、replace 与 session stop 都会归还它。
        /// </summary>
        private static string InstallListenerPhaseSite(MethodHookDefinition definition, string ownerId, MethodHookListenerState listener)
        {
            if (definition == null || listener == null) return null;
            if (!string.Equals(listener.Trigger, DebugTriggerSources.PlayerLoopPhase, StringComparison.Ordinal)) return null;
            if (string.IsNullOrEmpty(ownerId)) return "owner-required";
            var failure = DebugPlayerLoopHost.Install(listener.Phase, ownerId);
            if (failure != null) return failure;
            // session stop 不会删除 listener 定义，因此必须有一个显式清理条目归还 phase 现场；
            // 同一 hook 的条目只注册一次，重复注册返回 conflict 属于幂等已存在。
            var stepFailure = DebugSessionHost.RegisterCleanupStep(
                listener.SessionId,
                UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.CloseObservation),
                "player-loop-phase:" + definition.HookId,
                UnityCliDebugFacilityOwners.Observation,
                report =>
                {
                    // 读当前预留身份：replace 会换身份，闭包不得捕捉旧 token。
                    if (!DebugPlayerLoopHost.Release(definition.TriggerOwnerId)) return true;
                    if (report != null) report.ClosedObservations++;
                    return true;
                },
                // 移除框架自己的 PlayerLoop 子系统只能通过主线程专属的 PlayerLoop 安装 API 完成：
                // 主线程暂停期间由控制通道服务的 stop 必须推迟它，而不是在后台线程上执行。
                true);
            if (stepFailure == null || string.Equals(stepFailure, UnityCliDebugSessionFailures.CleanupStepConflict, StringComparison.Ordinal)) return null;
            DebugPlayerLoopHost.Release(ownerId);
            return "cleanup-step:" + stepFailure;
        }

        /// <summary>replace 的 effect 前校验；目标不变，因此不重新安装 detour。</summary>
        internal static MethodHookMutationPlan PrepareReplace(MethodHookReplaceRequest request)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, request == null ? null : request.EditorSessionId, request == null ? (long?)null : request.ExpectedRevision);
            RequireHookOwner("replace", definition, null);
            var capture = NormalizeCapture(request.Capture);
            var callbacks = NormalizeCallbacks(request.Callbacks);
            var listener = PrepareListener(request.Listener);
            if (listener != null) RequireListenerSession(listener);
            if (!capture.HasBehaviour(callbacks))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "A hook requires at least one effective capture or callback behaviour.", "Enable capture fields or provide prefixCode/postfixCode.");
            if (callbacks != null && (!string.IsNullOrWhiteSpace(callbacks.PrefixCode) || !string.IsNullOrWhiteSpace(callbacks.PostfixCode))) EnsureBackend();
            // 新 listener 的预算预留发生在 effect 之前：拒绝时旧配置保持不变。
            string triggerPlanId = null;
            if (listener != null)
            {
                triggerPlanId = "hook-plan:" + Guid.NewGuid().ToString("N").Substring(0, 16);
                int used;
                var failure = DebugTriggerBudget.TryPlan(listener.SessionId, DebugTriggerBudget.KindListener, triggerPlanId, out used);
                if (failure != null) throw TriggerBudgetRejection(listener, failure, used);
            }
            // replace 可以引入回调体，因此必须在 effect 之前做同一冲突校验。
            lock (Sync)
            {
                EnsureNoConflictingIntervention(definition.TargetKey, definition, OwnsArbitraryIntervention(callbacks != null && (!string.IsNullOrWhiteSpace(callbacks.PrefixCode) || !string.IsNullOrWhiteSpace(callbacks.PostfixCode)), request.Breakpoint != null ? request.Breakpoint : (MethodHookBreakpointSpec)null));
            }
            // 提交的断点配置同样必须在 effect 之前保留好 session 资源槽位。
            DebugPauseBreakpointPlan breakpointPlan = null;
            if (request.Breakpoint != null)
            {
                try
                {
                    breakpointPlan = DebugPauseHost.Prepare(request.Breakpoint, "breakpoint");
                }
                catch
                {
                    ReleaseTriggerPlan(triggerPlanId);
                    throw;
                }
            }
            return new MethodHookMutationPlan
            {
                Action = "replace",
                Definition = definition,
                Capture = capture,
                Callbacks = callbacks,
                Priority = request.Priority ?? definition.Priority,
                EmitHitEvents = request.EmitHitEvents ?? definition.EmitHitEvents,
                Listener = listener,
                ListenerSubmitted = request.Listener != null,
                TriggerPlanId = triggerPlanId,
                BreakpointPlan = breakpointPlan,
                BreakpointSubmitted = request.Breakpoint != null
            };
        }

        /// <summary>替换回调指针与捕获配置；保留启停意图，不重复安装 detour。</summary>
        internal static MethodHookReplaceResult ApplyReplace(MethodHookMutationPlan plan, MethodHookPreparedCallbacks prepared)
        {
            var definition = plan.Definition;
            // 先建立新 listener 声明的 phase 现场：失败时旧配置与旧现场完全不变，新旧只要一个真相。
            var phaseFailure = InstallListenerPhaseSite(definition, plan.TriggerPlanId, plan.ListenerSubmitted ? plan.Listener : null);
            if (phaseFailure != null)
            {
                ReleaseTriggerPlan(plan.TriggerPlanId);
                throw DebugPlayerLoopHost.Unavailable(plan.Listener.Phase, phaseFailure);
            }
            if (definition.State == MethodHookState.Disabled || definition.State == MethodHookState.Faulted)
            {
                // 更新配置但不改变启停意图；faulted 仍需显式 enable 才重新激活。
                lock (Sync)
                {
                    ApplyReplaceConfiguration(definition, plan, prepared);
                    ApplyBreakpointReplace(definition, plan);
                }
                AppendLifecycle(definition, "replaced", "configuration-only");
                return BuildReplaceResult(definition, true);
            }
            MethodHookTargetRuntime runtime;
            lock (Sync)
            {
                runtime = FindRuntimeByKey(definition.TargetKey);
            }
            try
            {
                lock (Sync)
                {
                    ApplyReplaceConfiguration(definition, plan, prepared);
                    ApplyBreakpointReplace(definition, plan);
                }
                AppendLifecycle(definition, "replaced", "applied");
                return BuildReplaceResult(definition, true);
            }
            finally
            {
                // 替换后发布新快照；在途调用继续用它自己固定的旧 revision 完成。
                if (runtime != null) lock (Sync) PublishDispatchSnapshot(runtime);
            }
        }

        private static void ApplyReplaceConfiguration(MethodHookDefinition definition, MethodHookMutationPlan plan, MethodHookPreparedCallbacks prepared)
        {
            var prefix = plan.Callbacks == null ? string.Empty : plan.Callbacks.PrefixCode ?? string.Empty;
            var postfix = plan.Callbacks == null ? string.Empty : plan.Callbacks.PostfixCode ?? string.Empty;
            var listenChanged = plan.ListenerSubmitted && !ListenerEquals(definition.Listener, plan.Listener);
            var changed = !string.Equals(definition.PrefixCode, prefix, StringComparison.Ordinal)
                || !string.Equals(definition.PostfixCode, postfix, StringComparison.Ordinal)
                || !CaptureEquals(definition.Capture, plan.Capture)
                || definition.Priority != plan.Priority
                || definition.EmitHitEvents != plan.EmitHitEvents
                || listenChanged;
            if (!changed)
            {
                // 配置未变时不得消耗新预留：新旧只要一个真相。
                ReleaseTriggerPlan(plan.TriggerPlanId);
                return;
            }
            if (listenChanged)
            {
                ReleaseTriggerPlan(definition.TriggerOwnerId);
                definition.Listener = plan.Listener;
                definition.TriggerOwnerId = plan.TriggerPlanId ?? string.Empty;
            }
            else
            {
                ReleaseTriggerPlan(plan.TriggerPlanId);
            }
            definition.Capture = plan.Capture;
            definition.PrefixCode = prefix;
            definition.PostfixCode = postfix;
            definition.Priority = plan.Priority;
            definition.EmitHitEvents = plan.EmitHitEvents;
            if (prepared != null)
            {
                definition.Callback = prepared.Callback;
                definition.CallbackKey = prepared.CompileKey;
                definition.PrefixHash = prepared.PrefixHash;
                definition.PostfixHash = prepared.PostfixHash;
            }
            else if (!definition.HasCallbacks)
            {
                definition.Callback = null;
                definition.CallbackKey = string.Empty;
                definition.PrefixHash = string.Empty;
                definition.PostfixHash = string.Empty;
            }
            definition.Revision++;
            PersistDefinitions("replace");
        }

        /// <summary>
        /// 提交的断点配置接管：先释放旧配置的现场与 session 资源槽位，再接管新预留。
        /// 未提交 breakpoint 字段时保留当前断点，与 listener 的"省略即保留"语义一致。
        /// </summary>
        private static void ApplyBreakpointReplace(MethodHookDefinition definition, MethodHookMutationPlan plan)
        {
            if (!plan.BreakpointSubmitted) return;
            DebugPauseHost.ReleaseOwnership(definition, DebugPauseReleaseReasons.HookReplaced);
            definition.Breakpoint = plan.BreakpointPlan == null ? null : plan.BreakpointPlan.Breakpoint;
            definition.BreakpointOwner = plan.BreakpointPlan == null ? null : plan.BreakpointPlan.Owner;
            DebugPauseHost.CommitPlan(plan.BreakpointPlan, definition.HookId);
            PersistDefinitions("replace-breakpoint");
        }

        private static MethodHookReplaceResult BuildReplaceResult(MethodHookDefinition definition, bool changed)
        {
            return new MethodHookReplaceResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                Changed = changed,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                EventCursor = DebugEventStore.BeginCursor(new UnityCliDebugEventFilter { HookId = definition.HookId }),
                Target = definition.TargetSpec == null ? null : SummarizeStoredTarget(definition),
                Capture = definition.Capture.Summarize(),
                Callbacks = SummarizeCallbacks(definition),
                Listener = SummarizeListener(definition.Listener),
                Breakpoint = SummarizeBreakpoint(definition)
            };
        }

        /// <summary>enable 的 effect 前校验；armed/dormant 为 no-op，disabled/faulted 需要重解析。</summary>
        internal static MethodHookMutationPlan PrepareEnable(MethodHookEnableRequest request)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, request == null ? null : request.EditorSessionId, request == null ? (long?)null : request.ExpectedRevision);
            RequireHookOwner("enable", definition, null);
            var plan = new MethodHookMutationPlan { Action = "enable", Definition = definition };
            lock (Sync)
            {
                if (definition.State == MethodHookState.Armed || definition.State == MethodHookState.Dormant)
                {
                    plan.NoOp = true;
                    return plan;
                }
            }
            plan.RequiresTargetResolve = true;
            plan.Resolved = MethodHookTargetResolver.Resolve(definition.TargetSpec);
            EnsureBackend();
            lock (Sync)
            {
                var known = Targets.ContainsKey(MethodHookTargetKey.From(plan.Resolved.Method).Text) || FindRuntimeByKey(definition.TargetKey) != null;
                if (!known && AdapterIdentities.Count >= DebugLimits.MaxAdapterIdentities)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "This domain already loaded " + DebugLimits.MaxAdapterIdentities.ToString(CultureInfo.InvariantCulture) + " distinct adapter identities.", "Restart the Editor before enabling a hook on a new target.");
                // 重新激活回调定义相当于重新取得该 target 的干预权，因此必须重跑同一冲突校验。
                EnsureNoConflictingIntervention(MethodHookTargetKey.From(plan.Resolved.Method).Text, definition, OwnsArbitraryIntervention(definition.IsIntervention, BreakpointOf(definition)));
            }
            return plan;
        }

        /// <summary>激活被保留或被 faulted 的定义；恢复失败不伪装成功。</summary>
        internal static MethodHookEnableResult ApplyEnable(MethodHookMutationPlan plan, MethodHookPreparedCallbacks prepared)
        {
            var definition = plan.Definition;
            if (plan.NoOp)
            {
                lock (Sync)
                {
                    return new MethodHookEnableResult
                    {
                        HookId = definition.HookId,
                        Revision = definition.Revision,
                        State = MethodHookStateText.Of(definition.State),
                        Changed = false,
                        EditorSessionId = UnityCliBridge.EditorSessionId(),
                        EventCursor = DebugEventStore.BeginCursor(new UnityCliDebugEventFilter { HookId = definition.HookId })
                    };
                }
            }
            MethodHookTargetRuntime runtime;
            lock (Sync)
            {
                runtime = EnsureTargetRuntime(plan.Resolved, definition.TargetSpec);
                if (!runtime.Definitions.Contains(definition)) runtime.Definitions.Add(definition);
                definition.Resolved = plan.Resolved.Method;
                if (prepared != null)
                {
                    definition.Callback = prepared.Callback;
                    definition.CallbackKey = prepared.CompileKey;
                    definition.PrefixHash = prepared.PrefixHash;
                    definition.PostfixHash = prepared.PostfixHash;
                }
            }
            try
            {
                AttachIfNeeded(runtime);
                lock (Sync)
                {
                    definition.State = definition.ActivationMatches(playScope) ? MethodHookState.Armed : MethodHookState.Dormant;
                    definition.FaultCode = string.Empty;
                    definition.FaultMessage = string.Empty;
                    definition.Revision++;
                    Suppression.Remove(definition.HookId);
                    PersistDefinitions("enable");
                    DebugStore.SaveSuppression(Suppression);
                }
                AppendLifecycle(definition, "enabled", MethodHookStateText.Of(definition.State));
            }
            finally
            {
                // 新 revision 发布后旧 revision 的在途故障不再影响它。
                lock (Sync) PublishDispatchSnapshot(runtime);
            }
            return new MethodHookEnableResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                Changed = true,
                CallbackRecompiled = prepared != null,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                EventCursor = DebugEventStore.BeginCursor(new UnityCliDebugEventFilter { HookId = definition.HookId })
            };
        }

        /// <summary>disable 的 effect 前校验；已经 disabled 的定义是 no-op。</summary>
        internal static MethodHookMutationPlan PrepareDisable(MethodHookDisableRequest request)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, request == null ? null : request.EditorSessionId, request == null ? (long?)null : request.ExpectedRevision);
            RequireHookOwner("disable", definition, null);
            return new MethodHookMutationPlan
            {
                Action = "disable",
                Definition = definition,
                NoOp = definition.State == MethodHookState.Disabled
            };
        }

        /// <summary>先关闭 gate 再持久化 disabled；撤销不要求目标仍可解析。</summary>
        internal static MethodHookDisableResult ApplyDisable(MethodHookMutationPlan plan)
        {
            var definition = plan.Definition;
            lock (Sync)
            {
                if (plan.NoOp)
                {
                    return new MethodHookDisableResult
                    {
                        HookId = definition.HookId,
                        Revision = definition.Revision,
                        State = MethodHookStateText.Of(definition.State),
                        Changed = false,
                        EditorSessionId = UnityCliBridge.EditorSessionId()
                    };
                }
                definition.State = MethodHookState.Disabled;
                definition.Revision++;
                // 断点的现场与 session 资源槽位随定义失效：disable 后不再保留现场。
                DebugPauseHost.ReleaseOwnership(definition, DebugPauseReleaseReasons.HookDisabled);
                var released = ReleaseAdapterIfUnused(definition);
                // 已关闭的定义不再持有未来注入预算；enable 时不会自动恢复，必须重装 listener。
                ReleaseTriggerPlan(definition.TriggerOwnerId);
                definition.TriggerOwnerId = string.Empty;
                PersistDefinitions("disable");
                AppendLifecycle(definition, "disabled", "disabled");
                return new MethodHookDisableResult
                {
                    HookId = definition.HookId,
                    Revision = definition.Revision,
                    State = MethodHookStateText.Of(definition.State),
                    Changed = true,
                    AdapterReleased = released,
                    EditorSessionId = UnityCliBridge.EditorSessionId()
                };
            }
        }

        /// <summary>remove 的 effect 前校验；不存在时返回 E_HOOK_NOT_FOUND。callerOwner 只用于 program 解除自己的 binding。</summary>
        internal static MethodHookMutationPlan PrepareRemove(MethodHookRemoveRequest request, string callerOwner = null)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, request == null ? null : request.EditorSessionId, request == null ? (long?)null : request.ExpectedRevision);
            RequireHookOwner("remove", definition, callerOwner);
            return new MethodHookMutationPlan { Action = "remove", Definition = definition };
        }

        /// <summary>删除定义与其 Attachment；缓冲历史事件仍可读。</summary>
        internal static MethodHookRemoveResult ApplyRemove(MethodHookMutationPlan plan)
        {
            var definition = plan.Definition;
            lock (Sync)
            {
                // 先关闭新入口：在途命中按自己的帧完成，但删除后不再进入该定义。
                Volatile.Write(ref definition.DispatchClosed, (int)Volatile.Read(ref definition.Revision));
                Definitions.Remove(definition.HookId);
                Suppression.Remove(definition.HookId);
                // 删除定义时其断点现场与资源槽位一齐释放，不留下无人认领的暂停。
                DebugPauseHost.ReleaseOwnership(definition, DebugPauseReleaseReasons.HookRemoved);
                // 定义被删除后它的计划注入必定不再执行，预算必须立即归还。
                ReleaseTriggerPlan(definition.TriggerOwnerId);
                definition.TriggerOwnerId = string.Empty;
                var released = ReleaseAdapterIfUnused(definition);
                var remaining = Definitions.Values.Count(item => string.Equals(item.TargetKey, definition.TargetKey, StringComparison.Ordinal));
                PersistDefinitions("remove");
                DebugStore.SaveSuppression(Suppression);
                AppendLifecycle(definition, "removed", "removed");
                return new MethodHookRemoveResult
                {
                    HookId = definition.HookId,
                    State = "removed",
                    Removed = true,
                    AdapterReleased = released,
                    RemainingDefinitionsForTarget = remaining,
                    EditorSessionId = UnityCliBridge.EditorSessionId()
                };
            }
        }

        /// <summary>分页读取定义摘要；不恢复、不编译、不清理。</summary>
        internal static MethodHookListResult List(MethodHookListRequest request)
        {
            EnsureReady();
            var session = UnityCliBridge.EditorSessionId();
            var limit = request == null || request.Limit == null ? DebugLimits.ListDefaultLimit : Math.Min(Math.Max(1, request.Limit.Value), DebugLimits.ListMaxLimit);
            var state = request == null ? null : request.State;
            var label = request == null ? null : request.Label;
            var scope = UnityCliSerializer.Sha256Canonical(new Dictionary<string, object>
            {
                { "session", session }, { "state", state ?? string.Empty }, { "label", label ?? string.Empty }
            });
            var offset = 0;
            if (request != null && !string.IsNullOrEmpty(request.Cursor)) offset = DecodeOffsetCursor(request.Cursor, session, scope);
            List<MethodHookDefinition> ordered;
            lock (Sync)
            {
                ordered = Definitions.Values
                    .Where(definition => string.IsNullOrEmpty(state) || string.Equals(MethodHookStateText.Of(definition.State), state, StringComparison.Ordinal))
                    .Where(definition => string.IsNullOrEmpty(label) || (definition.Label ?? string.Empty).IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(definition => definition.InstallSequence)
                    .ToList();
            }
            var page = ordered.Skip(offset).Take(limit).ToList();
            var nextOffset = offset + page.Count;
            var hasMore = nextOffset < ordered.Count;
            return new MethodHookListResult
            {
                Items = page.Select(definition => PublicItem(definition, session)).ToList(),
                Total = ordered.Count,
                NextOffset = nextOffset,
                HasMore = hasMore,
                NextCursor = hasMore ? EncodeOffsetCursor(session, scope, nextOffset) : null,
                EditorSessionId = session
            };
        }

        /// <summary>
        /// 按 exact target key 读取同一 target 上的定义摘要（不分页）。
        /// 其他 owner 需要判断“这个 exact target 上是否已有 armed detour 与真实命中”时使用它：
        /// 公开的 identity 文本不区分重载，用它匹配会挑错现场。
        /// </summary>
        internal static List<MethodHookListItem> ListByTargetKey(string targetKey)
        {
            EnsureReady();
            var session = UnityCliBridge.EditorSessionId();
            List<MethodHookDefinition> matches;
            lock (Sync)
            {
                matches = Definitions.Values
                    .Where(definition => string.Equals(definition.TargetKey, targetKey ?? string.Empty, StringComparison.Ordinal))
                    .OrderBy(definition => definition.InstallSequence)
                    .ToList();
            }
            return matches.Select(definition => PublicItem(definition, session)).ToList();
        }

        /// <summary>
        /// F-16：列表项必须自足。只持有列表输出的客户端必须能直接对同一 identity 执行 remove/replace/disable，
        /// 因此 editorSessionId、listener（trigger/phase 与命中计数）与断点摘要都在同一条目里给出。
        /// </summary>
        private static MethodHookListItem PublicItem(MethodHookDefinition definition, string session)
        {
            var item = definition.ToListItem();
            item.EditorSessionId = session;
            item.Listener = SummarizeListener(definition.Listener);
            item.Breakpoint = SummarizeBreakpoint(definition);
            return item;
        }

        /// <summary>只读精查一个定义；源码按 slot 分页且默认不返回。</summary>
        internal static MethodHookInfoResult Info(MethodHookInfoRequest request)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, null, null, allowUnknownSession: true);
            var session = UnityCliBridge.EditorSessionId();
            MethodHookRuntimeSummary counters;
            string foreignOwners = string.Empty;
            lock (Sync)
            {
                counters = SummaryLocked();
                var runtime = FindRuntimeByKey(definition.TargetKey);
                if (runtime != null && runtime.Attached) foreignOwners = SafeOwnerSummary(runtime);
            }
            var result = new MethodHookInfoResult
            {
                HookId = definition.HookId,
                Label = definition.Label,
                State = MethodHookStateText.Of(definition.State),
                Revision = definition.Revision,
                EditorSessionId = session,
                Activation = definition.Activation,
                Priority = definition.Priority,
                EmitHitEvents = definition.EmitHitEvents,
                HitCount = definition.HitCount,
                BypassCount = definition.BypassCount,
                Target = definition.TargetSpec == null ? null : SummarizeStoredTarget(definition),
                Capture = definition.Capture.Summarize(),
                Callbacks = SummarizeCallbacks(definition),
                // 精查必须与 install/replace/list 用同一份 listener 投影：未声明时为 null，声明时给出声明事实与只读计数。
                Listener = SummarizeListener(definition.Listener),
                Breakpoint = SummarizeBreakpoint(definition),
                LastFault = string.IsNullOrEmpty(definition.FaultCode) ? null : new MethodHookFaultSummary
                {
                    Code = definition.FaultCode,
                    Message = definition.FaultMessage,
                    Revision = definition.FaultRevision,
                    InvocationId = definition.FaultInvocationId,
                    Owner = definition.FaultOwner
                },
                EventSummary = new MethodHookEventSummary
                {
                    TotalRecords = DebugEventStore.Count,
                    OldestAvailableCursor = DebugEventStore.OldestAvailableSequence,
                    LatestSequence = DebugEventStore.LatestSequence,
                    Gaps = DebugEventStore.Gaps.ToList()
                },
                StateBytes = definition.StateBag == null ? 0 : definition.StateBag.ByteSize,
                StaleFaults = Interlocked.Read(ref definition.StaleFaults),
                DroppedSupplements = Interlocked.Read(ref droppedSupplements),
                AdapterIdentities = counters.AdapterIdentities,
                CompiledArtifacts = counters.CompiledArtifacts,
                CompiledArtifactBytes = counters.CompiledArtifactBytes,
                ForeignOwners = foreignOwners
            };
            if (request != null && !string.IsNullOrEmpty(request.SourceSlot))
            {
                var source = string.Equals(request.SourceSlot, "postfix", StringComparison.Ordinal) ? definition.PostfixCode : definition.PrefixCode;
                var offset = Math.Max(0, request.SourceOffset ?? 0);
                var bytes = Encoding.UTF8.GetByteCount(source ?? string.Empty);
                var page = SubstringByUtf8Bytes(source ?? string.Empty, offset, DebugLimits.MaxInfoSourcePageBytes);
                result.SourcePage = new MethodHookSourcePage
                {
                    Slot = request.SourceSlot,
                    Offset = offset,
                    TotalBytes = bytes,
                    Text = page,
                    Truncated = offset + Encoding.UTF8.GetByteCount(page) < bytes
                };
            }
            return result;
        }

        /// <summary>新域恢复时重建 Attachment：先解析并建立 detour，回调编译留给中央 tick。</summary>
        private static void RestoreAttachment(MethodHookDefinition definition)
        {
            try
            {
                var resolved = MethodHookTargetResolver.Resolve(definition.TargetSpec);
                definition.Resolved = resolved.Method;
                var runtime = EnsureTargetRuntime(resolved, definition.TargetSpec);
                runtime.Definitions.Add(definition);
                AttachIfNeeded(runtime);
                if (!definition.HasCallbacks)
                {
                    definition.State = definition.ActivationMatches(playScope) ? MethodHookState.Armed : MethodHookState.Dormant;
                    AppendEvent(new UnityCliDebugEvent
                    {
                        HookId = definition.HookId,
                        Revision = definition.Revision,
                        Kind = "lifecycle",
                        PlaySessionId = playSessionId,
                        ClockSegment = clockSegment,
                        Name = "reattached",
                        MethodIdentity = runtime.MethodIdentity
                    });
                }
            }
            catch (Exception exception)
            {
                var code = exception is BridgeCommandException ? ((BridgeCommandException)exception).Code : "E_HOOK_PATCH_FAILED";
                FaultDefinition(definition, Volatile.Read(ref definition.Revision), code, exception.Message, 0, "restore");
            }
        }

        private static MethodHookTargetSpec BuildTargetSpec(MethodInfo method)
        {
            var declaring = method.DeclaringType;
            var parameters = method.GetParameters();
            var types = new List<string>(parameters.Length);
            foreach (var parameter in parameters) types.Add(MethodHookTargetResolver.NormalizeTypeName(parameter.ParameterType));
            return new MethodHookTargetSpec
            {
                AssemblyName = declaring.Assembly.GetName().Name,
                TypeName = declaring.FullName,
                MethodName = method.Name,
                ParameterTypes = types,
                ReturnType = MethodHookTargetResolver.NormalizeTypeName(method.ReturnType),
                IsStatic = method.IsStatic
            };
        }

        // ------------------------------------------------------------------
        // dry-run：只报告本动作会尝试的变更，不编译、不加载、不安装、不保存
        // ------------------------------------------------------------------

        internal static MethodHookInstallResult DryRunInstall(MethodHookInstallPlan plan)
        {
            return new MethodHookInstallResult
            {
                Revision = 0,
                State = "planned",
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                Activation = plan.Activation,
                Priority = plan.Priority,
                Target = MethodHookTargetResolver.Summarize(plan.Resolved, BuildTargetSpec(plan.Resolved.Method)),
                Capture = plan.Capture.Summarize(),
                Callbacks = new List<MethodHookCallbackSummary>
                {
                    new MethodHookCallbackSummary { Slot = "prefix", Present = plan.Callbacks != null && !string.IsNullOrWhiteSpace(plan.Callbacks.PrefixCode), SourceBytes = plan.Callbacks == null ? 0 : Encoding.UTF8.GetByteCount(plan.Callbacks.PrefixCode ?? string.Empty) },
                    new MethodHookCallbackSummary { Slot = "postfix", Present = plan.Callbacks != null && !string.IsNullOrWhiteSpace(plan.Callbacks.PostfixCode), SourceBytes = plan.Callbacks == null ? 0 : Encoding.UTF8.GetByteCount(plan.Callbacks.PostfixCode ?? string.Empty) }
                },
                DryRun = true,
                PlannedEffects = new List<MethodHookPlannedEffect>
                {
                    new MethodHookPlannedEffect
                    {
                        Type = "debug.hook-install",
                        State = "planned",
                        Note = "Would compile callback source when present, create one definition, attach the owned Adapter to the exact target and publish this session's snapshot. Compilation success and business hits are not proven by this preview."
                    }
                }
            };
        }

        internal static MethodHookReplaceResult DryRunReplace(MethodHookMutationPlan plan)
        {
            var definition = plan.Definition;
            return new MethodHookReplaceResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                Changed = false,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                Target = definition.TargetSpec == null ? null : SummarizeStoredTarget(definition),
                Capture = plan.Capture.Summarize(),
                Callbacks = new List<MethodHookCallbackSummary>
                {
                    new MethodHookCallbackSummary { Slot = "prefix", Present = plan.Callbacks != null && !string.IsNullOrWhiteSpace(plan.Callbacks.PrefixCode), SourceBytes = plan.Callbacks == null ? 0 : Encoding.UTF8.GetByteCount(plan.Callbacks.PrefixCode ?? string.Empty) },
                    new MethodHookCallbackSummary { Slot = "postfix", Present = plan.Callbacks != null && !string.IsNullOrWhiteSpace(plan.Callbacks.PostfixCode), SourceBytes = plan.Callbacks == null ? 0 : Encoding.UTF8.GetByteCount(plan.Callbacks.PostfixCode ?? string.Empty) }
                },
                DryRun = true,
                PlannedEffects = new List<MethodHookPlannedEffect>
                {
                    new MethodHookPlannedEffect
                    {
                        Type = "debug.hook-replace",
                        HookId = definition.HookId,
                        State = MethodHookStateText.Of(definition.State),
                        Note = "Would compile the new callback source when present and swap this definition's capture and callback configuration without reinstalling the target patch."
                    }
                }
            };
        }

        internal static MethodHookEnableResult DryRunEnable(MethodHookMutationPlan plan)
        {
            var definition = plan.Definition;
            return new MethodHookEnableResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                Changed = !plan.NoOp,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DryRun = true,
                PlannedEffects = new List<MethodHookPlannedEffect>
                {
                    new MethodHookPlannedEffect
                    {
                        Type = "debug.hook-enable",
                        HookId = definition.HookId,
                        State = MethodHookStateText.Of(definition.State),
                        Note = plan.NoOp
                            ? "The definition is already armed or dormant; the real call would be a no-op with fresh readback."
                            : "Would re-resolve the exact target, recompile callback source when needed, attach the owned Adapter and publish the new revision."
                    }
                }
            };
        }

        internal static MethodHookDisableResult DryRunDisable(MethodHookMutationPlan plan)
        {
            var definition = plan.Definition;
            return new MethodHookDisableResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                Changed = !plan.NoOp,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DryRun = true,
                PlannedEffects = new List<MethodHookPlannedEffect>
                {
                    new MethodHookPlannedEffect
                    {
                        Type = "debug.hook-disable",
                        HookId = definition.HookId,
                        State = MethodHookStateText.Of(definition.State),
                        Note = plan.NoOp
                            ? "The definition is already disabled; the real call would be a no-op with fresh readback."
                            : "Would close this definition's gate, publish it as disabled and release the owned Adapter only when no other definition keeps the target."
                    }
                }
            };
        }

        internal static MethodHookRemoveResult DryRunRemove(MethodHookMutationPlan plan)
        {
            var definition = plan.Definition;
            var remaining = Definitions.Values.Count(item => !ReferenceEquals(item, definition) && string.Equals(item.TargetKey, definition.TargetKey, StringComparison.Ordinal));
            return new MethodHookRemoveResult
            {
                HookId = definition.HookId,
                State = "planned",
                Removed = false,
                AdapterReleased = false,
                RemainingDefinitionsForTarget = remaining,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DryRun = true,
                PlannedEffects = new List<MethodHookPlannedEffect>
                {
                    new MethodHookPlannedEffect
                    {
                        Type = "debug.hook-remove",
                        HookId = definition.HookId,
                        State = "planned",
                        Note = remaining == 0
                            ? "Would delete this definition, publish the snapshot, release the owned Adapter for this target and keep buffered events readable."
                            : "Would delete this definition and publish the snapshot while keeping the shared Adapter for the remaining definitions."
                    }
                }
            };
        }
    }
}
#endif

