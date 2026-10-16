using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UnityCli
{
    /// <summary>debug program 定义的状态机取值；installed 是 dormant，不代表任何 listener 已激活。</summary>
    public static class UnityCliDebugProgramStates
    {
        public const string Installed = "installed";
        public const string Active = "active";
        public const string Stopped = "stopped";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Installed, StringComparison.Ordinal)
                || string.Equals(value, Active, StringComparison.Ordinal)
                || string.Equals(value, Stopped, StringComparison.Ordinal);
        }
    }

    /// <summary>programRunId 的状态机取值；终态只有发布后才可读。</summary>
    public static class UnityCliDebugProgramRunStates
    {
        public const string Scheduled = "scheduled";
        public const string Running = "running";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Interrupted = "interrupted";

        public static bool IsTerminal(string value)
        {
            return string.Equals(value, Completed, StringComparison.Ordinal)
                || string.Equals(value, Failed, StringComparison.Ordinal)
                || string.Equals(value, Cancelled, StringComparison.Ordinal)
                || string.Equals(value, Interrupted, StringComparison.Ordinal);
        }
    }

    /// <summary>冻结的 handler 类别；签名由 DebugProgramAbi 单点声明与校验。</summary>
    public static class UnityCliDebugProgramEntryCategories
    {
        /// <summary>持续步骤：接收步骤上下文，返回宿主拥有的 step/wait 结果。</summary>
        public const string Step = "step";

        /// <summary>方法 callback：接收当前 invocation，同步返回。</summary>
        public const string Method = "method";

        /// <summary>listener：接收事件上下文，同步返回。</summary>
        public const string Listener = "listener";

        /// <summary>单次 entry：返回有界可投影值。</summary>
        public const string OneShot = "oneshot";

        /// <summary>清理 entry：接收原因与取消状态，返回 void。</summary>
        public const string Cleanup = "cleanup";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Step, StringComparison.Ordinal)
                || string.Equals(value, Method, StringComparison.Ordinal)
                || string.Equals(value, Listener, StringComparison.Ordinal)
                || string.Equals(value, OneShot, StringComparison.Ordinal)
                || string.Equals(value, Cleanup, StringComparison.Ordinal);
        }

        public static string[] All()
        {
            return new[] { Step, Method, Listener, OneShot, Cleanup };
        }
    }

    /// <summary>声明的执行点；Editor 与 Editor Play 是唯一执行域。</summary>
    public static class UnityCliDebugProgramExecutionSites
    {
        /// <summary>主线程中央 scheduler 的一个 tick；Edit 与 Play 都可用。</summary>
        public const string EditorTick = "editor-tick";

        /// <summary>exact 方法的 prefix 命中线程；当场同步执行。</summary>
        public const string MethodPrefix = "method-prefix";

        /// <summary>exact 方法的 postfix 命中线程；当场同步执行。</summary>
        public const string MethodPostfix = "method-postfix";

        /// <summary>已绑定 target 或框架 phase 上的信号执行点；由中央 scheduler 有界投递。</summary>
        public const string Signal = "signal";

        /// <summary>非主线程的定点点位；一次同步调用，不搬运跨帧 continuation。</summary>
        public const string WorkerThread = "worker-thread";

        /// <summary>Unity PlayerLoop phase；只属于 Development/Release Player，Editor 目标明确不可用。</summary>
        public const string PlayerLoopPhase = "player-loop-phase";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, EditorTick, StringComparison.Ordinal)
                || string.Equals(value, MethodPrefix, StringComparison.Ordinal)
                || string.Equals(value, MethodPostfix, StringComparison.Ordinal)
                || string.Equals(value, Signal, StringComparison.Ordinal)
                || string.Equals(value, WorkerThread, StringComparison.Ordinal)
                || string.Equals(value, PlayerLoopPhase, StringComparison.Ordinal);
        }

        /// <summary>该方法执行点是否由一次真实 dispatch 触发，而不是由 scheduler 排定。</summary>
        public static bool IsDispatchSite(string value)
        {
            return string.Equals(value, MethodPrefix, StringComparison.Ordinal)
                || string.Equals(value, MethodPostfix, StringComparison.Ordinal)
                || string.Equals(value, Signal, StringComparison.Ordinal)
                || string.Equals(value, WorkerThread, StringComparison.Ordinal);
        }

        public static string[] All()
        {
            return new[] { EditorTick, MethodPrefix, MethodPostfix, Signal, WorkerThread, PlayerLoopPhase };
        }
    }

    /// <summary>binding 的挂载阶段；每个阶段各占一个 exact target 槽位。</summary>
    public static class UnityCliDebugProgramBindingPhases
    {
        public const string Prefix = "prefix";
        public const string Postfix = "postfix";
        public const string Signal = "signal";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Prefix, StringComparison.Ordinal)
                || string.Equals(value, Postfix, StringComparison.Ordinal)
                || string.Equals(value, Signal, StringComparison.Ordinal);
        }
    }

    /// <summary>稳定的 engine-free 失败原因；Editor 侧把它映射为稳定错误码与可执行 nextActions。</summary>
    public static class UnityCliDebugProgramFailures
    {
        public const string LimitExceeded = "limit-exceeded";
        public const string DuplicateLabel = "duplicate-label";
        public const string MissingProgram = "missing-program";
        public const string WrongState = "wrong-state";
        public const string RevisionConflict = "revision-conflict";
        public const string StateEpochConflict = "state-epoch-conflict";
        public const string StateTooLarge = "state-too-large";
        public const string LabelInvalid = "label-invalid";
        public const string SourceTooLarge = "source-too-large";
        public const string EntryInvalid = "entry-invalid";
        public const string BindingInvalid = "binding-invalid";
        public const string RunNotFound = "run-not-found";
        public const string RunLimitExceeded = "run-limit-exceeded";
        public const string RunActive = "run-active";
        public const string ArgsTooLarge = "args-too-large";
        public const string DurationInvalid = "duration-invalid";
        public const string ExecutionSiteInvalid = "execution-site-invalid";
        public const string SiteUnavailable = "execution-site-unavailable";
        public const string RestoredNothing = "no-definition-loaded";
    }

    /// <summary>program 域唯一的资源上限来源；实现不得再写一份数量常量。</summary>
    public sealed class UnityCliDebugProgramCapacity
    {
        public UnityCliDebugProgramCapacity(int maxProgramsPerSession, int maxProgramsGlobal, int maxActiveRunsGlobal, int maxSourceBytes, int maxEntries, int maxBindings, int maxStateBytes, int maxArgsBytes, long maxRunDurationMs, int listDefaultLimit, int listMaxLimit)
        {
            if (maxProgramsPerSession < 1) throw new ArgumentOutOfRangeException("maxProgramsPerSession");
            if (maxProgramsGlobal < maxProgramsPerSession) throw new ArgumentOutOfRangeException("maxProgramsGlobal");
            if (maxActiveRunsGlobal < 1) throw new ArgumentOutOfRangeException("maxActiveRunsGlobal");
            if (maxSourceBytes < 1) throw new ArgumentOutOfRangeException("maxSourceBytes");
            if (maxEntries < 1 || maxBindings < 1) throw new ArgumentOutOfRangeException("maxEntries");
            if (maxStateBytes < 1 || maxArgsBytes < 1) throw new ArgumentOutOfRangeException("maxStateBytes");
            if (maxRunDurationMs < 1L) throw new ArgumentOutOfRangeException("maxRunDurationMs");
            if (listDefaultLimit < 1 || listMaxLimit < listDefaultLimit) throw new ArgumentOutOfRangeException("listMaxLimit");
            MaxProgramsPerSession = maxProgramsPerSession;
            MaxProgramsGlobal = maxProgramsGlobal;
            MaxActiveRunsGlobal = maxActiveRunsGlobal;
            MaxSourceBytes = maxSourceBytes;
            MaxEntries = maxEntries;
            MaxBindings = maxBindings;
            MaxStateBytes = maxStateBytes;
            MaxArgsBytes = maxArgsBytes;
            MaxRunDurationMs = maxRunDurationMs;
            ListDefaultLimit = listDefaultLimit;
            ListMaxLimit = listMaxLimit;
        }

        public int MaxProgramsPerSession { get; private set; }
        public int MaxProgramsGlobal { get; private set; }
        public int MaxActiveRunsGlobal { get; private set; }
        public int MaxSourceBytes { get; private set; }
        public int MaxEntries { get; private set; }
        public int MaxBindings { get; private set; }
        public int MaxStateBytes { get; private set; }
        public int MaxArgsBytes { get; private set; }
        public long MaxRunDurationMs { get; private set; }
        public int ListDefaultLimit { get; private set; }
        public int ListMaxLimit { get; private set; }
    }

    /// <summary>一个命名 entry；Resolved 由 Editor 侧在 ABI 校验后写入，Shared 不持有反射。</summary>
    public sealed class UnityCliDebugProgramEntry
    {
        public string Name = string.Empty;
        public string Category = string.Empty;

        /// <summary>声明 entry 的类型全名；由调用方给出，ABI 校验按它精确解析。</summary>
        public string DeclaringType = string.Empty;

        /// <summary>该 entry 的实际签名文本；ABI 校验后用真实 method 描述填充。</summary>
        public string Signature = string.Empty;

        public UnityCliDebugProgramEntry Clone()
        {
            return new UnityCliDebugProgramEntry { Name = Name, Category = Category, DeclaringType = DeclaringType, Signature = Signature };
        }
    }

    /// <summary>一个显式 binding：exact target 的一个阶段绑定到一个 method/step entry。</summary>
    public sealed class UnityCliDebugProgramBinding
    {
        public string BindingId = string.Empty;
        public string Phase = UnityCliDebugProgramBindingPhases.Prefix;
        public string Entry = string.Empty;
        public string TargetKey = string.Empty;
        public string TargetSummary = string.Empty;

        /// <summary>hook host 为该 binding 建立的 definition 身份；未挂载时为空。</summary>
        public string HookId = string.Empty;

        /// <summary>该 binding 当前是否已由 hook host 挂载。</summary>
        public bool Attached;

        public UnityCliDebugProgramBinding Clone()
        {
            return new UnityCliDebugProgramBinding
            {
                BindingId = BindingId,
                Phase = Phase,
                Entry = Entry,
                TargetKey = TargetKey,
                TargetSummary = TargetSummary,
                HookId = HookId,
                Attached = Attached
            };
        }
    }

    /// <summary>
    /// 一个 program 定义。源码、ABI、bindings 与 revision 状态是不可变发布单元：
    /// replace 只在 CAS 通过后整体替换，失败保留原配置。
    /// </summary>
    public sealed class UnityCliDebugProgramDefinition
    {
        public string ProgramId = string.Empty;
        public string SessionId = string.Empty;
        public string Label = string.Empty;
        public string EntryType = string.Empty;
        public string State = UnityCliDebugProgramStates.Installed;
        public long Revision = 1L;
        public string Source = string.Empty;
        public string SourceSha256 = string.Empty;
        public long SourceBytes;
        public string CreatedAtUtc = string.Empty;
        public string StoppedAtUtc = string.Empty;
        public double StoppedAtMonotonicMs;
        public string ArtifactIdentity = string.Empty;
        public string AssemblyName = string.Empty;

        /// <summary>revision 状态的 CAS epoch；每次成功发布 revision 递增，冲突时保留旧配置。</summary>
        public long StateEpoch;

        /// <summary>当前 revision 的 JSON 状态快照；与定义生命周期状态 State 是两个不同事实。</summary>
        public object RevisionState;
        public long StateBytes;

        /// <summary>声明的 revision 状态 schema；与状态一起发布，供 info 读回。</summary>
        public object StateSchema;
        public long StateSchemaBytes;

        public readonly List<UnityCliDebugProgramEntry> Entries = new List<UnityCliDebugProgramEntry>();
        public readonly List<UnityCliDebugProgramBinding> Bindings = new List<UnityCliDebugProgramBinding>();

        /// <summary>当前仍在执行的 callback/entry 调用数量；replace 必须报告它。</summary>
        public int InFlightCalls;

        /// <summary>当前持续 step driver 是否已建立；每个 program 同时最多一个。</summary>
        public bool StepDriverPlanted;

        public UnityCliDebugProgramEntry FindEntry(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var entry in Entries)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal)) return entry;
            }
            return null;
        }

        public UnityCliDebugProgramEntry RequireEntry(string name)
        {
            var entry = FindEntry(name);
            if (entry == null) throw new InvalidOperationException("Unknown entry " + name + " in program " + ProgramId + ".");
            return entry;
        }
    }

    /// <summary>一次 program 激活或定点调用；terminal 只在原代码返回且清理可证明后发布。</summary>
    public sealed class UnityCliDebugProgramRun
    {
        public string ProgramRunId = string.Empty;
        public string ProgramId = string.Empty;
        public string SessionId = string.Empty;
        public string Entry = string.Empty;
        public string EntryCategory = string.Empty;
        public string ExecutionSite = string.Empty;
        public string Target = string.Empty;
        public string RunId = string.Empty;
        public object Args;
        public long ArgsBytes;

        /// <summary>该运行固定使用的 revision；driver 的后续 step 逐个记录实际 revision。</summary>
        public long Revision;
        public string State = UnityCliDebugProgramRunStates.Scheduled;

        public long DurationMs;
        public double CreatedAtUtcMs;
        public double DeadlineUtcMs;
        public double DeadlineMonotonicMs;
        public string CreatedAtUtc = string.Empty;
        public string StartedAtUtc = string.Empty;
        public string CompletedAtUtc = string.Empty;
        public int TriggeredThreadId;
        public int ExecutionThreadId;

        /// <summary>进入同步代码后到期只能请求取消；该标志说明同步代码仍在途。</summary>
        public bool CancellationRequested;
        public bool InFlight;

        public long StepCount;
        public long CheckCount;
        public long DeliveryCount;
        public readonly List<long> StepRevisions = new List<long>();

        /// <summary>最后一次可信业务结果；未返回任何业务结果时保持 "none"。</summary>
        public string ResultTrust = "none";
        public object Result;
        public string FailureCode = string.Empty;
        public string FailureMessage = string.Empty;

        public bool IsTerminal { get { return UnityCliDebugProgramRunStates.IsTerminal(State); } }
    }

    /// <summary>预算占用投影；拒绝路径必须携带同一份事实。</summary>
    public sealed class UnityCliDebugProgramBudget
    {
        public int ProgramsUsed;
        public int ProgramsRemaining;
        public int MaxProgramsGlobal;
        public int SessionProgramsUsed;
        public int SessionProgramsRemaining;
        public int MaxProgramsPerSession;
        public int ActiveRunsUsed;
        public int ActiveRunsRemaining;
        public int MaxActiveRunsGlobal;

        public Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "programsUsed", (long)ProgramsUsed },
                { "programsRemaining", (long)ProgramsRemaining },
                { "maxProgramsGlobal", (long)MaxProgramsGlobal },
                { "sessionProgramsUsed", (long)SessionProgramsUsed },
                { "sessionProgramsRemaining", (long)SessionProgramsRemaining },
                { "maxProgramsPerSession", (long)MaxProgramsPerSession },
                { "activeRunsUsed", (long)ActiveRunsUsed },
                { "activeRunsRemaining", (long)ActiveRunsRemaining },
                { "maxActiveRunsGlobal", (long)MaxActiveRunsGlobal }
            };
        }
    }

    /// <summary>install/replace/run 的 engine-free 结果；失败时预算与占用仍然报告当前事实。</summary>
    public sealed class UnityCliDebugProgramOutcome
    {
        public bool Success;
        public string Failure = string.Empty;
        public UnityCliDebugProgramDefinition Definition;
        public UnityCliDebugProgramRun Run;
        public long OldRevision;
        public int OldInFlightCalls;
        public UnityCliDebugProgramBudget Budget;
    }

    /// <summary>
    /// debug program 的唯一内存 owner：定义表、revision CAS、programRun 状态机、预算与运行占用。
    ///
    /// 规则：
    /// - 定义只属于一个 sessionId 与一个 Editor 启动；owner 不从选择、上次调用或 CLI 进程推断。
    /// - 一个 program 同时最多一个活动 run；活跃 run 的全局数量有硬上限。
    /// - pending 超期必须关闭执行权并记 cancelled，绝不迟到执行；进入同步代码后只请求取消。
    /// - 所有状态变化在内部锁内一次完成，读取方拿到的是不可变快照。
    /// - 本类型不持有 Unity 对象、反射结果、线程或文件句柄。
    /// </summary>
    public sealed class UnityCliDebugProgramRegistry
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, UnityCliDebugProgramDefinition> programs = new Dictionary<string, UnityCliDebugProgramDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, UnityCliDebugProgramRun> runs = new Dictionary<string, UnityCliDebugProgramRun>(StringComparer.Ordinal);

        public UnityCliDebugProgramRegistry(UnityCliDebugProgramCapacity capacity)
        {
            if (capacity == null) throw new ArgumentNullException("capacity");
            Capacity = capacity;
            NextProgramIndex = 1;
            NextRunIndex = 1;
        }

        public UnityCliDebugProgramCapacity Capacity { get; private set; }

        public int NextProgramIndex { get; private set; }
        public int NextRunIndex { get; private set; }

        /// <summary>下一个将被分配的 programId；只用于 effect 前的编译准备，不消耗序号。</summary>
        public string PeekProgramId()
        {
            lock (sync) return "prog-" + NextProgramIndex.ToString(CultureInfo.InvariantCulture);
        }

        public int ProgramCount { get { lock (sync) return programs.Count; } }

        public IReadOnlyList<UnityCliDebugProgramDefinition> Programs()
        {
            lock (sync) return programs.Values.OrderBy(program => ProgramOrdinal(program.ProgramId)).ToList();
        }

        public UnityCliDebugProgramDefinition Find(string programId)
        {
            if (string.IsNullOrEmpty(programId)) return null;
            lock (sync)
            {
                UnityCliDebugProgramDefinition program;
                return programs.TryGetValue(programId, out program) ? program : null;
            }
        }

        public UnityCliDebugProgramDefinition FindByLabel(string sessionId, string label)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(label)) return null;
            lock (sync)
            {
                foreach (var program in programs.Values)
                {
                    if (string.Equals(program.SessionId, sessionId, StringComparison.Ordinal)
                        && string.Equals(program.Label, label, StringComparison.Ordinal)) return program;
                }
                return null;
            }
        }

        public IReadOnlyList<UnityCliDebugProgramDefinition> ProgramsFor(string sessionId)
        {
            lock (sync)
            {
                return programs.Values
                    .Where(program => string.Equals(program.SessionId, sessionId, StringComparison.Ordinal))
                    .OrderBy(program => ProgramOrdinal(program.ProgramId))
                    .ToList();
            }
        }

        public UnityCliDebugProgramRun FindRun(string programRunId)
        {
            if (string.IsNullOrEmpty(programRunId)) return null;
            lock (sync)
            {
                UnityCliDebugProgramRun run;
                return runs.TryGetValue(programRunId, out run) ? run : null;
            }
        }

        public IReadOnlyList<UnityCliDebugProgramRun> RunsFor(string programId)
        {
            lock (sync)
            {
                return runs.Values
                    .Where(run => string.Equals(run.ProgramId, programId, StringComparison.Ordinal))
                    .OrderBy(run => RunOrdinal(run.ProgramRunId))
                    .ToList();
            }
        }

        /// <summary>全部非终态运行的只读快照；中央 scheduler 据此关闭超期运行。</summary>
        public IReadOnlyList<UnityCliDebugProgramRun> ActiveRuns()
        {
            lock (sync) return runs.Values.Where(run => !run.IsTerminal).OrderBy(run => RunOrdinal(run.ProgramRunId)).ToList();
        }

        public UnityCliDebugProgramBudget Budget(string sessionId)
        {
            lock (sync) return BudgetLocked(sessionId);
        }

        // ------------------------------------------------------------------
        // 定义生命周期
        // ------------------------------------------------------------------

        /// <summary>不做任何状态改变的前置判定；返回稳定原因，null 表示 install 可以继续。</summary>
        public string CanInstall(string sessionId, string label, long sourceBytes, out UnityCliDebugProgramBudget budgetObject)
        {
            lock (sync)
            {
                budgetObject = BudgetLocked(sessionId);
                if (string.IsNullOrEmpty(sessionId)) return UnityCliDebugProgramFailures.MissingProgram;
                if (string.IsNullOrEmpty(label)) return UnityCliDebugProgramFailures.LabelInvalid;
                if (sourceBytes > Capacity.MaxSourceBytes) return UnityCliDebugProgramFailures.SourceTooLarge;
                if (FindByLabelLocked(sessionId, label) != null) return UnityCliDebugProgramFailures.DuplicateLabel;
                if (programs.Count >= Capacity.MaxProgramsGlobal) return UnityCliDebugProgramFailures.LimitExceeded;
                if (ProgramsForSessionLocked(sessionId).Count >= Capacity.MaxProgramsPerSession) return UnityCliDebugProgramFailures.LimitExceeded;
                return null;
            }
        }

        /// <summary>分配 prog-&lt;n&gt; 并建立 dormant 定义；失败不改变任何状态，也不消耗序号。</summary>
        public UnityCliDebugProgramOutcome Install(string sessionId, string label, string entryType, string source, string sourceSha256, long sourceBytes, object state, long stateBytes, object stateSchema, long stateSchemaBytes, string createdAtUtc, IReadOnlyList<UnityCliDebugProgramEntry> entries, IReadOnlyList<UnityCliDebugProgramBinding> bindings)
        {
            lock (sync)
            {
                UnityCliDebugProgramBudget budgetObject;
                var failure = CanInstall(sessionId, label, sourceBytes, out budgetObject);
                var outcome = new UnityCliDebugProgramOutcome { Budget = budgetObject };
                if (failure != null)
                {
                    outcome.Failure = failure;
                    return outcome;
                }
                var definition = new UnityCliDebugProgramDefinition
                {
                    ProgramId = "prog-" + NextProgramIndex.ToString(CultureInfo.InvariantCulture),
                    SessionId = sessionId,
                    Label = label,
                    EntryType = entryType ?? string.Empty,
                    State = UnityCliDebugProgramStates.Installed,
                    Revision = 1L,
                    Source = source ?? string.Empty,
                    SourceSha256 = sourceSha256 ?? string.Empty,
                    SourceBytes = sourceBytes,
                    CreatedAtUtc = createdAtUtc ?? string.Empty,
                    StateEpoch = 1L,
                    RevisionState = state,
                    StateBytes = stateBytes,
                    StateSchema = stateSchema,
                    StateSchemaBytes = stateSchemaBytes
                };
                if (entries != null) foreach (var entry in entries) definition.Entries.Add(entry.Clone());
                if (bindings != null) foreach (var binding in bindings) definition.Bindings.Add(binding.Clone());
                NextProgramIndex++;
                programs[definition.ProgramId] = definition;
                outcome.Success = true;
                outcome.Definition = definition;
                outcome.Budget = BudgetLocked(sessionId);
                return outcome;
            }
        }

        /// <summary>
        /// replace 的前置判定：revision、预算与状态 epoch 一起判定。
        /// 通过后调用方仍需完成编译、ABI 与 binding 校验，最后才用 PublishRevision 做 CAS 发布。
        /// </summary>
        public string CanReplace(string programId, long expectedRevision, long sourceBytes, long stateBytes, long stateSchemaBytes, out string outState)
        {
            lock (sync)
            {
                outState = string.Empty;
                var definition = FindLocked(programId);
                if (definition == null) return UnityCliDebugProgramFailures.MissingProgram;
                outState = definition.State;
                if (string.Equals(definition.State, UnityCliDebugProgramStates.Stopped, StringComparison.Ordinal)) return UnityCliDebugProgramFailures.WrongState;
                if (expectedRevision != definition.Revision) return UnityCliDebugProgramFailures.RevisionConflict;
                if (sourceBytes > Capacity.MaxSourceBytes) return UnityCliDebugProgramFailures.SourceTooLarge;
                if (stateBytes > Capacity.MaxStateBytes || stateSchemaBytes > Capacity.MaxStateBytes) return UnityCliDebugProgramFailures.StateTooLarge;
                return null;
            }
        }

        /// <summary>
        /// CAS 发布一个不可变的候选 revision：源码、ABI、bindings 与 revision 状态整体替换。
        /// expectedRevision 或 stateEpoch 不匹配时拒绝，调用方保留旧配置。
        /// </summary>
        public UnityCliDebugProgramOutcome PublishRevision(string programId, long expectedRevision, long expectedStateEpoch, string source, string sourceSha256, long sourceBytes, IReadOnlyList<UnityCliDebugProgramEntry> entries, IReadOnlyList<UnityCliDebugProgramBinding> bindings, object state, long stateBytes, object stateSchema, long stateSchemaBytes, string artifactIdentity, string assemblyName)
        {
            lock (sync)
            {
                var definition = FindLocked(programId);
                var outcome = new UnityCliDebugProgramOutcome();
                if (definition == null)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.MissingProgram;
                    return outcome;
                }
                outcome.OldRevision = definition.Revision;
                outcome.OldInFlightCalls = definition.InFlightCalls;
                outcome.Budget = BudgetLocked(definition.SessionId);
                if (expectedRevision != definition.Revision)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.RevisionConflict;
                    return outcome;
                }
                if (expectedStateEpoch != definition.StateEpoch)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.StateEpochConflict;
                    return outcome;
                }
                if (sourceBytes > Capacity.MaxSourceBytes)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.SourceTooLarge;
                    return outcome;
                }
                if (stateBytes > Capacity.MaxStateBytes || stateSchemaBytes > Capacity.MaxStateBytes)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.StateTooLarge;
                    return outcome;
                }
                definition.Source = source ?? string.Empty;
                definition.SourceSha256 = sourceSha256 ?? string.Empty;
                definition.SourceBytes = sourceBytes;
                definition.Entries.Clear();
                if (entries != null) foreach (var entry in entries) definition.Entries.Add(entry.Clone());
                definition.Bindings.Clear();
                if (bindings != null) foreach (var binding in bindings) definition.Bindings.Add(binding.Clone());
                definition.RevisionState = state;
                definition.StateBytes = stateBytes;
                definition.StateSchema = stateSchema;
                definition.StateSchemaBytes = stateSchemaBytes;
                definition.ArtifactIdentity = artifactIdentity ?? string.Empty;
                definition.AssemblyName = assemblyName ?? string.Empty;
                // revision 与 state epoch 一起前进：旧在途调用继续使用旧状态，发布后不自动合并它的写入。
                definition.Revision++;
                definition.StateEpoch++;
                outcome.Success = true;
                outcome.Definition = definition;
                return outcome;
            }
        }

        /// <summary>把 binding 的挂载事实写回定义；只有候选发布之后才允许调用。</summary>
        public bool RecordBindingAttachment(string programId, string bindingId, string hookId, string targetKey, bool attached)
        {
            lock (sync)
            {
                var definition = FindLocked(programId);
                if (definition == null) return false;
                foreach (var binding in definition.Bindings)
                {
                    if (!string.Equals(binding.BindingId, bindingId, StringComparison.Ordinal)) continue;
                    binding.HookId = hookId ?? string.Empty;
                    binding.TargetKey = targetKey ?? string.Empty;
                    binding.Attached = attached;
                    return true;
                }
                return false;
            }
        }

        /// <summary>记录 callback/entry 进入与退出；replace 报告的在途数量来自这里。</summary>
        public void EnterCallback(string programId)
        {
            lock (sync)
            {
                var definition = FindLocked(programId);
                if (definition != null) definition.InFlightCalls++;
            }
        }

        public void ExitCallback(string programId)
        {
            lock (sync)
            {
                var definition = FindLocked(programId);
                if (definition != null && definition.InFlightCalls > 0) definition.InFlightCalls--;
            }
        }

        /// <summary>CAS 更新 revision 状态；epoch 不匹配时拒绝而不是合并。</summary>
        public string TryUpdateState(string programId, long expectedEpoch, object candidate, long candidateBytes, out long newEpoch)
        {
            lock (sync)
            {
                newEpoch = 0L;
                var definition = FindLocked(programId);
                if (definition == null) return UnityCliDebugProgramFailures.MissingProgram;
                if (expectedEpoch != definition.StateEpoch) return UnityCliDebugProgramFailures.StateEpochConflict;
                if (candidateBytes > Capacity.MaxStateBytes) return UnityCliDebugProgramFailures.StateTooLarge;
                definition.RevisionState = candidate;
                definition.StateBytes = candidateBytes;
                definition.StateEpoch++;
                newEpoch = definition.StateEpoch;
                return null;
            }
        }

        /// <summary>不改变任何状态的 remove 前置判定。</summary>
        public string CanRemove(string programId, out string state, out int activeRuns, out int attachedBindings)
        {
            lock (sync)
            {
                state = string.Empty;
                activeRuns = 0;
                attachedBindings = 0;
                var definition = FindLocked(programId);
                if (definition == null) return UnityCliDebugProgramFailures.MissingProgram;
                state = definition.State;
                activeRuns = ActiveRunsLocked(programId);
                attachedBindings = definition.Bindings.Count(binding => binding.Attached);
                if (activeRuns > 0) return UnityCliDebugProgramFailures.RunActive;
                if (definition.InFlightCalls > 0) return UnityCliDebugProgramFailures.RunActive;
                return null;
            }
        }

        /// <summary>删除定义与它全部已封闭运行记录；调用方必须先撤除 binding 并证明清理完成。</summary>
        public UnityCliDebugProgramOutcome Remove(string programId)
        {
            lock (sync)
            {
                var outcome = new UnityCliDebugProgramOutcome();
                var definition = FindLocked(programId);
                if (definition == null)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.MissingProgram;
                    return outcome;
                }
                var stale = runs.Values.Where(run => string.Equals(run.ProgramId, programId, StringComparison.Ordinal)).Select(run => run.ProgramRunId).ToList();
                foreach (var key in stale) runs.Remove(key);
                programs.Remove(programId);
                outcome.Success = true;
                outcome.Budget = BudgetLocked(definition.SessionId);
                return outcome;
            }
        }

        /// <summary>stop 关闭该定义未来的派发与 driver；定义与证据保留。</summary>
        public bool CloseFutureDispatch(string programId, string stoppedAtUtc, double stoppedAtMonotonicMs)
        {
            lock (sync)
            {
                var definition = FindLocked(programId);
                if (definition == null) return false;
                definition.State = UnityCliDebugProgramStates.Stopped;
                definition.StepDriverPlanted = false;
                definition.StoppedAtUtc = stoppedAtUtc ?? string.Empty;
                definition.StoppedAtMonotonicMs = stoppedAtMonotonicMs;
                return true;
            }
        }

        /// <summary>该定义在接受新运行前必须处于 installed。</summary>
        public string CanActivate(string programId, out string state)
        {
            lock (sync)
            {
                state = string.Empty;
                var definition = FindLocked(programId);
                if (definition == null) return UnityCliDebugProgramFailures.MissingProgram;
                state = definition.State;
                if (string.Equals(definition.State, UnityCliDebugProgramStates.Stopped, StringComparison.Ordinal)) return UnityCliDebugProgramFailures.WrongState;
                return null;
            }
        }

        public void MarkActive(string programId)
        {
            lock (sync)
            {
                var definition = FindLocked(programId);
                if (definition != null && string.Equals(definition.State, UnityCliDebugProgramStates.Installed, StringComparison.Ordinal))
                    definition.State = UnityCliDebugProgramStates.Active;
            }
        }

        /// <summary>运行结束后的定义回到 dormant（installed），因此可以再次 run；只有 stop 才置为 stopped。</summary>
        public void MarkDormant(string programId)
        {
            lock (sync)
            {
                var definition = FindLocked(programId);
                if (definition != null && string.Equals(definition.State, UnityCliDebugProgramStates.Active, StringComparison.Ordinal))
                    definition.State = UnityCliDebugProgramStates.Installed;
            }
        }

        /// <summary>该 program 当前是否存在活动 run；用于 step driver 的单一性判定。</summary>
        public int ActiveRunsFor(string programId)
        {
            lock (sync) return ActiveRunsLocked(programId);
        }

        // ------------------------------------------------------------------
        // program run 生命周期
        // ------------------------------------------------------------------

        /// <summary>排定一次运行；拒绝时不消耗序号，也不改变任何运行状态。</summary>
        public UnityCliDebugProgramOutcome ScheduleRun(string programId, string entry, string entryCategory, string executionSite, string target, string runId, object args, long argsBytes, long durationMs, double createdAtUtcMs, string createdAtUtc, double nowMonotonicMs)
        {
            lock (sync)
            {
                var outcome = new UnityCliDebugProgramOutcome();
                var definition = FindLocked(programId);
                if (definition == null)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.MissingProgram;
                    return outcome;
                }
                outcome.Budget = BudgetLocked(definition.SessionId);
                if (string.Equals(definition.State, UnityCliDebugProgramStates.Stopped, StringComparison.Ordinal))
                {
                    outcome.Failure = UnityCliDebugProgramFailures.WrongState;
                    return outcome;
                }
                if (definition.FindEntry(entry) == null)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.EntryInvalid;
                    return outcome;
                }
                if (argsBytes > Capacity.MaxArgsBytes)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.ArgsTooLarge;
                    return outcome;
                }
                if (durationMs < 1L || durationMs > Capacity.MaxRunDurationMs)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.DurationInvalid;
                    return outcome;
                }
                if (!UnityCliDebugProgramExecutionSites.IsDefined(executionSite))
                {
                    outcome.Failure = UnityCliDebugProgramFailures.ExecutionSiteInvalid;
                    return outcome;
                }
                if (ActiveRunsLocked(programId) > 0)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.RunActive;
                    return outcome;
                }
                if (ActiveRunCountLocked() >= Capacity.MaxActiveRunsGlobal)
                {
                    outcome.Failure = UnityCliDebugProgramFailures.RunLimitExceeded;
                    return outcome;
                }
                var run = new UnityCliDebugProgramRun
                {
                    ProgramRunId = "prun-" + NextRunIndex.ToString(CultureInfo.InvariantCulture),
                    ProgramId = definition.ProgramId,
                    SessionId = definition.SessionId,
                    Entry = entry,
                    EntryCategory = entryCategory ?? string.Empty,
                    ExecutionSite = executionSite,
                    Target = target ?? string.Empty,
                    RunId = runId ?? string.Empty,
                    Args = args,
                    ArgsBytes = argsBytes,
                    Revision = definition.Revision,
                    State = UnityCliDebugProgramRunStates.Scheduled,
                    DurationMs = durationMs,
                    CreatedAtUtcMs = createdAtUtcMs,
                    DeadlineUtcMs = createdAtUtcMs + durationMs,
                    DeadlineMonotonicMs = nowMonotonicMs + durationMs,
                    CreatedAtUtc = createdAtUtc ?? string.Empty
                };
                NextRunIndex++;
                runs[run.ProgramRunId] = run;
                outcome.Success = true;
                outcome.Definition = definition;
                outcome.Run = run;
                outcome.Budget = BudgetLocked(definition.SessionId);
                return outcome;
            }
        }

        /// <summary>
        /// 取得执行权：scheduled→running 只允许一个赢家。
        /// 已取消或已超过 deadline 的 pending 运行在此关闭执行权并记 cancelled，绝不迟到执行。
        /// </summary>
        public bool TryBeginRun(string programRunId, double nowUtcMs, int threadId)
        {
            lock (sync)
            {
                var run = FindRunLocked(programRunId);
                if (run == null || !string.Equals(run.State, UnityCliDebugProgramRunStates.Scheduled, StringComparison.Ordinal)) return false;
                if (run.CancellationRequested || nowUtcMs > run.DeadlineUtcMs)
                {
                    run.State = UnityCliDebugProgramRunStates.Cancelled;
                    run.FailureCode = "E_DEBUG_PROGRAM_RUN_EXPIRED";
                    run.FailureMessage = "The pending run reached its deadline before it obtained its execution right, so it will never execute.";
                    run.CompletedAtUtc = UtcText();
                    return false;
                }
                run.State = UnityCliDebugProgramRunStates.Running;
                run.InFlight = true;
                run.StartedAtUtc = UtcText();
                run.ExecutionThreadId = threadId;
                return true;
            }
        }

        /// <summary>
        /// 持续 step driver 取得下一个步骤的执行权：scheduled→running 或 running→running。
        /// 同一个 program 同时只存在一个 driver，因此只有它能连续取得步骤权；已取消、已终态、
        /// 已超过 deadline 或同步代码仍在途时一律拒绝，绝不迟到执行，也绝不重入。
        /// </summary>
        public bool TryBeginStep(string programRunId, double nowUtcMs, int threadId)
        {
            lock (sync)
            {
                var run = FindRunLocked(programRunId);
                if (run == null || run.IsTerminal) return false;
                if (run.CancellationRequested || nowUtcMs > run.DeadlineUtcMs) return false;
                var scheduled = string.Equals(run.State, UnityCliDebugProgramRunStates.Scheduled, StringComparison.Ordinal);
                if (!scheduled && !string.Equals(run.State, UnityCliDebugProgramRunStates.Running, StringComparison.Ordinal)) return false;
                if (run.InFlight) return false;
                if (scheduled) run.StartedAtUtc = UtcText();
                run.State = UnityCliDebugProgramRunStates.Running;
                run.InFlight = true;
                run.ExecutionThreadId = threadId;
                return true;
            }
        }

        /// <summary>pending 超期扫描：返回本次关闭的执行权数量。</summary>
        public int CancelExpiredRuns(double nowUtcMs)
        {
            lock (sync)
            {
                var cancelled = 0;
                foreach (var run in runs.Values)
                {
                    if (!string.Equals(run.State, UnityCliDebugProgramRunStates.Scheduled, StringComparison.Ordinal)) continue;
                    if (nowUtcMs <= run.DeadlineUtcMs) continue;
                    run.State = UnityCliDebugProgramRunStates.Cancelled;
                    run.FailureCode = "E_DEBUG_PROGRAM_RUN_EXPIRED";
                    run.FailureMessage = "The pending run reached its deadline before it obtained its execution right, so it will never execute.";
                    run.CompletedAtUtc = UtcText();
                    cancelled++;
                }
                return cancelled;
            }
        }

        /// <summary>
        /// 取消一次运行。pending 成功关闭执行权并返回 true；已经 running 时只请求取消并返回 false，
        /// 调用方必须报告同步代码仍在途。
        /// </summary>
        public bool RequestCancellation(string programRunId, string reason)
        {
            lock (sync)
            {
                var run = FindRunLocked(programRunId);
                if (run == null || run.IsTerminal) return false;
                if (string.Equals(run.State, UnityCliDebugProgramRunStates.Scheduled, StringComparison.Ordinal))
                {
                    run.State = UnityCliDebugProgramRunStates.Cancelled;
                    run.FailureCode = "E_DEBUG_PROGRAM_RUN_CANCELLED";
                    run.FailureMessage = reason ?? string.Empty;
                    run.CompletedAtUtc = UtcText();
                    return true;
                }
                run.CancellationRequested = true;
                return false;
            }
        }

        /// <summary>记录一个已完成的 step 与其实际 revision；后续 step 在安全边界采用新 revision。</summary>
        public void RecordStep(string programRunId, long revision, bool cancelled)
        {
            lock (sync)
            {
                var run = FindRunLocked(programRunId);
                if (run == null) return;
                if (cancelled)
                {
                    run.State = UnityCliDebugProgramRunStates.Cancelled;
                    run.InFlight = false;
                    run.FailureCode = "E_DEBUG_PROGRAM_RUN_CANCELLED";
                    run.FailureMessage = "The step driver stopped cooperatively after a cancellation request.";
                    run.CompletedAtUtc = UtcText();
                    return;
                }
                run.StepCount++;
                run.StepRevisions.Add(revision);
            }
        }

        /// <summary>记录一次可信业务结果；只有 entry 真正返回时才允许调用。</summary>
        public void RecordResult(string programRunId, object result)
        {
            lock (sync)
            {
                var run = FindRunLocked(programRunId);
                if (run == null || run.IsTerminal) return;
                run.Result = result;
                run.ResultTrust = "returned-by-entry";
            }
        }

        /// <summary>记录一次有界投递事实；投递与当场执行分别计数。</summary>
        public void RecordDelivery(string programRunId)
        {
            lock (sync)
            {
                var run = FindRunLocked(programRunId);
                if (run != null) run.DeliveryCount++;
            }
        }

        /// <summary>
        /// 发布终态。只有原代码已经返回且调用方已证明清理时才允许调用；
        /// 未完成的同步代码在这里被拒绝，调用方必须继续报告在途。
        /// </summary>
        public bool CompleteRun(string programRunId, string terminal, string failureCode, string failureMessage, double nowUtcMs)
        {
            lock (sync)
            {
                var run = FindRunLocked(programRunId);
                if (run == null || run.IsTerminal) return false;
                if (run.InFlight) return false;
                if (!UnityCliDebugProgramRunStates.IsTerminal(terminal)) return false;
                run.State = terminal;
                run.FailureCode = failureCode ?? string.Empty;
                run.FailureMessage = failureMessage ?? string.Empty;
                run.CompletedAtUtc = UtcText();
                return true;
            }
        }

        /// <summary>标记同步代码已返回；terminal 只能在该标记之后发布。</summary>
        public void MarkCodeReturned(string programRunId)
        {
            lock (sync)
            {
                var run = FindRunLocked(programRunId);
                if (run != null) run.InFlight = false;
            }
        }

        /// <summary>runtime/域边界中断：把所有非终态运行置为 interrupted。</summary>
        public int InterruptActiveRuns(string reason)
        {
            lock (sync)
            {
                var interrupted = 0;
                foreach (var run in runs.Values)
                {
                    if (run.IsTerminal) continue;
                    run.State = UnityCliDebugProgramRunStates.Interrupted;
                    run.InFlight = false;
                    run.FailureCode = "E_DEBUG_PROGRAM_RUN_INTERRUPTED";
                    run.FailureMessage = reason ?? string.Empty;
                    interrupted++;
                }
                return interrupted;
            }
        }

        public void MarkStepDriverPlanted(string programId, bool planted)
        {
            lock (sync)
            {
                var definition = FindLocked(programId);
                if (definition != null) definition.StepDriverPlanted = planted;
            }
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        private UnityCliDebugProgramDefinition FindLocked(string programId)
        {
            if (string.IsNullOrEmpty(programId)) return null;
            UnityCliDebugProgramDefinition program;
            return programs.TryGetValue(programId, out program) ? program : null;
        }

        private UnityCliDebugProgramDefinition FindByLabelLocked(string sessionId, string label)
        {
            foreach (var program in programs.Values)
            {
                if (string.Equals(program.SessionId, sessionId, StringComparison.Ordinal)
                    && string.Equals(program.Label, label, StringComparison.Ordinal)) return program;
            }
            return null;
        }

        private List<UnityCliDebugProgramDefinition> ProgramsForSessionLocked(string sessionId)
        {
            return programs.Values.Where(program => string.Equals(program.SessionId, sessionId, StringComparison.Ordinal)).ToList();
        }

        private UnityCliDebugProgramRun FindRunLocked(string programRunId)
        {
            if (string.IsNullOrEmpty(programRunId)) return null;
            UnityCliDebugProgramRun run;
            return runs.TryGetValue(programRunId, out run) ? run : null;
        }

        private int ActiveRunsLocked(string programId)
        {
            var count = 0;
            foreach (var run in runs.Values)
            {
                if (run.IsTerminal) continue;
                if (!string.Equals(run.ProgramId, programId, StringComparison.Ordinal)) continue;
                count++;
            }
            return count;
        }

        private int ActiveRunCountLocked()
        {
            var count = 0;
            foreach (var run in runs.Values)
            {
                if (!run.IsTerminal) count++;
            }
            return count;
        }

        private UnityCliDebugProgramBudget BudgetLocked(string sessionId)
        {
            var sessionPrograms = string.IsNullOrEmpty(sessionId) ? 0 : ProgramsForSessionLocked(sessionId).Count;
            var activeRuns = ActiveRunCountLocked();
            return new UnityCliDebugProgramBudget
            {
                ProgramsUsed = programs.Count,
                ProgramsRemaining = Math.Max(0, Capacity.MaxProgramsGlobal - programs.Count),
                MaxProgramsGlobal = Capacity.MaxProgramsGlobal,
                SessionProgramsUsed = sessionPrograms,
                SessionProgramsRemaining = Math.Max(0, Capacity.MaxProgramsPerSession - sessionPrograms),
                MaxProgramsPerSession = Capacity.MaxProgramsPerSession,
                ActiveRunsUsed = activeRuns,
                ActiveRunsRemaining = Math.Max(0, Capacity.MaxActiveRunsGlobal - activeRuns),
                MaxActiveRunsGlobal = Capacity.MaxActiveRunsGlobal
            };
        }

        private static long ProgramOrdinal(string programId)
        {
            return OrdinalOf(programId);
        }

        private static long RunOrdinal(string programRunId)
        {
            return OrdinalOf(programRunId);
        }

        private static long OrdinalOf(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return long.MaxValue;
            var separator = identity.LastIndexOf('-');
            long value;
            if (separator < 0 || !long.TryParse(identity.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return long.MaxValue;
            return value;
        }

        /// <summary>当前 UTC 文本；只用于公开事实文本，不参与任何超时判定。</summary>
        private static string UtcText()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }
    }
}
