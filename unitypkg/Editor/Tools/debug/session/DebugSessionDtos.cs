#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// debug session 组的 closed Request/Result DTO。
    ///
    /// 全部字段都是可由 schema 派生器静态展开的封闭类型；
    /// 只有 initialState 显式声明为任意 JSON，其余字段都必须有具体类型。
    /// 结果里的资源、句柄、清理报告与预算都是只读事实投影，不包含内部路由字段。
    /// </summary>
    public sealed class DebugSessionBudgetView
    {
        public long SessionsUsed { get; set; }
        public long SessionsRemaining { get; set; }
        public int MaxSessions { get; set; }
        public long ActiveResourcesUsed { get; set; }
        public long ActiveResourcesRemaining { get; set; }
        public int MaxActiveResourcesPerSession { get; set; }
        public long StoreBytesUsed { get; set; }
        public long StoreBytesRemaining { get; set; }
        public long MaxStoreBytes { get; set; }
        public long StorePublication { get; set; }
    }

    /// <summary>单个资源 owner 的注册状态；未注册的 owner 必须以该条目显式出现。</summary>
    public sealed class DebugSessionOwnerView
    {
        public string Owner { get; set; }
        public bool Registered { get; set; }
        public string Note { get; set; }
    }

    /// <summary>清理管线中一步的执行结果；未注册 owner 时报告零并带显式标记。</summary>
    public sealed class DebugSessionCleanupStepView
    {
        public long Order { get; set; }
        public string Name { get; set; }
        public string Owner { get; set; }
        public bool OwnerRegistered { get; set; }
        public long Entries { get; set; }
        public bool Succeeded { get; set; }
        public string Note { get; set; }
    }

    /// <summary>同一 target 上其他 session owner 的数量。</summary>
    public sealed class DebugSessionTargetOwnersView
    {
        public string Target { get; set; }
        public long OtherOwners { get; set; }
    }

    /// <summary>清理报告投影；每个字段类别无论是否为空都必须出现。</summary>
    public sealed class DebugSessionCleanupView
    {
        public string Status { get; set; }

        /// <summary>本次 stop 由哪个入口服务：main-thread 或 pause-control-channel。</summary>
        public string StopEntry { get; set; }

        public bool CleanupPending { get; set; }
        public long StoppedFutureEntries { get; set; }
        public long CancelledPendingOperations { get; set; }
        public long ReleasedPauses { get; set; }
        public long ReleasedInput { get; set; }
        public long ClosedObservations { get; set; }
        public long RemovedDetours { get; set; }
        public long PendingDetours { get; set; }
        public long ExplicitCleanupEntries { get; set; }
        public long ReleasedWaits { get; set; }
        public long ReleasedHandles { get; set; }
        public long ActiveInvocationCount { get; set; }
        public long ActiveStepCount { get; set; }
        public long ActiveEvaluateCount { get; set; }
        public int ActiveThreadId { get; set; }
        public long ActiveRevision { get; set; }
        public bool UntrackedEffectsPossible { get; set; }

        /// <summary>宿主不能静态证明未管理线程/任务/设备不存在时为 true；此时 unmanagedFacts 给出精确陈述。</summary>
        public bool UnmanagedUnknown { get; set; }

        public long LoadedNonUnloadableAssemblyCount { get; set; }
        public long LoadedNonUnloadableAssemblyBytes { get; set; }
        public List<DebugSessionCleanupStepView> Steps { get; set; }
        public List<DebugSessionOwnerView> Owners { get; set; }
        public List<DebugSessionTargetOwnersView> SharedTargets { get; set; }
        public List<string> RestoredFields { get; set; }
        public List<string> ConflictingFields { get; set; }

        /// <summary>目标已失效、因此无法判断或写回的受管理字段。</summary>
        public List<string> StaleTargets { get; set; }

        /// <summary>无法恢复的结构化效果；每一条都带可证明的原因。</summary>
        public List<string> UnrecoverableEffects { get; set; }

        /// <summary>本 session 执行过的任意代码事实。</summary>
        public List<string> DeclaredEffects { get; set; }

        public List<string> UnmanagedFacts { get; set; }

        /// <summary>把 engine-free 报告投影为公开 DTO；缺失类别按空列表补齐而不是省略。</summary>
        public static DebugSessionCleanupView From(UnityCliDebugCleanupReport report)
        {
            if (report == null) return new DebugSessionCleanupView { Steps = new List<DebugSessionCleanupStepView>(), Owners = new List<DebugSessionOwnerView>(), SharedTargets = new List<DebugSessionTargetOwnersView>(), RestoredFields = new List<string>(), ConflictingFields = new List<string>(), StaleTargets = new List<string>(), UnrecoverableEffects = new List<string>(), DeclaredEffects = new List<string>(), UnmanagedFacts = new List<string>() };
            var view = new DebugSessionCleanupView
            {
                Status = report.Status,
                StopEntry = report.StopEntry,
                CleanupPending = report.CleanupPending,
                StoppedFutureEntries = report.StoppedFutureEntries,
                CancelledPendingOperations = report.CancelledPendingOperations,
                ReleasedPauses = report.ReleasedPauses,
                ReleasedInput = report.ReleasedInput,
                ClosedObservations = report.ClosedObservations,
                RemovedDetours = report.RemovedDetours,
                PendingDetours = report.PendingDetours,
                ExplicitCleanupEntries = report.ExplicitCleanupEntries,
                ReleasedWaits = report.ReleasedWaits,
                ReleasedHandles = report.ReleasedHandles,
                ActiveInvocationCount = report.ActiveInvocationCount,
                ActiveStepCount = report.ActiveStepCount,
                ActiveEvaluateCount = report.ActiveEvaluateCount,
                ActiveThreadId = report.ActiveThreadId,
                ActiveRevision = report.ActiveRevision,
                UntrackedEffectsPossible = report.UntrackedEffectsPossible,
                UnmanagedUnknown = report.UnmanagedUnknown,
                LoadedNonUnloadableAssemblyCount = report.LoadedNonUnloadableAssemblyCount,
                LoadedNonUnloadableAssemblyBytes = report.LoadedNonUnloadableAssemblyBytes,
                Steps = new List<DebugSessionCleanupStepView>(),
                Owners = new List<DebugSessionOwnerView>(),
                SharedTargets = new List<DebugSessionTargetOwnersView>(),
                RestoredFields = new List<string>(report.RestoredFields),
                ConflictingFields = new List<string>(report.ConflictingFields),
                StaleTargets = new List<string>(report.StaleTargets),
                UnrecoverableEffects = new List<string>(report.UnrecoverableEffects),
                DeclaredEffects = new List<string>(report.DeclaredEffects),
                UnmanagedFacts = new List<string>(report.UnmanagedFacts)
            };
            foreach (var step in report.Steps)
            {
                view.Steps.Add(new DebugSessionCleanupStepView
                {
                    Order = step.Order,
                    Name = step.Name,
                    Owner = step.Owner,
                    OwnerRegistered = step.OwnerRegistered,
                    Entries = step.Entries,
                    Succeeded = step.Succeeded,
                    Note = step.Note
                });
            }
            foreach (var owner in report.Owners)
            {
                view.Owners.Add(new DebugSessionOwnerView { Owner = owner.Owner, Registered = owner.Registered, Note = owner.Note });
            }
            foreach (var target in report.SharedTargets)
            {
                view.SharedTargets.Add(new DebugSessionTargetOwnersView { Target = target.Target, OtherOwners = target.OtherOwners });
            }
            return view;
        }
    }

    /// <summary>session 持有的资源投影。</summary>
    public sealed class DebugSessionResourceView
    {
        public string ResourceId { get; set; }
        public string Kind { get; set; }
        public string Target { get; set; }
        public string State { get; set; }
        public bool HasCleanupDelegate { get; set; }
    }

    /// <summary>句柄计数摘要；没有注册 owner 时计数为零并带显式标记。</summary>
    public sealed class DebugSessionHandleView
    {
        public long Resources { get; set; }
        public long Waits { get; set; }
        public long Injections { get; set; }
        public long Listeners { get; set; }
        public long Detours { get; set; }
        public bool OwnerRegistered { get; set; }
        public string Note { get; set; }
    }

    /// <summary>最近一次运行摘要；没有运行 owner 时如实报告不可用。</summary>
    public sealed class DebugSessionRunView
    {
        public bool Available { get; set; }
        public bool OwnerRegistered { get; set; }
        public string RunId { get; set; }
        public string Kind { get; set; }
        public string StartedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public bool Success { get; set; }
        public string Detail { get; set; }
        public string Note { get; set; }
    }

    /// <summary>定义摘要；info 与 list 共用同一投影，避免两处字段语义漂移。</summary>
    public sealed class DebugSessionSummaryView
    {
        public string SessionId { get; set; }
        public string Label { get; set; }
        public string ActivationScope { get; set; }
        public string ClockKind { get; set; }
        public string State { get; set; }
        public long Revision { get; set; }
        public string CreatedAtUtc { get; set; }
        public string StoppedAtUtc { get; set; }
        public double StoppedAtMonotonicMs { get; set; }
        public long InitialStateBytes { get; set; }
        public bool CleanupPending { get; set; }
        public string CleanupStatus { get; set; }
        public bool RestoredWithoutActivation { get; set; }
        public List<DebugSessionResourceView> ActiveResources { get; set; }
        public DebugSessionHandleView Handles { get; set; }

        /// <summary>共享定义的公开投影；ownerRegistered 表示该 session 是否注册了任何清理条目。
        /// listeners 与 injections 来自唯一的触发预算账本，因此读取端看到的就是真实占用。</summary>
        public static DebugSessionSummaryView From(UnityCliDebugSessionDefinition definition, bool ownerRegistered, long listeners = 0L, long injections = 0L)
        {
            var view = new DebugSessionSummaryView
            {
                SessionId = definition.SessionId,
                Label = definition.Label,
                ActivationScope = definition.ActivationScope,
                ClockKind = definition.ClockKind,
                State = definition.State,
                Revision = definition.Revision,
                CreatedAtUtc = definition.CreatedAtUtc,
                StoppedAtUtc = definition.StoppedAtUtc,
                StoppedAtMonotonicMs = definition.StoppedAtMonotonicMs,
                InitialStateBytes = definition.InitialStateBytes,
                CleanupPending = definition.CleanupPending,
                CleanupStatus = definition.CleanupPending ? "pending" : "complete",
                RestoredWithoutActivation = definition.RestoredWithoutActivation,
                ActiveResources = new List<DebugSessionResourceView>(),
                Handles = new DebugSessionHandleView
                {
                    Resources = definition.ActiveResourceCount,
                    Waits = 0L,
                    Injections = injections,
                    Listeners = listeners,
                    Detours = 0L,
                    OwnerRegistered = ownerRegistered,
                    Note = ownerRegistered ? string.Empty : UnityCliDebugOwnerMarkers.NoOwnerRegistered
                }
            };
            foreach (var resource in definition.Resources)
            {
                view.ActiveResources.Add(new DebugSessionResourceView
                {
                    ResourceId = resource.ResourceId,
                    Kind = resource.Kind,
                    Target = resource.Target,
                    State = resource.State,
                    HasCleanupDelegate = resource.Cleanup != null
                });
            }
            return view;
        }
    }

    // ---- create ----

    /// <summary>
    /// 日志捕获配置。启用后 session 挂载唯一一次线程化日志订阅；
    /// messageContains 与 signal 构成一个真实的条件触发：匹配的记录会发布同名 host 信号，
    /// 但日志记录本身绝不被解释成业务调用的精确边界。
    /// </summary>
    public sealed class DebugLogCaptureSpec
    {
        /// <summary>true 时挂载日志捕获；false 或省略时不订阅。</summary>
        public bool? Enabled { get; set; }
        /// <summary>捕获的最低等级：log、warning、error、exception 或 all。</summary>
        [UnityCliStringEnum("log", "warning", "error", "exception", "all")] public string Level { get; set; }
        /// <summary>可选的文本条件；省略时不筛选文本。</summary>
        public string MessageContains { get; set; }
        /// <summary>可选的 host 信号名；匹配的记录会唤醒等待该信号的 driver。</summary>
        public string Signal { get; set; }
    }

    public sealed class DebugSessionCreateRequest
    {
        [UnityCliRequired] public string Label { get; set; }
        [UnityCliRequired, UnityCliStringEnum("editor", "play", "editor-and-play")] public string ActivationScope { get; set; }
        [UnityCliRequired, UnityCliStringEnum("monotonic", "editor-tick", "unity-frame", "project-logical")] public string ClockKind { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> InitialState { get; set; }
        /// <summary>可选日志捕获；启用时注册线程化日志监听并在 stop 时退订。</summary>
        public DebugLogCaptureSpec LogCapture { get; set; }
    }

    public sealed class DebugSessionCreateResult
    {
        public string SessionId { get; set; }
        public string Label { get; set; }
        public string ActivationScope { get; set; }
        public string ClockKind { get; set; }
        public string State { get; set; }
        public long Revision { get; set; }
        public string CreatedAtUtc { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public long InitialStateBytes { get; set; }
        public bool CleanupPending { get; set; }
        public long StorePublication { get; set; }
        /// <summary>本次 create 是否实际挂载了线程化日志捕获。</summary>
        public bool LogCaptureSubscribed { get; set; }
        /// <summary>声明的日志捕获等级；未启用时为空。</summary>
        public string LogCaptureLevel { get; set; }
        /// <summary>计划注入与输入预算的当前占用；与 session.info 同一来源。</summary>
        public DebugSessionHandleView Handles { get; set; }
        public DebugSessionBudgetView Budget { get; set; }
    }

    // ---- info ----

    public sealed class DebugSessionInfoRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }
    }

    public sealed class DebugSessionInfoResult
    {
        public DebugSessionSummaryView Session { get; set; }
        public DebugSessionCleanupView Cleanup { get; set; }
        public DebugSessionRunView LastRun { get; set; }
        public DebugSessionBudgetView Budget { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public long StorePublication { get; set; }
        public bool StoreUsable { get; set; }
        public string StoreReason { get; set; }
    }

    // ---- list ----

    public sealed class DebugSessionListRequest
    {
        [UnityCliRange(1, DebugLimits.SessionListMaxLimit)] public int? Limit { get; set; }
        public string Cursor { get; set; }

        /// <summary>
        /// 可选的状态过滤，取值与 UnityCliDebugSessionStates 的闭集一致。
        /// 过滤发生在分页之前：total、cursor 与 hasMore 只描述过滤后的集合；
        /// 过滤值同时进入 continuation scope，因此把游标用于不同过滤会返回 E_CONTINUATION_STALE，
        /// 而不是静默把两个集合的偏移混在一起。
        /// </summary>
        [UnityCliStringEnum("open", "stopping", "stopped")] public string State { get; set; }
    }

    public sealed class DebugSessionListResult
    {
        public List<DebugSessionSummaryView> Items { get; set; }
        public string NextCursor { get; set; }
        public long Total { get; set; }
        public long NextOffset { get; set; }
        public bool HasMore { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public long StorePublication { get; set; }
        public bool StoreUsable { get; set; }
        public string StoreReason { get; set; }
        public DebugSessionBudgetView Budget { get; set; }
    }

    // ---- stop ----

    public sealed class DebugSessionStopRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }
    }

    public sealed class DebugSessionStopResult
    {
        public string SessionId { get; set; }
        public string Label { get; set; }
        public string State { get; set; }
        public long Revision { get; set; }
        public bool Changed { get; set; }
        public bool CleanupPending { get; set; }
        public string EditorSessionId { get; set; }
        public string RuntimeId { get; set; }
        public string DomainGeneration { get; set; }
        public long StorePublication { get; set; }
        public DebugSessionCleanupView Cleanup { get; set; }
        public DebugSessionBudgetView Budget { get; set; }
    }

    // ---- remove ----

    public sealed class DebugSessionRemoveRequest
    {
        [UnityCliRequired] public string SessionId { get; set; }
    }

    public sealed class DebugSessionRemoveResult
    {
        public string SessionId { get; set; }
        public bool Removed { get; set; }
        public string State { get; set; }
        public long SessionsUsed { get; set; }
        public long SessionsRemaining { get; set; }
        public int MaxSessions { get; set; }
        public string EditorSessionId { get; set; }
        public string DomainGeneration { get; set; }
        public long StorePublication { get; set; }
    }
}
#endif
