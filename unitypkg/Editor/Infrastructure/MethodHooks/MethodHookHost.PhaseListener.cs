#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// 监听式触发源在真实 PlayerLoop phase 上的到达与执行。
    ///
    /// 规则：
    /// - phase 到达只由中央 DebugPlayerLoopHost 插入的子系统驱动：这里不注册 PlayerLoop 子系统、
    ///   不注册 EditorApplication.update，也不建立第二个 scheduler 或第二份投递预算。
    /// - 一个真实 phase 到达时，只有声明了该 phase 的**活动**定义被求值，并复用既有 listener 的条件、
    ///   线程与有限命中规则；满足的定义在同一执行点段投递，随后由同一队列、同一每段预算与同一单赢家语义执行。
    /// - 有限命中用尽后立即通过既有释放路径归还 phase 现场与计划注入预算，不留一个永远不会触发的声明。
    /// - 一个 phase 到达没有方法实例、没有参数也没有返回值，因此只执行不需要方法现场的绑定动作，
    ///   其余声明动作以此为稳定覆盖缺口，绝不静默跳过。
    /// - 非主线程不可达：PlayerLoop phase 只在主线程执行，因此这里可以读取托管字段与主线程时钟。
    /// </summary>
    internal static partial class MethodHookHost
    {
        /// <summary>phase 触发源上无法执行的声明动作的稳定覆盖事实名。</summary>
        internal const string PhaseActionCoverageName = "phase-action-requires-invocation";

        private static long phaseListenerTriggers;
        private static long phaseListenerActionGaps;

        /// <summary>
        /// 一个真实 PlayerLoop phase 到达时的监听式触发求值；只由 DebugScheduler 的 phase 推进段调用。
        /// 没有声明该 phase 的活动 listener 时不做任何分配与投递。
        /// </summary>
        internal static void TriggerPhaseListeners(string phase)
        {
            if (string.IsNullOrEmpty(phase) || !DebugPlayerLoopPhases.IsDefined(phase)) return;
            if (!HasPhaseListener(phase)) return;
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var mainThread = IsMainThread();
            // 到达现场没有实例，因此实例身份与条件比较都按“无实例”求值：这与 method-hit 路径共用同一实现。
            var identity = MethodHookInstanceIdentity.Of(null, mainThread);
            foreach (var binding in PhaseListenerCandidates(phase))
            {
                var listener = binding.Listener;
                if (!listener.Matches(threadId, identity, null, null, mainThread)) continue;
                if (!listener.TryCountHit())
                {
                    ReportListenerExhausted(binding, listener);
                    continue;
                }
                // 投递给该 phase 自己，由同一执行点段、同一每段预算与同一单赢家语义执行。
                DebugScheduler.EnqueueDelivery(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    listener.Phase,
                    null,
                    binding.Revision,
                    DebugLimits.ProgramSignalTimeoutMs,
                    null,
                    listener.Phase,
                    binding.HookId);
                Interlocked.Increment(ref phaseListenerTriggers);
                // 有限命中用尽：该声明不会再触发，立即归还它自己的现场与预算。
                if (listener.Exhausted) ReleaseExhaustedPhaseSite(binding);
            }
        }

        /// <summary>
        /// 一个 phase listener 投递的执行：记录完整投递事实，并执行不需要方法现场的绑定动作。
        /// 由中央投递取得路径调用，因此投递事实、过期与单赢家语义与 program 投递完全一致。
        /// </summary>
        internal static void ExecutePhaseListenerTrigger(DebugScheduler.Delivery delivery)
        {
            if (delivery == null || string.IsNullOrEmpty(delivery.HookId)) return;
            MethodHookDefinition definition;
            lock (Sync)
            {
                // 已被删除、已故障或 session 已停止的定义不再有触发事实可记录：各自的终态已经记录了该结局。
                if (!Definitions.TryGetValue(delivery.HookId, out definition)) return;
                if (!DispatchAllowed(definition)) return;
            }
            var phase = delivery.Phase ?? string.Empty;
            var binding = FindPhaseBinding(delivery.HookId, delivery.Revision);
            DebugTriggerSources.AppendTriggerEvent(
                UnityCliDebugSources.Trigger,
                DebugTriggerSources.PlayerLoopPhase,
                definition.HookId,
                definition.HookId,
                delivery.Revision,
                delivery.TriggeredThreadId,
                delivery.ExecutionThreadId,
                delivery.TriggeredAtMonotonicMs,
                delivery.QueuedAtMonotonicMs,
                delivery.StartedAtMonotonicMs,
                false,
                "player-loop-phase:" + phase,
                "trigger:" + DebugTriggerSources.PlayerLoopPhase + ":" + phase,
                CapturePhaseFields(binding));
            ReportPhaseActionGap(definition, binding, delivery.Revision, phase);
        }

        /// <summary>
        /// 该 phase 上是否至少有一个活动的 phase listener；零分配短路，未声明的帧没有额外成本。
        /// </summary>
        private static bool HasPhaseListener(string phase)
        {
            lock (Sync)
            {
                foreach (var definition in Definitions.Values)
                {
                    if (MatchesPhaseListener(definition, phase)) return true;
                }
            }
            return false;
        }

        /// <summary>该 phase 上当前活动的 listener 绑定；读取已发布绑定与原子计数，或遍历可变定义集合。</summary>
        private static List<MethodHookBinding> PhaseListenerCandidates(string phase)
        {
            var candidates = new List<MethodHookBinding>();
            lock (Sync)
            {
                foreach (var definition in Definitions.Values)
                {
                    if (!MatchesPhaseListener(definition, phase)) continue;
                    var binding = FindBindingLocked(definition);
                    if (binding != null) candidates.Add(binding);
                }
            }
            return candidates;
        }

        /// <summary>
        /// 一个定义是否在该 phase 上活动：触发源与 phase 必须精确相等，并且定义按既有的
        /// activation 与派发关闭规则处于可派发状态（与 method-hit 命中路径同一组判定）。
        /// </summary>
        private static bool MatchesPhaseListener(MethodHookDefinition definition, string phase)
        {
            var listener = definition.Listener;
            if (listener == null) return false;
            if (!string.Equals(listener.Trigger, DebugTriggerSources.PlayerLoopPhase, StringComparison.Ordinal)) return false;
            if (!string.Equals(listener.Phase, phase, StringComparison.Ordinal)) return false;
            if (definition.State != MethodHookState.Armed) return false;
            if (!DispatchAllowed(definition)) return false;
            return definition.ActivationMatches(playScope);
        }

        /// <summary>该定义在当前 revision 上已发布的不可变绑定；未发布时返回 null。</summary>
        private static MethodHookBinding FindBindingLocked(MethodHookDefinition definition)
        {
            MethodHookTargetRuntime runtime;
            if (!Targets.TryGetValue(definition.TargetKey ?? string.Empty, out runtime)) return null;
            var revision = Volatile.Read(ref definition.Revision);
            foreach (var binding in runtime.Dispatch.Ordered)
            {
                if (!string.Equals(binding.HookId, definition.HookId, StringComparison.Ordinal)) continue;
                if (binding.Revision != revision) continue;
                return binding;
            }
            return null;
        }

        /// <summary>一个已取得执行权的投递所属 revision 的绑定；找不到时返回 null。</summary>
        private static MethodHookBinding FindPhaseBinding(string hookId, long revision)
        {
            lock (Sync)
            {
                foreach (var runtime in Targets.Values)
                {
                    foreach (var binding in runtime.Dispatch.Ordered)
                    {
                        if (!string.Equals(binding.HookId, hookId, StringComparison.Ordinal)) continue;
                        if (binding.Revision != revision) continue;
                        return binding;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 有限命中用尽的 listener 归还自己的 phase 现场与计划注入预算。
        /// 只归还本定义自己的预留，因此同一 phase 上其他 owner（program 或其他 listener）不受影响；
        /// 正在该 phase 自己的回调里时，现场移除由 phase owner 登记后统一写入。
        /// </summary>
        private static void ReleaseExhaustedPhaseSite(MethodHookBinding binding)
        {
            var definition = binding == null ? null : binding.Definition;
            if (definition == null) return;
            string ownerId;
            lock (Sync)
            {
                ownerId = definition.TriggerOwnerId;
                definition.TriggerOwnerId = string.Empty;
            }
            if (!string.IsNullOrEmpty(ownerId)) ReleaseTriggerPlan(ownerId);
        }

        /// <summary>
        /// 在 phase 到达现场读取声明的静态字段：这是唯一不需要方法实例的捕获动作，
        /// 读取使用与命中路径相同的发布时字段解析与投影边界。
        /// </summary>
        private static Dictionary<string, object> CapturePhaseFields(MethodHookBinding binding)
        {
            if (binding == null || binding.Capture.StaticFields.Length == 0) return null;
            var truncation = new MethodHookProjection.Truncation();
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in binding.Capture.StaticFields)
            {
                // 解析为非静态的字段需要一个实例：它不是现场可读的，由覆盖缺口事实如实报告。
                if (field.Field == null || !field.Field.IsStatic) continue;
                try
                {
                    fields[field.Key] = MethodHookProjection.Project(field.Field.GetValue(null), truncation, true);
                }
                catch (Exception exception)
                {
                    fields[field.Key] = "unreadable-field:" + exception.GetType().Name;
                }
            }
            return fields.Count == 0 ? null : fields;
        }

        /// <summary>
        /// 声明了但在 phase 到达现场无法执行的绑定动作。这些动作都需要方法实例、参数或返回值，
        /// 因此必须在事件流里留下稳定、可读的覆盖缺口，而不是静默不执行。
        /// </summary>
        private static void ReportPhaseActionGap(MethodHookDefinition definition, MethodHookBinding binding, long revision, string phase)
        {
            var declared = new List<string>();
            var capture = definition.Capture;
            if (capture != null)
            {
                if (capture.Arguments) declared.Add("capture.arguments");
                if (capture.Result) declared.Add("capture.result");
                if (capture.InstanceFields.Count > 0) declared.Add("capture.instanceFields");
            }
            if (binding != null && binding.Capture.StaticFields.Any(field => field.Field == null || !field.Field.IsStatic)) declared.Add("capture.staticFields");
            if (definition.HasCallbacks) declared.Add("callbacks");
            if (definition.Breakpoint != null) declared.Add("breakpoint");
            if (declared.Count == 0) return;
            Interlocked.Increment(ref phaseListenerActionGaps);
            AppendEvent(new UnityCliDebugEvent
            {
                HookId = definition.HookId,
                Revision = revision,
                Kind = "coverage",
                SourceId = UnityCliDebugSources.Hook,
                ExecutionPoint = "trigger:" + DebugTriggerSources.PlayerLoopPhase + ":" + phase,
                Name = PhaseActionCoverageName,
                MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName,
                Value = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "reason", PhaseActionCoverageName },
                    { "action", "not-run" },
                    { "phase", phase },
                    { "declared", declared }
                }
            });
        }
    }
}
#endif
