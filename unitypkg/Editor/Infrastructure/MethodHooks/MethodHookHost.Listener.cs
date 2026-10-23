#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 监听式触发的 hook 侧实现：listener 条件的 effect 前校验、命中线程上的过滤与有限命中消费，
    /// 以及“已绑定 exact target 到达执行点”时的跨线程投递取得。
    ///
    /// 规则：
    /// - 当场 handler 在原调用线程同步执行；只有这条路径可以改参数或返回值；
    /// - 跨线程操作命名一个已经绑定的 exact target，并在该 target 到达执行点时原子取得执行权；
    /// - 目标未到达时只留在 pending 直至过期，永不补执行、永不换线程；
    /// - listener 过滤只读取已发布的不可变快照与原子计数，不遍历可变定义集合。
    /// </summary>
    internal static partial class MethodHookHost
    {
        private static long sampledHits;
        private static long coverageSkips;
        private static long listenerExhaustedHits;

        // ------------------------------------------------------------------
        // 条件规范化与发布
        // ------------------------------------------------------------------

        /// <summary>install/replace 的 listener 校验与规范化；失败在 effect 之前抛出稳定错误。</summary>
        internal static MethodHookListenerState PrepareListener(MethodHookListenerSpec spec)
        {
            if (spec == null) return null;
            var trigger = string.IsNullOrEmpty(spec.Trigger) ? DebugTriggerSources.MethodHit : spec.Trigger;
            if (!DebugTriggerSources.IsHookTrigger(trigger))
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field listener.trigger is invalid: it must be a trigger source a listener can subscribe to.",
                    new Dictionary<string, object> { { "path", "$.listener.trigger" }, { "expected", "method-hit, log, host-signal or player-loop-phase" }, { "actual", trigger ?? "null" }, { "availableTriggers", DebugTriggerSources.All() } },
                    new[]
                    {
                        "Run a main-thread listener through debug.program-run with executionSite editor-tick or player-loop-phase instead of subscribing the editor tick as a listener trigger.",
                        "Run tools describe debug.hook-install to read the listener contract, then submit the corrected input."
                    });
            // PlayerLoop phase 只在 Editor Play 下存在，并且一个 phase 到达没有方法实例也没有参数：
            // 声明自身的合法性先于执行域可用性校验，因此必然无法满足的过滤组合在任何环境下都被同一条拒绝，
            // 而且拒绝与不可用一样都发生在安装任何 phase 现场之前。
            var phase = string.Empty;
            if (string.Equals(trigger, DebugTriggerSources.PlayerLoopPhase, StringComparison.Ordinal))
            {
                if (!DebugPlayerLoopPhases.IsDefined(spec.Phase)) throw DebugPlayerLoopHost.PhaseUnknown("$.listener.phase", spec.Phase);
                RequireInvocationFreePhaseFilters(spec);
                DebugPlayerLoopHost.RequirePlayDomain(
                    trigger,
                    new Dictionary<string, object> { { "path", "$.listener.trigger" }, { "actual", trigger }, { "availableTriggers", DebugTriggerSources.All() } });
                phase = spec.Phase;
            }
            else if (!string.IsNullOrEmpty(spec.Phase))
            {
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field listener.phase is only meaningful together with listener.trigger player-loop-phase, so this listener was refused before any effect.",
                    new Dictionary<string, object> { { "path", "$.listener.phase" }, { "expected", "omitted unless listener.trigger is player-loop-phase" }, { "actual", spec.Phase }, { "trigger", trigger } },
                    new[] { "Omit listener.phase, or set listener.trigger player-loop-phase and declare the phase explicitly." });
            }
            if (string.IsNullOrEmpty(spec.SessionId))
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "A listener must name the session that owns its planned injections.",
                    new Dictionary<string, object> { { "path", "$.listener.sessionId" }, { "expected", "the id of an open debug session" }, { "actual", "missing" } },
                    new[]
                    {
                        "Run tools run debug.session-create to establish the owner of this investigation.",
                        "Run tools run debug.session-list to read the currently open sessions."
                    });
            var maxHits = spec.MaxHits ?? 0L;
            if (maxHits < 0L || maxHits > DebugLimits.MaxListenerHits)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field listener.maxHits is invalid: it must be inside the declared hit budget.",
                    new Dictionary<string, object> { { "path", "$.listener.maxHits" }, { "expected", "0.." + DebugLimits.MaxListenerHits.ToString(CultureInfo.InvariantCulture) }, { "actual", maxHits.ToString(CultureInfo.InvariantCulture) } },
                    new[] { "Pass maxHits inside the declared budget, or omit it for an unbounded listener; 1 means one-shot." });
            var conditions = new List<MethodHookCondition>();
            if (spec.Conditions != null)
            {
                if (spec.Conditions.Count > DebugLimits.MaxListenerConditions)
                    throw new BridgeCommandException(
                        "E_SCHEMA_INVALID",
                        "Field listener.conditions is invalid: it declares more conditions than the budget allows.",
                        new Dictionary<string, object> { { "path", "$.listener.conditions" }, { "expected", "at most " + DebugLimits.MaxListenerConditions.ToString(CultureInfo.InvariantCulture) }, { "actual", spec.Conditions.Count.ToString(CultureInfo.InvariantCulture) } },
                        new[] { "Reduce the declared conditions, or move the complex predicate into explicit debug code." });
                foreach (var condition in spec.Conditions) conditions.Add(NormalizeCondition(condition));
            }
            return new MethodHookListenerState
            {
                SessionId = spec.SessionId,
                Trigger = trigger,
                Phase = phase,
                MaxHits = maxHits,
                ThreadId = spec.ThreadId ?? 0,
                InstanceIdentity = spec.InstanceIdentity ?? string.Empty,
                Conditions = conditions.ToArray()
            };
        }

        /// <summary>
        /// 一个 phase 触发声明的可评估性校验：phase 到达没有方法实例、没有参数也没有返回值，
        /// 因此只有字段比较能在该触发源上被满足。声明参数条件或实例身份筛选的组合永远不可能触发，
        /// 必须在安装现场之前就以稳定错误码明确拒绝，而不是留下一个永远不会触发的声明。
        /// </summary>
        private static void RequireInvocationFreePhaseFilters(MethodHookListenerSpec spec)
        {
            if (!string.IsNullOrEmpty(spec.InstanceIdentity))
                throw PhaseFilterRequiresInvocation("$.listener.instanceIdentity", "an omitted instance identity", spec.InstanceIdentity);
            if (spec.Conditions == null) return;
            for (var index = 0; index < spec.Conditions.Count; index++)
            {
                var condition = spec.Conditions[index];
                if (condition == null || !string.Equals(condition.Kind, MethodHookConditionKinds.Argument, StringComparison.Ordinal)) continue;
                throw PhaseFilterRequiresInvocation("$.listener.conditions[" + index.ToString(CultureInfo.InvariantCulture) + "].kind", "field", condition.Kind);
            }
        }

        /// <summary>phase 触发源上不可评估的过滤条件的稳定拒绝；恢复路径直接给出可执行改写方式。</summary>
        private static BridgeCommandException PhaseFilterRequiresInvocation(string path, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION",
                "A PlayerLoop phase arrival has no method instance and no arguments, so a listener that filters on either could never fire; this declaration was refused before any phase site was installed.",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", path },
                    { "expected", expected },
                    { "actual", actual ?? "null" },
                    { "trigger", DebugTriggerSources.PlayerLoopPhase }
                },
                new[]
                {
                    "Compare a declared field instead: a field condition is evaluated on the phase's own main thread and can be satisfied there.",
                    "Declare listener.trigger method-hit when the filter needs an argument or an instance identity.",
                    "Run tools describe debug.hook-install to read the listener contract, then submit the corrected input."
                });
        }

        /// <summary>一个命中条件的规范化；字面量按规范 JSON 保存，比较时不依赖字段顺序。</summary>
        private static MethodHookCondition NormalizeCondition(MethodHookConditionSpec spec)
        {
            if (spec == null) throw ConditionInvalid("$.listener.conditions[]", "a condition object", "null");
            var kind = spec.Kind;
            if (!MethodHookConditionKinds.IsDefined(kind)) throw ConditionInvalid("$.listener.conditions[].kind", string.Join(", ", MethodHookConditionKinds.All()), kind ?? "null");
            var op = spec.Operator;
            if (!MethodHookConditionOperators.IsDefined(op)) throw ConditionInvalid("$.listener.conditions[].operator", string.Join(", ", MethodHookConditionOperators.All()), op ?? "null");
            if (string.IsNullOrEmpty(spec.Name)) throw ConditionInvalid("$.listener.conditions[].name", "a non-empty argument index or field name", "empty");
            var condition = new MethodHookCondition
            {
                Kind = kind,
                Name = spec.Name,
                Operator = op,
                DeclaringType = spec.DeclaringType ?? string.Empty,
                LiteralCanonical = UnityCliSerializer.Canonicalize(spec.Value)
            };
            if (!string.Equals(kind, MethodHookConditionKinds.Argument, StringComparison.Ordinal)) return condition;
            int index;
            if (!int.TryParse(spec.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0)
                throw ConditionInvalid("$.listener.conditions[].name", "a zero-based argument index", spec.Name);
            condition.ArgumentIndex = index;
            return condition;
        }

        private static BridgeCommandException ConditionInvalid(string path, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "Listener condition field is invalid.",
                new Dictionary<string, object> { { "path", path }, { "expected", expected }, { "actual", actual ?? "null" } },
                new[] { "Run tools describe debug.hook-install to read the listener condition contract, then submit the corrected input." });
        }

        /// <summary>
        /// 发布时解析 listener 条件引用的字段。命中路径因此只做 GetValue 与有界投影，
        /// 与捕获字段使用同一解析顺序；无法解析的字段保持 null，并在匹配时按不匹配处理。
        /// </summary>
        internal static void ResolveListenerFields(MethodHookListenerState listener, MethodInfo resolved)
        {
            if (listener == null || listener.Conditions.Length == 0) return;
            var declaringType = resolved == null ? null : resolved.DeclaringType;
            foreach (var condition in listener.Conditions)
            {
                if (string.Equals(condition.Kind, MethodHookConditionKinds.Argument, StringComparison.Ordinal)) continue;
                try
                {
                    condition.Field = MethodHookTargetResolver.ResolveField(declaringType, condition.DeclaringType, condition.Name);
                }
                catch
                {
                    condition.Field = null;
                }
            }
        }

        /// <summary>listener 事实的公开投影；未声明 listener 时返回 null。</summary>
        internal static MethodHookListenerSummary SummarizeListener(MethodHookListenerState listener)
        {
            if (listener == null) return null;
            return new MethodHookListenerSummary
            {
                SessionId = listener.SessionId,
                Trigger = listener.Trigger,
                Phase = listener.Phase,
                MaxHits = listener.MaxHits,
                Hits = Interlocked.Read(ref listener.Hits),
                FilteredHits = Interlocked.Read(ref listener.Filtered),
                ThreadId = listener.ThreadId,
                InstanceIdentity = listener.InstanceIdentity,
                Conditions = listener.Conditions.Select(condition => condition.Describe()).ToList()
            };
        }

        // ------------------------------------------------------------------
        // 命中路径
        // ------------------------------------------------------------------

        /// <summary>
        /// 一次命中是否通过该定义的 listener。通过后消费一个有限命中次数。
        /// 次数用尽或条件不匹配都按“未匹配”处理并只计数，绝不执行回调。
        /// </summary>
        private static bool ListenerAllows(MethodHookBinding binding, object instance, object[] arguments, bool allowUnityIdentity)
        {
            var listener = binding.Listener;
            if (listener == null) return true;
            var identity = MethodHookInstanceIdentity.Of(instance, allowUnityIdentity);
            if (!listener.Matches(Thread.CurrentThread.ManagedThreadId, identity, arguments, instance, allowUnityIdentity)) return false;
            if (listener.TryCountHit()) return true;
            ReportListenerExhausted(binding, listener);
            return false;
        }

        /// <summary>
        /// 有限命中已经用完的有界事实；读取端据此区分“已到次数上限”与“条件未匹配”。
        /// 方法命中路径与 PlayerLoop phase 路径共用这一处记录，因此该事实只有一个实现与一种形状。
        /// </summary>
        private static void ReportListenerExhausted(MethodHookBinding binding, MethodHookListenerState listener)
        {
            Interlocked.Increment(ref listenerExhaustedHits);
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                HookId = binding == null ? string.Empty : binding.HookId,
                Revision = binding == null ? 0L : binding.Revision,
                Kind = "coverage",
                SourceId = UnityCliDebugSources.Hook,
                Name = "listener-exhausted",
                MethodIdentity = binding == null || binding.Resolved == null ? string.Empty : MethodHookMethodText.Describe(binding.Resolved),
                Value = new Dictionary<string, object> { { "reason", "listener-exhausted" }, { "action", "callback-not-run" }, { "maxHits", listener.MaxHits } }
            });
        }

        /// <summary>
        /// 已绑定的 exact target 到达执行点时，在该命中线程上原子取得本 target 的 pending 投递。
        /// 取得执行权与取消竞争只有一个赢家；投递只执行一次，未到达的投递永不迟到执行。
        /// </summary>
        internal static void DispatchTargetDeliveries(string targetKey)
        {
            if (string.IsNullOrEmpty(targetKey)) return;
            DebugProgramHost.DispatchDeliveriesForTarget(targetKey, Thread.CurrentThread.ManagedThreadId);
        }

        /// <summary>
        /// 该 exact target 当前是否已经绑定：signal 投递必须有可寻址的执行点，
        /// 一个从未绑定的 target 会让投递永远 pending。
        /// </summary>
        internal static bool IsTargetBound(string targetKey)
        {
            lock (Sync) return !string.IsNullOrEmpty(targetKey) && Targets.ContainsKey(targetKey);
        }

        /// <summary>hook 派发的观测事实：命中、采样、丢弃与 listener 过滤计数。</summary>
        internal static Dictionary<string, object> DispatchObservability()
        {
            long hits = 0L;
            long filtered = 0L;
            lock (Sync)
            {
                foreach (var definition in Definitions.Values)
                {
                    hits += Interlocked.Read(ref definition.HitCount);
                    if (definition.Listener != null) filtered += Interlocked.Read(ref definition.Listener.Filtered);
                }
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "dispatchHits", hits },
                { "dispatchFiltered", filtered },
                { "dispatchSamples", Interlocked.Read(ref sampledHits) },
                { "dispatchDrops", Interlocked.Read(ref droppedSupplements) + Interlocked.Read(ref coverageSkips) },
                { "listenerExhaustedHits", Interlocked.Read(ref listenerExhaustedHits) },
                { "phaseListenerTriggers", Interlocked.Read(ref phaseListenerTriggers) },
                { "phaseListenerActionGaps", Interlocked.Read(ref phaseListenerActionGaps) },
                { "maxListenerConditions", (long)DebugLimits.MaxListenerConditions }
            };
        }

        /// <summary>本次 Unity 启动的触发观测计数作废；域重载与测试复位共用这里。</summary>
        private static void ResetListenerCounters()
        {
            Interlocked.Exchange(ref sampledHits, 0L);
            Interlocked.Exchange(ref coverageSkips, 0L);
            Interlocked.Exchange(ref listenerExhaustedHits, 0L);
            Interlocked.Exchange(ref phaseListenerTriggers, 0L);
            Interlocked.Exchange(ref phaseListenerActionGaps, 0L);
        }

        /// <summary>两个 listener 配置是否等价；只比较声明的过滤条件，不比较命中计数。</summary>
        private static bool ListenerEquals(MethodHookListenerState left, MethodHookListenerState right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (!string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
                || !string.Equals(left.Trigger, right.Trigger, StringComparison.Ordinal)
                || !string.Equals(left.Phase, right.Phase, StringComparison.Ordinal)
                || left.MaxHits != right.MaxHits
                || left.ThreadId != right.ThreadId
                || !string.Equals(left.InstanceIdentity, right.InstanceIdentity, StringComparison.Ordinal)
                || left.Conditions.Length != right.Conditions.Length) return false;
            for (var index = 0; index < left.Conditions.Length; index++)
            {
                var a = left.Conditions[index];
                var b = right.Conditions[index];
                if (!string.Equals(a.Kind, b.Kind, StringComparison.Ordinal)
                    || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                    || !string.Equals(a.Operator, b.Operator, StringComparison.Ordinal)
                    || !string.Equals(a.DeclaringType, b.DeclaringType, StringComparison.Ordinal)
                    || !string.Equals(a.LiteralCanonical, b.LiteralCanonical, StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// listener 条件的求值实现。实际值按与事件捕获相同的投影边界物化，
    /// 再与字面量的规范 JSON 比较，因此条件不依赖类型具体形状。
    /// </summary>
    internal static class MethodHookListenerEvaluator
    {
        internal static bool Matches(MethodHookCondition condition, object[] arguments, object instance, bool allowUnityIdentity)
        {
            if (condition == null) return true;
            object actual;
            if (!TryReadValue(condition, arguments, instance, out actual)) return false;
            var truncation = new MethodHookProjection.Truncation();
            var projected = MethodHookProjection.Project(actual, truncation, allowUnityIdentity);
            var canonical = UnityCliSerializer.Canonicalize(projected);
            var equals = string.Equals(canonical, condition.LiteralCanonical, StringComparison.Ordinal);
            return string.Equals(condition.Operator, MethodHookConditionOperators.NotEquals, StringComparison.Ordinal) ? !equals : equals;
        }

        /// <summary>读取条件引用的实际值；参数越界或字段不可解析时按不匹配处理并如实记录原因。</summary>
        private static bool TryReadValue(MethodHookCondition condition, object[] arguments, object instance, out object value)
        {
            value = null;
            if (string.Equals(condition.Kind, MethodHookConditionKinds.Argument, StringComparison.Ordinal))
            {
                if (arguments == null || condition.ArgumentIndex < 0 || condition.ArgumentIndex >= arguments.Length) return false;
                value = arguments[condition.ArgumentIndex];
                return true;
            }
            if (condition.Field == null) return false;
            try
            {
                value = condition.Field.GetValue(condition.Field.IsStatic ? null : instance);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
#endif
