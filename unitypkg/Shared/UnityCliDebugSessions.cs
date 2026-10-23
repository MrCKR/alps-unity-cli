using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UnityCli
{
    /// <summary>debug session 状态机的冻结取值。</summary>
    public static class UnityCliDebugSessionStates
    {
        /// <summary>定义存在且可以接受新资源。</summary>
        public const string Open = "open";

        /// <summary>stop 正在执行清理管线，尚未得到终态。</summary>
        public const string Stopping = "stopping";

        /// <summary>已关闭未来派发；定义与证据保留，等待 remove。</summary>
        public const string Stopped = "stopped";

        /// <summary>state 是否为已声明的状态机取值。</summary>
        public static bool IsDefined(string value)
        {
            return string.Equals(value, Open, StringComparison.Ordinal)
                || string.Equals(value, Stopping, StringComparison.Ordinal)
                || string.Equals(value, Stopped, StringComparison.Ordinal);
        }
    }

    /// <summary>session 的激活范围。Editor 状态与请求范围冲突时创建被拒绝，而不是产生永不激活的定义。</summary>
    public static class UnityCliDebugActivationScopes
    {
        /// <summary>只在 Edit 模式激活。</summary>
        public const string Editor = "editor";

        /// <summary>只在 Play 模式激活。</summary>
        public const string Play = "play";

        /// <summary>两种模式都可激活。</summary>
        public const string EditorAndPlay = "editor-and-play";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Editor, StringComparison.Ordinal)
                || string.Equals(value, Play, StringComparison.Ordinal)
                || string.Equals(value, EditorAndPlay, StringComparison.Ordinal);
        }

        /// <summary>该范围是否允许在 Edit 模式激活。</summary>
        public static bool AllowsEdit(string value)
        {
            return string.Equals(value, Editor, StringComparison.Ordinal) || string.Equals(value, EditorAndPlay, StringComparison.Ordinal);
        }

        /// <summary>该范围是否允许在 Play 模式激活。</summary>
        public static bool AllowsPlay(string value)
        {
            return string.Equals(value, Play, StringComparison.Ordinal) || string.Equals(value, EditorAndPlay, StringComparison.Ordinal);
        }

        /// <summary>请求范围是否与当前 Play 状态冲突；冲突意味着该定义永远无法激活。</summary>
        public static bool Conflicts(string value, bool isPlaying)
        {
            return isPlaying ? !AllowsPlay(value) : !AllowsEdit(value);
        }
    }

    /// <summary>资源种类；决定清理管线中由哪个设施负责释放。</summary>
    public static class UnityCliDebugResourceKinds
    {
        public const string Hook = "hook";
        public const string Program = "program";
        public const string Input = "input";
        public const string Pause = "pause";
        public const string Run = "run";
        public const string Observation = "observation";

        /// <summary>资源记录的状态取值。</summary>
        public static bool IsDefined(string value)
        {
            return string.Equals(value, Hook, StringComparison.Ordinal)
                || string.Equals(value, Program, StringComparison.Ordinal)
                || string.Equals(value, Input, StringComparison.Ordinal)
                || string.Equals(value, Pause, StringComparison.Ordinal)
                || string.Equals(value, Run, StringComparison.Ordinal)
                || string.Equals(value, Observation, StringComparison.Ordinal);
        }
    }

    /// <summary>资源记录的状态机。</summary>
    public static class UnityCliDebugResourceStates
    {
        /// <summary>slot 已保留但设施尚未完成 effect。</summary>
        public const string Reserved = "reserved";

        /// <summary>设施已完成 effect，资源正在生效。</summary>
        public const string Active = "active";

        /// <summary>槽位不再占用；Play 退出等环境变化会置为该状态并等待清理。</summary>
        public const string Invalidated = "invalidated";
    }

    /// <summary>清理管线的冻结步骤所有者；未注册的 owner 必须显式报告零。</summary>
    public static class UnityCliDebugFacilityOwners
    {
        public const string Hook = "hook";
        public const string Program = "program";
        public const string Input = "input";
        public const string Pause = "pause";
        public const string Run = "run";
        public const string Observation = "observation";

        /// <summary>冻结的设施顺序；报告必须按该顺序包含全部条目。</summary>
        public static string[] All()
        {
            return new[] { Hook, Program, Input, Pause, Run, Observation };
        }
    }

    /// <summary>清理管线的冻结步骤定义。</summary>
    public static class UnityCliDebugCleanupSteps
    {
        /// <summary>停止新的派发与待注入。</summary>
        public const string StopDispatch = "stop-dispatch";

        /// <summary>发出协作取消并恢复被暂停的执行。</summary>
        public const string CancelAndResume = "cancel-and-resume";

        /// <summary>释放虚拟输入。</summary>
        public const string ReleaseInput = "release-input";

        /// <summary>关闭该 owner 的观察与 listener。</summary>
        public const string CloseObservation = "close-observation";

        /// <summary>请求撤销该 owner 的 hook 与 lookup。</summary>
        public const string DetachHooks = "detach-hooks";

        /// <summary>执行显式且仍然适用的有限清理条目。</summary>
        public const string ExplicitCleanup = "explicit-cleanup";

        /// <summary>释放等待与句柄。</summary>
        public const string ReleaseWaits = "release-waits";

        /// <summary>归集并发布报告；该步骤本身不改变业务状态。</summary>
        public const string EmitReport = "emit-report";

        /// <summary>清理必须严格按该顺序执行的步骤名。</summary>
        public static string[] Ordered()
        {
            return new[]
            {
                StopDispatch, CancelAndResume, ReleaseInput, CloseObservation, DetachHooks, ExplicitCleanup, ReleaseWaits, EmitReport
            };
        }

        /// <summary>步骤的 1-based 顺序号；未知名字返回 0。</summary>
        public static int OrderOf(string name)
        {
            var ordered = Ordered();
            for (var index = 0; index < ordered.Length; index++)
            {
                if (string.Equals(ordered[index], name, StringComparison.Ordinal)) return index + 1;
            }
            return 0;
        }
    }

    /// <summary>
    /// 停止入口的稳定取值：报告必须说明本次 stop 由哪个入口服务。
    /// 入口决定哪些清理条目在 effect 之前被推迟，因此它是一条事实，不是诊断文本。
    /// </summary>
    public static class UnityCliDebugStopEntries
    {
        /// <summary>主线程上的 debug.session-stop 调用。</summary>
        public const string MainThread = "main-thread";

        /// <summary>主线程暂停期间由暂停控制通道在后台线程上服务的 stop。</summary>
        public const string PauseControlChannel = "pause-control-channel";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, MainThread, StringComparison.Ordinal)
                || string.Equals(value, PauseControlChannel, StringComparison.Ordinal);
        }
    }

    /// <summary>清理报告和 owner 摘要中的未注册标记；公开文本与实现共用同一来源。</summary>
    public static class UnityCliDebugOwnerMarkers
    {
        public const string NoOwnerRegistered = "no owner registered";
    }

    /// <summary>session 域唯一的资源上限来源；实现不得再写一份数量常量。</summary>
    public sealed class UnityCliDebugSessionCapacity
    {
        public UnityCliDebugSessionCapacity(int maxSessions, int maxActiveResourcesPerSession, int maxCleanupStepsPerSession, int listDefaultLimit, int listMaxLimit, int maxStateBytes, long maxStoreBytes)
        {
            if (maxSessions < 1) throw new ArgumentOutOfRangeException("maxSessions");
            if (maxActiveResourcesPerSession < 1) throw new ArgumentOutOfRangeException("maxActiveResourcesPerSession");
            if (maxCleanupStepsPerSession < 1) throw new ArgumentOutOfRangeException("maxCleanupStepsPerSession");
            if (listDefaultLimit < 1 || listMaxLimit < listDefaultLimit) throw new ArgumentOutOfRangeException("listMaxLimit");
            if (maxStateBytes < 1) throw new ArgumentOutOfRangeException("maxStateBytes");
            if (maxStoreBytes < 1L) throw new ArgumentOutOfRangeException("maxStoreBytes");
            MaxSessions = maxSessions;
            MaxActiveResourcesPerSession = maxActiveResourcesPerSession;
            MaxCleanupStepsPerSession = maxCleanupStepsPerSession;
            ListDefaultLimit = listDefaultLimit;
            ListMaxLimit = listMaxLimit;
            MaxStateBytes = maxStateBytes;
            MaxStoreBytes = maxStoreBytes;
        }

        public int MaxSessions { get; private set; }
        public int MaxActiveResourcesPerSession { get; private set; }
        public int MaxCleanupStepsPerSession { get; private set; }
        public int ListDefaultLimit { get; private set; }
        public int ListMaxLimit { get; private set; }
        public int MaxStateBytes { get; private set; }
        public long MaxStoreBytes { get; private set; }
    }

    /// <summary>稳定的 engine-free 失败原因；Editor 侧把原因映射为稳定错误码，Shared 不引用 Bridge 异常类型。</summary>
    public static class UnityCliDebugSessionFailures
    {
        public const string LimitExceeded = "limit-exceeded";
        public const string DuplicateLabel = "duplicate-label";
        public const string MissingSession = "missing-session";
        public const string WrongState = "wrong-state";
        public const string ScopeConflict = "scope-conflict";
        public const string CleanupPending = "cleanup-pending";
        public const string LabelInvalid = "label-invalid";
        public const string StateTooLarge = "state-too-large";
        public const string CleanupStepConflict = "cleanup-step-conflict";
    }

    /// <summary>session 定义；持久字段与本次 domain 的运行时事实分开保存。</summary>
    public sealed class UnityCliDebugSessionDefinition
    {
        public string SessionId = string.Empty;
        public string Label = string.Empty;
        public string ActivationScope = UnityCliDebugActivationScopes.EditorAndPlay;
        public string ClockKind = UnityCliDebugClocks.Monotonic;
        public string State = UnityCliDebugSessionStates.Open;
        public long Revision = 1L;
        public string CreatedAtUtc = string.Empty;
        public double CreatedAtMonotonicMs;
        public string StoppedAtUtc = string.Empty;
        public double StoppedAtMonotonicMs;
        public long InitialStateBytes;
        public object InitialState;

        /// <summary>该定义由上一个 domain generation 恢复而来，因此不带任何活动资源。</summary>
        public bool RestoredWithoutActivation;

        /// <summary>
        /// 设施或清理管线登记的“本次清理未完成”标记（推迟的主线程条目、失败的清理条目、Play 退出失效等）。
        /// 它只是待清理真值的一个来源，不与读取方直接见面。
        /// </summary>
        private bool cleanupStalled;

        /// <summary>
        /// 该定义当前是否仍有未完成的清理。读取方看到的真值只有一个：
        /// 显式停滞标记，或本注册表里仍属于本 session 的活动资源。
        /// 因此报告、info/list 投影与 remove 判定不可能在还有注册资源时宣称清理已完成。
        /// 写入只设置停滞标记；重载恢复写 false 表示既无资源也无停滞。
        /// </summary>
        public bool CleanupPending
        {
            get { return cleanupStalled || Resources.Count > 0; }
            set { cleanupStalled = value; }
        }

        /// <summary>
        /// 该定义的未来派发是否已关闭。stop 一开始就置位（早于清理管线），
        /// 因此任何线程上的命中都不会在清理期间进入新的用户代码；只在同一次启动内有效。
        /// </summary>
        internal bool DispatchClosed;

        internal readonly List<UnityCliDebugResourceRecord> Resources = new List<UnityCliDebugResourceRecord>();
        internal readonly List<UnityCliDebugCleanupStepRegistration> CleanupSteps = new List<UnityCliDebugCleanupStepRegistration>();

        /// <summary>最近一次真正运行过的清理报告；重复 stop 与只读投影据此返回同一终态事实。</summary>
        internal UnityCliDebugCleanupReport LastReport;

        /// <summary>
        /// 仍然占用活动槽位的资源数量。
        /// 失效但尚未释放的资源仍占用槽位，因此必须计入：否则未清理资源可以无界积累。
        /// </summary>
        public int ActiveResourceCount
        {
            get { return Resources.Count; }
        }
    }

    /// <summary>资源 owner 记录；identity、kind、所属 session、state 与清理委托槽位都在这里。</summary>
    public sealed class UnityCliDebugResourceRecord
    {
        public string ResourceId = string.Empty;
        public string Kind = string.Empty;
        public string SessionId = string.Empty;
        public string Target = string.Empty;
        public string State = UnityCliDebugResourceStates.Reserved;
        public string PlaySegmentId = string.Empty;

        /// <summary>设施在 reserve 时给出的清理委托；无委托表示清理无法被证明。</summary>
        public Func<UnityCliDebugCleanupReport, bool> Cleanup;

        /// <summary>
        /// 该资源的清理是否必须在 Unity 主线程上执行。
        /// 主线程暂停期间由控制通道服务的 stop 必须在 effect 之前推迟它为 pending，绝不跨线程执行。
        /// </summary>
        public bool RequiresMainThread;
    }

    /// <summary>设施注册的显式有限清理条目。</summary>
    internal sealed class UnityCliDebugCleanupStepRegistration
    {
        internal int Order;
        internal string Name = string.Empty;
        internal string Owner = string.Empty;
        internal Func<UnityCliDebugCleanupReport, bool> Apply;

        /// <summary>
        /// 该条目是否必须在 Unity 主线程上执行（detour 撤除、PlayerLoop 子系统移除、业务字段写回与用户清理代码）。
        /// 由注册它的设施声明，因为只有该 owner 知道自己的清理跨线程是否安全。
        /// </summary>
        internal bool RequiresMainThread;
    }

    /// <summary>管线当前处理到的步骤；owner 未注册时字段保持零并带显式标记。</summary>
    public sealed class UnityCliDebugCleanupStepStatus
    {
        public int Order;
        public string Name = string.Empty;
        public string Owner = string.Empty;
        public bool OwnerRegistered;
        public int Entries;
        public bool Succeeded = true;
        public string Note = string.Empty;
    }

    /// <summary>清理报告；每个字段类别在任何一次清理中都必须存在，空值也必须出现。</summary>
    public sealed class UnityCliDebugCleanupReport
    {
        public string SessionId = string.Empty;

        /// <summary>本次 stop 由哪个入口服务：main-thread 或 pause-control-channel。</summary>
        public string StopEntry = UnityCliDebugStopEntries.MainThread;

        public string Status = "complete";
        public bool CleanupPending;

        public long StoppedFutureEntries;
        public long CancelledPendingOperations;
        public long ReleasedPauses;
        public long ReleasedInput;
        public long ClosedObservations;
        public long RemovedDetours;
        public long PendingDetours;
        public long ExplicitCleanupEntries;
        public long ReleasedWaits;
        public long ReleasedHandles;

        public long ActiveInvocationCount;
        public long ActiveStepCount;
        public long ActiveEvaluateCount;
        public int ActiveThreadId;
        public long ActiveRevision;

        public bool UntrackedEffectsPossible;

        /// <summary>
        /// 宿主不能静态证明该 session 没有创建未管理的线程、任务或设备时为 true。
        /// 只要本 session 拥有过执行资源或执行过任意用户代码就必须为 true；此时报告同时给出精确事实。
        /// </summary>
        public bool UnmanagedUnknown;

        public int LoadedNonUnloadableAssemblyCount;
        public long LoadedNonUnloadableAssemblyBytes;

        public readonly List<UnityCliDebugTargetOwners> SharedTargets = new List<UnityCliDebugTargetOwners>();

        /// <summary>已经按条件恢复语义写回原值的字段。</summary>
        public readonly List<string> RestoredFields = new List<string>();

        /// <summary>目标仍有效但当前值已被其他写入改变的字段；这些值绝不被覆盖。</summary>
        public readonly List<string> ConflictingFields = new List<string>();

        /// <summary>目标已失效、因此无法判断或写回的受管理字段。</summary>
        public readonly List<string> StaleTargets = new List<string>();

        /// <summary>无法恢复的结构化效果：每一条都给出可证明的原因，绝不声称已回滚。</summary>
        public readonly List<string> UnrecoverableEffects = new List<string>();

        /// <summary>本 session 执行过的任意代码事实；有任意代码时报告未跟踪效果风险。</summary>
        public readonly List<string> DeclaredEffects = new List<string>();

        public readonly List<string> UnmanagedFacts = new List<string>();
        public readonly List<UnityCliDebugCleanupStepStatus> Steps = new List<UnityCliDebugCleanupStepStatus>();
        public readonly List<UnityCliDebugOwnerStatus> Owners = new List<UnityCliDebugOwnerStatus>();
    }

    /// <summary>同一 target 上仍然存在的其他 session owner 数量。</summary>
    public sealed class UnityCliDebugTargetOwners
    {
        public string Target = string.Empty;
        public int OtherOwners;
    }

    /// <summary>单个设施 owner 的注册状态；未注册时必须显式报告而不是省略。</summary>
    public sealed class UnityCliDebugOwnerStatus
    {
        public string Owner = string.Empty;
        public bool Registered;
        public string Note = string.Empty;
    }

    /// <summary>create 的结果；失败时 Occupancy 仍报告当前占用，供调用方构造可执行 nextActions。</summary>
    public sealed class UnityCliDebugSessionCreateOutcome
    {
        public bool Success;
        public string Failure;
        public string Message = string.Empty;
        public UnityCliDebugSessionDefinition Definition;
        public int SessionsUsed;
        public int ActiveResourcesUsed;
    }

    /// <summary>stop 的结果；重复 stop 返回当前事实而不是重放旧计数。</summary>
    public sealed class UnityCliDebugSessionStopOutcome
    {
        public bool Success;
        public string Failure;
        public string Message = string.Empty;
        public UnityCliDebugSessionDefinition Definition;
        public UnityCliDebugCleanupReport Report;
        public bool Changed;
        public int SessionsUsed;
    }

    /// <summary>remove 的结果；不满足清理前置条件时 Failure 为 cleanup-pending。</summary>
    public sealed class UnityCliDebugSessionRemoveOutcome
    {
        public bool Success;
        public string Failure;
        public string Message = string.Empty;
        public string SessionId = string.Empty;
        public string State = string.Empty;
        public bool CleanupPending;
        public int SessionsUsed;
        public int ActiveResourcesUsed;
    }

    /// <summary>reserve 的结果；失败前不得执行任何 effect。</summary>
    public sealed class UnityCliDebugResourceReservation
    {
        public bool Success;
        public string Failure;
        public string Message = string.Empty;
        public UnityCliDebugResourceRecord Resource;
        public int ActiveResourcesUsed;
        public int SessionsUsed;
    }

    /// <summary>
    /// debug session 定义的唯一内存 owner：状态机、id 分配、资源占用登记、有序清理管线与持久化载荷编解码。
    ///
    /// 规则：
    /// - 定义只属于一个 Editor Session 与一个 canonical 项目；owner 不从选择、上次调用或 CLI 进程推断。
    /// - session id 在一个 Editor Session 内单调递增且不复用，即使存储文件不可用也继续递增。
    /// - 活动资源数量有硬上限；达到上限时在 effect 前失败并返回当前占用。
    /// - 清理管线严格按 UnityCliDebugCleanupSteps.Ordered 执行；未注册 owner 的步骤报告零并带显式标记。
    /// - 清理只遍历本 session 的资源与清理条目，绝不触碰其他 session 的资源。
    /// - 本类型不持有任何 Unity 对象、线程或文件句柄；读取与写入由 Editor 侧 owner 负责。
    /// </summary>
    public sealed class UnityCliDebugSessionRegistry
    {
        /// <summary>
        /// 主线程暂停期间需要主线程的清理条目被推迟时的唯一原因文本。
        /// 它是“未完成”的精确理由，不是完成声明：线程恢复后重复同一个 stop 即可完成这些条目。
        /// </summary>
        public const string MainThreadDeferredReason = "deferred: this cleanup entry needs the Unity main thread, which is paused at a debug hook site";

        private readonly Dictionary<string, UnityCliDebugSessionDefinition> definitions = new Dictionary<string, UnityCliDebugSessionDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, UnityCliDebugResourceRecord> resources = new Dictionary<string, UnityCliDebugResourceRecord>(StringComparer.Ordinal);

        public UnityCliDebugSessionRegistry(UnityCliDebugSessionCapacity capacity)
        {
            if (capacity == null) throw new ArgumentNullException("capacity");
            Capacity = capacity;
            // session id 从 1 开始，持久化的 nextSessionIndex 因此永远是合法正数。
            NextSessionIndex = 1;
        }

        public UnityCliDebugSessionCapacity Capacity { get; private set; }

        /// <summary>下一个 session 序号；持久化并跨 Domain Reload 单调递增。</summary>
        public int NextSessionIndex { get; private set; }

        /// <summary>本注册表来自的存储 publication；0 表示空存储。</summary>
        public long Publication { get; private set; }

        public int SessionCount { get { return definitions.Count; } }

        /// <summary>按 session 序号升序返回定义；读取方不得修改返回的实例。</summary>
        public IReadOnlyList<UnityCliDebugSessionDefinition> Sessions()
        {
            return definitions.Values.OrderBy(definition => SessionOrdinal(definition.SessionId)).ThenBy(definition => definition.SessionId, StringComparer.Ordinal).ToList();
        }

        public UnityCliDebugSessionDefinition Find(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            UnityCliDebugSessionDefinition definition;
            return definitions.TryGetValue(sessionId, out definition) ? definition : null;
        }

        /// <summary>label 是否已被当前存储中的定义使用；比较按 ordinal 精确匹配。</summary>
        public bool LabelExists(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            foreach (var definition in definitions.Values)
            {
                if (string.Equals(definition.Label, label, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>该 session 当前的活动资源数量。</summary>
        public int ActiveResourceCount(string sessionId)
        {
            var definition = Find(sessionId);
            return definition == null ? 0 : definition.ActiveResourceCount;
        }

        /// <summary>该 session 的资源记录快照；包含失效但仍占用清理管线的记录。</summary>
        public IReadOnlyList<UnityCliDebugResourceRecord> ResourcesFor(string sessionId)
        {
            var definition = Find(sessionId);
            if (definition == null) return new List<UnityCliDebugResourceRecord>();
            return definition.Resources.OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToList();
        }

        // ------------------------------------------------------------------
        // 定义生命周期
        // ------------------------------------------------------------------

        /// <summary>
        /// 不改变任何状态的前置判定；返回稳定原因，null 表示 create 可以继续。
        /// effect 前的预校验与 effect 内的最终判定共用本方法，避免两份不一致的规则。
        /// </summary>
        public string CanCreate(string label, string activationScope, long initialStateBytes, bool isPlaying, out int sessionsUsed)
        {
            sessionsUsed = definitions.Count;
            if (string.IsNullOrEmpty(label)) return UnityCliDebugSessionFailures.LabelInvalid;
            if (initialStateBytes > Capacity.MaxStateBytes) return UnityCliDebugSessionFailures.StateTooLarge;
            if (definitions.Count >= Capacity.MaxSessions) return UnityCliDebugSessionFailures.LimitExceeded;
            if (LabelExists(label)) return UnityCliDebugSessionFailures.DuplicateLabel;
            if (UnityCliDebugActivationScopes.Conflicts(activationScope, isPlaying)) return UnityCliDebugSessionFailures.ScopeConflict;
            return null;
        }

        /// <summary>
        /// 分配 dbg-&lt;n&gt; 并建立定义；失败时不改变任何状态，也不消耗序号。
        /// </summary>
        public UnityCliDebugSessionCreateOutcome Create(string label, string activationScope, string clockKind, object initialState, long initialStateBytes, string createdAtUtc, double createdAtMonotonicMs, bool isPlaying, bool restoredFromRecipe = false)
        {
            var outcome = new UnityCliDebugSessionCreateOutcome();
            int used;
            var failure = CanCreate(label, activationScope, initialStateBytes, isPlaying, out used);
            outcome.SessionsUsed = used;
            if (failure != null)
            {
                outcome.Failure = failure;
                outcome.Message = DescribeCreateFailure(failure);
                return outcome;
            }

            var definition = new UnityCliDebugSessionDefinition
            {
                SessionId = "dbg-" + NextSessionIndex.ToString(CultureInfo.InvariantCulture),
                Label = label,
                ActivationScope = activationScope,
                ClockKind = clockKind,
                State = UnityCliDebugSessionStates.Open,
                Revision = 1L,
                CreatedAtUtc = createdAtUtc ?? string.Empty,
                CreatedAtMonotonicMs = createdAtMonotonicMs,
                InitialStateBytes = initialStateBytes,
                InitialState = initialState,
                // recipe 导入的新 identity 与重载恢复一样：定义存在，但没有任何活动资源。
                RestoredWithoutActivation = restoredFromRecipe
            };
            NextSessionIndex++;
            definitions[definition.SessionId] = definition;
            outcome.Success = true;
            outcome.Definition = definition;
            outcome.SessionsUsed = definitions.Count;
            return outcome;
        }

        private static string DescribeCreateFailure(string failure)
        {
            if (string.Equals(failure, UnityCliDebugSessionFailures.LabelInvalid, StringComparison.Ordinal)) return "Session label must not be empty.";
            if (string.Equals(failure, UnityCliDebugSessionFailures.StateTooLarge, StringComparison.Ordinal)) return "Initial state exceeds the declared per-session byte budget.";
            if (string.Equals(failure, UnityCliDebugSessionFailures.LimitExceeded, StringComparison.Ordinal)) return "Session definition limit reached.";
            if (string.Equals(failure, UnityCliDebugSessionFailures.DuplicateLabel, StringComparison.Ordinal)) return "Session label is already used by another definition in this Editor session.";
            if (string.Equals(failure, UnityCliDebugSessionFailures.ScopeConflict, StringComparison.Ordinal)) return "Requested activation scope cannot be activated in the current editor mode.";
            return "Session definition was rejected.";
        }

        /// <summary>
        /// 该 session 当前是否允许新的派发。
        /// stop 一开始就把定义置为关闭，因此清理期间任何线程上的命中都不会进入新的用户代码；
        /// 定义不存在、已关闭或不在 open 状态都必须返回 false。
        /// </summary>
        public bool IsDispatchOpen(string sessionId)
        {
            var definition = Find(sessionId);
            if (definition == null) return false;
            if (definition.DispatchClosed) return false;
            return string.Equals(definition.State, UnityCliDebugSessionStates.Open, StringComparison.Ordinal);
        }

        /// <summary>
        /// 关闭未来派发并执行清理管线。open 定义第一次调用会真正清理；
        /// 已 stopped 且无待清理项的定义返回当前事实且不重复执行清理。
        /// stopEntry 记录本次停止的入口，报告必须回显它。
        /// </summary>
        public UnityCliDebugSessionStopOutcome Stop(string sessionId, string stoppedAtUtc, double stoppedAtMonotonicMs, int mainThreadId, bool offMainStop = false, string stopEntry = null)
        {
            if (!UnityCliDebugStopEntries.IsDefined(stopEntry)) stopEntry = offMainStop ? UnityCliDebugStopEntries.PauseControlChannel : UnityCliDebugStopEntries.MainThread;
            var outcome = new UnityCliDebugSessionStopOutcome { SessionsUsed = definitions.Count };
            var definition = Find(sessionId);
            if (definition == null)
            {
                outcome.Failure = UnityCliDebugSessionFailures.MissingSession;
                outcome.Message = "No session definition with that sessionId exists in the loaded store.";
                return outcome;
            }
            if (string.Equals(definition.State, UnityCliDebugSessionStates.Stopped, StringComparison.Ordinal) && !definition.CleanupPending)
            {
                outcome.Success = true;
                outcome.Changed = false;
                outcome.Definition = definition;
                outcome.Report = BuildIdleReport(definition, mainThreadId, "session was already stopped; no cleanup ran for this call");
                outcome.Report.StopEntry = stopEntry;
                return outcome;
            }

            // 已 stopped 但仍有待清理时重跑管线，让调用方可以重试清理；停止时间不被重试改写。
            var retry = string.Equals(definition.State, UnityCliDebugSessionStates.Stopped, StringComparison.Ordinal);
            // 第 1 阶段之前先关闭未来派发：此后任何线程的命中都不会再进入用户代码。
            definition.DispatchClosed = true;
            definition.State = UnityCliDebugSessionStates.Stopping;
            if (!retry)
            {
                definition.StoppedAtUtc = stoppedAtUtc ?? string.Empty;
                definition.StoppedAtMonotonicMs = stoppedAtMonotonicMs;
            }
            var report = RunCleanupPipeline(definition, mainThreadId, offMainStop, stopEntry);
            definition.State = UnityCliDebugSessionStates.Stopped;
            // 写入只登记管线自身发现的停滞；已被管线释放的资源由 CleanupPending 的读取真值减去。
            definition.CleanupPending = report.CleanupPending;
            definition.Revision++;
            outcome.Success = true;
            outcome.Changed = true;
            outcome.Definition = definition;
            outcome.Report = report;
            return outcome;
        }

        /// <summary>
        /// 不改变任何状态的 remove 前置判定；返回稳定原因，null 表示可以删除。
        /// effect 前的预校验与 effect 内的最终判定共用本方法。
        /// 待清理判据与报告同源：仍然注册的活动资源、显式停滞标记（被推迟的条目、失败的清理、Play 退出失效）。
        /// </summary>
        public string CanRemove(string sessionId, out string state, out bool cleanupPending, out int activeResourcesUsed)
        {
            state = string.Empty;
            cleanupPending = false;
            activeResourcesUsed = 0;
            var definition = Find(sessionId);
            if (definition == null) return UnityCliDebugSessionFailures.MissingSession;
            state = definition.State;
            activeResourcesUsed = definition.ActiveResourceCount;
            cleanupPending = definition.CleanupPending;
            if (!string.Equals(definition.State, UnityCliDebugSessionStates.Stopped, StringComparison.Ordinal)) return UnityCliDebugSessionFailures.CleanupPending;
            return cleanupPending ? UnityCliDebugSessionFailures.CleanupPending : null;
        }

        /// <summary>
        /// 只有 stopped 且没有待清理项的定义才能删除；其余情况返回当前事实。
        /// 前置判定已保证本 session 不再持有任何注册资源，因此删除只移除定义本身，
        /// 绝不静默丢弃仍被引用的资源拥有关系。
        /// </summary>
        public UnityCliDebugSessionRemoveOutcome Remove(string sessionId)
        {
            var outcome = new UnityCliDebugSessionRemoveOutcome { SessionsUsed = definitions.Count, SessionId = sessionId ?? string.Empty };
            string state;
            bool cleanupPending;
            int activeResourcesUsed;
            var failure = CanRemove(sessionId, out state, out cleanupPending, out activeResourcesUsed);
            outcome.State = state;
            outcome.CleanupPending = cleanupPending;
            outcome.ActiveResourcesUsed = activeResourcesUsed;
            if (failure != null)
            {
                outcome.Failure = failure;
                outcome.Message = string.Equals(failure, UnityCliDebugSessionFailures.MissingSession, StringComparison.Ordinal)
                    ? "No session definition with that sessionId exists in the loaded store."
                    : string.Equals(state, UnityCliDebugSessionStates.Stopped, StringComparison.Ordinal)
                        ? "The session is stopped but its cleanup pipeline did not complete."
                        : "The session is not stopped yet; cleanup has not been completed.";
                return outcome;
            }
            definitions.Remove(sessionId);
            outcome.Success = true;
            outcome.SessionsUsed = definitions.Count;
            outcome.ActiveResourcesUsed = 0;
            return outcome;
        }

        // ------------------------------------------------------------------
        // 资源占用登记
        // ------------------------------------------------------------------

        /// <summary>
        /// 登记一条在本次清理管线之外发现的未完成条目（例如主线程专属的存储恢复事实被推迟）。
        /// 它把报告与定义的待清理真值一起置为 pending，因此 remove 判定不会与报告分叉；
        /// 条目名与精确原因由调用方已经写入报告的 fact 携带。
        /// </summary>
        public void MarkDeferredCleanupEntryPending(UnityCliDebugSessionDefinition definition, UnityCliDebugCleanupReport report)
        {
            if (report != null)
            {
                report.CleanupPending = true;
                report.Status = "pending";
            }
            if (definition != null) definition.CleanupPending = true;
        }

        /// <summary>
        /// 在 effect 之前保留一个活动资源槽位。失败时不改变任何状态，
        /// 设施必须放弃 effect 而不是继续执行。
        /// </summary>
        public UnityCliDebugResourceReservation Reserve(string sessionId, string kind, string target, string playSegmentId, Func<UnityCliDebugCleanupReport, bool> cleanup, bool requiresMainThread = false)
        {
            var reservation = new UnityCliDebugResourceReservation { SessionsUsed = definitions.Count };
            var definition = Find(sessionId);
            if (definition == null)
            {
                reservation.Failure = UnityCliDebugSessionFailures.MissingSession;
                reservation.Message = "No session definition with that sessionId exists in the loaded store.";
                return reservation;
            }
            if (string.Equals(definition.State, UnityCliDebugSessionStates.Open, StringComparison.Ordinal) == false)
            {
                reservation.Failure = UnityCliDebugSessionFailures.WrongState;
                reservation.Message = "A session that is not open cannot accept new resources.";
                reservation.ActiveResourcesUsed = definition.ActiveResourceCount;
                return reservation;
            }
            if (!UnityCliDebugResourceKinds.IsDefined(kind))
            {
                reservation.Failure = UnityCliDebugSessionFailures.LabelInvalid;
                reservation.Message = "Unknown resource kind.";
                reservation.ActiveResourcesUsed = definition.ActiveResourceCount;
                return reservation;
            }
            reservation.ActiveResourcesUsed = definition.ActiveResourceCount;
            if (definition.ActiveResourceCount >= Capacity.MaxActiveResourcesPerSession)
            {
                reservation.Failure = UnityCliDebugSessionFailures.LimitExceeded;
                reservation.Message = "Active resource limit reached for this session.";
                return reservation;
            }
            var resource = new UnityCliDebugResourceRecord
            {
                ResourceId = definition.SessionId + "/" + kind + "-" + (definition.Resources.Count + 1).ToString(CultureInfo.InvariantCulture),
                Kind = kind,
                SessionId = definition.SessionId,
                Target = target ?? string.Empty,
                State = UnityCliDebugResourceStates.Reserved,
                PlaySegmentId = playSegmentId ?? string.Empty,
                Cleanup = cleanup,
                RequiresMainThread = requiresMainThread
            };
            definition.Resources.Add(resource);
            resources[resource.ResourceId] = resource;
            reservation.Success = true;
            reservation.Resource = resource;
            reservation.ActiveResourcesUsed = definition.ActiveResourceCount;
            return reservation;
        }

        /// <summary>设施完成 effect 后把保留的槽位标记为活动。</summary>
        public bool Attach(string resourceId)
        {
            var resource = FindResource(resourceId);
            if (resource == null || !string.Equals(resource.State, UnityCliDebugResourceStates.Reserved, StringComparison.Ordinal)) return false;
            resource.State = UnityCliDebugResourceStates.Active;
            return true;
        }

        /// <summary>设施释放资源；释放后该记录不再参与清理。</summary>
        public bool Release(string resourceId)
        {
            var resource = FindResource(resourceId);
            if (resource == null) return false;
            var definition = Find(resource.SessionId);
            if (definition != null) definition.Resources.Remove(resource);
            resources.Remove(resource.ResourceId);
            return true;
        }

        /// <summary>注册显式有限清理条目；同一 (order, name) 只能注册一次。</summary>
        public string RegisterCleanupStep(string sessionId, int order, string name, string owner, Func<UnityCliDebugCleanupReport, bool> apply, bool requiresMainThread = false)
        {
            var definition = Find(sessionId);
            if (definition == null) return UnityCliDebugSessionFailures.MissingSession;
            if (apply == null || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(owner)) return UnityCliDebugSessionFailures.LabelInvalid;
            if (order < 1 || order > UnityCliDebugCleanupSteps.Ordered().Length) return UnityCliDebugSessionFailures.LabelInvalid;
            if (definition.CleanupSteps.Count >= Capacity.MaxCleanupStepsPerSession) return UnityCliDebugSessionFailures.LimitExceeded;
            foreach (var existing in definition.CleanupSteps)
            {
                if (existing.Order == order && string.Equals(existing.Name, name, StringComparison.Ordinal))
                    return UnityCliDebugSessionFailures.CleanupStepConflict;
            }
            definition.CleanupSteps.Add(new UnityCliDebugCleanupStepRegistration { Order = order, Name = name, Owner = owner, Apply = apply, RequiresMainThread = requiresMainThread });
            return null;
        }

        /// <summary>
        /// Play 退出后把在 Play 段内建立的资源置为失效并标记待清理；
        /// 定义本身与已关闭证据保留。返回本次失效的资源数量。
        /// </summary>
        public int InvalidatePlayScopedResources(string playSegmentId)
        {
            var invalidated = 0;
            foreach (var definition in definitions.Values)
            {
                foreach (var resource in definition.Resources)
                {
                    if (string.Equals(resource.State, UnityCliDebugResourceStates.Invalidated, StringComparison.Ordinal)) continue;
                    if (string.IsNullOrEmpty(resource.PlaySegmentId)) continue;
                    if (string.Equals(resource.PlaySegmentId, playSegmentId, StringComparison.Ordinal)) continue;
                    resource.State = UnityCliDebugResourceStates.Invalidated;
                    definition.CleanupPending = true;
                    invalidated++;
                }
            }
            return invalidated;
        }

        // ------------------------------------------------------------------
        // 清理管线
        // ------------------------------------------------------------------

        /// <summary>
        /// 严格按冻结顺序执行本 session 的清理。每一步只遍历本 session 的资源与清理条目；
        /// 未注册 owner 的步骤报告零并带显式标记，失败或缺少委托时保持 cleanupPending。
        /// </summary>
        public UnityCliDebugCleanupReport RunCleanupPipeline(string sessionId, int mainThreadId)
        {
            var definition = Find(sessionId);
            return definition == null ? new UnityCliDebugCleanupReport { SessionId = sessionId ?? string.Empty } : RunCleanupPipeline(definition, mainThreadId);
        }

        private UnityCliDebugCleanupReport RunCleanupPipeline(UnityCliDebugSessionDefinition definition, int mainThreadId, bool offMainStop = false, string stopEntry = null)
        {
            var report = BuildIdleReport(definition, mainThreadId, null);
            report.StopEntry = UnityCliDebugStopEntries.IsDefined(stopEntry) ? stopEntry : UnityCliDebugStopEntries.MainThread;
            // 骨架只服务于只读投影；本次清理状态与步骤结果完全由本方法决定。
            report.Steps.Clear();
            report.CleanupPending = false;
            report.RestoredFields.Clear();
            report.ConflictingFields.Clear();
            report.StaleTargets.Clear();
            report.UnrecoverableEffects.Clear();
            report.DeclaredEffects.Clear();
            // 释放资源前先记录本 session 使用的 target，报告必须晚于释放披露同一 target 上的其他 owner。
            var targets = SessionTargets(definition);
            var ordered = UnityCliDebugCleanupSteps.Ordered();
            for (var index = 0; index < ordered.Length; index++)
            {
                var order = index + 1;
                var name = ordered[index];
                var status = new UnityCliDebugCleanupStepStatus { Order = order, Name = name, Note = UnityCliDebugOwnerMarkers.NoOwnerRegistered };
                report.Steps.Add(status);

                var registrations = definition.CleanupSteps.Where(step => step.Order == order).OrderBy(step => step.Owner, StringComparer.Ordinal).ToList();
                if (registrations.Count == 0)
                {
                    // 没有该步骤的 owner：保留显式零报告，绝不静默省略该类别。
                    if (definition.Resources.Count > 0) report.UntrackedEffectsPossible = true;
                    continue;
                }
                status.OwnerRegistered = true;
                status.Owner = registrations[0].Owner;
                status.Note = string.Empty;
                foreach (var registration in registrations)
                {
                    if (offMainStop && registration.RequiresMainThread)
                    {
                        DeferMainThreadEntry(report, status, registration);
                        continue;
                    }
                    if (registration.Apply == null)
                    {
                        status.Succeeded = false;
                        status.Note = "cleanup entry has no delegate";
                        report.CleanupPending = true;
                        continue;
                    }
                    try
                    {
                        if (!registration.Apply(report)) { status.Succeeded = false; report.CleanupPending = true; }
                    }
                    catch (Exception exception)
                    {
                        status.Succeeded = false;
                        status.Note = exception.GetType().Name + ": " + exception.Message;
                        report.CleanupPending = true;
                    }
                    status.Entries++;
                }
            }

            // 显式有限清理条目：仍然适用的资源执行自己的清理委托；成功即释放，失败或缺少委托即保持待清理。
            foreach (var resource in definition.Resources.ToList())
            {
                if (offMainStop && resource.RequiresMainThread)
                {
                    // 需要主线程的资源清理同样必须在 effect 之前推迟：它保留自己的槽位，并如实计入待清理 detour。
                    report.PendingDetours++;
                    report.CleanupPending = true;
                    report.UnmanagedFacts.Add("resource " + resource.ResourceId + " (" + resource.Kind + ") keeps its slot: " + MainThreadDeferredReason);
                    continue;
                }
                if (resource.Cleanup == null)
                {
                    report.UntrackedEffectsPossible = true;
                    report.UnmanagedFacts.Add("resource " + resource.ResourceId + " has no cleanup delegate");
                    report.UnrecoverableEffects.Add("resource " + resource.ResourceId + " (" + resource.Kind + ") has no cleanup delegate, so its effect cannot be undone by this owner");
                    report.PendingDetours++;
                    report.CleanupPending = true;
                    continue;
                }
                var released = false;
                try { released = resource.Cleanup(report); }
                catch (Exception exception)
                {
                    report.UnmanagedFacts.Add("resource " + resource.ResourceId + " cleanup failed: " + exception.GetType().Name);
                    released = false;
                }
                if (released)
                {
                    Release(resource.ResourceId);
                    report.ExplicitCleanupEntries++;
                    continue;
                }
                report.PendingDetours++;
                report.CleanupPending = true;
            }

            // 待清理真值来自管线结束后的注册表状态：这里仍然注册的资源就是本次 stop 之后仍未完成的清理。
            // 事实只在管线之后生成，因此报告不可能一边列出仍然注册的资源，一边声称清理已完成。
            if (definition.Resources.Count > 0)
            {
                report.CleanupPending = true;
                foreach (var resource in definition.Resources)
                    report.UnmanagedFacts.Add("resource " + resource.ResourceId + " (" + resource.Kind + ") is still registered after stop in state " + resource.State);
            }

            report.Status = report.CleanupPending ? "pending" : "complete";
            // 本 session 拥有过执行资源或执行过任意代码时，未管理的线程/任务/设备不能被静态证明不存在。
            if (!report.UnmanagedUnknown && (report.UntrackedEffectsPossible || report.DeclaredEffects.Count != 0 || definition.Resources.Count != 0))
            {
                report.UnmanagedUnknown = true;
                report.UnmanagedFacts.Add(UnmanagedUnknownFact);
            }
            report.SharedTargets.Clear();
            report.SharedTargets.AddRange(SharedTargets(targets, definition.SessionId));
            if (report.CleanupPending) report.UnmanagedFacts.Add("cleanup did not complete for this session");
            definition.LastReport = report;
            return report;
        }

        /// <summary>未管理对象的唯一诚实陈述：宿主只对自己管理的队列、引用与文件负责。</summary>
        internal const string UnmanagedUnknownFact = "unmanaged threads, tasks and devices cannot be statically proven absent: the host accounts for its own queues, references, files and compile artifacts, not for objects or threads created by user code";

        /// <summary>
        /// 主线程暂停期间需要主线程的清理条目的唯一推迟点：绝不执行，报告 pending 与精确原因。
        /// 决不在其它线程上替它执行，也不把未执行的条目写成已完成。
        /// </summary>
        private static void DeferMainThreadEntry(UnityCliDebugCleanupReport report, UnityCliDebugCleanupStepStatus status, UnityCliDebugCleanupStepRegistration registration)
        {
            status.Succeeded = false;
            status.Note = MainThreadDeferredReason;
            status.Entries++;
            report.CleanupPending = true;
            // 撤除 detour 属于必需的“已撤除 vs 待撤除”类别，推迟时必须计入待撤除数量。
            if (string.Equals(status.Name, UnityCliDebugCleanupSteps.DetachHooks, StringComparison.Ordinal)) report.PendingDetours++;
            report.UnmanagedFacts.Add("cleanup entry " + registration.Name + " (" + registration.Owner + ") was not run: " + MainThreadDeferredReason);
        }

        /// <summary>构造不做任何清理的报告骨架：所有字段类别存在，未注册 owner 显式报告零。</summary>
        private static UnityCliDebugCleanupReport BuildIdleReport(UnityCliDebugSessionDefinition definition, int mainThreadId, string note)
        {
            var report = new UnityCliDebugCleanupReport
            {
                SessionId = definition.SessionId,
                Status = "complete",
                CleanupPending = definition.CleanupPending,
                ActiveRevision = definition.Revision,
                ActiveInvocationCount = 0L,
                ActiveStepCount = 0L,
                ActiveEvaluateCount = 0L,
                ActiveThreadId = mainThreadId
            };
            // 重复 stop 与 stop 后的只读投影必须返回同一终态事实：
            // 已恢复/冲突字段、失效目标与不可恢复效果不会因为本次没有重跑管线而消失。
            if (definition.LastReport != null)
            {
                var previous = definition.LastReport;
                report.StopEntry = previous.StopEntry;
                report.StoppedFutureEntries = previous.StoppedFutureEntries;
                report.CancelledPendingOperations = previous.CancelledPendingOperations;
                report.ReleasedPauses = previous.ReleasedPauses;
                report.ReleasedInput = previous.ReleasedInput;
                report.ClosedObservations = previous.ClosedObservations;
                report.RemovedDetours = previous.RemovedDetours;
                report.PendingDetours = previous.PendingDetours;
                report.ExplicitCleanupEntries = previous.ExplicitCleanupEntries;
                report.ReleasedWaits = previous.ReleasedWaits;
                report.ReleasedHandles = previous.ReleasedHandles;
                report.UntrackedEffectsPossible = previous.UntrackedEffectsPossible;
                report.UnmanagedUnknown = previous.UnmanagedUnknown;
                report.LoadedNonUnloadableAssemblyCount = previous.LoadedNonUnloadableAssemblyCount;
                report.LoadedNonUnloadableAssemblyBytes = previous.LoadedNonUnloadableAssemblyBytes;
                report.RestoredFields.AddRange(previous.RestoredFields);
                report.ConflictingFields.AddRange(previous.ConflictingFields);
                report.StaleTargets.AddRange(previous.StaleTargets);
                report.UnrecoverableEffects.AddRange(previous.UnrecoverableEffects);
                report.DeclaredEffects.AddRange(previous.DeclaredEffects);
                report.UnmanagedFacts.AddRange(previous.UnmanagedFacts);
                report.SharedTargets.AddRange(previous.SharedTargets);
            }
            foreach (var name in UnityCliDebugCleanupSteps.Ordered())
            {
                report.Steps.Add(new UnityCliDebugCleanupStepStatus
                {
                    Order = UnityCliDebugCleanupSteps.OrderOf(name),
                    Name = name,
                    Note = UnityCliDebugOwnerMarkers.NoOwnerRegistered
                });
            }
            foreach (var owner in UnityCliDebugFacilityOwners.All())
            {
                var registered = false;
                foreach (var step in definition.CleanupSteps)
                {
                    if (string.Equals(step.Owner, owner, StringComparison.Ordinal)) { registered = true; break; }
                }
                if (!registered)
                {
                    foreach (var resource in definition.Resources)
                    {
                        if (string.Equals(resource.Kind, owner, StringComparison.Ordinal)) { registered = true; break; }
                    }
                }
                report.Owners.Add(new UnityCliDebugOwnerStatus
                {
                    Owner = owner,
                    Registered = registered,
                    Note = registered ? string.Empty : UnityCliDebugOwnerMarkers.NoOwnerRegistered
                });
            }
            if (!string.IsNullOrEmpty(note)) report.UnmanagedFacts.Add(note);
            return report;
        }

        /// <summary>本 session 当前引用的非空 target 集合。</summary>
        private static List<string> SessionTargets(UnityCliDebugSessionDefinition definition)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in definition.Resources)
            {
                if (!string.IsNullOrEmpty(resource.Target)) targets.Add(resource.Target);
            }
            return targets.OrderBy(value => value, StringComparer.Ordinal).ToList();
        }

        /// <summary>给定 target 列表上其他 session 仍然持有的资源数量；只统计本注册表内的记录。</summary>
        private List<UnityCliDebugTargetOwners> SharedTargets(List<string> targets, string sessionId)
        {
            var result = new List<UnityCliDebugTargetOwners>();
            foreach (var target in targets)
            {
                var others = new HashSet<string>(StringComparer.Ordinal);
                foreach (var owner in definitions.Values)
                {
                    if (string.Equals(owner.SessionId, sessionId, StringComparison.Ordinal)) continue;
                    foreach (var resource in owner.Resources)
                    {
                        if (string.Equals(resource.Target, target, StringComparison.Ordinal)) others.Add(owner.SessionId);
                    }
                }
                result.Add(new UnityCliDebugTargetOwners { Target = target, OtherOwners = others.Count });
            }
            return result;
        }

        /// <summary>
        /// 只读的当前清理状态：报告全部字段类别与仍未释放的资源，不执行任何清理条目。
        /// info 与拒绝路径只能使用本方法；只有 stop 才运行管线。
        /// </summary>
        public UnityCliDebugCleanupReport DescribeCleanup(string sessionId, int mainThreadId)
        {
            var definition = Find(sessionId);
            if (definition == null) return new UnityCliDebugCleanupReport { SessionId = sessionId ?? string.Empty };
            var report = BuildIdleReport(definition, mainThreadId, "no cleanup ran for this read");
            foreach (var resource in definition.Resources)
            {
                report.UnmanagedFacts.Add("resource " + resource.ResourceId + " still holds its slot in state " + resource.State);
            }
            if (definition.Resources.Count > 0) report.UntrackedEffectsPossible = true;
            report.CleanupPending = definition.CleanupPending;
            report.Status = report.CleanupPending ? "pending" : "complete";
            report.SharedTargets.Clear();
            report.SharedTargets.AddRange(SharedTargets(SessionTargets(definition), definition.SessionId));
            return report;
        }

        private UnityCliDebugResourceRecord FindResource(string resourceId)
        {
            if (string.IsNullOrEmpty(resourceId)) return null;
            UnityCliDebugResourceRecord resource;
            return resources.TryGetValue(resourceId, out resource) ? resource : null;
        }

        private static long SessionOrdinal(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return long.MaxValue;
            var separator = sessionId.LastIndexOf('-');
            long value;
            if (separator < 0 || !long.TryParse(sessionId.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return long.MaxValue;
            return value;
        }

        // ------------------------------------------------------------------
        // 持久化载荷
        // ------------------------------------------------------------------

        /// <summary>
        /// 构造完整载荷（含 checksum）。publication 由单写者给出，
        /// 必须严格大于上一次发布的数值。
        /// </summary>
        public Dictionary<string, object> CapturePayload(string editorSessionId, string domainGeneration, long publication)
        {
            var sessions = new List<object>();
            foreach (var definition in Sessions())
            {
                sessions.Add(new Dictionary<string, object>
                {
                    { "sessionId", definition.SessionId },
                    { "label", definition.Label },
                    { "activationScope", definition.ActivationScope },
                    { "clockKind", definition.ClockKind },
                    { "state", definition.State },
                    { "revision", definition.Revision },
                    { "createdAtUtc", definition.CreatedAtUtc },
                    { "createdAtMonotonicMs", definition.CreatedAtMonotonicMs },
                    { "stoppedAtUtc", definition.StoppedAtUtc },
                    { "stoppedAtMonotonicMs", definition.StoppedAtMonotonicMs },
                    { "initialStateBytes", definition.InitialStateBytes },
                    { "initialState", definition.InitialState }
                });
            }
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "format", UnityCliDebugSessionStorePayload.Format },
                { "editorSessionId", editorSessionId ?? string.Empty },
                { "domainGeneration", domainGeneration ?? string.Empty },
                { "publication", publication },
                { "nextSessionIndex", (long)NextSessionIndex },
                { "sessions", sessions }
            };
            payload[UnityCliDebugSessionStorePayload.ChecksumField] = UnityCliDebugSessionStorePayload.Checksum(payload);
            Publication = publication;
            return payload;
        }

        /// <summary>
        /// 从已解析载荷恢复定义。任何一条 fail-closed 规则命中时返回 false 并给出稳定原因；
        /// 恢复的定义一律不带活动资源，因此不可能在恢复时重新激活资源。
        /// </summary>
        public static bool TryRestore(IDictionary<string, object> payload, UnityCliDebugSessionCapacity capacity, string editorSessionId, string domainGeneration, IReadOnlyList<string> acceptedDomainGenerations, long publicationFloor, out UnityCliDebugSessionRegistry registry, out string failure)
        {
            registry = null;
            failure = null;
            if (payload == null) { failure = "payload-missing"; return false; }
            if (!string.Equals(ReadText(payload, "format"), UnityCliDebugSessionStorePayload.Format, StringComparison.Ordinal)) { failure = "format-mismatch"; return false; }
            if (!string.Equals(ReadText(payload, "editorSessionId"), editorSessionId, StringComparison.Ordinal)) { failure = "editor-session-mismatch"; return false; }

            var generation = ReadText(payload, "domainGeneration");
            var accepted = false;
            if (string.Equals(generation, domainGeneration, StringComparison.Ordinal)) accepted = true;
            if (!accepted && acceptedDomainGenerations != null)
            {
                foreach (var candidate in acceptedDomainGenerations)
                {
                    if (string.Equals(generation, candidate, StringComparison.Ordinal)) { accepted = true; break; }
                }
            }
            if (!accepted) { failure = "domain-generation-mismatch"; return false; }

            var expectedChecksum = ReadText(payload, UnityCliDebugSessionStorePayload.ChecksumField);
            if (string.IsNullOrEmpty(expectedChecksum) || !IsSha256(expectedChecksum)) { failure = "checksum-missing"; return false; }
            var unsigned = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in payload)
            {
                if (string.Equals(pair.Key, UnityCliDebugSessionStorePayload.ChecksumField, StringComparison.Ordinal)) continue;
                unsigned[pair.Key] = pair.Value;
            }
            if (!string.Equals(expectedChecksum, UnityCliDebugSessionStorePayload.Checksum(unsigned), StringComparison.Ordinal)) { failure = "checksum-mismatch"; return false; }

            var publication = ReadLong(payload, "publication");
            if (publication < 1L) { failure = "publication-invalid"; return false; }
            if (publication < publicationFloor) { failure = "publication-regressed"; return false; }

            var nextIndex = ReadLong(payload, "nextSessionIndex");
            if (nextIndex < 1L) { failure = "session-index-invalid"; return false; }

            var entries = ReadList(payload, "sessions");
            if (entries == null) { failure = "sessions-invalid"; return false; }
            if (entries.Count > capacity.MaxSessions) { failure = "session-count-exceeded"; return false; }

            var restored = new UnityCliDebugSessionRegistry(capacity) { Publication = publication, NextSessionIndex = checked((int)Math.Min(nextIndex, int.MaxValue)) };
            var highestIndex = 0;
            foreach (var entry in entries)
            {
                var data = entry as IDictionary<string, object>;
                if (data == null) { failure = "session-entry-invalid"; return false; }
                var sessionId = ReadText(data, "sessionId");
                var label = ReadText(data, "label");
                var activationScope = ReadText(data, "activationScope");
                var clockKind = ReadText(data, "clockKind");
                var state = ReadText(data, "state");
                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(label)) { failure = "session-entry-invalid"; return false; }
                if (!UnityCliDebugActivationScopes.IsDefined(activationScope)) { failure = "session-scope-invalid"; return false; }
                if (!UnityCliDebugSessionStates.IsDefined(state)) { failure = "session-state-invalid"; return false; }
                if (!IsDeclaredClock(clockKind)) { failure = "session-clock-invalid"; return false; }
                if (restored.definitions.ContainsKey(sessionId)) { failure = "session-duplicate"; return false; }
                var stateBytes = ReadLong(data, "initialStateBytes");
                if (stateBytes < 0L || stateBytes > capacity.MaxStateBytes) { failure = "session-state-invalid"; return false; }
                var definition = new UnityCliDebugSessionDefinition
                {
                    SessionId = sessionId,
                    Label = label,
                    ActivationScope = activationScope,
                    ClockKind = clockKind,
                    State = state,
                    Revision = ReadLong(data, "revision"),
                    CreatedAtUtc = ReadText(data, "createdAtUtc"),
                    CreatedAtMonotonicMs = ReadDouble(data, "createdAtMonotonicMs"),
                    StoppedAtUtc = ReadText(data, "stoppedAtUtc"),
                    StoppedAtMonotonicMs = ReadDouble(data, "stoppedAtMonotonicMs"),
                    InitialStateBytes = stateBytes,
                    InitialState = ReadValue(data, "initialState"),
                    // 活动资源与待清理标记都是运行时事实：重载后不存在任何资源，因此不能宣称仍待清理。
                    CleanupPending = false,
                    RestoredWithoutActivation = !string.Equals(generation, domainGeneration, StringComparison.Ordinal)
                };
                if (definition.Revision < 1L) { failure = "session-revision-invalid"; return false; }
                restored.definitions[sessionId] = definition;
                var ordinal = (int)Math.Min(SessionOrdinal(sessionId), int.MaxValue);
                if (ordinal != int.MaxValue && ordinal > highestIndex) highestIndex = ordinal;
            }
            if (restored.NextSessionIndex < highestIndex + 1) restored.NextSessionIndex = highestIndex + 1;
            registry = restored;
            return true;
        }

        /// <summary>该 clockKind 是否为 debug 域已声明的时钟种类；列表只有 UnityCliDebugClocks 一份。</summary>
        private static bool IsDeclaredClock(string value)
        {
            return UnityCliDebugClocks.IsDefined(value);
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (var ch in value)
            {
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
            }
            return true;
        }

        private static string ReadText(IDictionary<string, object> payload, string key)
        {
            object value;
            if (payload == null || !payload.TryGetValue(key, out value) || value == null) return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static object ReadValue(IDictionary<string, object> payload, string key)
        {
            object value;
            return payload != null && payload.TryGetValue(key, out value) ? value : null;
        }

        private static long ReadLong(IDictionary<string, object> payload, string key)
        {
            object value;
            if (payload == null || !payload.TryGetValue(key, out value) || value == null) return 0L;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return -1L; }
        }

        private static double ReadDouble(IDictionary<string, object> payload, string key)
        {
            object value;
            if (payload == null || !payload.TryGetValue(key, out value) || value == null) return 0d;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0d; }
        }

        private static List<object> ReadList(IDictionary<string, object> payload, string key)
        {
            object value;
            if (payload == null || !payload.TryGetValue(key, out value)) return null;
            var list = value as List<object>;
            return list ?? new List<object>();
        }
    }

    /// <summary>
    /// 固定存储载荷的唯一编解码 owner：冻结格式号、跨键序稳定的 canonical 校验和，
    /// 以及读取端必须具备的 fail-closed 判定。
    /// </summary>
    public static class UnityCliDebugSessionStorePayload
    {
        /// <summary>冻结格式号；字段集变化必须同步升号，旧格式一律不可用。</summary>
        public const string Format = "unitycli.debug.session/1";

        /// <summary>校验和字段名；计算时不得参与自身输入。</summary>
        public const string ChecksumField = "checksum";

        /// <summary>对不含 checksum 的载荷计算跨键序稳定的 SHA-256 小写 hex。</summary>
        public static string Checksum(IDictionary<string, object> payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (payload.ContainsKey(ChecksumField)) throw new ArgumentException("Checksum input must not contain the checksum field.", "payload");
            var builder = new StringBuilder();
            AppendCanonical(builder, payload);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var result = new StringBuilder(64);
                foreach (var item in hash) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        /// <summary>
        /// 生成与写入端键序无关的 canonical 文本。
        /// 只接受 JSON 值：null、bool、数字、string、object、array；其他类型立即失败。
        /// </summary>
        public static void AppendCanonical(StringBuilder builder, object value)
        {
            if (builder == null) throw new ArgumentNullException("builder");
            if (value == null) { builder.Append("n|"); return; }
            var text = value as string;
            if (text != null) { builder.Append('s').Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|'); return; }
            if (value is bool) { builder.Append((bool)value ? "b1|" : "b0|"); return; }
            if (value is double) { builder.Append('d').Append(((double)value).ToString("R", CultureInfo.InvariantCulture)).Append('|'); return; }
            if (value is float) { builder.Append('f').Append(((float)value).ToString("R", CultureInfo.InvariantCulture)).Append('|'); return; }
            if (value is decimal) { builder.Append('m').Append(((decimal)value).ToString(CultureInfo.InvariantCulture)).Append('|'); return; }
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
            {
                builder.Append('i').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('|');
                return;
            }
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var keys = new List<string>();
                foreach (DictionaryEntry entry in dictionary) keys.Add(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                keys.Sort(StringComparer.Ordinal);
                builder.Append("o").Append(keys.Count.ToString(CultureInfo.InvariantCulture)).Append('{');
                foreach (var key in keys)
                {
                    builder.Append(key.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(key).Append('|');
                    AppendCanonical(builder, dictionary[key]);
                }
                builder.Append('}');
                return;
            }
            var items = value as IList;
            if (items != null)
            {
                builder.Append("a").Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append('[');
                foreach (var item in items) AppendCanonical(builder, item);
                builder.Append(']');
                return;
            }
            throw new InvalidOperationException("Unsupported debug session payload value: " + value.GetType().FullName + ". Payload must contain only JSON values.");
        }
    }
}
