#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>install/replace 的 effect 前提炼结果；校验、编译与 ABI 全部通过后才有该计划。</summary>
    internal sealed class DebugProgramPlan
    {
        internal DebugProgramInstallRequest InstallRequest;
        internal DebugProgramReplaceRequest ReplaceRequest;
        internal string ProgramId = string.Empty;
        internal string SessionId = string.Empty;
        internal string Label = string.Empty;
        internal string EntryType = string.Empty;
        internal string Source = string.Empty;
        internal string SourceSha256 = string.Empty;
        internal long SourceBytes;
        internal object State;
        internal long StateBytes;
        internal object StateSchema;
        internal long StateSchemaBytes;
        internal long ExpectedRevision;
        internal long ExpectedStateEpoch;
        internal UnityCliDebugProgramDefinition Definition;
        internal Assembly Assembly;
        internal string ArtifactIdentity = string.Empty;
        internal string AssemblyName = string.Empty;
        internal Dictionary<string, ResolvedProgramEntry> Resolved = new Dictionary<string, ResolvedProgramEntry>(StringComparer.Ordinal);
        internal Dictionary<Type, object> WrapperInstances = new Dictionary<Type, object>();
        internal List<UnityCliDebugProgramEntry> Entries = new List<UnityCliDebugProgramEntry>();
        internal List<DebugProgramBindingPlan> Bindings = new List<DebugProgramBindingPlan>();
        internal UnityCliDebugProgramBudget Budget;
    }

    /// <summary>一个 binding 的挂载准备：hook 计划、已编译桥接回调与 exact target 身份。</summary>
    internal sealed class DebugProgramBindingPlan
    {
        internal UnityCliDebugProgramBinding Binding;
        internal MethodHookInstallPlan HookPlan;
        internal MethodHookPreparedCallbacks Prepared;
    }

    /// <summary>run 的 effect 前提炼结果。</summary>
    internal sealed class DebugProgramRunPlan
    {
        internal string ProgramId = string.Empty;
        internal string Entry = string.Empty;
        internal string EntryCategory = string.Empty;
        internal string ExecutionSite = string.Empty;

        /// <summary>声明的 PlayerLoop phase；只有 executionSite 为 player-loop-phase 时非空。</summary>
        internal string Phase = string.Empty;
        internal Dictionary<string, object> Args;
        internal long ArgsBytes;
        internal long DurationMs;
        internal string RunId = string.Empty;
        internal string Target = string.Empty;
        internal UnityCliDebugProgramDefinition Definition;
    }

    /// <summary>stop 的 effect 前提炼结果。</summary>
    internal sealed class DebugProgramRunController
    {
        internal UnityCliDebugProgramRun Run;
        internal UnityCliDebugProgramDefinition Definition;
    }

    /// <summary>remove 的 effect 前提炼结果。</summary>
    internal sealed class DebugProgramDefinitionController
    {
        internal UnityCliDebugProgramDefinition Definition;
    }

    /// <summary>一次编译的产物事实；identity 用于共享编译预算记账。</summary>
    internal sealed class DebugProgramCompilation
    {
        internal Assembly Assembly;
        internal string Identity = string.Empty;
        internal string AssemblyName = string.Empty;
        internal long Bytes;
    }

    /// <summary>
    /// debug program 域的唯一运行时 owner。
    ///
    /// 职责：
    /// - 持有本 domain 唯一的 program 定义表与其解析后的 entry/ABI 绑定；
    /// - 把 engine-free 失败原因归约为稳定错误码、details 与可执行 nextActions；
    /// - 用唯一下的动态编译 owner 编译 program 源码与 binding 桥接回调；
    /// - 把 program 拥有的活动资源登记到唯一 session owner，并注册有序清理条目；
    /// - 把 ProgramRun 生命周期与 step driver 交给唯一中央 scheduler 推进。
    ///
    /// 规则：
    /// - 定义只属于一个 sessionId；owner 永不从选择、上次调用或 CLI 进程推断。
    /// - 读取型调用不编译、不改变 Unity 状态；install 不激活任何 listener。
    /// - 本类不注册任何 update 回调，也不持有线程或文件句柄。
    /// </summary>
    internal static partial class DebugProgramHost
    {
        private const int MaxDiagnosticBytes = 512;
        private const string BindingLabelPrefix = "program-binding:";
        private static readonly object Sync = new object();
        private static UnityCliDebugProgramRegistry registry;
        private static string domainGeneration = string.Empty;
        private static string observedRuntimeId = string.Empty;
        private static readonly Dictionary<string, Dictionary<string, ResolvedProgramEntry>> ResolvedEntries = new Dictionary<string, Dictionary<string, ResolvedProgramEntry>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<Type, object>> WrapperSets = new Dictionary<string, Dictionary<Type, object>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, DebugProgramBindingPlan>> PreparedBindings = new Dictionary<string, Dictionary<string, DebugProgramBindingPlan>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ActiveResourceByProgram = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>本 domain 的 program 上限来源。</summary>
        internal static UnityCliDebugProgramCapacity Capacity()
        {
            return new UnityCliDebugProgramCapacity(
                DebugLimits.MaxProgramsPerSession,
                DebugLimits.MaxProgramsGlobal,
                DebugLimits.MaxActiveProgramRunsGlobal,
                DebugLimits.MaxProgramSourceBytes,
                DebugLimits.MaxProgramEntries,
                DebugLimits.MaxProgramBindings,
                DebugLimits.MaxProgramStateBytes,
                DebugLimits.MaxProgramArgsBytes,
                DebugLimits.MaxProgramRunMaxDurationMs,
                DebugLimits.ProgramListDefaultLimit,
                DebugLimits.ProgramListMaxLimit);
        }

        /// <summary>当前 domain 身份；Domain Reload 后必然变化。</summary>
        internal static string DomainGeneration()
        {
            return UnityCliContinuationToken.Fingerprint(UnityCliBridge.ReadGenerationIdentity());
        }

        /// <summary>
        /// 本 domain 唯一定义表。域身份变化时一次性作废上一代全部 program 真值与调度状态；
        /// Play 运行段变化时保持定义但把全部非终态运行置为 interrupted，并清空调度状态。
        /// 程序定义不跨 Domain Reload 存活，重启后必须由调用方重新 install。
        /// </summary>
        internal static UnityCliDebugProgramRegistry Registry()
        {
            lock (Sync)
            {
                var generation = DomainGeneration();
                var runtimeId = DebugSessionHost.RuntimeId();
                if (registry != null && string.Equals(domainGeneration, generation, StringComparison.Ordinal))
                {
                    if (!string.Equals(observedRuntimeId, runtimeId, StringComparison.Ordinal))
                    {
                        observedRuntimeId = runtimeId;
                        var interrupted = registry.InterruptActiveRuns("the Play runtime segment changed");
                        DebugScheduler.Reset();
                        ActiveResourceByProgram.Clear();
                        if (interrupted > 0)
                            UnityCliBridge.WriteLog("debug_program_runtime_interrupted count=" + interrupted.ToString(CultureInfo.InvariantCulture));
                    }
                    return registry;
                }
                registry = new UnityCliDebugProgramRegistry(Capacity());
                domainGeneration = generation;
                observedRuntimeId = runtimeId;
                ResolvedEntries.Clear();
                WrapperSets.Clear();
                PreparedBindings.Clear();
                ActiveResourceByProgram.Clear();
                SessionStopStepsRegistered.Clear();
                DebugScheduler.Reset();
                return registry;
            }
        }

        // ------------------------------------------------------------------
        // install
        // ------------------------------------------------------------------

        /// <summary>
        /// install 的 effect 前完整准备：输入校验、预算、源码读取、唯一编译 owner 编译、ABI 解析与
        /// binding 准备。任何一步失败都在 effect barrier 之前抛出，不产生定义、不激活 listener。
        /// </summary>
        internal static async ValueTask<DebugProgramPlan> PrepareInstallAsync(DebugProgramInstallRequest request, IUnityCliWaiter waiter, TimeSpan timeout, CancellationToken ct)
        {
            RequireInstallShape(request);
            var session = Registry();
            RequireOpenSession(request.SessionId);
            var label = request.Label.Trim();
            RequireLabel(label);
            var source = ReadSource(request.Source, request.File, out var sourceBytes);
            var stateBytes = request.State == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(request.State));
            var stateSchemaBytes = request.StateSchema == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(request.StateSchema));
            if (stateBytes > DebugLimits.MaxProgramStateBytes || stateSchemaBytes > DebugLimits.MaxProgramStateBytes) throw StateTooLarge(Math.Max(stateBytes, stateSchemaBytes));
            var entries = NormalizeEntries(request.Entries);
            var bindings = NormalizeBindings(request.Bindings, entries);
            UnityCliDebugProgramBudget budget;
            var failure = session.CanInstall(request.SessionId, label, sourceBytes, out budget);
            if (failure != null) throw InstallRejection(failure, label, request.SessionId, sourceBytes, budget);
            var programId = session.PeekProgramId();
            var compilation = await CompileAsync(programId, source, "program", DebugLimits.MaxProgramSourceBytes, waiter, timeout, ct);
            var plan = new DebugProgramPlan
            {
                InstallRequest = request,
                ProgramId = programId,
                SessionId = request.SessionId,
                Label = label,
                EntryType = request.EntryType,
                Source = source,
                SourceSha256 = SourceHash(source),
                SourceBytes = sourceBytes,
                State = request.State,
                StateBytes = stateBytes,
                StateSchema = request.StateSchema,
                StateSchemaBytes = stateSchemaBytes,
                Assembly = compilation.Assembly,
                ArtifactIdentity = compilation.Identity,
                AssemblyName = compilation.AssemblyName,
                Entries = entries,
                Bindings = bindings,
                Budget = budget
            };
            await ResolveAndPrepareBindingsAsync(plan, session, waiter, timeout, ct);
            return plan;
        }

        /// <summary>install 的唯一 effect：建立 dormant 定义并记录已解析的 entry 与已准备的 binding。</summary>
        internal static DebugProgramInstallResult ApplyInstall(DebugProgramPlan plan)
        {
            if (plan == null) throw SchemaInvalid("install plan", "a completed install preparation", "missing");
            var session = Registry();
            var canonical = DomainGeneration();
            if (!string.Equals(canonical, domainGeneration, StringComparison.Ordinal))
                throw new BridgeCommandException(
                    "E_DEBUG_PROGRAM_DOMAIN_CHANGED",
                    "The Unity domain changed between preparation and commit, so the prepared program was discarded.",
                    new Dictionary<string, object> { { "programId", plan.ProgramId } },
                    new[] { "Submit the same debug.program-install again in the current domain." });
            var outcome = session.Install(
                plan.SessionId,
                plan.Label,
                plan.EntryType,
                plan.Source,
                plan.SourceSha256,
                plan.SourceBytes,
                plan.State,
                plan.StateBytes,
                plan.StateSchema,
                plan.StateSchemaBytes,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                plan.Entries,
                plan.Bindings.Select(item => item.Binding).ToList());
            if (!outcome.Success) throw InstallRejection(outcome.Failure, plan.Label, plan.SessionId, plan.SourceBytes, outcome.Budget);
            StoreResolved(plan);
            AppendLifecycle(outcome.Definition, null, "installed", "dormant; no listener was activated");
            return new DebugProgramInstallResult
            {
                ProgramId = outcome.Definition.ProgramId,
                SessionId = outcome.Definition.SessionId,
                Label = outcome.Definition.Label,
                Revision = outcome.Definition.Revision,
                State = outcome.Definition.State,
                SourceSha256 = outcome.Definition.SourceSha256,
                SourceBytes = outcome.Definition.SourceBytes,
                StateEpoch = outcome.Definition.StateEpoch,
                StateBytes = outcome.Definition.StateBytes,
                EntryType = outcome.Definition.EntryType,
                Entries = outcome.Definition.Entries.Select(SummarizeEntry).ToList(),
                Bindings = outcome.Definition.Bindings.Select(SummarizeBinding).ToList(),
                BindingsValidated = true,
                BindingsAttached = 0L,
                Installed = true,
                Dormant = true,
                AssemblyName = outcome.Definition.AssemblyName,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = canonical,
                Budget = BudgetView(outcome.Budget),
                Note = "Installation only prepared bindings and compiled the source. No detour is attached, no listener is active and no business code ran; the program stays dormant until debug.program-run activates it."
            };
        }

        // ------------------------------------------------------------------
        // replace
        // ------------------------------------------------------------------

        /// <summary>
        /// replace 的 effect 前完整准备。顺序固定：revision/预算检查 → 编译候选 → ABI 与全部 binding 校验
        /// → 新目标 attachment 准备。失败时旧配置保持不变。
        /// </summary>
        internal static async ValueTask<DebugProgramPlan> PrepareReplaceAsync(DebugProgramReplaceRequest request, IUnityCliWaiter waiter, TimeSpan timeout, CancellationToken ct)
        {
            RequireReplaceShape(request);
            var session = Registry();
            var definition = session.Find(request.ProgramId);
            if (definition == null) throw MissingProgram(request.ProgramId);
            RequireOpenSession(definition.SessionId);
            var label = request.Label == null ? string.Empty : request.Label.Trim();
            if (label.Length == 0) label = definition.Label;
            RequireLabel(label);
            var source = ReadSource(request.Source, request.File, out var sourceBytes);
            var stateBytes = request.State == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(request.State));
            var stateSchemaBytes = request.StateSchema == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(request.StateSchema));
            string state;
            var failure = session.CanReplace(request.ProgramId, request.ExpectedRevision, sourceBytes, stateBytes, stateSchemaBytes, out state);
            if (failure != null) throw ReplaceRejection(failure, definition, request.ExpectedRevision, state, sourceBytes, session.Budget(definition.SessionId));
            var entries = NormalizeEntries(request.Entries);
            var bindings = NormalizeBindings(request.Bindings, entries);
            var compilation = await CompileAsync(definition.ProgramId, source, "program-replace", DebugLimits.MaxProgramSourceBytes, waiter, timeout, ct);
            var plan = new DebugProgramPlan
            {
                ReplaceRequest = request,
                ProgramId = definition.ProgramId,
                SessionId = definition.SessionId,
                Label = label,
                EntryType = definition.EntryType,
                Source = source,
                SourceSha256 = SourceHash(source),
                SourceBytes = sourceBytes,
                State = request.State,
                StateBytes = stateBytes,
                StateSchema = request.StateSchema,
                StateSchemaBytes = stateSchemaBytes,
                ExpectedRevision = request.ExpectedRevision,
                ExpectedStateEpoch = definition.StateEpoch,
                Definition = definition,
                Assembly = compilation.Assembly,
                ArtifactIdentity = compilation.Identity,
                AssemblyName = compilation.AssemblyName,
                Entries = entries,
                Bindings = bindings,
                Budget = session.Budget(definition.SessionId)
            };
            await ResolveAndPrepareBindingsAsync(plan, session, waiter, timeout, ct);
            return plan;
        }

        /// <summary>
        /// replace 的唯一 effect：CAS 发布不可变 revision，然后撤除不再属于候选的旧 binding。
        /// CAS 失败保留旧配置；旧在途调用继续使用旧 revision 与其状态。
        /// </summary>
        internal static DebugProgramReplaceResult ApplyReplace(DebugProgramPlan plan)
        {
            if (plan == null) throw SchemaInvalid("replace plan", "a completed replace preparation", "missing");
            var session = Registry();
            var previous = session.Find(plan.ProgramId);
            if (previous == null) throw MissingProgram(plan.ProgramId);
            var previousBindings = previous.Bindings.ToList();
            var outcome = session.PublishRevision(
                plan.ProgramId,
                plan.ExpectedRevision,
                plan.ExpectedStateEpoch,
                plan.Source,
                plan.SourceSha256,
                plan.SourceBytes,
                plan.Entries,
                plan.Bindings.Select(item => item.Binding).ToList(),
                plan.State,
                plan.StateBytes,
                plan.StateSchema,
                plan.StateSchemaBytes,
                plan.ArtifactIdentity,
                plan.AssemblyName);
            if (!outcome.Success)
            {
                AppendLifecycle(previous, null, "replace-refused", outcome.Failure);
                throw ReplaceRejection(outcome.Failure, previous, plan.ExpectedRevision, previous.State, plan.SourceBytes, outcome.Budget);
            }
            var current = session.Find(plan.ProgramId);
            foreach (var old in previousBindings)
            {
                if (current.Bindings.Any(candidate => string.Equals(candidate.BindingId, old.BindingId, StringComparison.Ordinal))) continue;
                DetachBinding(current.ProgramId, old, "replaced");
            }
            StoreResolved(plan);
            AppendLifecycle(current, null, "replaced", "revision " + current.Revision.ToString(CultureInfo.InvariantCulture));
            return new DebugProgramReplaceResult
            {
                ProgramId = current.ProgramId,
                SessionId = current.SessionId,
                OldRevision = outcome.OldRevision,
                NewRevision = current.Revision,
                StateEpoch = current.StateEpoch,
                State = current.State,
                Published = true,
                OldInFlightCalls = outcome.OldInFlightCalls,
                StateBytes = current.StateBytes,
                Entries = current.Entries.Select(SummarizeEntry).ToList(),
                Bindings = current.Bindings.Select(SummarizeBinding).ToList(),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DomainGeneration(),
                Budget = BudgetView(session.Budget(current.SessionId)),
                Note = "The new revision is published as one immutable configuration. Calls already in flight keep using the previous revision and its state, and their writes are not merged into the new state."
            };
        }

        // ------------------------------------------------------------------
        // run
        // ------------------------------------------------------------------

        /// <summary>run 的 effect 前校验：定义状态、entry、执行点、时长与全局活动运行预算。</summary>
        internal static DebugProgramRunPlan PrepareRun(DebugProgramRunRequest request)
        {
            RequireRunShape(request);
            var session = Registry();
            var definition = session.Find(request.ProgramId);
            if (definition == null) throw MissingProgram(request.ProgramId);
            RequireOpenSession(definition.SessionId);
            var entry = definition.FindEntry(request.Entry);
            if (entry == null) throw EntryMissing(definition, request.Entry);
            if (!UnityCliDebugProgramExecutionSites.IsDefined(request.ExecutionSite)) throw SiteInvalid(request.ExecutionSite, definition);
            // PlayerLoop phase 只在 Editor Play 下存在：Edit 模式明确拒绝，绝不用 editor tick 冒充；
            // phase 名称必须属于封闭集合，非法名称在安装之前就失败。
            var phase = string.Empty;
            if (string.Equals(request.ExecutionSite, UnityCliDebugProgramExecutionSites.PlayerLoopPhase, StringComparison.Ordinal))
            {
                DebugPlayerLoopHost.RequirePlayDomain(
                    request.ExecutionSite,
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "programId", definition.ProgramId },
                        { "entry", request.Entry },
                        { "executionSite", request.ExecutionSite },
                        { "availableSites", UnityCliDebugProgramExecutionSites.All() },
                        { "availableTriggers", DebugTriggerSources.All() }
                    });
                if (!DebugPlayerLoopPhases.IsDefined(request.Phase)) throw DebugPlayerLoopHost.PhaseUnknown("$.phase", request.Phase);
                phase = request.Phase;
            }
            else if (!string.IsNullOrEmpty(request.Phase))
            {
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field phase is only meaningful together with executionSite player-loop-phase, so this run was refused before any effect.",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "path", "$.phase" },
                        { "expected", "omitted unless executionSite is player-loop-phase" },
                        { "actual", request.Phase },
                        { "executionSite", request.ExecutionSite ?? string.Empty }
                    },
                    new[] { "Omit phase, or set executionSite player-loop-phase and pass executionSite's phase explicitly." });
            }
            if (request.DurationMs < 1L || request.DurationMs > DebugLimits.MaxProgramRunMaxDurationMs) throw DurationInvalid(request.DurationMs);
            var argsBytes = request.Args == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(request.Args));
            if (argsBytes > DebugLimits.MaxProgramArgsBytes) throw ArgsTooLarge(argsBytes);
            string state;
            var failure = session.CanActivate(definition.ProgramId, out state);
            if (failure != null) throw RunRejection(failure, definition, state, request.ExecutionSite, session.Budget(definition.SessionId));
            RequireRunSiteCategory(definition, entry, request.ExecutionSite);
            RequireSignalExecutionPoint(definition, entry, request.ExecutionSite);
            if (session.ActiveRunsFor(definition.ProgramId) > 0)
                throw RunRejection(UnityCliDebugProgramFailures.RunActive, definition, state, request.ExecutionSite, session.Budget(definition.SessionId));
            return new DebugProgramRunPlan
            {
                ProgramId = definition.ProgramId,
                Entry = entry.Name,
                EntryCategory = entry.Category,
                ExecutionSite = request.ExecutionSite,
                Phase = phase,
                Args = request.Args,
                ArgsBytes = argsBytes,
                DurationMs = request.DurationMs,
                RunId = request.RunId,
                Target = TargetText(entry, definition, request.ExecutionSite, phase),
                Definition = definition
            };
        }

        /// <summary>
        /// run 的唯一 effect：登记活动资源、激活声明的 bindings、排定运行并建立唯一 step driver。
        /// 返回 scheduled 或 running 的实际状态；这不代表业务目标已经完成。
        /// </summary>
        internal static DebugProgramRunResult ApplyRun(DebugProgramRunPlan plan)
        {
            if (plan == null) throw SchemaInvalid("run plan", "a completed run preparation", "missing");
            var session = Registry();
            var definition = session.Find(plan.ProgramId);
            if (definition == null) throw MissingProgram(plan.ProgramId);
            // 声明 PlayerLoop phase 的运行先建立该 phase 现场：安装失败时本次运行还没有任何登记，
            // 因此不存在需要回滚的半个运行、binding 或资源槽位。
            if (string.Equals(plan.ExecutionSite, UnityCliDebugProgramExecutionSites.PlayerLoopPhase, StringComparison.Ordinal))
            {
                var installFailure = DebugPlayerLoopHost.Install(plan.Phase, ProgramPhaseOwner(definition.ProgramId));
                if (installFailure != null) throw DebugPlayerLoopHost.Unavailable(plan.Phase, installFailure);
            }
            var nowMonotonic = DebugEventClocks.MonotonicMilliseconds;
            var outcome = session.ScheduleRun(
                plan.ProgramId,
                plan.Entry,
                plan.EntryCategory,
                plan.ExecutionSite,
                plan.Target,
                plan.RunId,
                plan.Args,
                plan.ArgsBytes,
                plan.DurationMs,
                UtcMilliseconds(),
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                nowMonotonic);
            if (!outcome.Success) throw RunRejection(outcome.Failure, definition, definition.State, plan.ExecutionSite, outcome.Budget);
            var run = outcome.Run;
            // run 资源清理会关闭运行并撤除 detour，因此必须在主线程上完成；
            // 主线程暂停期间由控制通道服务的 stop 必须推迟它，而不是在后台线程上执行。
            var reservation = DebugSessionHost.ReserveResource(definition.SessionId, UnityCliDebugResourceKinds.Run, plan.Target, report => CloseProgramOnCleanup(definition.ProgramId, report),
                true);
            if (!reservation.Success)
            {
                session.RequestCancellation(run.ProgramRunId, "active resource slot refused");
                throw new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The session has no free active resource slot for this program run, so no run was activated.",
                    new Dictionary<string, object>
                    {
                        { "limit", "active-resources" },
                        { "used", (long)reservation.ActiveResourcesUsed },
                        { "max", (long)DebugLimits.MaxActiveResourcesPerSession },
                        { "programId", definition.ProgramId }
                    },
                    new[]
                    {
                        "Run tools run debug.program-stop for a run you no longer need, then submit this run again.",
                        "Run tools run debug.session-info on the owning session to read its active resources."
                    });
            }
            DebugSessionHost.AttachResource(reservation.Resource.ResourceId);
            lock (Sync) ActiveResourceByProgram[definition.ProgramId] = reservation.Resource.ResourceId;
            RegisterProgramCleanupStep(definition);
            var attached = AttachBindings(definition);
            session.MarkActive(definition.ProgramId);
            var driverPlanted = false;
            if (string.Equals(plan.ExecutionSite, UnityCliDebugProgramExecutionSites.EditorTick, StringComparison.Ordinal)
                || string.Equals(plan.ExecutionSite, UnityCliDebugProgramExecutionSites.PlayerLoopPhase, StringComparison.Ordinal))
            {
                // 声明 PlayerLoop phase 的运行由中央框架先建立该 phase 现场（已在本次 effect 开头完成）：
                // 安装失败时本运行不被激活，也不留下半个定义。
                var failure = DebugScheduler.PlantDriver(definition.ProgramId, run.ProgramRunId, plan.Entry, run.Revision, nowMonotonic, run.DeadlineMonotonicMs, plan.Phase);
                if (failure != null)
                {
                    session.RequestCancellation(run.ProgramRunId, failure);
                    ReleaseRunResource(definition.ProgramId);
                    throw new BridgeCommandException(
                        "E_DEBUG_PROGRAM_DRIVER_CONFLICT",
                        "This program already owns one continuous step driver, so a second driver was refused before any effect.",
                        new Dictionary<string, object> { { "programId", definition.ProgramId }, { "reason", failure } },
                        new[]
                        {
                            "Run tools run debug.program-stop for the active run before starting another driver.",
                            "Run tools run debug.program-info to read the active run of this program."
                        });
                }
                session.MarkStepDriverPlanted(definition.ProgramId, true);
                driverPlanted = true;
            }
            // method/listener 运行是绑定激活窗口：它保持 scheduled，直到 exact target 真正命中或投递真正到达时
            // 才由那次执行当场取得执行权。激活本身不是同步代码在途，因此这里不得提前进入 running；
            // 目标始终不到达时由声明时长关闭为 cancelled，绝不留下永远 running/inFlight 的运行。
            AppendLifecycle(definition, run, "run-scheduled", plan.ExecutionSite + " / " + plan.Target);
            return new DebugProgramRunResult
            {
                ProgramRunId = run.ProgramRunId,
                ProgramId = definition.ProgramId,
                SessionId = definition.SessionId,
                Entry = run.Entry,
                EntryCategory = run.EntryCategory,
                State = run.State,
                Revision = run.Revision,
                ExecutionSite = run.ExecutionSite,
                Target = run.Target,
                RunId = run.RunId,
                DurationMs = run.DurationMs,
                DeadlineMonotonicMs = run.DeadlineMonotonicMs,
                StepDriver = driverPlanted,
                Scheduled = true,
                Running = string.Equals(run.State, UnityCliDebugProgramRunStates.Running, StringComparison.Ordinal),
                Terminal = run.IsTerminal,
                BindingsAttached = attached,
                Scheduler = DebugScheduler.Describe(),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DomainGeneration(),
                Budget = BudgetView(session.Budget(definition.SessionId)),
                Note = "A scheduled or running program does not mean the business goal succeeded. Terminal is false while the run is waiting for its execution point or executing it; only a terminal run plus a trusted business result shows what the entry actually did."
            };
        }

        // ------------------------------------------------------------------
        // wait / info / list
        // ------------------------------------------------------------------

        /// <summary>wait：只观察一次已存在的运行，可选有界等待；不重新执行、不创建运行。</summary>
        internal static async ValueTask<DebugProgramWaitResult> WaitAsync(DebugProgramWaitRequest request, IUnityCliWaiter waiter, CancellationToken ct)
        {
            RequireWaitShape(request);
            var session = Registry();
            var run = session.FindRun(request.ProgramRunId);
            if (run == null) throw MissingRun(request.ProgramRunId);
            var waitMs = request.WaitMs ?? 0;
            if (waitMs < 0 || waitMs > DebugLimits.ProgramWaitMaxMs)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "waitMs must be inside the declared wait budget.",
                    new Dictionary<string, object>
                    {
                        { "path", "$.waitMs" },
                        { "expected", "0.." + DebugLimits.ProgramWaitMaxMs.ToString(CultureInfo.InvariantCulture) },
                        { "actual", waitMs.ToString(CultureInfo.InvariantCulture) }
                    },
                    new[] { "Pass a smaller waitMs, or omit it to read the current facts without waiting." });
            var deadline = DebugEventClocks.MonotonicMilliseconds + waitMs;
            var waited = false;
            while (!run.IsTerminal && DebugEventClocks.MonotonicMilliseconds < deadline && !ct.IsCancellationRequested)
            {
                waited = true;
                await waiter.NextFrame(1, ct);
            }
            return new DebugProgramWaitResult
            {
                ProgramRunId = run.ProgramRunId,
                ProgramId = run.ProgramId,
                SessionId = run.SessionId,
                Entry = run.Entry,
                State = run.State,
                Terminal = run.IsTerminal,
                Revision = run.Revision,
                ExecutionSite = run.ExecutionSite,
                Target = run.Target,
                DurationMs = run.DurationMs,
                StartedAtUtc = run.StartedAtUtc,
                CompletedAtUtc = run.CompletedAtUtc,
                DeadlineMonotonicMs = run.DeadlineMonotonicMs,
                StepCount = run.StepCount,
                CheckCount = run.CheckCount,
                DeliveryCount = run.DeliveryCount,
                CancellationRequested = run.CancellationRequested,
                InFlight = run.InFlight,
                ResultTrust = run.ResultTrust,
                Result = BoundedValue(run.Result),
                LastTrustedResultAvailable = !string.Equals(run.ResultTrust, "none", StringComparison.Ordinal),
                StepRevisions = new List<long>(run.StepRevisions),
                FailureCode = run.FailureCode,
                FailureMessage = UnityCliBridge.BoundedText(run.FailureMessage, MaxDiagnosticBytes),
                Waited = waited,
                WaitMs = waitMs,
                Scheduler = DebugScheduler.Describe(),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DomainGeneration(),
                Budget = BudgetView(session.Budget(run.SessionId)),
                Note = "wait only observes this programRunId. It never re-executes the entry; a non-terminal state means the run is still pending or still in flight."
            };
        }

        /// <summary>info：读取一个定义的完整事实；不编译、不激活、不改变 Unity 状态。</summary>
        internal static DebugProgramInfoResult Info(DebugProgramInfoRequest request)
        {
            RequireInfoShape(request);
            var session = Registry();
            var definition = session.Find(request.ProgramId);
            if (definition == null) throw MissingProgram(request.ProgramId);
            return new DebugProgramInfoResult
            {
                Program = SummarizeProgram(definition),
                ActiveRuns = session.RunsFor(definition.ProgramId).Where(run => !run.IsTerminal).Select(SummarizeRun).ToList(),
                Driver = SummarizeDriver(definition.ProgramId),
                Scheduler = DebugScheduler.Describe(),
                CompiledArtifacts = DebugCompilerOwner.Budget().ToData(),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                RuntimeId = DebugSessionHost.RuntimeId(),
                DomainGeneration = DomainGeneration(),
                Budget = BudgetView(session.Budget(definition.SessionId)),
                Note = "info reads source hash, revision, bindings, active invocations and occupancy from the live owner. It does not compile, activate or restore anything."
            };
        }

        /// <summary>list：按 program 序号分页读取定义摘要；不编译、不改变 Unity 状态。</summary>
        internal static DebugProgramListResult List(DebugProgramListRequest request)
        {
            var session = Registry();
            var definitions = string.IsNullOrEmpty(request == null ? null : request.SessionId)
                ? session.Programs().ToList()
                : session.ProgramsFor(request.SessionId).ToList();
            var editorSessionId = UnityCliBridge.EditorSessionId();
            var scope = UnityCliContinuationToken.Fingerprint(editorSessionId + "\0debug.program-list");
            var offset = 0;
            if (request != null && !string.IsNullOrEmpty(request.Cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(request.Cursor, scope, editorSessionId, out position)
                    || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
                    || offset < 0 || offset > definitions.Count)
                    throw new BridgeCommandException(
                        "E_CONTINUATION_STALE",
                        "The program list continuation does not match the current Editor session or the loaded definition table.",
                        null,
                        new[] { "Start a new debug.program-list read without a cursor." });
            }
            var limit = request == null || request.Limit == null
                ? DebugLimits.ProgramListDefaultLimit
                : Math.Min(Math.Max(1, request.Limit.Value), DebugLimits.ProgramListMaxLimit);
            var page = new List<DebugProgramSummaryView>();
            for (var index = offset; index < definitions.Count && page.Count < limit; index++) page.Add(SummarizeProgram(definitions[index]));
            var nextOffset = offset + page.Count;
            return new DebugProgramListResult
            {
                Items = page,
                NextCursor = nextOffset < definitions.Count ? UnityCliContinuationToken.Create(scope, editorSessionId, nextOffset.ToString(CultureInfo.InvariantCulture)) : null,
                Total = definitions.Count,
                NextOffset = nextOffset,
                HasMore = nextOffset < definitions.Count,
                Scheduler = DebugScheduler.Describe(),
                EditorSessionId = editorSessionId,
                DomainGeneration = DomainGeneration(),
                Budget = BudgetView(session.Budget(request == null ? null : request.SessionId)),
                Note = "list is a read-only page of program definitions. It never recompiles source and never activates a listener."
            };
        }

        // ------------------------------------------------------------------
        // stop
        // ------------------------------------------------------------------

        /// <summary>stop 的 effect 前校验；未知运行在这里失败。</summary>
        internal static DebugProgramRunController PrepareStop(DebugProgramStopRequest request)
        {
            RequireStopShape(request);
            var session = Registry();
            var run = session.FindRun(request.ProgramRunId);
            if (run == null) throw MissingRun(request.ProgramRunId);
            return new DebugProgramRunController { Run = run, Definition = session.Find(run.ProgramId) };
        }

        /// <summary>
        /// stop 的唯一 effect：取消尚未执行的步骤、协作停止、释放该运行拥有的资源。
        /// 它不删除定义，也不触碰该 program 的其他运行。
        /// </summary>
        internal static DebugProgramStopResult ApplyStop(DebugProgramRunController controller)
        {
            var session = Registry();
            var run = controller.Run;
            var definition = session.Find(run.ProgramId);
            var alreadyTerminal = run.IsTerminal;
            var pendingClosed = session.RequestCancellation(run.ProgramRunId, "debug.program-stop requested a cooperative stop");
            var cleanupRan = false;
            if (alreadyTerminal)
            {
                // 已完成运行：不重放清理，只报告当前事实。
            }
            else if (!pendingClosed)
            {
                // 同步代码已经在途：只能请求取消并报告仍在途，终态仍等原代码返回。
                DebugScheduler.ResumeWaits(run.Entry);
            }
            else
            {
                cleanupRan = RunCleanupEntry(definition, run, "stop");
                DebugScheduler.CloseDriver(run.ProgramId);
                session.MarkStepDriverPlanted(run.ProgramId, false);
                session.MarkCodeReturned(run.ProgramRunId);
                session.CompleteRun(run.ProgramRunId, UnityCliDebugProgramRunStates.Cancelled, "E_DEBUG_PROGRAM_RUN_CANCELLED", "debug.program-stop closed the future steps of this run.", UtcMilliseconds());
                DetachBindings(definition);
                ReleaseRunResource(run.ProgramId);
                var remaining = session.RunsFor(run.ProgramId).Any(candidate => !candidate.IsTerminal);
                if (!remaining) session.CloseFutureDispatch(run.ProgramId, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), DebugEventClocks.MonotonicMilliseconds);
            }
            var latest = session.FindRun(run.ProgramRunId) ?? run;
            AppendLifecycle(definition, latest, "stop", pendingClosed ? "pending-run-closed" : "cancellation-requested-in-flight");
            return new DebugProgramStopResult
            {
                ProgramRunId = latest.ProgramRunId,
                ProgramId = latest.ProgramId,
                State = latest.State,
                Terminal = latest.IsTerminal,
                Changed = pendingClosed || alreadyTerminal,
                CancellationRequested = latest.CancellationRequested,
                InFlight = latest.InFlight,
                NotYetExecutedStepsCancelled = pendingClosed || alreadyTerminal,
                CleanupRan = cleanupRan,
                DefinitionDeleted = false,
                Driver = SummarizeDriver(latest.ProgramId),
                OtherRunsUntouched = session.RunsFor(latest.ProgramId).Count(candidate => !string.Equals(candidate.ProgramRunId, latest.ProgramRunId, StringComparison.Ordinal)),
                Scheduler = DebugScheduler.Describe(),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DomainGeneration(),
                Budget = BudgetView(session.Budget(latest.SessionId)),
                Note = "stop closes the future steps of exactly this programRunId and releases the resources that run owns. The program definition and every other run are left untouched."
            };
        }

        // ------------------------------------------------------------------
        // remove
        // ------------------------------------------------------------------

        /// <summary>remove 的 effect 前校验；活动运行或仍在途调用在这里被拒绝并给出当前事实。</summary>
        internal static DebugProgramDefinitionController PrepareRemove(DebugProgramRemoveRequest request)
        {
            RequireRemoveShape(request);
            var session = Registry();
            var definition = session.Find(request.ProgramId);
            if (definition == null) throw MissingProgram(request.ProgramId);
            string state;
            int activeRuns;
            int attachedBindings;
            var failure = session.CanRemove(request.ProgramId, out state, out activeRuns, out attachedBindings);
            if (failure != null) throw RemoveRejection(failure, definition, state, activeRuns, attachedBindings, session.Budget(definition.SessionId));
            return new DebugProgramDefinitionController { Definition = definition };
        }

        /// <summary>
        /// remove 的唯一 effect：撤除该定义的全部 binding，删除定义与运行记录。
        /// 仍然加载且无法卸载的程序集必须在结果里如实报告。
        /// </summary>
        internal static DebugProgramRemoveResult ApplyRemove(DebugProgramDefinitionController controller)
        {
            var session = Registry();
            var definition = controller.Definition;
            var detached = DetachBindings(definition);
            ReleaseRunResource(definition.ProgramId);
            var outcome = session.Remove(definition.ProgramId);
            if (!outcome.Success) throw RemoveRejection(outcome.Failure, definition, definition.State, 0, detached, outcome.Budget);
            lock (Sync)
            {
                ResolvedEntries.Remove(definition.ProgramId);
                WrapperSets.Remove(definition.ProgramId);
                PreparedBindings.Remove(definition.ProgramId);
            }
            var compiled = DebugCompilerOwner.Budget();
            return new DebugProgramRemoveResult
            {
                ProgramId = definition.ProgramId,
                SessionId = definition.SessionId,
                Removed = true,
                DetachedBindings = detached,
                RevocableRoutesReleased = detached,
                LoadedNonUnloadableAssemblies = compiled.Count,
                LoadedNonUnloadableBytes = compiled.Bytes,
                Unloadable = false,
                Scheduler = DebugScheduler.Describe(),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DomainGeneration(),
                Budget = BudgetView(outcome.Budget),
                Note = "The definition and its revocable routes are gone. Assemblies already loaded in this domain cannot be unloaded; only a Domain Reload reclaims them, and those bytes keep counting against the shared compile budget."
            };
        }
    }
}
#endif
