using System;

namespace UnityCli
{
    /// <summary>
    /// 可选设备后端的冻结身份。设备后端只提供“会话拥有的虚拟设备”，不替代通用业务输入路径。
    /// </summary>
    public static class UnityCliDebugInputBackends
    {
        /// <summary>可选的 Input System 设备后端；缺失时只报 unavailable，绝不安装依赖。</summary>
        public const string InputSystem = "input-system";

        /// <summary>没有任何可用设备后端。</summary>
        public const string None = "none";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, InputSystem, StringComparison.Ordinal)
                || string.Equals(value, None, StringComparison.Ordinal);
        }
    }

    /// <summary>可用性状态；只有现场探测成功才是 available。</summary>
    public static class UnityCliDebugInputBackendStates
    {
        public const string Available = "available";
        public const string Unavailable = "unavailable";
    }

    /// <summary>会话拥有的虚拟设备种类；每种会话至多一个。</summary>
    public static class UnityCliDebugInputDeviceKinds
    {
        /// <summary>键设备；布局名 Keyboard。</summary>
        public const string Keyboard = "keyboard";

        /// <summary>指针设备；布局名 Mouse。</summary>
        public const string Pointer = "pointer";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Keyboard, StringComparison.Ordinal) || string.Equals(value, Pointer, StringComparison.Ordinal);
        }
    }

    /// <summary>输入提交的取值；释放是独立 Tool，不是提交动作。</summary>
    public static class UnityCliDebugInputActions
    {
        /// <summary>按下控制并携带声明的结束边界。</summary>
        public const string Press = "press";

        /// <summary>只移动指针、不按下任何控制。</summary>
        public const string Move = "move";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Press, StringComparison.Ordinal) || string.Equals(value, Move, StringComparison.Ordinal);
        }
    }

    /// <summary>输入下发的结束边界；两者都是显式声明，不存在默认或无界保持。</summary>
    public static class UnityCliDebugInputEndBoundaries
    {
        /// <summary>按下后经过声明的毫秒数即请求释放。</summary>
        public const string HoldMs = "hold-ms";

        /// <summary>保持到显式 debug.input-release、暂停、目标失效、session stop 或 Play 退出。</summary>
        public const string UntilRelease = "until-release";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, HoldMs, StringComparison.Ordinal) || string.Equals(value, UntilRelease, StringComparison.Ordinal);
        }
    }

    /// <summary>指针的目标坐标空间；下发结果必须回报实际解析出的尺寸，调用方不需要猜。</summary>
    public static class UnityCliDebugInputPointerSpaces
    {
        /// <summary>当前 Game 视图/屏幕像素坐标；原点在左上角。</summary>
        public const string ScreenPixels = "screen-pixels";

        /// <summary>Game 视图归一化坐标；[0,1] 区间内按实际解析尺寸换算。</summary>
        public const string GameViewNormalized = "game-view-normalized";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, ScreenPixels, StringComparison.Ordinal)
                || string.Equals(value, GameViewNormalized, StringComparison.Ordinal);
        }
    }

    /// <summary>指针按键的冻结取值。</summary>
    public static class UnityCliDebugInputButtons
    {
        public const string Left = "left";
        public const string Right = "right";
        public const string Middle = "middle";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Left, StringComparison.Ordinal)
                || string.Equals(value, Right, StringComparison.Ordinal)
                || string.Equals(value, Middle, StringComparison.Ordinal);
        }
    }

    /// <summary>释放范围的冻结取值。</summary>
    public static class UnityCliDebugInputReleaseScopes
    {
        public const string All = "all";
        public const string Keys = "keys";
        public const string Pointer = "pointer";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, All, StringComparison.Ordinal)
                || string.Equals(value, Keys, StringComparison.Ordinal)
                || string.Equals(value, Pointer, StringComparison.Ordinal);
        }
    }

    /// <summary>一条输入记录的状态：held 仍被本 owner 按住，pending 已请求释放但未确认，released 已确认释放。</summary>
    public static class UnityCliDebugInputHoldStates
    {
        public const string Held = "held";
        public const string Pending = "pending";
        public const string Released = "released";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Held, StringComparison.Ordinal)
                || string.Equals(value, Pending, StringComparison.Ordinal)
                || string.Equals(value, Released, StringComparison.Ordinal);
        }
    }

    /// <summary>会话拥有的设备状态。</summary>
    public static class UnityCliDebugInputDeviceStates
    {
        public const string Active = "active";
        public const string Released = "released";
    }

    /// <summary>
    /// 稳定原因码。它们是机器可分支的事实：unavailable 的原因、释放确认失败的原因都在这里。
    /// </summary>
    public static class UnityCliDebugInputReasons
    {
        /// <summary>设备后端依赖未加载（例如缺少可选输入包）；本 owner 从不安装它。</summary>
        public const string BackendNotLoaded = "backend-not-loaded";

        /// <summary>后端类型存在但缺少本 owner 需要的 API 成员；缺失成员列表随诊断一起返回。</summary>
        public const string ApiMemberMissing = "api-member-missing";

        /// <summary>设备后端探测未找到任何受支持实现。</summary>
        public const string BackendUnsupported = "backend-unsupported";

        /// <summary>目标坐标空间无法解析（没有可读的 Game 视图/屏幕尺寸）。</summary>
        public const string TargetSpaceUnresolved = "target-space-unresolved";

        /// <summary>同一设备上同名控制已存在不同结束边界的按下记录。</summary>
        public const string HoldConflict = "hold-conflict";

        /// <summary>同名设备已存在且不属于本 owner；本 owner 绝不 reset 或接管它。</summary>
        public const string DeviceNameOwnedElsewhere = "device-name-owned-elsewhere";

        /// <summary>按下记录到达声明边界；释放已请求。</summary>
        public const string HoldBoundaryReached = "hold-boundary-reached";

        /// <summary>显式释放调用。</summary>
        public const string ExplicitRelease = "explicit-release";

        /// <summary>主线程在暂停前当场释放了可达的按下状态。</summary>
        public const string PauseMainThread = "pause-main-thread";

        /// <summary>worker 线程暂停；释放请求交由中央调度在主线程补完。</summary>
        public const string PauseWorkerDeferred = "pause-worker-deferred";

        /// <summary>session 停止清理释放了该 owner 的输入。</summary>
        public const string SessionStop = "session-stop";

        /// <summary>Play 退出使该 Play 段的设备与按下状态失效。</summary>
        public const string PlayExit = "play-exit";

        /// <summary>设备已不存在于后端的设备列表：不可能仍然保持任何按下状态。</summary>
        public const string DeviceAbsent = "device-absent";

        /// <summary>释放需要在主线程执行，但本次调用不在主线程；请求已登记等待中央调度。</summary>
        public const string RequiresMainThread = "requires-main-thread";

        /// <summary>后端当前不可用，无法读取设备列表来确认释放。</summary>
        public const string BackendUnavailable = "backend-unavailable";

        /// <summary>后端拒绝移除设备；本 owner 无法证明按下状态已清除。</summary>
        public const string DeviceRemovalRefused = "device-removal-refused";

        /// <summary>后端拒绝建立本 owner 的虚拟设备；因此没有设备，也没有任何下发。</summary>
        public const string DeviceCreationRefused = "device-creation-refused";

        /// <summary>后端拒绝提交键或指针状态；因此没有下发，或释放提交没有生效。</summary>
        public const string DispatchRefused = "dispatch-refused";

        /// <summary>后端拒绝读取按下状态；只有读回确认才能报告 released，因此释放保持待确认。</summary>
        public const string HoldReadRefused = "hold-read-refused";

        /// <summary>后端设备列表不可读，无法证明本 owner 的设备名不会被已有设备占用。</summary>
        public const string DeviceListUnreadable = "device-list-unreadable";

        /// <summary>释放状态已提交，但设备仍未报告清除；只有下一次输入更新之后才能确认。</summary>
        public const string ReleaseNotApplied = "release-not-applied";

        /// <summary>纯指针移动不保持任何控制，因此它自己就已经是已释放状态。</summary>
        public const string MoveOnly = "move-only";

        /// <summary>调用方声明的 wrapper 目标无法解析为可拦截的托管方法。</summary>
        public const string WrapperUnresolved = "wrapper-unresolved";

        /// <summary>调用方没有声明候选 managed wrapper。</summary>
        public const string NoWrapperDeclared = "no-wrapper-declared";

        /// <summary>候选 wrapper 上没有任何仍生效的 detour。</summary>
        public const string NoLiveDetour = "no-live-detour";

        /// <summary>候选 wrapper 已有 detour，但从未观察到真实调用；不能据此声明支持。</summary>
        public const string NoObservedInvocation = "no-observed-invocation";

        /// <summary>调用不在 Unity 主线程上，而该动作必须由主线程执行。</summary>
        public const string OffMainThread = "off-main-thread";

        /// <summary>
        /// 该取值是否属于本闭集。设备后端的 out reason 只在属于本闭集时才可以直接成为公开原因：
        /// 后端在异常路径返回的自由文本不是稳定原因，必须由本 owner 分类后另行诊断。
        /// </summary>
        public static bool IsDefined(string value)
        {
            for (var index = 0; index < All.Length; index++)
                if (string.Equals(value, All[index], StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>闭集内的全部稳定原因码。</summary>
        private static readonly string[] All =
        {
            BackendNotLoaded, ApiMemberMissing, BackendUnsupported, TargetSpaceUnresolved, HoldConflict,
            DeviceNameOwnedElsewhere, HoldBoundaryReached, ExplicitRelease, PauseMainThread, PauseWorkerDeferred,
            SessionStop, PlayExit, DeviceAbsent, RequiresMainThread, BackendUnavailable, DeviceRemovalRefused,
            DeviceCreationRefused, DispatchRefused, HoldReadRefused, DeviceListUnreadable, ReleaseNotApplied,
            MoveOnly, WrapperUnresolved, NoWrapperDeclared, NoLiveDetour, NoObservedInvocation, OffMainThread
        };
    }

    /// <summary>
    /// 通用输入路径的种类。它们不是设备后端，也不需要安装任何输入包；
    /// 名称与 Tool 名对应，调用方据此发现真实工具。
    /// </summary>
    public static class UnityCliDebugInputRouteKinds
    {
        /// <summary>直接调用业务入口方法（debug.method-invoke）。</summary>
        public const string BusinessEntry = "business-entry";

        /// <summary>可拦截 managed wrapper 上的参数、结果或跳过原方法干预（debug.hook.*）。</summary>
        public const string ManagedWrapperInjection = "managed-wrapper-injection";

        /// <summary>任意同步调试代码（debug.code-execute）。</summary>
        public const string ArbitraryCode = "arbitrary-code";

        /// <summary>程序 entry 在声明执行点上运行（debug.program-run）。</summary>
        public const string ProgramEntry = "program-entry";

        /// <summary>跨线程有界投递到目标线程安全点（debug.program-run 的 signal binding）。</summary>
        public const string ThreadSafePointDelivery = "thread-safe-point-delivery";
    }
}
