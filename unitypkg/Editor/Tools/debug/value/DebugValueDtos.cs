#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// 一次读取的时钟溯源。
    ///
    /// 冻结的四种种类在 UnityCliDebugClocks：monotonic、editor-tick、unity-frame、project-logical。
    /// 每个投影都记录来源、线程、采集时刻、有效区间与是否同现场采集，并明确它是否就是 Time.frameCount。
    /// </summary>
    public sealed class DebugClockView
    {
        /// <summary>时钟种类，见 UnityCliDebugClocks。</summary>
        public string Kind { get; set; }

        /// <summary>该种类的读数；unity-frame 的 off-main 情形下是主线程缓存帧号。</summary>
        public double Value { get; set; }

        /// <summary>主线程采集到的真实 Unity 帧号；off-main 采集时为 0。</summary>
        public long Frame { get; set; }

        /// <summary>off-main 采集时主线程最后发布的帧号；主线程采集时为 0。</summary>
        public long LastObservedMainFrame { get; set; }

        /// <summary>执行域相位（edit 或 play）；它是执行域事实，不是帧号。</summary>
        public string Phase { get; set; }

        /// <summary>采集线程 id。</summary>
        public int ThreadId { get; set; }

        /// <summary>是否在可证明的主线程上采集。</summary>
        public bool MainThread { get; set; }

        /// <summary>该读数的具体提供者。</summary>
        public string Source { get; set; }

        /// <summary>采集时刻的进程单调毫秒。</summary>
        public double CapturedAtMonotonicMs { get; set; }

        /// <summary>时钟与业务值是否在同一次调用、同一现场采集。</summary>
        public bool CapturedSameSite { get; set; }

        /// <summary>有效区间下界（单调毫秒）。</summary>
        public double ValidFromMonotonicMs { get; set; }

        /// <summary>有效区间上界（单调毫秒）。</summary>
        public double ValidToMonotonicMs { get; set; }

        /// <summary>有效区间的语义说明；单点观察不等于连续覆盖。</summary>
        public string ValidityNote { get; set; }

        /// <summary>该读数是否就是 Unity 的 Time.frameCount。</summary>
        public bool IsUnityFrameCount { get; set; }
    }

    /// <summary>一个由项目值发布的命名逻辑时钟；它明确不是 Time.frameCount。</summary>
    public sealed class DebugLogicalClockView
    {
        public string Name { get; set; }
        public double Value { get; set; }

        /// <summary>项目值的原始文本，避免大整数经 double 丢失精度。</summary>
        public string RawText { get; set; }
        public string Source { get; set; }
        public int ThreadId { get; set; }
        public bool MainThread { get; set; }
        public double CapturedAtMonotonicMs { get; set; }
        public bool CapturedSameSite { get; set; }
        public double ValidFromMonotonicMs { get; set; }
        public double ValidToMonotonicMs { get; set; }
        public string ValidityNote { get; set; }
        public bool IsUnityFrameCount { get; set; }

        /// <summary>发布该时钟时的 domain generation；与本域当前 generation 不一致时该值属于旧域。</summary>
        public string DomainGeneration { get; set; }
    }

    /// <summary>一个托管对象句柄的公开事实。</summary>
    public sealed class DebugHandleView
    {
        public string HandleId { get; set; }
        public string SessionId { get; set; }
        public string TypeName { get; set; }

        /// <summary>true 表示强引用；强引用改变目标存活，是显式记录的干预。</summary>
        public bool Strong { get; set; }

        /// <summary>句柄状态：live 或 stale。</summary>
        public string State { get; set; }

        /// <summary>stale 的可证明原因：target-collected、unity-object-destroyed、runtime-changed、domain-generation-changed。</summary>
        public string StaleReason { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public string CreatedAtUtc { get; set; }
        public double CreatedAtMonotonicMs { get; set; }

        /// <summary>该句柄对目标存活的影响说明。</summary>
        public string LifetimeNote { get; set; }
    }

    /// <summary>句柄预算投影；与 DebugLimits 同源。</summary>
    public sealed class DebugValueHandleBudgetView
    {
        public string SessionId { get; set; }
        public int SessionUsed { get; set; }
        public int SessionRemaining { get; set; }
        public int MaxHandlesPerSession { get; set; }
        public int GlobalUsed { get; set; }
        public int GlobalRemaining { get; set; }
        public int MaxHandlesGlobal { get; set; }
    }

    /// <summary>条件恢复记录投影；session.stop 按该记录恢复本 session 的受管理字段写入。</summary>
    public sealed class DebugValueRestoreView
    {
        public bool Registered { get; set; }
        public string RestoreId { get; set; }
        public string SessionId { get; set; }
        public string TargetIdentity { get; set; }
        public string Path { get; set; }
        public string MemberTypeName { get; set; }

        /// <summary>写入前的原始值（有界投影）。</summary>
        [UnityCliAnyJson] public object OriginalValue { get; set; }

        /// <summary>本次写入的值（有界投影）。</summary>
        [UnityCliAnyJson] public object WrittenValue { get; set; }

        public string WrittenAtUtc { get; set; }
        public double WrittenAtMonotonicMs { get; set; }

        /// <summary>恢复条件说明：只有目标仍有效且当前值仍等于本次写入值时才恢复。</summary>
        public string Condition { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// debug.value-read 的公开输入契约。
    ///
    /// rootKind 决定根身份：static-field 使用 typeName+fieldName，selector 使用现有 Unity selector 字段，
    /// handle 使用 handleId。fieldPath 是相对根的值的有界路径，分段只能是字段名或一维数组下标。
    /// </summary>
    public sealed class DebugValueReadRequest
    {
        [UnityCliRequired, UnityCliStringEnum("static-field", "selector", "handle")] public string RootKind { get; set; }

        /// <summary>static-field 根的声明类型全名。</summary>
        public string TypeName { get; set; }

        /// <summary>static-field 根的精确字段名。</summary>
        public string FieldName { get; set; }

        /// <summary>selector 根的 Unity instanceId。</summary>
        public int? InstanceId { get; set; }

        /// <summary>selector 根的完整层级路径（大小写敏感）。</summary>
        public string Path { get; set; }

        /// <summary>selector 根的资产路径。</summary>
        public string AssetPath { get; set; }

        /// <summary>selector 根的资产 GUID。</summary>
        public string Guid { get; set; }

        /// <summary>selector 根的组件 instanceId。</summary>
        public int? ComponentInstanceId { get; set; }

        /// <summary>selector 根的组件下标。</summary>
        public int? ComponentIndex { get; set; }

        /// <summary>selector 根的组件类型名。</summary>
        public string Component { get; set; }

        /// <summary>selector 根的组件类型全名。</summary>
        public string ComponentType { get; set; }

        /// <summary>handle 根的句柄 id。</summary>
        public string HandleId { get; set; }

        /// <summary>相对根的有界字段名/数组下标路径；空表示读取根本身。</summary>
        public List<string> FieldPath { get; set; }

        /// <summary>把本次读到的值发布为该项目命名逻辑时钟。</summary>
        public string LogicalClockName { get; set; }

        /// <summary>是否为本次读取的根建立一个弱句柄；默认 none。</summary>
        [UnityCliStringEnum("none", "weak")] public string CreateHandle { get; set; }

        /// <summary>持有句柄所需的 session；createHandle 非 none 时必填。</summary>
        public string SessionId { get; set; }

        /// <summary>
        /// 显式把本次读取路由到一个暂停现场的线程上；省略时走正常路径。
        /// 命名 pauseId 后，读取在该现场自己的线程上执行，且在主线程暂停期间仍可到达。
        /// </summary>
        public string PauseId { get; set; }
    }

    /// <summary>
    /// debug.value-read 的公开输出契约。
    /// 纯读取：只返回有界值投影、根身份、声明类型、线程与时钟溯源。
    /// </summary>
    public sealed class DebugValueReadResult
    {
        public string RootKind { get; set; }

        /// <summary>根的稳定身份文本（类型与字段名、selector 事实或句柄 id）。</summary>
        public string RootIdentity { get; set; }

        /// <summary>根值的运行时类型全名。</summary>
        public string RootTypeName { get; set; }

        /// <summary>路径的规范文本；空路径为空字符串。</summary>
        public string Path { get; set; }

        /// <summary>拥有最后一个路径成员的声明类型全名；空路径时为根类型。</summary>
        public string DeclaringTypeName { get; set; }

        /// <summary>最终值的声明类型全名。</summary>
        public string ValueTypeName { get; set; }

        /// <summary>本次读取得到的值是否有界投影；identity-only 表示只报告类型而不序列化对象图。</summary>
        public string ValueProjection { get; set; }

        [UnityCliAnyJson] public object Value { get; set; }

        /// <summary>值是否为 null。</summary>
        public bool ValueIsNull { get; set; }

        /// <summary>null 的原因：actual-null 或 intermediate-null；句柄失效不是 null，而是 E_DEBUG_TARGET_STALE。</summary>
        public string NullReason { get; set; }

        /// <summary>路径在何处变为 null，仅 intermediate-null 时非空。</summary>
        public string NullAtPath { get; set; }

        /// <summary>投影是否触发了深度、条目或字符串截断。</summary>
        public bool ValueTruncated { get; set; }

        /// <summary>值投影的安全边界说明。</summary>
        public string ProjectionNote { get; set; }

        /// <summary>执行本次读取的线程 id。</summary>
        public int ThreadId { get; set; }

        /// <summary>本次读取是否在可证明的主线程上执行。</summary>
        public bool MainThread { get; set; }

        /// <summary>四种时钟的溯源投影；命名逻辑时钟只在本次发布时出现。</summary>
        public List<DebugClockView> Clocks { get; set; }

        /// <summary>当前已发布的命名逻辑时钟；旧 generation 的值由 domainGeneration 标出。</summary>
        public List<DebugLogicalClockView> LogicalClocks { get; set; }

        /// <summary>读取一致性：普通字段读取为 unsynchronized。</summary>
        public string Consistency { get; set; }

        public string ConsistencyNote { get; set; }

        /// <summary>本次为根建立的句柄；未请求时为空。</summary>
        public DebugHandleView Handle { get; set; }

        public DebugValueHandleBudgetView Handles { get; set; }
        public string SessionId { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
    }

    /// <summary>
    /// debug.value-write 的公开输入契约。
    /// 类型检查的结构化写入，不是属性 setter，也不是方法调用器。
    /// </summary>
    public sealed class DebugValueWriteRequest
    {
        [UnityCliRequired, UnityCliStringEnum("static-field", "selector", "handle")] public string RootKind { get; set; }

        public string TypeName { get; set; }
        public string FieldName { get; set; }
        public int? InstanceId { get; set; }
        public string Path { get; set; }
        public string AssetPath { get; set; }
        public string Guid { get; set; }
        public int? ComponentInstanceId { get; set; }
        public int? ComponentIndex { get; set; }
        public string Component { get; set; }
        public string ComponentType { get; set; }
        public string HandleId { get; set; }

        /// <summary>相对根的有界字段名/数组下标路径；空表示写入根本身。</summary>
        public List<string> FieldPath { get; set; }

        /// <summary>写入值；必须是该字段声明类型的合法值。</summary>
        [UnityCliPresenceRequired, UnityCliAnyJson] public object Value { get; set; }

        /// <summary>是否使用 expectedValue 作为写入前置条件。</summary>
        public bool GuardByExpectedValue { get; set; }

        /// <summary>前置条件值；guardByExpectedValue 为 true 时读取并在不相等时不写入。</summary>
        [UnityCliAnyJson] public object ExpectedValue { get; set; }

        /// <summary>是否登记条件恢复记录；默认 true。</summary>
        public bool RegisterRestore { get; set; } = true;

        /// <summary>是否为根建立句柄；strong 是改变目标存活的显式干预。</summary>
        [UnityCliStringEnum("none", "weak", "strong")] public string CreateHandle { get; set; }

        /// <summary>写入必须属于的 session；决定条件恢复记录与句柄归属。</summary>
        [UnityCliRequired] public string SessionId { get; set; }

        /// <summary>
        /// 显式把本次写入路由到一个暂停现场的线程上；省略时走正常路径。
        /// 命名 pauseId 后，写入在该现场自己的线程上执行，且在主线程暂停期间仍可到达。
        /// </summary>
        public string PauseId { get; set; }
    }

    /// <summary>
    /// debug.value-write 的公开输出契约。
    /// 结果区分"已写入"与"前置条件不满足因此未写入"，并给出条件恢复记录与效果类别。
    /// </summary>
    public sealed class DebugValueWriteResult
    {
        /// <summary>applied 或 guard-mismatch；guard-mismatch 表示本次没有写入任何字段。</summary>
        public string Outcome { get; set; }

        /// <summary>本次是否实际写入了字段。</summary>
        public bool Applied { get; set; }

        /// <summary>是否评估了 expectedValue 前置条件。</summary>
        public bool GuardEvaluated { get; set; }

        /// <summary>前置条件是否满足；未评估时为 false。</summary>
        public bool GuardSatisfied { get; set; }

        /// <summary>效果类别：结构化干预。</summary>
        public string EffectCategory { get; set; }

        /// <summary>本次效果的可证明范围说明。</summary>
        public string EffectNote { get; set; }

        public string RootKind { get; set; }
        public string RootIdentity { get; set; }
        public string RootTypeName { get; set; }
        public string Path { get; set; }
        public string DeclaringTypeName { get; set; }
        public string MemberTypeName { get; set; }

        /// <summary>写入前的原值（有界投影）；未写入时报告实际观测到的当前值。</summary>
        [UnityCliAnyJson] public object PreviousValue { get; set; }

        /// <summary>本次提交的值（有界投影）。</summary>
        [UnityCliAnyJson] public object WrittenValue { get; set; }

        /// <summary>是否经过值类型副本链写回；true 表示中间分段为 struct，已显式写回。</summary>
        public bool StructWriteBack { get; set; }
        public string StructWriteBackNote { get; set; }

        public int ThreadId { get; set; }
        public bool MainThread { get; set; }
        public List<DebugClockView> Clocks { get; set; }
        public DebugValueRestoreView Restore { get; set; }
        public DebugHandleView Handle { get; set; }
        public DebugValueHandleBudgetView Handles { get; set; }
        public string SessionId { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
    }
}
#endif
