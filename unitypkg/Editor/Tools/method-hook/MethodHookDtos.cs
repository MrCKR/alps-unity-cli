#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// method-hook 组的 closed Request/Result DTO。
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
        [UnityCliItemsRange(0, MethodHookLimits.MaxCaptureFields)] public List<MethodHookCaptureFieldSpec> InstanceFields { get; set; }
        [UnityCliItemsRange(0, MethodHookLimits.MaxCaptureFields)] public List<MethodHookCaptureFieldSpec> StaticFields { get; set; }
        [UnityCliRange(1, MethodHookLimits.MaxSampleEvery)] public int? SampleEvery { get; set; }
    }

    /// <summary>同步 C# 回调体；由固定 wrapper 编译，不接受 async/Task/await。</summary>
    public sealed class MethodHookCallbackSpec
    {
        public string PrefixCode { get; set; }
        public string PostfixCode { get; set; }
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
        [UnityCliRange(1, MethodHookLimits.ListMaxLimit)] public int? Limit { get; set; }
        public string Cursor { get; set; }
    }

    public sealed class MethodHookListItem
    {
        public string HookId { get; set; }
        public string Label { get; set; }
        public string State { get; set; }
        public long Revision { get; set; }
        public string Activation { get; set; }
        public int Priority { get; set; }
        public string MethodIdentity { get; set; }
        public long HitCount { get; set; }
        public string FaultCode { get; set; }
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
        public List<MethodHookGap> Gaps { get; set; }
    }

    public sealed class MethodHookGap
    {
        public long FromSequence { get; set; }
        public long ToSequence { get; set; }
        public string Reason { get; set; }
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
        public long HitCount { get; set; }
        public long BypassCount { get; set; }
        public MethodHookTargetSummary Target { get; set; }
        public MethodHookCaptureSummary Capture { get; set; }
        public List<MethodHookCallbackSummary> Callbacks { get; set; }
        public MethodHookFaultSummary LastFault { get; set; }
        public MethodHookEventSummary EventSummary { get; set; }
        public long StateBytes { get; set; }
        public int AdapterIdentities { get; set; }
        public int CompiledArtifacts { get; set; }
        public long CompiledArtifactBytes { get; set; }
        /// <summary>该 target 上的非本 CLI patch owner 摘要；为空表示没有观察到共存 owner。</summary>
        public string ForeignOwners { get; set; }
        public MethodHookSourcePage SourcePage { get; set; }
    }

    // ---- events ----

    public sealed class MethodHookEventsRequest
    {
        public string HookId { get; set; }
        [UnityCliStringEnum("prefix", "postfix", "custom", "fault", "lifecycle")] public string Kind { get; set; }
        public string PlaySessionId { get; set; }
        [UnityCliRange(1, MethodHookLimits.EventsMaxLimit)] public int? Limit { get; set; }
        public string Cursor { get; set; }
    }

    public sealed class MethodHookEventItem
    {
        public long Sequence { get; set; }
        public string HookId { get; set; }
        public long Revision { get; set; }
        public long InvocationId { get; set; }
        public string Kind { get; set; }
        public string PlaySessionId { get; set; }
        public string ClockSegment { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public long Frame { get; set; }
        public bool MainThread { get; set; }
        public bool Sampled { get; set; }
        public bool CallbackPending { get; set; }
        public string Instance { get; set; }
        public string MethodIdentity { get; set; }
        [UnityCliAnyJson] public object Arguments { get; set; }
        [UnityCliAnyJson] public object Result { get; set; }
        [UnityCliAnyJson] public object Fields { get; set; }
        public string Name { get; set; }
        [UnityCliAnyJson] public object Value { get; set; }
        public string FaultCode { get; set; }
        public string FaultMessage { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class MethodHookEventsResult
    {
        public List<MethodHookEventItem> Events { get; set; }
        public string NextCursor { get; set; }
        public bool HasMore { get; set; }
        public long OldestAvailableCursor { get; set; }
        public List<MethodHookGap> Gaps { get; set; }
        public string EditorSessionId { get; set; }
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
