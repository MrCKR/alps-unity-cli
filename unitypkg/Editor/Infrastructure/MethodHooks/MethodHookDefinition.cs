#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>Hook Definition 的公开状态闭集。</summary>
    internal enum MethodHookState
    {
        Armed,
        Dormant,
        Restoring,
        Disabled,
        Faulted
    }

    internal static class MethodHookStateText
    {
        internal static string Of(MethodHookState state)
        {
            switch (state)
            {
                case MethodHookState.Armed: return "armed";
                case MethodHookState.Dormant: return "dormant";
                case MethodHookState.Restoring: return "restoring";
                case MethodHookState.Disabled: return "disabled";
                default: return "faulted";
            }
        }

        internal static MethodHookState Parse(string value)
        {
            if (string.Equals(value, "armed", StringComparison.Ordinal)) return MethodHookState.Armed;
            if (string.Equals(value, "dormant", StringComparison.Ordinal)) return MethodHookState.Dormant;
            if (string.Equals(value, "restoring", StringComparison.Ordinal)) return MethodHookState.Restoring;
            if (string.Equals(value, "disabled", StringComparison.Ordinal)) return MethodHookState.Disabled;
            if (string.Equals(value, "faulted", StringComparison.Ordinal)) return MethodHookState.Faulted;
            return MethodHookState.Dormant;
        }
    }

    /// <summary>一个 exact 字段选择；Field 只在当前域解析，重挂必须重新解析。</summary>
    internal sealed class MethodHookCaptureField
    {
        internal string DeclaringType;
        internal string FieldName;
        internal FieldInfo Field;

        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "declaringType", DeclaringType ?? string.Empty },
                { "fieldName", FieldName ?? string.Empty }
            };
        }

        internal static MethodHookCaptureField FromData(object data)
        {
            var dictionary = data as IDictionary<string, object>;
            if (dictionary == null) return null;
            object value;
            var fieldName = dictionary.TryGetValue("fieldName", out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
            if (string.IsNullOrWhiteSpace(fieldName)) return null;
            var declaringType = dictionary.TryGetValue("declaringType", out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
            return new MethodHookCaptureField { DeclaringType = declaringType, FieldName = fieldName };
        }

        internal MethodHookCaptureFieldSpec ToSpec()
        {
            return new MethodHookCaptureFieldSpec { DeclaringType = string.IsNullOrEmpty(DeclaringType) ? null : DeclaringType, FieldName = FieldName };
        }
    }

    /// <summary>无自定义代码的捕获配置。</summary>
    internal sealed class MethodHookCapture
    {
        internal bool Arguments;
        internal bool Result;
        internal int SampleEvery = 1;
        internal List<MethodHookCaptureField> InstanceFields = new List<MethodHookCaptureField>();
        internal List<MethodHookCaptureField> StaticFields = new List<MethodHookCaptureField>();

        internal MethodHookCapture Clone()
        {
            return new MethodHookCapture
            {
                Arguments = Arguments,
                Result = Result,
                SampleEvery = SampleEvery,
                InstanceFields = new List<MethodHookCaptureField>(InstanceFields),
                StaticFields = new List<MethodHookCaptureField>(StaticFields)
            };
        }

        internal int FieldCount { get { return InstanceFields.Count + StaticFields.Count; } }

        /// <summary>至少一种捕获或回调行为必须有效。</summary>
        internal bool HasBehaviour(MethodHookCallbackSpec callbacks)
        {
            var hasCode = callbacks != null && (!string.IsNullOrWhiteSpace(callbacks.PrefixCode) || !string.IsNullOrWhiteSpace(callbacks.PostfixCode));
            return hasCode || Arguments || Result || FieldCount > 0;
        }

        internal MethodHookCaptureSummary Summarize()
        {
            var fields = new List<string>();
            foreach (var field in InstanceFields) fields.Add("instance:" + (string.IsNullOrEmpty(field.DeclaringType) ? string.Empty : field.DeclaringType + ".") + field.FieldName);
            foreach (var field in StaticFields) fields.Add("static:" + (string.IsNullOrEmpty(field.DeclaringType) ? string.Empty : field.DeclaringType + ".") + field.FieldName);
            return new MethodHookCaptureSummary
            {
                Arguments = Arguments,
                Result = Result,
                SampleEvery = SampleEvery,
                InstanceFieldCount = InstanceFields.Count,
                StaticFieldCount = StaticFields.Count,
                Fields = fields
            };
        }

        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "arguments", Arguments },
                { "result", Result },
                { "sampleEvery", (long)SampleEvery },
                { "instanceFields", InstanceFields.Select(field => (object)field.ToData()).ToList() },
                { "staticFields", StaticFields.Select(field => (object)field.ToData()).ToList() }
            };
        }

        internal static MethodHookCapture FromData(object data)
        {
            var dictionary = data as IDictionary<string, object>;
            if (dictionary == null) return null;
            var capture = new MethodHookCapture();
            object value;
            if (dictionary.TryGetValue("arguments", out value) && value is bool) capture.Arguments = (bool)value;
            if (dictionary.TryGetValue("result", out value) && value is bool) capture.Result = (bool)value;
            if (dictionary.TryGetValue("sampleEvery", out value))
            {
                long parsed;
                if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                    capture.SampleEvery = (int)Math.Min(parsed, DebugLimits.MaxSampleEvery);
            }
            var instanceFields = dictionary.TryGetValue("instanceFields", out value) ? value as IEnumerable<object> : null;
            if (instanceFields != null) foreach (var item in instanceFields) { var field = MethodHookCaptureField.FromData(item); if (field != null) capture.InstanceFields.Add(field); }
            var staticFields = dictionary.TryGetValue("staticFields", out value) ? value as IEnumerable<object> : null;
            if (staticFields != null) foreach (var item in staticFields) { var field = MethodHookCaptureField.FromData(item); if (field != null) capture.StaticFields.Add(field); }
            return capture;
        }
    }

    /// <summary>
    /// Hook Definition：某一 Editor Session 中获授权的拦截意图。
    /// 它不是当前域的 patch；Attachment 由 MethodHookTargetRuntime 表达并在重挂时重建。
    /// </summary>
    internal sealed class MethodHookDefinition
    {
        internal string HookId;
        internal long Revision = 1;
        internal string Label = string.Empty;
        internal int Priority;
        internal string Activation = "play";
        internal MethodHookTargetSpec TargetSpec;
        internal string TargetKey;
        internal MethodHookCapture Capture = new MethodHookCapture();
        internal string PrefixCode = string.Empty;
        internal string PostfixCode = string.Empty;
        internal bool EmitHitEvents = true;
        /// <summary>
        /// 启停状态。任意线程都按 Volatile 读取，因此 worker 命中与主线程变更之间不存在半可见状态。
        /// </summary>
        private int stateValue = (int)MethodHookState.Dormant;
        internal MethodHookState State
        {
            get { return (MethodHookState)Volatile.Read(ref stateValue); }
            set { Volatile.Write(ref stateValue, (int)value); }
        }
        /// <summary>
        /// 已关闭派发的 revision；0 表示开放。
        /// worker 线程用 CAS 只关闭自己那次命中所属的 revision，主线程发布新 revision 时重置为 0，
        /// 因此旧 revision 的在途故障永远停不掉已发布的新 revision。
        /// </summary>
        internal int DispatchClosed;
        internal string FaultCode = string.Empty;
        internal string FaultMessage = string.Empty;
        internal long FaultRevision;
        internal long FaultInvocationId;
        internal string FaultOwner = string.Empty;
        internal long InstallSequence;
        internal long HitCount;
        internal long BypassCount;
        /// <summary>在已被取代的 revision 上发生的故障计数；只统计，不改变当前 revision 的状态。</summary>
        internal long StaleFaults;
        internal long SampleIndex;
        internal MethodHookStateBag StateBag;
        /// <summary>从快照恢复的 State 值；新域建立 State bag 时使用。</summary>
        internal Dictionary<string, object> RestoredStateValues = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>
        /// 该定义的所有者身份；空串表示由直接的 debug.hook.* 调用拥有。
        /// program 挂载的 binding 以 "program:&lt;programId&gt;" 标记，并只对当前 domain 有效：
        /// program 定义不跨 Domain Reload 存活，因此旧 domain 的所有权不得在新 domain 里假装仍然有效。
        /// </summary>
        internal string Owner = string.Empty;

        /// <summary>
        /// 本次 Editor 启动中在触发预算账本里为该定义预留的计划身份；不持久化。
        /// 重载后由新域重新预留，因此不会把一个旧启动的预留当成仍然有效。
        /// </summary>
        internal string TriggerOwnerId = string.Empty;

        /// <summary>声明的 listener 过滤与有限命中状态；null 表示无 listener，命中即生效。</summary>
        internal MethodHookListenerState Listener;

        /// <summary>
        /// 声明的断点命中行为；null 表示该定义不断点。配置在安装/替换时冻结，命中路径只读它。
        /// </summary>
        internal MethodHookBreakpoint Breakpoint;

        /// <summary>
        /// 该断点占用的 session 资源接线点；仅在声明了 breakpoint.sessionId 时非 null。
        /// 清理回调必须能按 hookId 反查现场，而 hookId 在 effect 之后才生成，因此二者由本对象接线。
        /// </summary>
        internal DebugPauseOwner BreakpointOwner;

        // 仅当前域有效；重挂必须重新解析与重新编译。
        internal MethodInfo Resolved;
        internal IUnityCliMethodHookCallback Callback;
        internal string CallbackKey = string.Empty;
        internal string PrefixHash = string.Empty;
        internal string PostfixHash = string.Empty;

        internal bool HasCallbacks { get { return !string.IsNullOrEmpty(PrefixCode) || !string.IsNullOrEmpty(PostfixCode); } }

        /// <summary>调用方可以提交任意 C# 回调体，因此写范围无法静态证明，属于“任意干预”。</summary>
        internal bool IsIntervention { get { return HasCallbacks; } }

        /// <summary>稳定的线程安全计数增量；命中路径可能来自任意托管线程。</summary>
        internal void CountHit() { Interlocked.Increment(ref HitCount); }

        internal void CountBypass() { Interlocked.Increment(ref BypassCount); }

        /// <summary>取得本次命中的采样序号；step 为 1 时不消耗序号。</summary>
        internal long NextSampleIndex(int step)
        {
            return step <= 1 ? 0L : Interlocked.Increment(ref SampleIndex);
        }

        /// <summary>activation 与当前 Edit/Play 状态是否匹配。</summary>
        internal bool ActivationMatches(bool isPlaying)
        {
            return string.Equals(Activation, "editor-and-play", StringComparison.Ordinal) || isPlaying;
        }

        internal MethodHookListItem ToListItem()
        {
            return new MethodHookListItem
            {
                HookId = HookId,
                Label = Label,
                State = MethodHookStateText.Of(State),
                Revision = Revision,
                Activation = Activation,
                Priority = Priority,
                MethodIdentity = TargetSpec == null ? string.Empty : TargetSpec.TypeName + "." + TargetSpec.MethodName,
                HitCount = HitCount,
                FaultCode = string.IsNullOrEmpty(FaultCode) ? null : FaultCode
            };
        }

        /// <summary>
        /// 可跨 Editor 启动复用的声明投影：recipe 与任何外部读取端读的都是这一份。
        ///
        /// 只写声明事实。listener 的线程身份属于本次 Editor 启动，导入它的新启动不可能指向同一个线程，
        /// 而且该键是 recipe 校验的排除键，写出去只会让导出内容被自己的导入按 excluded-content 拒绝，
        /// 因此这里不写 threadId。instanceIdentity 是调用方声明的实例身份字符串而不是托管对象身份，保留。
        /// 现场身份由 <see cref="ToSnapshotData"/> 单独补写，调用方必须显式要求它。
        /// </summary>
        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "hookId", HookId },
                { "revision", Revision },
                { "label", Label ?? string.Empty },
                { "priority", (long)Priority },
                { "activation", Activation ?? "play" },
                { "targetKey", TargetKey ?? string.Empty },
                { "target", TargetSpec == null ? null : TargetSpec.ToData() },
                { "capture", Capture == null ? new MethodHookCapture().ToData() : Capture.ToData() },
                { "prefixCode", PrefixCode ?? string.Empty },
                { "postfixCode", PostfixCode ?? string.Empty },
                { "emitHitEvents", EmitHitEvents },
                { "breakpoint", Breakpoint == null ? null : new Dictionary<string, object>
                    {
                        { "kind", Breakpoint.Kind ?? string.Empty },
                        { "position", Breakpoint.Position ?? string.Empty },
                        { "unbounded", Breakpoint.Unbounded },
                        { "durationMs", Breakpoint.DurationMs },
                        { "stackDepth", (long)Breakpoint.StackDepth },
                        { "sessionId", Breakpoint.SessionId ?? string.Empty }
                    } },
                { "state", MethodHookStateText.Of(State) },
                { "faultCode", FaultCode ?? string.Empty },
                { "faultMessage", FaultMessage ?? string.Empty },
                { "faultRevision", FaultRevision },
                { "faultInvocationId", FaultInvocationId },
                { "faultOwner", FaultOwner ?? string.Empty },
                { "installSequence", InstallSequence },
                { "hitCount", HitCount },
                { "bypassCount", BypassCount },
                // listener 只写声明；有限命中计数与现场线程身份都属于本次 Editor 启动，重载或导入后不假装延续。
                { "listener", Listener == null ? null : new Dictionary<string, object>
                    {
                        { "sessionId", Listener.SessionId ?? string.Empty },
                        { "trigger", Listener.Trigger ?? DebugTriggerSources.MethodHit },
                        { "phase", Listener.Phase ?? string.Empty },
                        { "maxHits", Listener.MaxHits },
                        { "instanceIdentity", Listener.InstanceIdentity ?? string.Empty },
                        { "conditions", Listener.Conditions.Select(condition => (object)new Dictionary<string, object>
                            {
                                { "kind", condition.Kind ?? string.Empty },
                                { "name", condition.Name ?? string.Empty },
                                { "operator", condition.Operator ?? string.Empty },
                                { "declaringType", condition.DeclaringType ?? string.Empty },
                                { "literalCanonical", condition.LiteralCanonical ?? string.Empty }
                            }).ToList() }
                    } },
                { "stateValues", StateBag == null ? new Dictionary<string, object>() : StateBag.Snapshot() }
            };
        }

        /// <summary>
        /// 本次 Editor 启动的定义快照：在声明投影上补写只对本次启动有效的现场身份。
        ///
        /// debug store 的文件带 Editor session 身份，只被同一个 Editor session 读回，因此它可以承载现场身份；
        /// recipe 会被另一个启动导入，所以不得承载。
        /// </summary>
        internal Dictionary<string, object> ToSnapshotData()
        {
            var data = ToData();
            if (Listener == null) return data;
            var listener = data["listener"] as Dictionary<string, object>;
            if (listener != null) listener["threadId"] = (long)Listener.ThreadId;
            return data;
        }

        internal static MethodHookDefinition FromData(object data)
        {
            var dictionary = data as IDictionary<string, object>;
            if (dictionary == null) return null;
            var definition = new MethodHookDefinition();
            object value;
            definition.HookId = dictionary.TryGetValue("hookId", out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
            if (string.IsNullOrWhiteSpace(definition.HookId)) return null;
            definition.Revision = Long(dictionary, "revision", 1);
            definition.Label = Text(dictionary, "label");
            definition.Priority = (int)Long(dictionary, "priority", 0);
            definition.Activation = Text(dictionary, "activation");
            if (string.IsNullOrEmpty(definition.Activation)) definition.Activation = "play";
            definition.TargetKey = Text(dictionary, "targetKey");
            var target = dictionary.TryGetValue("target", out value) ? value : null;
            definition.TargetSpec = MethodHookTargetSpec.FromData(target);
            if (definition.TargetSpec == null) return null;
            definition.Capture = dictionary.TryGetValue("capture", out value) ? MethodHookCapture.FromData(value) : null;
            if (definition.Capture == null) definition.Capture = new MethodHookCapture();
            definition.PrefixCode = Text(dictionary, "prefixCode");
            definition.PostfixCode = Text(dictionary, "postfixCode");
            definition.EmitHitEvents = Bool(dictionary, "emitHitEvents", true);
            definition.Breakpoint = ReadBreakpoint(dictionary);
            definition.State = MethodHookStateText.Parse(Text(dictionary, "state"));
            definition.FaultCode = Text(dictionary, "faultCode");
            definition.FaultMessage = Text(dictionary, "faultMessage");
            definition.FaultRevision = Long(dictionary, "faultRevision", 0);
            definition.FaultInvocationId = Long(dictionary, "faultInvocationId", 0);
            definition.FaultOwner = Text(dictionary, "faultOwner");
            definition.InstallSequence = Long(dictionary, "installSequence", 0);
            definition.HitCount = Long(dictionary, "hitCount", 0);
            definition.BypassCount = Long(dictionary, "bypassCount", 0);
            definition.Listener = ReadListener(dictionary);
            if (dictionary.TryGetValue("stateValues", out value))
            {
                var stateValues = value as IDictionary<string, object>;
                if (stateValues != null) foreach (var pair in stateValues) definition.RestoredStateValues[pair.Key] = pair.Value;
            }
            return definition;
        }

        private static string Text(IDictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty; }

        /// <summary>
        /// 从快照恢复断点配置。资源槽位属于本次 Editor 启动的机制状态，不随定义恢复：
        /// 新域里该断点不再占用 session 资源槽位，重建时重新预留。
        /// </summary>
        private static MethodHookBreakpoint ReadBreakpoint(IDictionary<string, object> data)
        {
            object value;
            var breakpoint = data.TryGetValue("breakpoint", out value) ? value as IDictionary<string, object> : null;
            if (breakpoint == null) return null;
            var kind = Text(breakpoint, "kind");
            if (!DebugPauseBreakpointKinds.IsDefined(kind)) return null;
            var position = Text(breakpoint, "position");
            var durationMs = Long(breakpoint, "durationMs", 0L);
            var unbounded = Bool(breakpoint, "unbounded", false);
            // bounded 且 durationMs 非正数的快照不可信：不当作无限，直接不恢复断点。
            if (string.Equals(kind, DebugPauseBreakpointKinds.Pause, StringComparison.Ordinal) && !unbounded && durationMs <= 0L) return null;
            return new MethodHookBreakpoint
            {
                Kind = kind,
                Position = DebugPausePositions.IsDefined(position) ? position : DebugPausePositions.Prefix,
                Unbounded = unbounded,
                DurationMs = unbounded ? 0L : durationMs,
                StackDepth = (int)Math.Min(Math.Max(0L, Long(breakpoint, "stackDepth", 0L)), DebugLimits.MaxPauseStackFrames),
                SessionId = Text(breakpoint, "sessionId")
            };
        }

        /// <summary>从快照恢复 listener 配置；计数器从零开始，不伪装成延续了旧域的命中次数。</summary>
        private static MethodHookListenerState ReadListener(IDictionary<string, object> data)
        {
            object value;
            var listener = data.TryGetValue("listener", out value) ? value as IDictionary<string, object> : null;
            if (listener == null) return null;
            var state = new MethodHookListenerState
            {
                SessionId = Text(listener, "sessionId"),
                Trigger = Text(listener, "trigger"),
                Phase = Text(listener, "phase"),
                MaxHits = Long(listener, "maxHits", 0),
                ThreadId = (int)Long(listener, "threadId", 0),
                InstanceIdentity = Text(listener, "instanceIdentity")
            };
            if (string.IsNullOrEmpty(state.Trigger)) state.Trigger = DebugTriggerSources.MethodHit;
            // phase 只在声明的触发源是 player-loop-phase 时才有意义；损坏文件不得造出一个谜之现场。
            if (!string.Equals(state.Trigger, DebugTriggerSources.PlayerLoopPhase, StringComparison.Ordinal)) state.Phase = string.Empty;
            else if (!DebugPlayerLoopPhases.IsDefined(state.Phase)) state.Phase = string.Empty;
            var conditions = new List<MethodHookCondition>();
            var items = listener.TryGetValue("conditions", out value) ? value as IEnumerable<object> : null;
            if (items != null)
            {
                foreach (var item in items)
                {
                    var condition = item as IDictionary<string, object>;
                    if (condition == null || conditions.Count >= DebugLimits.MaxListenerConditions) continue;
                    var kind = Text(condition, "kind");
                    var name = Text(condition, "name");
                    var op = Text(condition, "operator");
                    if (!MethodHookConditionKinds.IsDefined(kind) || !MethodHookConditionOperators.IsDefined(op) || string.IsNullOrEmpty(name)) continue;
                    var record = new MethodHookCondition
                    {
                        Kind = kind,
                        Name = name,
                        Operator = op,
                        DeclaringType = Text(condition, "declaringType"),
                        LiteralCanonical = Text(condition, "literalCanonical")
                    };
                    if (string.Equals(kind, MethodHookConditionKinds.Argument, StringComparison.Ordinal))
                    {
                        int index;
                        if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0) continue;
                        record.ArgumentIndex = index;
                    }
                    conditions.Add(record);
                }
            }
            state.Conditions = conditions.ToArray();
            return state;
        }
        private static long Long(IDictionary<string, object> data, string key, long fallback) { long parsed; return long.TryParse(Text(data, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback; }
        private static bool Bool(IDictionary<string, object> data, string key, bool fallback) { bool parsed; return bool.TryParse(Text(data, key), out parsed) ? parsed : fallback; }

        private static List<string> StringList(IDictionary<string, object> data, string key)
        {
            object value;
            var result = new List<string>();
            if (!data.TryGetValue(key, out value)) return result;
            var items = value as IEnumerable<object>;
            if (items == null) return result;
            foreach (var item in items) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty);
            return result;
        }
    }

    /// <summary>
    /// 一个 exact target 在当前域的 Attachment。
    /// 该 target 下的多个逻辑定义共用同一套 Adapter 与 dispatcher；patch 只在最后一个定义离开时撤销。
    /// </summary>
    internal sealed class MethodHookTargetRuntime
    {
        internal string TargetKey;
        internal MethodHookTargetSpec Spec;
        internal MethodBase Method;
        internal string MethodIdentity;
        internal string AssemblyIdentity;
        internal bool Attached;
        /// <summary>定义成员表（只由主线程读写）；命中路径不得遍历它。</summary>
        internal readonly List<MethodHookDefinition> Definitions = new List<MethodHookDefinition>();

        /// <summary>已发布的不可变派发快照；命中路径只按引用读取它。</summary>
        internal MethodHookDispatch Dispatch = MethodHookDispatch.Empty;
    }

    /// <summary>
    /// 一次 Invocation 的宿主实现。回调只通过 IUnityCliHookInvocation 读写本次调用；
    /// 参数与结果先暂存，回调正常返回后才提交，抛异常时不提交该回调尚未提交的变更。
    /// 本次调用的配置、revision 与线程身份来自 prefix 固定的绑定，不重新查询当前定义。
    /// </summary>
    internal sealed class MethodHookInvocation : IUnityCliHookInvocation
    {
        private readonly MethodHookBinding binding;
        private readonly object instance;
        private readonly object[] arguments;
        private readonly MethodHookInterceptionStamp stamp;
        private readonly bool isPrefix;
        /// <summary>命中线程是否可证明为主线程；false 时禁止读取 Unity 主线程 API。</summary>
        private readonly bool allowUnityIdentity;
        private object[] stagedArguments;
        private object result;
        private bool hasStagedResult;
        private bool skipRequested;
        private int fieldWrites;
        private readonly List<UnityCliDebugEvent> emitted = new List<UnityCliDebugEvent>();
        private readonly List<Dictionary<string, object>> fields = new List<Dictionary<string, object>>();
        private bool truncated;

        internal MethodHookInvocation(MethodHookBinding binding, object instance, object[] arguments, object result, MethodHookInterceptionStamp stamp, bool isPrefix, bool allowUnityIdentity)
        {
            this.binding = binding;
            this.instance = instance;
            this.arguments = arguments ?? Array.Empty<object>();
            this.result = result;
            this.stamp = stamp ?? new MethodHookInterceptionStamp();
            this.isPrefix = isPrefix;
            this.allowUnityIdentity = allowUnityIdentity;
        }

        internal long InvocationId { get; set; }
        internal bool HasStagedArguments { get { return stagedArguments != null; } }
        internal object[] StagedArguments { get { return stagedArguments; } }
        internal bool HasStagedResult { get { return hasStagedResult; } }
        internal object StagedResult { get { return result; } }
        internal bool SkipRequested { get { return skipRequested; } }
        internal bool Truncated { get { return truncated; } }
        /// <summary>现场服务（pause.evaluate）直接写入的字段次数；只用于如实报告已发生的干预。</summary>
        internal int FieldWrites { get { return fieldWrites; } }
        internal IReadOnlyList<UnityCliDebugEvent> Emitted { get { return emitted; } }
        internal IReadOnlyList<Dictionary<string, object>> CapturedFields { get { return fields; } }

        /// <summary>记录捕获字段值；访问失败时给出具体字段错误。</summary>
        internal void AddCapturedField(FieldInfo field, object value)
        {
            fields.Add(new Dictionary<string, object>
            {
                { "field", (field.DeclaringType == null ? string.Empty : field.DeclaringType.FullName + ".") + field.Name },
                { "value", value }
            });
        }

        internal void AddEmitted(UnityCliDebugEvent record) { emitted.Add(record); }

        public object Instance { get { return instance; } }
        public object[] Arguments { get { return stagedArguments ?? arguments; } }
        public object Result { get { return result; } set { result = value; hasStagedResult = true; } }
        public string HookId { get { return binding.HookId; } }
        public string MethodIdentity { get { return MethodHookMethodText.Describe(binding.Resolved); } }
        public string PlaySessionId { get { return stamp.PlaySessionId; } }
        public string ClockSegment { get { return stamp.ClockSegment; } }
        public double ElapsedMilliseconds { get { return stamp.ElapsedMilliseconds; } }
        public int Frame { get { return stamp.Frame; } }
        public bool IsPrefix { get { return isPrefix; } }
        public IDictionary<string, object> State { get { return binding.Definition.StateBag; } }

        public void SetArgument(int index, object value)
        {
            if (!isPrefix)
                throw new InvalidOperationException("SetArgument is only available in a prefix callback; the original call has already received its arguments.");
            if (index < 0 || index >= arguments.Length)
                throw new ArgumentOutOfRangeException("index", "Argument index " + index + " is outside the target signature.");
            var parameter = binding.Resolved.GetParameters()[index];
            if (value != null && !parameter.ParameterType.IsInstanceOfType(value))
            {
                var expected = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                if (value != null && !expected.IsInstanceOfType(value))
                    throw new InvalidCastException("Argument " + index + " is " + value.GetType().FullName + " and cannot be assigned to " + expected.FullName + ".");
            }
            // 暂存基线必须是当前有效参数（含之前已暂存的修改），否则后续现场写入会丢掉先前的回调修改。
            if (stagedArguments == null) stagedArguments = (object[])Arguments.Clone();
            stagedArguments[index] = value;
        }

        public void SkipOriginal() { skipRequested = true; }

        public object ReadField(string declaringType, string fieldName)
        {
            var field = MethodHookTargetResolver.ResolveField(instance == null ? binding.Resolved.DeclaringType : instance.GetType(), declaringType, fieldName);
            try
            {
                return field.GetValue(field.IsStatic ? null : instance);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Field '" + fieldName + "' could not be read: " + exception.GetType().Name + ".", exception);
            }
        }

        public void WriteField(string declaringType, string fieldName, object value)
        {
            var field = MethodHookTargetResolver.ResolveField(instance == null ? binding.Resolved.DeclaringType : instance.GetType(), declaringType, fieldName);
            try
            {
                if (value != null && !field.FieldType.IsInstanceOfType(value))
                    throw new InvalidCastException("Field '" + fieldName + "' is " + field.FieldType.FullName + " and cannot accept " + value.GetType().FullName + ".");
                field.SetValue(field.IsStatic ? null : instance, value);
                fieldWrites++;
            }
            catch (BridgeCommandException) { throw; }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Field '" + fieldName + "' could not be written: " + exception.GetType().Name + ".", exception);
            }
        }

        public void Emit(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Emit requires a non-empty event name.");
            var truncation = new MethodHookProjection.Truncation();
            var projected = MethodHookProjection.Project(value, truncation, allowUnityIdentity);
            var record = new UnityCliDebugEvent
            {
                HookId = binding.HookId,
                Revision = binding.Revision,
                InvocationId = InvocationId,
                Kind = "custom",
                PlaySessionId = stamp.PlaySessionId,
                ClockSegment = stamp.ClockSegment,
                ElapsedMilliseconds = stamp.ElapsedMilliseconds,
                Frame = stamp.Frame,
                MethodIdentity = MethodHookMethodText.Describe(binding.Resolved),
                Instance = InstanceIdentity(),
                Name = UnityCliBridge.BoundedText(name, 128),
                Value = projected,
                Truncated = truncation.Truncated
            };
            var size = record.Value == null ? 0 : MethodHookProjection.EstimateBytes(record.Value);
            if (size > DebugLimits.MaxEmitBytes)
            {
                record.Value = new Dictionary<string, object> { { "truncated", true }, { "bytes", size } };
                record.Truncated = true;
            }
            if (record.Truncated) truncated = true;
            emitted.Add(record);
        }

        /// <summary>
        /// 实例身份摘要。worker 命中只能给出托管类型名，并如实标记身份将由主线程补充，
        /// 绝不把主线程才能读取的名称当成命中时刻的值。
        /// </summary>
        internal string InstanceIdentity()
        {
            return MethodHookInstanceIdentity.Of(instance, allowUnityIdentity);
        }
    }

    /// <summary>
    /// 一次命中可见的实例身份。事件记录与 listener 条件比较共用这一个实现，
    /// 因此“已声明的实例筛选”与“事件里报告的身份”永远是同一个字符串。
    /// </summary>
    internal static class MethodHookInstanceIdentity
    {
        internal static string Of(object instance, bool allowUnityIdentity)
        {
            if (instance == null) return "static";
            var typeName = instance.GetType().FullName ?? instance.GetType().Name;
            if (!allowUnityIdentity)
                return instance is UnityEngine.Object ? "unity-object-pending:" + typeName : typeName;
            var truncation = new MethodHookProjection.Truncation();
            var projected = MethodHookProjection.Project(instance, truncation, true);
            var dictionary = projected as IDictionary<string, object>;
            if (dictionary != null && dictionary.ContainsKey("name"))
            {
                var name = Convert.ToString(dictionary["name"], CultureInfo.InvariantCulture);
                return typeName + ":" + name;
            }
            return typeName;
        }
    }
}
#endif
