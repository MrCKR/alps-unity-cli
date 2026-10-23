#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityCli
{
    /// <summary>
    /// debug 组的 closed Request/Result DTO。
    /// 全部字段都是可由 schema 派生器静态展开的封闭类型；任意 JSON 只出现在显式标记的位置。
    /// </summary>
    public sealed class MethodHookTargetSpec
    {
        [UnityCliRequired] public string AssemblyName { get; set; }
        [UnityCliRequired] public string TypeName { get; set; }
        [UnityCliRequired] public string MethodName { get; set; }
        /// <summary>exact 参数类型全名；无参数也必须传空数组。</summary>
        [UnityCliRequired, UnityCliItemsRange(0, 64)] public List<string> ParameterTypes { get; set; }
        [UnityCliRequired] public string ReturnType { get; set; }
        [UnityCliRequired] public bool IsStatic { get; set; }

        /// <summary>
        /// 写入持久化或 recipe 的稳定声明数据；键集与跨版本读取端必须一致。
        /// </summary>
        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "assemblyName", AssemblyName ?? string.Empty },
                { "typeName", TypeName ?? string.Empty },
                { "methodName", MethodName ?? string.Empty },
                { "parameterTypes", (ParameterTypes ?? new List<string>()).Cast<object>().ToList() },
                { "returnType", ReturnType ?? string.Empty },
                { "isStatic", IsStatic }
            };
        }

        /// <summary>读取声明数据；缺字段时返回 null，不按缺省猜测目标。</summary>
        internal static MethodHookTargetSpec FromData(object data)
        {
            var dictionary = data as IDictionary<string, object>;
            if (dictionary == null) return null;
            var parameters = new List<string>();
            object value;
            if (dictionary.TryGetValue("parameterTypes", out value))
            {
                var items = value as IEnumerable<object>;
                if (items != null)
                {
                    foreach (var item in items) parameters.Add(item == null ? string.Empty : Convert.ToString(item, CultureInfo.InvariantCulture));
                }
            }
            object isStatic;
            dictionary.TryGetValue("isStatic", out isStatic);
            return new MethodHookTargetSpec
            {
                AssemblyName = Convert.ToString(dictionary.ContainsKey("assemblyName") ? dictionary["assemblyName"] : null, CultureInfo.InvariantCulture) ?? string.Empty,
                TypeName = Convert.ToString(dictionary.ContainsKey("typeName") ? dictionary["typeName"] : null, CultureInfo.InvariantCulture) ?? string.Empty,
                MethodName = Convert.ToString(dictionary.ContainsKey("methodName") ? dictionary["methodName"] : null, CultureInfo.InvariantCulture) ?? string.Empty,
                ParameterTypes = parameters,
                ReturnType = Convert.ToString(dictionary.ContainsKey("returnType") ? dictionary["returnType"] : null, CultureInfo.InvariantCulture) ?? string.Empty,
                IsStatic = isStatic is bool && (bool)isStatic
            };
        }
    }

    /// <summary>exact 字段选择；declaringType 省略时从实例类型向上解析。</summary>
    public sealed class MethodHookCaptureFieldSpec
    {
        public string DeclaringType { get; set; }
        [UnityCliRequired] public string FieldName { get; set; }
    }

    /// <summary>无自定义代码的捕获配置；采样步长只影响记录，不改变执行。</summary>
    public sealed class MethodHookCaptureSpec
    {
        public bool? Arguments { get; set; }
        public bool? Result { get; set; }
        [UnityCliItemsRange(0, DebugLimits.MaxCaptureFields)] public List<MethodHookCaptureFieldSpec> InstanceFields { get; set; }
        [UnityCliItemsRange(0, DebugLimits.MaxCaptureFields)] public List<MethodHookCaptureFieldSpec> StaticFields { get; set; }
        [UnityCliRange(1, DebugLimits.MaxSampleEvery)] public int? SampleEvery { get; set; }
    }

    /// <summary>同步 C# 回调体；由固定 wrapper 编译，不接受 async/Task/await。</summary>
    public sealed class MethodHookCallbackSpec
    {
        public string PrefixCode { get; set; }
        public string PostfixCode { get; set; }
    }

    /// <summary>一条 listener 命中条件；复杂条件用显式调试代码表达，不引入 DSL。</summary>
    public sealed class MethodHookConditionSpec
    {
        /// <summary>取值来源：argument 或 field。phase 触发源上没有参数，因此那里只允许 field。</summary>
        [UnityCliRequired, UnityCliStringEnum("argument", "field")] public string Kind { get; set; }
        /// <summary>argument 时为从 0 开始的参数下标；field 时为字段名。</summary>
        [UnityCliRequired] public string Name { get; set; }
        /// <summary>比较算子：equals 或 not-equals。比较使用两侧的规范 JSON。</summary>
        [UnityCliRequired, UnityCliStringEnum("equals", "not-equals")] public string Operator { get; set; }
        /// <summary>field 时的声明类型；省略时从命中实例类型向上解析。</summary>
        public string DeclaringType { get; set; }
        /// <summary>比较字面量；按任意 JSON 提交，实际值按同一投影边界物化后再比较。</summary>
        [UnityCliAnyJson] public object Value { get; set; }
    }

    /// <summary>
    /// 监听式触发配置。声明后该定义只在其条件满足时生效；未声明时命中即生效。
    /// maxHits 为有限命中次数，1 表示 one-shot；复杂条件用显式调试代码在回调里表达。
    /// </summary>
    public sealed class MethodHookListenerSpec
    {
        /// <summary>拥有本次计划注入的 session；必须是存在且处于 open 的定义。</summary>
        [UnityCliRequired] public string SessionId { get; set; }
        /// <summary>
        /// 触发源：method-hit、log、host-signal 或 player-loop-phase。
        /// player-loop-phase 只在 Editor Play 下存在，必须同时声明 phase，Edit 模式下被拒绝；
        /// 一个 phase 到达没有方法实例与参数，因此该触发源上只能声明字段条件与字段捕获，
        /// 参数条件、instanceIdentity 与回调体在安装时被拒绝或被记录为稳定覆盖缺口；
        /// editor-tick 不是 listener 触发源，需要主线程 tick 时用 debug.program-run 的 executionSite。
        /// </summary>
        [UnityCliStringEnum("method-hit", "log", "host-signal", "player-loop-phase")] public string Trigger { get; set; }

        /// <summary>
        /// trigger 为 player-loop-phase 时必需的具名 phase；该 phase 到达时本定义被求值，
        /// 满足条件则在同一执行点段投递并执行，有限命中用尽后由既有释放路径归还现场。
        /// 声明后框架先安装该 phase 现场，释放、session stop、Play 退出与 Domain Reload 都会移除它。
        /// </summary>
        [UnityCliStringEnum("before-update", "after-update", "fixed-update", "late-update")] public string Phase { get; set; }
        /// <summary>有限命中次数；省略或 0 表示不限制，1 表示 one-shot。</summary>
        [UnityCliRange(0, DebugLimits.MaxListenerHits)] public long? MaxHits { get; set; }
        /// <summary>只在指定托管线程上生效；省略表示任意线程。</summary>
        [UnityCliRange(1, 65535)] public int? ThreadId { get; set; }
        /// <summary>精确实例身份；与事件里报告的实例身份字符串完全相等才生效。phase 触发源上没有实例，声明后会被拒绝。</summary>
        public string InstanceIdentity { get; set; }
        [UnityCliItemsRange(0, DebugLimits.MaxListenerConditions)] public List<MethodHookConditionSpec> Conditions { get; set; }
    }

    /// <summary>listener 事实的公开投影；未声明 listener 时整个字段为 null。</summary>
    public sealed class MethodHookListenerSummary
    {
        public string SessionId { get; set; }
        public string Trigger { get; set; }

        /// <summary>trigger 为 player-loop-phase 时的具名 phase；否则为空。</summary>
        public string Phase { get; set; }
        public long MaxHits { get; set; }
        public long Hits { get; set; }
        public long FilteredHits { get; set; }
        public int ThreadId { get; set; }
        public string InstanceIdentity { get; set; }
        public List<string> Conditions { get; set; }
    }

    /// <summary>exact target 的公开摘要。</summary>
    public sealed class MethodHookTargetSummary
    {
        public string AssemblyName { get; set; }
        public string AssemblyIdentity { get; set; }
        public string TypeName { get; set; }
        public string MethodName { get; set; }
        public List<string> ParameterTypes { get; set; }
        public string ReturnType { get; set; }
        public bool IsStatic { get; set; }
        public string MethodIdentity { get; set; }
    }

    /// <summary>dry-run 计划效果；只描述本动作会尝试的变更，不证明编译或业务有效。</summary>
    public sealed class MethodHookPlannedEffect
    {
        public string Type { get; set; }
        public string HookId { get; set; }
        public string State { get; set; }
        public string Note { get; set; }
    }

    public sealed class MethodHookCaptureSummary
    {
        public bool Arguments { get; set; }
        public bool Result { get; set; }
        public int SampleEvery { get; set; }
        public int InstanceFieldCount { get; set; }
        public int StaticFieldCount { get; set; }
        public List<string> Fields { get; set; }
    }

    public sealed class MethodHookCallbackSummary
    {
        public string Slot { get; set; }
        public string Hash { get; set; }
        public int SourceBytes { get; set; }
        public bool Present { get; set; }
    }

    // ---- install ----

    public sealed class MethodHookInstallRequest
    {
        public string Label { get; set; }
        [UnityCliRequired] public MethodHookTargetSpec Target { get; set; }
        [UnityCliStringEnum("play", "editor-and-play")] public string Activation { get; set; }
        [UnityCliRange(-1000, 1000)] public int? Priority { get; set; }
        public MethodHookCaptureSpec Capture { get; set; }
        public MethodHookCallbackSpec Callbacks { get; set; }
        public bool? EmitHitEvents { get; set; }
        /// <summary>可选监听式触发；声明后命中先经过 listener 过滤，只有匹配的命中执行回调。</summary>
        public MethodHookListenerSpec Listener { get; set; }
        /// <summary>
        /// 可选断点命中行为：snapshot 捕获后继续，pause 发布 pauseId 并停住命中线程。
        /// pause 必须显式声明 pauseTimeout，没有默认值；声明 sessionId 时它拥有该断点的暂停资源。
        /// </summary>
        public MethodHookBreakpointSpec Breakpoint { get; set; }
    }

    public sealed class MethodHookInstallResult
    {
        public string HookId { get; set; }
        public long Revision { get; set; }
        public string State { get; set; }
        public string EditorSessionId { get; set; }
        public string Activation { get; set; }
        public int Priority { get; set; }
        public string EventCursor { get; set; }
        public MethodHookTargetSummary Target { get; set; }
        public MethodHookCaptureSummary Capture { get; set; }
        public List<MethodHookCallbackSummary> Callbacks { get; set; }
        public MethodHookListenerSummary Listener { get; set; }
        /// <summary>断点配置及其 session 资源占用；未声明断点时为空。</summary>
        public MethodHookBreakpointSummary Breakpoint { get; set; }
        public bool? DryRun { get; set; }
        [UnityCliItemsRange(0, 8)] public List<MethodHookPlannedEffect> PlannedEffects { get; set; }
    }

    // ---- replace ----

    public sealed class MethodHookReplaceRequest
    {
        [UnityCliRequired] public string HookId { get; set; }
        [UnityCliRequired] public string EditorSessionId { get; set; }
        [UnityCliRequired] public long ExpectedRevision { get; set; }
        public MethodHookCaptureSpec Capture { get; set; }
        public MethodHookCallbackSpec Callbacks { get; set; }
        [UnityCliRange(-1000, 1000)] public int? Priority { get; set; }
        public bool? EmitHitEvents { get; set; }
        /// <summary>提交时替换监听配置；省略字段表示保留当前 listener，传 null 表示清除 listener。</summary>
        public MethodHookListenerSpec Listener { get; set; }
        /// <summary>提交时替换断点配置；省略字段表示保留当前断点。</summary>
        public MethodHookBreakpointSpec Breakpoint { get; set; }
    }

    public sealed class MethodHookReplaceResult
    {
        public string HookId { get; set; }
        public long Revision { get; set; }
        public string State { get; set; }
        public bool Changed { get; set; }
        public string EditorSessionId { get; set; }
        public MethodHookTargetSummary Target { get; set; }
        public MethodHookCaptureSummary Capture { get; set; }
        public List<MethodHookCallbackSummary> Callbacks { get; set; }
        public MethodHookListenerSummary Listener { get; set; }
        /// <summary>替换后的断点配置；未声明断点时为 null。</summary>
        public MethodHookBreakpointSummary Breakpoint { get; set; }
        public string EventCursor { get; set; }
        public bool? DryRun { get; set; }
        [UnityCliItemsRange(0, 8)] public List<MethodHookPlannedEffect> PlannedEffects { get; set; }
    }

    // ---- enable / disable ----

    public sealed class MethodHookEnableRequest
    {
        [UnityCliRequired] public string HookId { get; set; }
        [UnityCliRequired] public string EditorSessionId { get; set; }
        [UnityCliRequired] public long ExpectedRevision { get; set; }
    }

    public sealed class MethodHookEnableResult
    {
        public string HookId { get; set; }
        public long Revision { get; set; }
        public string State { get; set; }
        public bool Changed { get; set; }
        public bool CallbackRecompiled { get; set; }
        public string EditorSessionId { get; set; }
        public string EventCursor { get; set; }
        public bool? DryRun { get; set; }
        [UnityCliItemsRange(0, 8)] public List<MethodHookPlannedEffect> PlannedEffects { get; set; }
    }

    public sealed class MethodHookDisableRequest
    {
        [UnityCliRequired] public string HookId { get; set; }
        [UnityCliRequired] public string EditorSessionId { get; set; }
        [UnityCliRequired] public long ExpectedRevision { get; set; }
    }

    public sealed class MethodHookDisableResult
    {
        public string HookId { get; set; }
        public long Revision { get; set; }
        public string State { get; set; }
        public bool Changed { get; set; }
        public bool AdapterReleased { get; set; }
        public string EditorSessionId { get; set; }
        public bool? DryRun { get; set; }
        [UnityCliItemsRange(0, 8)] public List<MethodHookPlannedEffect> PlannedEffects { get; set; }
    }

    public sealed class MethodHookRemoveRequest
    {
        [UnityCliRequired] public string HookId { get; set; }
        [UnityCliRequired] public string EditorSessionId { get; set; }
        [UnityCliRequired] public long ExpectedRevision { get; set; }
    }

    public sealed class MethodHookRemoveResult
    {
        public string HookId { get; set; }
        public string State { get; set; }
        public bool Removed { get; set; }
        public bool AdapterReleased { get; set; }
        public long RemainingDefinitionsForTarget { get; set; }
        public string EditorSessionId { get; set; }
        public bool? DryRun { get; set; }
        [UnityCliItemsRange(0, 8)] public List<MethodHookPlannedEffect> PlannedEffects { get; set; }
    }

    // ---- list ----

    public sealed class MethodHookListRequest
    {
        [UnityCliStringEnum("armed", "dormant", "restoring", "disabled", "faulted")] public string State { get; set; }
        public string Label { get; set; }
        [UnityCliRange(1, DebugLimits.ListMaxLimit)] public int? Limit { get; set; }
        public string Cursor { get; set; }
    }

    public sealed class MethodHookListItem
    {
        public string HookId { get; set; }
        public string Label { get; set; }
        public string State { get; set; }

        /// <summary>
        /// 当前 revision；这正是 debug.hook-remove/replace/enable/disable 要求的 expectedRevision。
        /// </summary>
        public long Revision { get; set; }

        public string Activation { get; set; }
        public int Priority { get; set; }
        public string MethodIdentity { get; set; }
        public long HitCount { get; set; }
        public string FaultCode { get; set; }

        /// <summary>
        /// 拥有该定义的 Editor session。
        /// debug.hook-remove/replace/enable/disable 都要求 editorSessionId，因此列表必须直接给出同一 identity，
        /// 使只持有列表输出的客户端不需要另一次调用也能完成清理。
        /// </summary>
        public string EditorSessionId { get; set; }

        /// <summary>
        /// 声明的监听式触发配置（含 trigger、phase、命中预算与实际命中数）；未声明 listener 时为 null。
        /// 列表因此能说明一个定义由哪个触发源驱动、声明的是哪一个 phase，不需要另一次 info。
        /// </summary>
        public MethodHookListenerSummary Listener { get; set; }

        /// <summary>声明的断点配置及其 session 资源占用；未声明断点时为空。</summary>
        public MethodHookBreakpointSummary Breakpoint { get; set; }
    }

    public sealed class MethodHookListResult
    {
        public List<MethodHookListItem> Items { get; set; }
        public string NextCursor { get; set; }
        public long Total { get; set; }
        public long NextOffset { get; set; }
        public bool HasMore { get; set; }
        public string EditorSessionId { get; set; }
    }

    // ---- info ----

    public sealed class MethodHookInfoRequest
    {
        [UnityCliRequired] public string HookId { get; set; }
        [UnityCliStringEnum("prefix", "postfix")] public string SourceSlot { get; set; }
        [UnityCliRange(0, 1048576)] public int? SourceOffset { get; set; }
    }

    public sealed class MethodHookFaultSummary
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public long Revision { get; set; }
        public long InvocationId { get; set; }
        public string Owner { get; set; }
    }

    public sealed class MethodHookEventSummary
    {
        public long TotalRecords { get; set; }
        public long OldestAvailableCursor { get; set; }
        public long LatestSequence { get; set; }
        public List<UnityCliDebugGap> Gaps { get; set; }
    }

    public sealed class MethodHookSourcePage
    {
        public string Slot { get; set; }
        public long Offset { get; set; }
        public long TotalBytes { get; set; }
        public string Text { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class MethodHookInfoResult
    {
        public string HookId { get; set; }
        public string Label { get; set; }
        public string State { get; set; }
        public long Revision { get; set; }
        public string EditorSessionId { get; set; }
        public string Activation { get; set; }
        public int Priority { get; set; }
        public bool EmitHitEvents { get; set; }
        /// <summary>调用次数；回调自身重入被抑制的调用不计入命中。</summary>
        public long HitCount { get; set; }
        /// <summary>
        /// 回调自身重入被抑制的次数。被抑制的调用不建立帧、不产生命中事件，
        /// 因此读取方必须用本计数区分“被抑制”与“未发生”。
        /// </summary>
        public long BypassCount { get; set; }
        public MethodHookTargetSummary Target { get; set; }
        public MethodHookCaptureSummary Capture { get; set; }
        public List<MethodHookCallbackSummary> Callbacks { get; set; }
        /// <summary>监听配置与其过滤/有限命中计数；未声明 listener 时为 null。</summary>
        public MethodHookListenerSummary Listener { get; set; }
        /// <summary>断点配置与它占用的 session 资源槽位；未声明断点时为 null。</summary>
        public MethodHookBreakpointSummary Breakpoint { get; set; }
        public MethodHookFaultSummary LastFault { get; set; }
        public MethodHookEventSummary EventSummary { get; set; }
        public long StateBytes { get; set; }
        /// <summary>发生在已被取代 revision 上的回调故障计数；只统计，不改变当前 revision 的启停状态。</summary>
        public long StaleFaults { get; set; }
        /// <summary>因线程侧补充队列已满而放弃的 Unity 显示身份补充数；命中事件本身不受影响。</summary>
        public long DroppedSupplements { get; set; }
        public int AdapterIdentities { get; set; }
        public int CompiledArtifacts { get; set; }
        public long CompiledArtifactBytes { get; set; }
        /// <summary>该 target 上的非本 CLI patch owner 摘要；为空表示没有观察到共存 owner。</summary>
        public string ForeignOwners { get; set; }
        public MethodHookSourcePage SourcePage { get; set; }
    }

    // ---- shared session-scoped introspection ----

    /// <summary>hook 子系统当前状态摘要，供 info/doctor 类读取复用同一 owner。</summary>
    public sealed class MethodHookRuntimeSummary
    {
        public string EditorSessionId { get; set; }
        public long DefinitionCount { get; set; }
        public long TargetCount { get; set; }
        public int AdapterIdentities { get; set; }
        public int CompiledArtifacts { get; set; }
        public long CompiledArtifactBytes { get; set; }
        public string ClockSegment { get; set; }
        public string PlaySessionId { get; set; }
    }
}
#endif
