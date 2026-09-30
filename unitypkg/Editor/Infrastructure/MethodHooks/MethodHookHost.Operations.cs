#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>method-hook 的公开操作：安装、替换、启停、删除、摘要、精查与事件续读。</summary>
    internal static partial class MethodHookHost
    {
        /// <summary>install 的 effect 前校验：语义、exact target、捕获字段、容量与后端可用。</summary>
        internal static MethodHookInstallPlan PrepareInstall(MethodHookInstallRequest request)
        {
            EnsureReady();
            if (request == null) throw new BridgeCommandException("E_SCHEMA_INVALID", "method-hook.install requires a request body.", "Pass target plus capture or callbacks.");
            var capture = NormalizeCapture(request.Capture);
            var callbacks = NormalizeCallbacks(request.Callbacks);
            if (!capture.HasBehaviour(callbacks))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "A hook requires at least one effective capture or callback behaviour.", "Enable capture.arguments, capture.result, an instance or static field, or provide prefixCode/postfixCode.");
            var activation = string.IsNullOrEmpty(request.Activation) ? "play" : request.Activation;
            if (!string.Equals(activation, "play", StringComparison.Ordinal) && !string.Equals(activation, "editor-and-play", StringComparison.Ordinal))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "activation must be play or editor-and-play.", "Pass activation: play or editor-and-play.");
            var resolved = MethodHookTargetResolver.Resolve(request.Target);
            EnsureBackend();
            lock (Sync)
            {
                if (Definitions.Count >= MethodHookLimits.MaxDefinitionsPerSession)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "The Editor session already holds " + MethodHookLimits.MaxDefinitionsPerSession.ToString(CultureInfo.InvariantCulture) + " hook definitions.", "Disable or remove a definition before installing another one.");
                var targetKey = MethodHookTargetKey.From(resolved.Method).Text;
                if (Definitions.Values.Count(definition => string.Equals(definition.TargetKey, targetKey, StringComparison.Ordinal)) >= MethodHookLimits.MaxDefinitionsPerTarget)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "This exact target already has " + MethodHookLimits.MaxDefinitionsPerTarget.ToString(CultureInfo.InvariantCulture) + " hook definitions.", "Remove one of the definitions on this target before installing another one.");
                if (!Targets.ContainsKey(MethodHookTargetKey.From(resolved.Method).Text) && AdapterIdentities.Count >= MethodHookLimits.MaxAdapterIdentities)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "This domain already loaded " + MethodHookLimits.MaxAdapterIdentities.ToString(CultureInfo.InvariantCulture) + " distinct adapter identities.", "Reuse existing targets or restart the Editor before targeting a new one.");
                foreach (var field in capture.InstanceFields) field.Field = MethodHookTargetResolver.ResolveField(resolved.Method.DeclaringType, field.DeclaringType, field.FieldName);
                foreach (var field in capture.StaticFields) field.Field = MethodHookTargetResolver.ResolveField(resolved.Method.DeclaringType, field.DeclaringType, field.FieldName);
                if (callbacks != null && (!string.IsNullOrWhiteSpace(callbacks.PrefixCode) || !string.IsNullOrWhiteSpace(callbacks.PostfixCode))) EnsureCompileBudget(0);
            }
            return new MethodHookInstallPlan
            {
                Resolved = resolved,
                Capture = capture,
                Callbacks = callbacks,
                Activation = activation,
                Priority = request.Priority ?? 0,
                EmitHitEvents = request.EmitHitEvents ?? true,
                Label = request.Label ?? string.Empty
            };
        }

        /// <summary>effect barrier 之后建立 Attachment、原子保存并读回，然后打开 gate。</summary>
        internal static MethodHookInstallResult ApplyInstall(MethodHookInstallPlan plan, MethodHookPreparedCallbacks prepared)
        {
            if (plan == null) throw new BridgeCommandException("E_SCHEMA_INVALID", "The install plan is unavailable.", "Submit method-hook.install again.");
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
            };
            definition.StateBag = new MethodHookStateBag(definition.HookId);
            MethodHookTargetRuntime runtime;
            lock (Sync)
            {
                InstallSequence(definition);
                Definitions[definition.HookId] = definition;
                runtime = EnsureTargetRuntime(plan.Resolved, definition.TargetSpec);
                runtime.Definitions.Add(definition);
                runtime.GateOpen = false;
            }
            try
            {
                AttachIfNeeded(runtime);
                lock (Sync)
                {
                    definition.State = definition.ActivationMatches(playScope) ? MethodHookState.Armed : MethodHookState.Dormant;
                    PersistDefinitions("install");
                }
                AppendLifecycle(definition, "installed", MethodHookStateText.Of(definition.State));
            }
            catch (Exception)
            {
                RollbackDefinition(definition, runtime);
                throw;
            }
            finally
            {
                lock (Sync) runtime.GateOpen = true;
            }
            return new MethodHookInstallResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                Activation = definition.Activation,
                Priority = definition.Priority,
                EventCursor = Events.BeginCursor(new MethodHookEventFilter { HookId = definition.HookId }, UnityCliBridge.EditorSessionId()),
                Target = MethodHookTargetResolver.Summarize(plan.Resolved, definition.TargetSpec),
                Capture = definition.Capture.Summarize(),
                Callbacks = SummarizeCallbacks(definition)
            };
        }

        /// <summary>replace 的 effect 前校验；目标不变，因此不重新安装 detour。</summary>
        internal static MethodHookMutationPlan PrepareReplace(MethodHookReplaceRequest request)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, request == null ? null : request.EditorSessionId, request == null ? (long?)null : request.ExpectedRevision);
            var capture = NormalizeCapture(request.Capture);
            var callbacks = NormalizeCallbacks(request.Callbacks);
            if (!capture.HasBehaviour(callbacks))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "A hook requires at least one effective capture or callback behaviour.", "Enable capture fields or provide prefixCode/postfixCode.");
            if (callbacks != null && (!string.IsNullOrWhiteSpace(callbacks.PrefixCode) || !string.IsNullOrWhiteSpace(callbacks.PostfixCode))) EnsureBackend();
            return new MethodHookMutationPlan
            {
                Action = "replace",
                Definition = definition,
                Capture = capture,
                Callbacks = callbacks,
                Priority = request.Priority ?? definition.Priority,
                EmitHitEvents = request.EmitHitEvents ?? definition.EmitHitEvents
            };
        }

        /// <summary>替换回调指针与捕获配置；保留启停意图，不重复安装 detour。</summary>
        internal static MethodHookReplaceResult ApplyReplace(MethodHookMutationPlan plan, MethodHookPreparedCallbacks prepared)
        {
            var definition = plan.Definition;
            if (definition.State == MethodHookState.Disabled || definition.State == MethodHookState.Faulted)
            {
                // 更新配置但不改变启停意图；faulted 仍需显式 enable 才重新激活。
                lock (Sync) ApplyReplaceConfiguration(definition, plan, prepared);
                AppendLifecycle(definition, "replaced", "configuration-only");
                return BuildReplaceResult(definition, true);
            }
            MethodHookTargetRuntime runtime;
            lock (Sync)
            {
                runtime = FindRuntimeByKey(definition.TargetKey);
                if (runtime != null) runtime.GateOpen = false;
            }
            try
            {
                lock (Sync) ApplyReplaceConfiguration(definition, plan, prepared);
                AppendLifecycle(definition, "replaced", "applied");
                return BuildReplaceResult(definition, true);
            }
            finally
            {
                if (runtime != null) lock (Sync) runtime.GateOpen = true;
            }
        }

        private static void ApplyReplaceConfiguration(MethodHookDefinition definition, MethodHookMutationPlan plan, MethodHookPreparedCallbacks prepared)
        {
            var prefix = plan.Callbacks == null ? string.Empty : plan.Callbacks.PrefixCode ?? string.Empty;
            var postfix = plan.Callbacks == null ? string.Empty : plan.Callbacks.PostfixCode ?? string.Empty;
            var changed = !string.Equals(definition.PrefixCode, prefix, StringComparison.Ordinal)
                || !string.Equals(definition.PostfixCode, postfix, StringComparison.Ordinal)
                || !CaptureEquals(definition.Capture, plan.Capture)
                || definition.Priority != plan.Priority
                || definition.EmitHitEvents != plan.EmitHitEvents;
            if (!changed) return;
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

        private static MethodHookReplaceResult BuildReplaceResult(MethodHookDefinition definition, bool changed)
        {
            return new MethodHookReplaceResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                Changed = changed,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                EventCursor = Events.BeginCursor(new MethodHookEventFilter { HookId = definition.HookId }, UnityCliBridge.EditorSessionId()),
                Target = definition.TargetSpec == null ? null : SummarizeStoredTarget(definition),
                Capture = definition.Capture.Summarize(),
                Callbacks = SummarizeCallbacks(definition)
            };
        }

        /// <summary>enable 的 effect 前校验；armed/dormant 为 no-op，disabled/faulted 需要重解析。</summary>
        internal static MethodHookMutationPlan PrepareEnable(MethodHookEnableRequest request)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, request == null ? null : request.EditorSessionId, request == null ? (long?)null : request.ExpectedRevision);
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
                if (!known && AdapterIdentities.Count >= MethodHookLimits.MaxAdapterIdentities)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "This domain already loaded " + MethodHookLimits.MaxAdapterIdentities.ToString(CultureInfo.InvariantCulture) + " distinct adapter identities.", "Restart the Editor before enabling a hook on a new target.");
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
                        EventCursor = Events.BeginCursor(new MethodHookEventFilter { HookId = definition.HookId }, UnityCliBridge.EditorSessionId())
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
                runtime.GateOpen = false;
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
                    MethodHookStore.SaveSuppression(Suppression);
                }
                AppendLifecycle(definition, "enabled", MethodHookStateText.Of(definition.State));
            }
            finally
            {
                lock (Sync) runtime.GateOpen = true;
            }
            return new MethodHookEnableResult
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                State = MethodHookStateText.Of(definition.State),
                Changed = true,
                CallbackRecompiled = prepared != null,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                EventCursor = Events.BeginCursor(new MethodHookEventFilter { HookId = definition.HookId }, UnityCliBridge.EditorSessionId())
            };
        }

        /// <summary>disable 的 effect 前校验；已经 disabled 的定义是 no-op。</summary>
        internal static MethodHookMutationPlan PrepareDisable(MethodHookDisableRequest request)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, request == null ? null : request.EditorSessionId, request == null ? (long?)null : request.ExpectedRevision);
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
                var released = ReleaseAdapterIfUnused(definition);
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

        /// <summary>remove 的 effect 前校验；不存在时返回 E_HOOK_NOT_FOUND。</summary>
        internal static MethodHookMutationPlan PrepareRemove(MethodHookRemoveRequest request)
        {
            EnsureReady();
            var definition = RequireDefinition(request == null ? null : request.HookId, request == null ? null : request.EditorSessionId, request == null ? (long?)null : request.ExpectedRevision);
            return new MethodHookMutationPlan { Action = "remove", Definition = definition };
        }

        /// <summary>删除定义与其 Attachment；缓冲历史事件仍可读。</summary>
        internal static MethodHookRemoveResult ApplyRemove(MethodHookMutationPlan plan)
        {
            var definition = plan.Definition;
            lock (Sync)
            {
                Definitions.Remove(definition.HookId);
                Suppression.Remove(definition.HookId);
                var released = ReleaseAdapterIfUnused(definition);
                var remaining = Definitions.Values.Count(item => string.Equals(item.TargetKey, definition.TargetKey, StringComparison.Ordinal));
                PersistDefinitions("remove");
                MethodHookStore.SaveSuppression(Suppression);
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
            var limit = request == null || request.Limit == null ? MethodHookLimits.ListDefaultLimit : Math.Min(Math.Max(1, request.Limit.Value), MethodHookLimits.ListMaxLimit);
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
                Items = page.Select(definition => definition.ToListItem()).ToList(),
                Total = ordered.Count,
                NextOffset = nextOffset,
                HasMore = hasMore,
                NextCursor = hasMore ? EncodeOffsetCursor(session, scope, nextOffset) : null,
                EditorSessionId = session
            };
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
                    TotalRecords = Events.Count,
                    OldestAvailableCursor = Events.OldestAvailableSequence,
                    LatestSequence = Events.LastSequence,
                    Gaps = Events.Gaps.ToList()
                },
                StateBytes = definition.StateBag == null ? 0 : definition.StateBag.ByteSize,
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
                var page = SubstringByUtf8Bytes(source ?? string.Empty, offset, MethodHookLimits.MaxInfoSourcePageBytes);
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

        /// <summary>只读事件续读；多个 reader 各自独立，不消费共享 cursor。</summary>
        internal static MethodHookEventsResult ReadEvents(MethodHookEventsRequest request)
        {
            EnsureReady();
            var session = UnityCliBridge.EditorSessionId();
            var filter = new MethodHookEventFilter
            {
                HookId = request == null ? null : request.HookId,
                Kind = request == null ? null : request.Kind,
                PlaySessionId = request == null ? null : request.PlaySessionId
            };
            var limit = EventsPageSize(request == null ? (int?)null : request.Limit);
            MethodHookEventPage page;
            try
            {
                page = Events.Read(filter, request == null ? null : request.Cursor, session, limit);
            }
            catch (MethodHookCursorException exception)
            {
                var details = new Dictionary<string, object> { { "oldestAvailableCursor", Events.OldestAvailableSequence } };
                throw new BridgeCommandException(exception.Code, exception.Message, details, exception.NextActions);
            }
            return new MethodHookEventsResult
            {
                Events = page.Records.Select(record => record.ToItem()).ToList(),
                NextCursor = new MethodHookEventCursor
                {
                    EditorSessionId = session,
                    ScopeHash = filter.ScopeKey(session),
                    ScannedSequence = page.NextScannedSequence
                }.Encode(),
                HasMore = page.HasMore,
                OldestAvailableCursor = page.OldestAvailableSequence,
                Gaps = Events.Gaps.ToList(),
                EditorSessionId = session
            };
        }

        /// <summary>事件页的规范化上限；Tool 声明的 read budget 与 host 实际读取必须使用同一数值。</summary>
        internal static int EventsPageSize(int? requested)
        {
            var value = requested ?? MethodHookLimits.EventsDefaultLimit;
            return Math.Min(Math.Max(1, value), MethodHookLimits.EventsMaxLimit);
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
                    AppendEvent(new MethodHookEventRecord
                    {
                        HookId = definition.HookId,
                        Revision = definition.Revision,
                        Kind = "lifecycle",
                        PlaySessionId = playSessionId,
                        ClockSegment = clockSegment,
                        Frame = tickCounter,
                        Name = "reattached",
                        MethodIdentity = runtime.MethodIdentity
                    });
                }
            }
            catch (Exception exception)
            {
                var code = exception is BridgeCommandException ? ((BridgeCommandException)exception).Code : "E_HOOK_PATCH_FAILED";
                FaultDefinition(definition, code, exception.Message, 0, "restore");
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
                        Type = "method-hook.install",
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
                        Type = "method-hook.replace",
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
                        Type = "method-hook.enable",
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
                        Type = "method-hook.disable",
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
                        Type = "method-hook.remove",
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

