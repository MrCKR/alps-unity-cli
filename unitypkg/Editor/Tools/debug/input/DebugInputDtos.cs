#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    // ---- info ----

    /// <summary>
    /// 后端与通用输入路径的只读报告；可选声明一个候选 managed wrapper 来现场验证那个输入路径。
    /// </summary>
    public sealed class DebugInputInfoRequest
    {
        /// <summary>可选：只报告该 session 拥有的设备与输入记录。</summary>
        public string SessionId { get; set; }

        /// <summary>可选：候选 managed wrapper 的 exact 目标，用于现场验证该 wrapper 输入路径。</summary>
        public MethodHookTargetSpec Wrapper { get; set; }
    }

    /// <summary>可选设备后端的可用性事实；unavailable 时给出稳定原因与缺失成员。</summary>
    public sealed class DebugInputBackendView
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Reason { get; set; }
        public string Detail { get; set; }
        public string ApiIdentity { get; set; }
        public List<string> MissingApiMembers { get; set; }

        /// <summary>本 owner 是否安装过依赖；恒为 false，因为它从不安装任何包。</summary>
        public bool InstalledByThisOwner { get; set; }

        public string Layouts { get; set; }
    }

    /// <summary>一条通用输入路径：它使用的真实 Tool 与真实的线程和逻辑帧语义。</summary>
    public sealed class DebugInputRouteView
    {
        public string Kind { get; set; }
        public string Tool { get; set; }
        public string Thread { get; set; }
        public string LogicalFrame { get; set; }
        public string Proof { get; set; }
    }

    /// <summary>候选 managed wrapper 的现场验证结果；只有真实 detour 加真实调用才算支持。</summary>
    public sealed class DebugInputLegacyView
    {
        public bool Supported { get; set; }
        public string Reason { get; set; }
        public string Statement { get; set; }
        public string MethodIdentity { get; set; }
        public string ResolutionError { get; set; }
        public DebugInputLegacyRouteView Route { get; set; }
        public List<string> DriveWindows { get; set; }
    }

    /// <summary>验证成立的那条 wrapper 路径：detour 事实与真实命中次数分开报告。</summary>
    public sealed class DebugInputLegacyRouteView
    {
        public string HookId { get; set; }
        public string SessionId { get; set; }
        public string State { get; set; }
        public long HitCount { get; set; }
        public string MethodIdentity { get; set; }
        public bool DetourVerified { get; set; }
        public bool InvocationVerified { get; set; }
    }

    /// <summary>一个会话拥有的虚拟设备。</summary>
    public sealed class DebugInputDeviceView
    {
        public string DeviceId { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public string Backend { get; set; }
        public string ApiIdentity { get; set; }
        public string ResourceId { get; set; }
        public string PlaySegmentId { get; set; }
        public double CreatedAtMonotonicMs { get; set; }
        public int CreatedThreadId { get; set; }
        public string RemovalReason { get; set; }
    }

    /// <summary>一条输入记录：动作、控制、结束边界、目标坐标空间与释放确认状态。</summary>
    public sealed class DebugInputHoldView
    {
        public string DispatchId { get; set; }
        public string SessionId { get; set; }
        public string DeviceId { get; set; }
        public string DeviceKind { get; set; }
        public string Action { get; set; }
        public List<string> Controls { get; set; }
        public string EndBoundary { get; set; }
        public long HoldMs { get; set; }
        public double ExpiresAtMonotonicMs { get; set; }
        public double DispatchedAtMonotonicMs { get; set; }
        public int DispatchedThreadId { get; set; }
        public string State { get; set; }
        public string ReleaseReason { get; set; }
        public double ReleasedAtMonotonicMs { get; set; }
        public bool ReleaseConfirmed { get; set; }
        public string PointerSpace { get; set; }
        public double PointerX { get; set; }
        public double PointerY { get; set; }
        public double ResolvedWidth { get; set; }
        public double ResolvedHeight { get; set; }

        /// <summary>读取时刻设备真实报告为按下的控制；空列表不等于没有请求过按下。</summary>
        public List<string> ConfirmedControls { get; set; }

        /// <summary>读取确认失败时的稳定原因；成功时为空。</summary>
        public string ConfirmReason { get; set; }
    }

    /// <summary>输入域自身的占用事实；不含监听式触发预算，后者由 debug.session.info 报告。</summary>
    public sealed class DebugInputBudgetView
    {
        public long DevicesUsed { get; set; }
        public long MaxDevicesPerSession { get; set; }
        public long HoldsUsed { get; set; }
        public long MaxHoldsPerSession { get; set; }
        public long KeysPerDispatchMax { get; set; }
        public long MaxHoldMs { get; set; }
        public long PlannedInputs { get; set; }
        public long MaxPlannedInputsPerSession { get; set; }
        public long PendingReleaseRequests { get; set; }
        public long MaxPendingReleaseRequests { get; set; }
    }

    public sealed class DebugInputInfoResult
    {
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public DebugInputBackendView Backend { get; set; }
        public List<DebugInputRouteView> GenericRoutes { get; set; }
        public DebugInputLegacyView Legacy { get; set; }
        public List<DebugInputDeviceView> Devices { get; set; }
        public List<DebugInputHoldView> Holds { get; set; }
        public DebugInputBudgetView Budget { get; set; }

        /// <summary>本 owner 的所有权声明：只驱动自己的虚拟设备，从不 reset 真实设备或其他 owner。</summary>
        public string OwnershipStatement { get; set; }
    }

    // ---- key ----

    /// <summary>键下发：按下声明的键并携带自己的显式结束边界；释放用 debug.input.release。</summary>
    public sealed class DebugInputKeyRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }

        /// <summary>会话拥有的键设备上要按下的键名；取值是设备后端定义的键控制名，例如 Space、W、LeftShift。</summary>
        [UnityCliRequired, UnityCliItemsRange(1, DebugLimits.MaxInputKeysPerDispatch)] public List<string> Keys { get; set; }

        /// <summary>必需的结束边界：hold-ms 或 until-release，没有默认值。</summary>
        [UnityCliRequired, UnityCliStringEnum("hold-ms", "until-release")] public string EndBoundary { get; set; }

        /// <summary>hold-ms 边界所需的毫秒数。</summary>
        [UnityCliRange(1, DebugLimits.MaxInputHoldMs)] public long? HoldMs { get; set; }
    }

    // ---- pointer ----

    /// <summary>
    /// 指针下发：把指针提交到声明坐标空间的位置。
    /// 声明按键时它是按下并携带自己的显式结束边界；不声明按键时它是纯移动。
    /// </summary>
    public sealed class DebugInputPointerRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }

        /// <summary>声明的目标坐标空间：screen-pixels 或 game-view-normalized。</summary>
        [UnityCliRequired, UnityCliStringEnum("screen-pixels", "game-view-normalized")] public string TargetSpace { get; set; }

        /// <summary>声明的位置；game-view-normalized 时要求在 0 与 1 之间。</summary>
        [UnityCliRequired] public double X { get; set; }
        [UnityCliRequired] public double Y { get; set; }

        /// <summary>要按下的指针按键：left、right、middle；空表示只移动指针。释放用 debug.input.release。</summary>
        [UnityCliItemsRange(0, DebugLimits.MaxInputPointerButtons)] public List<string> Buttons { get; set; }

        /// <summary>按下按键时必需的结束边界：hold-ms 或 until-release；纯移动不得声明。</summary>
        [UnityCliStringEnum("hold-ms", "until-release")] public string EndBoundary { get; set; }

        /// <summary>hold-ms 边界所需的毫秒数。</summary>
        [UnityCliRange(1, DebugLimits.MaxInputHoldMs)] public long? HoldMs { get; set; }
    }

    /// <summary>一次输入下发的完整结果：设备身份、结束边界、目标坐标空间解析与释放确认状态。</summary>
    public sealed class DebugInputDispatchResult
    {
        public string DispatchId { get; set; }
        public string SessionId { get; set; }
        public string DeviceId { get; set; }
        public string DeviceKind { get; set; }
        public string DeviceName { get; set; }
        public string Backend { get; set; }
        public string ApiIdentity { get; set; }
        public string Action { get; set; }
        public List<string> Controls { get; set; }
        public string EndBoundary { get; set; }
        public long HoldMs { get; set; }
        public double ExpiresAtMonotonicMs { get; set; }
        public string State { get; set; }
        public string ReleaseState { get; set; }
        public string ReleaseReason { get; set; }
        public bool ReleaseConfirmed { get; set; }
        public string PointerSpace { get; set; }
        public double PointerX { get; set; }
        public double PointerY { get; set; }
        public double ResolvedWidth { get; set; }
        public double ResolvedHeight { get; set; }
        public string ResolutionSource { get; set; }

        /// <summary>本次调用结束时设备真实报告为按下的控制；下发只证明已提交，不证明已应用。</summary>
        public List<string> ConfirmedControls { get; set; }

        /// <summary>读取确认失败时的稳定原因；成功时为空。</summary>
        public string ConfirmReason { get; set; }

        public double DispatchedAtMonotonicMs { get; set; }
        public int DispatchedThreadId { get; set; }
        public string ExecutionPoint { get; set; }

        /// <summary>本 owner 记录的触发事实的 sequence；用它读取 debug.events.read 里的同一条事件。</summary>
        public long EvidenceSequence { get; set; }

        public DebugInputBudgetView Budget { get; set; }

        /// <summary>诚实边界：设备后端接收事件不证明业务已读取它。</summary>
        public string Statement { get; set; }
    }

    // ---- release ----

    /// <summary>释放本 owner 在该 session 的输入；范围必须显式声明。</summary>
    public sealed class DebugInputReleaseRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }

        /// <summary>all 释放全部设备与记录；keys 只释放键设备；pointer 只释放指针设备。</summary>
        [UnityCliRequired, UnityCliStringEnum("all", "keys", "pointer")] public string Scope { get; set; }
    }

    public sealed class DebugInputReleaseResult
    {
        public string SessionId { get; set; }
        public string Scope { get; set; }

        /// <summary>只有每条目标记录都实际确认释放才为 released，否则为 pending。</summary>
        public string ReleaseState { get; set; }

        public double ReleasedAtMonotonicMs { get; set; }
        public int ReleasedThreadId { get; set; }
        public long ConfirmedCount { get; set; }
        public long PendingCount { get; set; }
        public List<string> PendingReasons { get; set; }

        /// <summary>pending 时说明由哪条主线程路径补完确认。</summary>
        public string DeferredTo { get; set; }

        public List<DebugInputHoldView> Holds { get; set; }
        public List<DebugInputDeviceView> Devices { get; set; }
        public DebugInputBudgetView Budget { get; set; }
        public string Statement { get; set; }
    }
}
#endif
