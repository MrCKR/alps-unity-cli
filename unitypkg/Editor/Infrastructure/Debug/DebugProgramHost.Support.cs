#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// program 的输入校验、源码读取、唯一编译 owner 使用、binding 准备与错误映射。
    /// 全部方法都在 effect barrier 之前调用；失败一律抛稳定错误且不改变任何状态。
    /// </summary>
    internal static partial class DebugProgramHost
    {
        // ------------------------------------------------------------------
        // 输入校验
        // ------------------------------------------------------------------

        private static void RequireInstallShape(DebugProgramInstallRequest request)
        {
            if (request == null) throw SchemaInvalid("body", "a debug.program-install request body", "missing");
            RequireSession(request.SessionId);
            RequireEntryType(request.EntryType);
            RequireSourceXorFile(request.Source, request.File);
            RequireEntrySpecs(request.Entries, request.Bindings);
        }

        private static void RequireReplaceShape(DebugProgramReplaceRequest request)
        {
            if (request == null) throw SchemaInvalid("body", "a debug.program-replace request body", "missing");
            RequireProgramId(request.ProgramId);
            if (request.ExpectedRevision < 1L)
                throw SchemaInvalid("expectedRevision", "the current revision of the target program", request.ExpectedRevision.ToString(CultureInfo.InvariantCulture));
            RequireSourceXorFile(request.Source, request.File);
            RequireEntrySpecs(request.Entries, request.Bindings);
        }

        private static void RequireRunShape(DebugProgramRunRequest request)
        {
            if (request == null) throw SchemaInvalid("body", "a debug.program-run request body", "missing");
            RequireProgramId(request.ProgramId);
            if (string.IsNullOrEmpty(request.Entry)) throw SchemaInvalid("entry", "the name of an installed entry", "missing");
            if (string.IsNullOrEmpty(request.ExecutionSite)) throw SchemaInvalid("executionSite", "one of the declared execution sites", "missing");
        }

        private static void RequireWaitShape(DebugProgramWaitRequest request)
        {
            if (request == null) throw SchemaInvalid("body", "a debug.program-wait request body", "missing");
            RequireRunId(request.ProgramRunId);
        }

        private static void RequireInfoShape(DebugProgramInfoRequest request)
        {
            if (request == null) throw SchemaInvalid("body", "a debug.program-info request body", "missing");
            RequireProgramId(request.ProgramId);
        }

        private static void RequireStopShape(DebugProgramStopRequest request)
        {
            if (request == null) throw SchemaInvalid("body", "a debug.program-stop request body", "missing");
            RequireRunId(request.ProgramRunId);
        }

        private static void RequireRemoveShape(DebugProgramRemoveRequest request)
        {
            if (request == null) throw SchemaInvalid("body", "a debug.program-remove request body", "missing");
            RequireProgramId(request.ProgramId);
        }

        private static void RequireProgramId(string programId)
        {
            if (string.IsNullOrEmpty(programId)) throw SchemaInvalid("programId", "the id of an installed program", "missing");
        }

        private static void RequireRunId(string programRunId)
        {
            if (string.IsNullOrEmpty(programRunId)) throw SchemaInvalid("programRunId", "the id of one program run", "missing");
        }

        private static void RequireSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new BridgeCommandException(
                    "E_DEBUG_PROGRAM_SESSION_REQUIRED",
                    "A program definition must belong to an explicit session.",
                    new Dictionary<string, object> { { "path", "$.sessionId" }, { "expected", "the id of an open debug session" } },
                    new[]
                    {
                        "Run tools run debug.session-create to establish the owner of this investigation.",
                        "Run tools run debug.session-list to read the currently open sessions."
                    });
        }

        private static void RequireEntryType(string entryType)
        {
            if (string.Equals(entryType, "code", StringComparison.Ordinal)) return;
            throw SchemaInvalid("entryType", "the single supported program form code", entryType ?? "null");
        }

        private static void RequireSourceXorFile(string source, string file)
        {
            var hasSource = !string.IsNullOrEmpty(source);
            var hasFile = !string.IsNullOrEmpty(file);
            if (hasSource == hasFile)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Exactly one of source or file must be provided.",
                    new Dictionary<string, object> { { "path", "$.source" }, { "expected", "source xor file" }, { "actual", hasSource ? "both" : "neither" } },
                    new[] { "Send inline source, or send the path of a .cs file, but not both." });
        }

        private static void RequireEntrySpecs(List<DebugProgramEntrySpec> entries, List<DebugProgramBindingSpec> bindings)
        {
            if (entries == null || entries.Count == 0)
                throw SchemaInvalid("entries", "at least one named entry", "empty");
            if (entries.Count > DebugLimits.MaxProgramEntries)
                throw SchemaInvalid("entries", DebugLimits.MaxProgramEntries.ToString(CultureInfo.InvariantCulture) + " entries or fewer", entries.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name)) throw SchemaInvalid("entries[].name", "a non-empty entry name", "empty");
                if (!UnityCliDebugProgramEntryCategories.IsDefined(entry.Category))
                    throw SchemaInvalid("entries[].category", "one of " + string.Join(", ", UnityCliDebugProgramEntryCategories.All()), entry.Category ?? "null");
                if (string.IsNullOrEmpty(entry.DeclaringType))
                    throw SchemaInvalid("entries[].declaringType", "the fully qualified type name inside the compiled program", "missing");
            }
            if (bindings == null) return;
            if (bindings.Count > DebugLimits.MaxProgramBindings)
                throw SchemaInvalid("bindings", DebugLimits.MaxProgramBindings.ToString(CultureInfo.InvariantCulture) + " bindings or fewer", bindings.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var binding in bindings)
            {
                if (binding == null) throw SchemaInvalid("bindings[]", "a binding specification", "null");
                if (!UnityCliDebugProgramBindingPhases.IsDefined(binding.Phase))
                    throw SchemaInvalid("bindings[].phase", "prefix, postfix or signal", binding.Phase ?? "null");
                if (string.IsNullOrEmpty(binding.Entry)) throw SchemaInvalid("bindings[].entry", "the name of an installed entry", "empty");
                // signal 阶段可以只声明 framework phase：省略 target 表示投递给中央 tick，而不是一个未绑定的 exact target。
                if (binding.Target == null && !string.Equals(binding.Phase, UnityCliDebugProgramBindingPhases.Signal, StringComparison.Ordinal))
                    throw SchemaInvalid("bindings[].target", "an exact target specification", "missing");
            }
        }

        /// <summary>
        /// 执行点与 entry 类别必须相容：editor-tick 与 player-loop-phase 接受 step 与 oneshot，方法执行点接受 method 与
        /// oneshot，signal 接受 listener 与 oneshot，worker-thread 接受 method 与 oneshot。
        /// player-loop-phase 额外接受 listener：监听式投递需要能在声明的真实 phase 上取得执行权。
        /// cleanup entry 只能由宿主在停止或取消时调用，不能作为运行入口。
        /// </summary>
        private static void RequireRunSiteCategory(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramEntry entry, string executionSite)
        {
            var step = string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.Step, StringComparison.Ordinal);
            var method = string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.Method, StringComparison.Ordinal);
            var listener = string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.Listener, StringComparison.Ordinal);
            var oneShot = string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.OneShot, StringComparison.Ordinal);
            var compatible = string.Equals(executionSite, UnityCliDebugProgramExecutionSites.EditorTick, StringComparison.Ordinal) ? step || oneShot
                : string.Equals(executionSite, UnityCliDebugProgramExecutionSites.PlayerLoopPhase, StringComparison.Ordinal) ? step || listener || oneShot
                : string.Equals(executionSite, UnityCliDebugProgramExecutionSites.Signal, StringComparison.Ordinal) ? listener || oneShot
                : string.Equals(executionSite, UnityCliDebugProgramExecutionSites.WorkerThread, StringComparison.Ordinal) ? method || oneShot
                : method || oneShot;
            if (compatible) return;
            throw new BridgeCommandException(
                "E_DEBUG_PROGRAM_ENTRY_SITE_MISMATCH",
                "The declared execution site cannot run an entry of that category.",
                new Dictionary<string, object>
                {
                    { "programId", definition.ProgramId },
                    { "entry", entry.Name },
                    { "category", entry.Category },
                    { "executionSite", executionSite ?? string.Empty },
                    { "signatures", UnityCliDebugProgramAbi.SignatureTable() }
                },
                new[]
                {
                    "Run a step or oneshot entry at editor-tick, or a step, listener or oneshot entry at player-loop-phase.",
                    "Run a method or oneshot entry at method-prefix, method-postfix or worker-thread.",
                    "Run a listener or oneshot entry at signal. A cleanup entry is only called by the host when a run stops."
                });
        }

        /// <summary>
        /// signal 执行点必须真实存在：投递要么指名一个已绑定的 exact target，要么投递给中央 framework phase。
        /// 指名一个从未绑定的 target 的投递不可能取得执行权，因此必须在 effect 之前拒绝，
        /// 而不是留下一个必然过期的 pending 投递。
        /// </summary>
        private static void RequireSignalExecutionPoint(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramEntry entry, string executionSite)
        {
            if (!string.Equals(executionSite, UnityCliDebugProgramExecutionSites.Signal, StringComparison.Ordinal)) return;
            var binding = definition.Bindings.FirstOrDefault(candidate =>
                string.Equals(candidate.Phase, UnityCliDebugProgramBindingPhases.Signal, StringComparison.Ordinal)
                && string.Equals(candidate.Entry, entry.Name, StringComparison.Ordinal));
            if (binding == null || string.IsNullOrEmpty(binding.TargetKey)) return;
            var boundByThisProgram = definition.Bindings.Any(candidate =>
                !string.Equals(candidate.Phase, UnityCliDebugProgramBindingPhases.Signal, StringComparison.Ordinal)
                && string.Equals(candidate.TargetKey, binding.TargetKey, StringComparison.Ordinal));
            if (boundByThisProgram || MethodHookHost.IsTargetBound(binding.TargetKey)) return;
            throw new BridgeCommandException(
                "E_DEBUG_PROGRAM_SIGNAL_TARGET_UNBOUND",
                "The signal binding names an exact target that no active hook or program binding binds, so a delivery could never take its execution right.",
                new Dictionary<string, object>
                {
                    { "programId", definition.ProgramId },
                    { "entry", entry.Name },
                    { "targetKey", binding.TargetKey },
                    { "targetSummary", binding.TargetSummary ?? string.Empty }
                },
                new[]
                {
                    "Add a prefix or postfix binding of this program on the same target so the exact target is bound.",
                    "Run tools run debug.hook-install on that target to bind it as a Hook execution point.",
                    "Omit bindings[].target for the signal binding to deliver the signal to the central editor-tick phase instead."
                });
        }

        /// <summary>session 必须存在且处于 open：已停止的 session 不再接收新的 program 资源。</summary>
        private static void RequireOpenSession(string sessionId)
        {
            DebugSessionInfoResult info;
            try
            {
                info = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId });
            }
            catch (BridgeCommandException exception)
            {
                throw new BridgeCommandException(
                    "E_DEBUG_PROGRAM_SESSION_UNAVAILABLE",
                    "The session that should own this program does not exist in the current Editor session.",
                    new Dictionary<string, object> { { "sessionId", sessionId ?? string.Empty }, { "sessionError", exception.Code } },
                    new[]
                    {
                        "Run tools run debug.session-list to read the loaded session definitions.",
                        "Run tools run debug.session-create to establish a new owner, then install the program again."
                    });
            }
            var state = info.Session == null ? string.Empty : info.Session.State;
            if (string.Equals(state, UnityCliDebugSessionStates.Open, StringComparison.Ordinal)) return;
            throw new BridgeCommandException(
                "E_DEBUG_PROGRAM_SESSION_STOPPED",
                "A session that is not open cannot accept a new program or a new run.",
                new Dictionary<string, object>
                {
                    { "sessionId", sessionId ?? string.Empty },
                    { "state", state },
                    { "cleanupPending", info.Session != null && info.Session.CleanupPending }
                },
                new[]
                {
                    "Run tools run debug.session-info to read the current state and cleanup detail.",
                    "Run tools run debug.session-create to start a new session for this investigation."
                });
        }

        private static void RequireLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) throw SchemaInvalid("label", "a non-empty label", "empty");
            var bytes = Encoding.UTF8.GetByteCount(label);
            if (bytes > DebugLimits.MaxSessionLabelBytes)
                throw SchemaInvalid("label", DebugLimits.MaxSessionLabelBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes or fewer", bytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes");
        }

        /// <summary>
        /// 读取源码：inline source 或一个 .cs 文件路径。文件只在准备阶段读取一次，
        /// 运行中的程序永不依赖该路径重新读取；设施不会把它写入 Assets、业务 Package 或业务程序集。
        /// </summary>
        private static string ReadSource(string source, string file, out long sourceBytes)
        {
            string text;
            if (!string.IsNullOrEmpty(source))
            {
                text = source;
            }
            else
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    throw new BridgeCommandException(
                        "E_SCHEMA_INVALID",
                        "file must point to a C# source file.",
                        new Dictionary<string, object> { { "path", "$.file" }, { "expected", "a path ending in .cs" }, { "actual", file } },
                        new[] { "Pass the path of a .cs file, or send the source inline." });
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists)
                        throw new BridgeCommandException(
                            "E_DEBUG_PROGRAM_SOURCE_UNREADABLE",
                            "The program source file does not exist.",
                            new Dictionary<string, object> { { "file", file } },
                            new[] { "Check the path of the program source file." });
                    if (info.Length > DebugLimits.MaxProgramSourceBytes)
                        throw SourceTooLarge(info.Length);
                    text = File.ReadAllText(info.FullName, Encoding.UTF8);
                }
                catch (BridgeCommandException) { throw; }
                catch (Exception exception)
                {
                    throw new BridgeCommandException(
                        "E_DEBUG_PROGRAM_SOURCE_UNREADABLE",
                        "The program source file could not be read: " + exception.GetType().Name + ".",
                        new Dictionary<string, object> { { "file", file } },
                        new[] { "Check the file permissions and path, then submit the request again." });
                }
            }
            sourceBytes = Encoding.UTF8.GetByteCount(text);
            if (sourceBytes > DebugLimits.MaxProgramSourceBytes) throw SourceTooLarge(sourceBytes);
            return text;
        }

        private static string SourceHash(string source)
        {
            return UnityCliSerializer.Sha256Canonical(new Dictionary<string, object>(StringComparer.Ordinal) { { "source", source ?? string.Empty } });
        }

        private static List<UnityCliDebugProgramEntry> NormalizeEntries(List<DebugProgramEntrySpec> specs)
        {
            var entries = new List<UnityCliDebugProgramEntry>();
            foreach (var spec in specs)
                entries.Add(new UnityCliDebugProgramEntry { Name = spec.Name, Category = spec.Category, DeclaringType = spec.DeclaringType });
            return entries;
        }

        /// <summary>把声明的 binding 数据转成 registry 事实；挂载计划在编译阶段补齐。</summary>
        private static List<DebugProgramBindingPlan> NormalizeBindings(List<DebugProgramBindingSpec> specs, List<UnityCliDebugProgramEntry> entries)
        {
            var bindings = new List<DebugProgramBindingPlan>();
            if (specs == null) return bindings;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var spec in specs)
            {
                index++;
                var entry = entries.FirstOrDefault(candidate => string.Equals(candidate.Name, spec.Entry, StringComparison.Ordinal));
                if (entry == null)
                    throw new BridgeCommandException(
                        "E_DEBUG_PROGRAM_BINDING_INVALID",
                        "A binding names an entry that the same request does not declare.",
                        new Dictionary<string, object> { { "entry", spec.Entry } },
                        new[] { "Declare every bound entry in entries, or remove the binding." });
                if (string.Equals(spec.Phase, UnityCliDebugProgramBindingPhases.Signal, StringComparison.Ordinal))
                {
                    if (!string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.Listener, StringComparison.Ordinal))
                        throw BindingCategoryMismatch(spec, entry, UnityCliDebugProgramEntryCategories.Listener);
                }
                else if (!string.Equals(entry.Category, UnityCliDebugProgramEntryCategories.Method, StringComparison.Ordinal))
                {
                    throw BindingCategoryMismatch(spec, entry, UnityCliDebugProgramEntryCategories.Method);
                }
                var bindingId = string.IsNullOrEmpty(spec.BindingId) ? "b" + index.ToString(CultureInfo.InvariantCulture) : spec.BindingId;
                if (!seen.Add(bindingId))
                    throw new BridgeCommandException(
                        "E_DEBUG_PROGRAM_BINDING_INVALID",
                        "Two bindings in the same request use the same bindingId.",
                        new Dictionary<string, object> { { "bindingId", bindingId } },
                        new[] { "Give every binding a distinct bindingId." });
                bindings.Add(new DebugProgramBindingPlan
                {
                    Binding = new UnityCliDebugProgramBinding
                    {
                        BindingId = bindingId,
                        Phase = spec.Phase,
                        Entry = spec.Entry,
                        TargetSummary = TargetSummaryOf(spec.Target),
                        // signal 阶段没有 detour，但投递必须能被一个已绑定的 exact target 寻址，因此这里就解析出 target key。
                        TargetKey = string.Equals(spec.Phase, UnityCliDebugProgramBindingPhases.Signal, StringComparison.Ordinal) && spec.Target != null
                            ? MethodHookTargetKey.From(MethodHookTargetResolver.Resolve(spec.Target).Method).Text
                            : string.Empty
                    }
                });
            }
            return bindings;
        }

        private static BridgeCommandException BindingCategoryMismatch(DebugProgramBindingSpec spec, UnityCliDebugProgramEntry entry, string expected)
        {
            return new BridgeCommandException(
                "E_DEBUG_PROGRAM_BINDING_INVALID",
                "A binding phase requires a specific entry category.",
                new Dictionary<string, object> { { "phase", spec.Phase }, { "entry", spec.Entry }, { "declaredCategory", entry.Category }, { "requiredCategory", expected } },
                new[] { "Declare the bound entry with category " + expected + ", or change the binding phase." });
        }

        // ------------------------------------------------------------------
        // 编译与 binding 准备
        // ------------------------------------------------------------------

        /// <summary>
        /// 用唯一的动态编译 owner 编译 program 源码。所有权、引用快照、准入判定与产物记账
        /// 全部来自 DebugCompilerOwner；这里不建立第二套编译器、队列、引用快照或临时区。
        /// </summary>
        private static async ValueTask<DebugProgramCompilation> CompileAsync(string programId, string source, string stage, int maxSourceBytes, IUnityCliWaiter waiter, TimeSpan timeout, CancellationToken ct)
        {
            if (!DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.MainThreadTick))
                throw new BridgeCommandException(
                    "E_UNITY_BUSY",
                    "Another dynamic compilation currently owns the single compiler.",
                    new Dictionary<string, object> { { "owner", "dynamic-compiler" } },
                    new[] { "Wait for the current compilation to reach its terminal result, then submit this request again." },
                    true,
                    250);
            try
            {
                var identity = DebugCompilerOwner.CandidateIdentity("program", stage + "\0" + programId + "\0" + source, null);
                RequireAdmission(identity, 0L);
                var stem = "UnityCli.Program." + identity.Substring(0, 16);
                var request = new DynamicCodeCompileRequest
                {
                    SourceText = source,
                    SourceFileName = stem + ".cs",
                    ResponseFileName = "references.rsp",
                    AssemblyFileName = stem + ".dll",
                    PdbFileName = stem + ".pdb",
                    AssemblySimpleName = stem,
                    Defines = null,
                    MaxSourceBytes = maxSourceBytes,
                    MaxArtifactBytes = DebugLimits.MaxSingleArtifactBytes
                };
                List<string> references;
                try
                {
                    references = DebugCompilerOwner.References(null, null, () => !ct.IsCancellationRequested);
                }
                catch (OperationCanceledException)
                {
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Program compilation exceeded the immutable deadline.", null, new[] { "Inspect the Editor state before submitting a new request." });
                }
                var compilation = await DynamicCodeCompiler.CompileAsync(request, references, () => !ct.IsCancellationRequested, waiter, timeout, ct);
                if (compilation.TimedOut)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Program compilation exceeded the immutable deadline.", null, new[] { "Inspect the Editor state before submitting a new request." });
                if (!compilation.Success)
                {
                    if (!string.IsNullOrEmpty(compilation.FailureCode))
                        throw new BridgeCommandException(compilation.FailureCode, compilation.FailureMessage, null, new[] { "Reduce the program source size before retrying." });
                    var details = new Dictionary<string, object> { { "stage", stage }, { "diagnostics", compilation.Diagnostics.Take(5).Cast<object>().ToList() } };
                    var message = compilation.Diagnostics.Count == 0
                        ? "Program compilation failed."
                        : "Program compilation failed: " + Convert.ToString(compilation.Diagnostics[0]["message"], CultureInfo.InvariantCulture);
                    throw new BridgeCommandException("E_CODE_COMPILE_FAILED", UnityCliBridge.BoundedText(message, 4096), details, new[] { "Fix the diagnostics described in details and submit the program again." });
                }
                RequireAdmission(identity, compilation.AssemblyBytes.LongLength);
                var assembly = System.Reflection.Assembly.Load(compilation.AssemblyBytes);
                // 加载成功即占用：Mono 不能逐个卸载，后续 ABI 失败也不扣减。
                DebugCompilerOwner.CommitLoadedArtifact(identity, compilation.AssemblyBytes.LongLength, assembly.GetName().Name);
                return new DebugProgramCompilation
                {
                    Assembly = assembly,
                    Identity = identity,
                    AssemblyName = assembly.GetName().Name,
                    Bytes = compilation.AssemblyBytes.LongLength
                };
            }
            finally
            {
                DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick);
            }
        }

        private static void RequireAdmission(string identity, long artifactBytes)
        {
            var admission = DebugCompilerOwner.TryAdmitCandidate(identity, artifactBytes);
            if (admission.Admitted) return;
            throw new BridgeCommandException(
                DebugCompilerOwner.BudgetExhaustedCode,
                "This domain cannot load another compiled artifact: " + admission.Reason + ".",
                new Dictionary<string, object> { { "reason", admission.Reason }, { "budget", admission.Report.ToData() } },
                new[] { DebugCompilerOwner.ReloadSuggestion });
        }

        /// <summary>
        /// ABI 解析与 binding 准备：精确解析每个 entry，然后为每个 binding 准备一个只调用
        /// program 派发入口的 hook callback。install 只准备，不挂载。
        /// </summary>
        private static async ValueTask ResolveAndPrepareBindingsAsync(DebugProgramPlan plan, UnityCliDebugProgramRegistry session, IUnityCliWaiter waiter, TimeSpan timeout, CancellationToken ct)
        {
            List<ResolvedProgramEntry> resolved;
            string failure;
            string detail;
            if (!UnityCliDebugProgramAbi.TryResolve(plan.Assembly, plan.Entries, out resolved, out failure, out detail))
                throw new BridgeCommandException(
                    "E_DEBUG_PROGRAM_ABI_INVALID",
                    "The program ABI is invalid: " + detail,
                    new Dictionary<string, object>
                    {
                        { "programId", plan.ProgramId },
                        { "failure", failure },
                        { "signatures", UnityCliDebugProgramAbi.SignatureTable() }
                    },
                    new[] { "Declare each entry with the exact signature for its category, then submit the program again." });
            plan.Resolved = resolved.ToDictionary(item => item.Entry.Name, item => item, StringComparer.Ordinal);
            plan.WrapperInstances = UnityCliDebugProgramAbi.CreateWrappers(resolved);
            foreach (var bindingPlan in plan.Bindings)
            {
                var target = FindBindingSpec(plan, bindingPlan.Binding.BindingId);
                var hook = new MethodHookInstallRequest
                {
                    Label = BindingLabelPrefix + plan.ProgramId + ":" + bindingPlan.Binding.BindingId,
                    Target = target.Target,
                    Activation = "editor-and-play",
                    EmitHitEvents = true,
                    Callbacks = new MethodHookCallbackSpec
                    {
                        PrefixCode = string.Equals(bindingPlan.Binding.Phase, UnityCliDebugProgramBindingPhases.Prefix, StringComparison.Ordinal)
                            ? BindingBody(plan.ProgramId, bindingPlan.Binding.Entry, true)
                            : null,
                        PostfixCode = string.Equals(bindingPlan.Binding.Phase, UnityCliDebugProgramBindingPhases.Postfix, StringComparison.Ordinal)
                            ? BindingBody(plan.ProgramId, bindingPlan.Binding.Entry, false)
                            : null
                    }
                };
                if (string.Equals(bindingPlan.Binding.Phase, UnityCliDebugProgramBindingPhases.Signal, StringComparison.Ordinal))
                {
                    // 信号 binding 没有 detour：投递由已绑定的 exact target 或框架 phase 触发。
                    hook.Callbacks = null;
                    hook.Capture = null;
                    bindingPlan.HookPlan = null;
                    bindingPlan.Prepared = null;
                    continue;
                }
                MethodHookInstallPlan hookPlan;
                try
                {
                    // program 挂载的 binding 带上 owner：直接的 debug.hook.* 变更必须被拒绝。
                    hookPlan = MethodHookHost.PrepareInstall(hook, ProgramOwnerOf(plan.ProgramId));
                }
                catch (BridgeCommandException exception)
                {
                    throw new BridgeCommandException(
                        exception.Code,
                        "A program binding could not be prepared: " + exception.Message,
                        new Dictionary<string, object> { { "programId", plan.ProgramId }, { "bindingId", bindingPlan.Binding.BindingId } },
                        new[] { "Resolve the exact target with tools run debug.method-find, or remove this binding from the program." });
                }
                bindingPlan.HookPlan = hookPlan;
                bindingPlan.Binding.TargetKey = MethodHookTargetKey.From(hookPlan.Resolved.Method).Text;
                bindingPlan.Prepared = await MethodHookHost.CompileCallbacksAsync(hook.Callbacks, waiter, timeout, ct);
            }
        }

        private static DebugProgramBindingSpec FindBindingSpec(DebugProgramPlan plan, string bindingId)
        {
            var specs = plan.InstallRequest != null ? plan.InstallRequest.Bindings : plan.ReplaceRequest.Bindings;
            var index = 0;
            foreach (var bindingPlan in plan.Bindings)
            {
                var spec = specs[index];
                index++;
                if (string.Equals(bindingPlan.Binding.BindingId, bindingId, StringComparison.Ordinal)) return spec;
            }
            return null;
        }

        /// <summary>program 挂载的 hook definition 的 owner 身份；只有该身份可以解除自己的 binding。</summary>
        internal static string ProgramOwnerOf(string programId)
        {
            return "program:" + (programId ?? string.Empty);
        }

        /// <summary>生成的 binding 回调体：只调用 program 派发入口，不复制 program 逻辑。</summary>
        private static string BindingBody(string programId, string entry, bool prefix)
        {
            var call = (prefix ? "UnityCli.DebugProgramDispatch.Prefix(" : "UnityCli.DebugProgramDispatch.Postfix(")
                + Quote(programId) + ", " + Quote(entry) + ", ctx)";
            return prefix ? call + ";" : call + ";";
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void StoreResolved(DebugProgramPlan plan)
        {
            lock (Sync)
            {
                ResolvedEntries[plan.ProgramId] = plan.Resolved;
                WrapperSets[plan.ProgramId] = plan.WrapperInstances;
                PreparedBindings[plan.ProgramId] = plan.Bindings.ToDictionary(item => item.Binding.BindingId, item => item, StringComparer.Ordinal);
            }
        }

        private static ResolvedProgramEntry FindResolved(string programId, string entry)
        {
            lock (Sync)
            {
                Dictionary<string, ResolvedProgramEntry> entries;
                if (!ResolvedEntries.TryGetValue(programId ?? string.Empty, out entries)) return null;
                ResolvedProgramEntry resolved;
                return entries.TryGetValue(entry ?? string.Empty, out resolved) ? resolved : null;
            }
        }

        private static ResolvedProgramEntry RequireResolved(UnityCliDebugProgramDefinition definition, string entry)
        {
            var resolved = FindResolved(definition.ProgramId, entry);
            if (resolved != null) return resolved;
            throw new BridgeCommandException(
                "E_DEBUG_PROGRAM_ENTRY_MISSING",
                "The resolved entry is unavailable in this domain.",
                new Dictionary<string, object> { { "programId", definition.ProgramId }, { "entry", entry } },
                new[] { "Run tools run debug.program-replace to republish the program in the current domain." });
        }

        // ------------------------------------------------------------------
        // 挂载与释放
        // ------------------------------------------------------------------

        /// <summary>挂载该定义当前声明的全部 detour binding；返回本次新挂载的数量。</summary>
        private static long AttachBindings(UnityCliDebugProgramDefinition definition)
        {
            var attached = 0L;
            var session = Registry();
            foreach (var binding in definition.Bindings)
            {
                if (binding.Attached) continue;
                if (!UnityCliDebugProgramBindingPhases.IsDefined(binding.Phase)) continue;
                if (string.Equals(binding.Phase, UnityCliDebugProgramBindingPhases.Signal, StringComparison.Ordinal))
                {
                    // 信号 binding 只登记，不安装 detour：投递由显式执行点调用触发。
                    session.RecordBindingAttachment(definition.ProgramId, binding.BindingId, string.Empty, string.Empty, true);
                    attached++;
                    continue;
                }
                DebugProgramBindingPlan prepared;
                lock (Sync)
                {
                    Dictionary<string, DebugProgramBindingPlan> plans;
                    PreparedBindings.TryGetValue(definition.ProgramId, out plans);
                    prepared = plans != null && plans.TryGetValue(binding.BindingId, out var value) ? value : null;
                }
                if (prepared == null || prepared.HookPlan == null)
                    throw new BridgeCommandException(
                        "E_DEBUG_PROGRAM_BINDING_NOT_PREPARED",
                        "A declared binding has no prepared mount plan, so it cannot be activated.",
                        new Dictionary<string, object> { { "programId", definition.ProgramId }, { "bindingId", binding.BindingId } },
                        new[] { "Run tools run debug.program-replace with the same bindings so they can be prepared and mounted together." });
                var result = MethodHookHost.ApplyInstall(prepared.HookPlan, prepared.Prepared);
                session.RecordBindingAttachment(definition.ProgramId, binding.BindingId, result.HookId, binding.TargetKey, true);
                attached++;
            }
            return attached;
        }

        /// <summary>撤除该定义当前挂载的全部 detour binding；返回撤除数量。</summary>
        private static int DetachBindings(UnityCliDebugProgramDefinition definition)
        {
            if (definition == null) return 0;
            var detached = 0;
            foreach (var binding in definition.Bindings.ToList())
            {
                if (!binding.Attached) continue;
                DetachBinding(definition.ProgramId, binding, "detach");
                detached++;
            }
            return detached;
        }

        /// <summary>
        /// 通过唯一 hook host 撤除一个 binding 的 detour。program 永远不直接触碰后端；
        /// 撤除失败只记录事实，不伪装成已释放。
        /// </summary>
        private static void DetachBinding(string programId, UnityCliDebugProgramBinding binding, string reason)
        {
            var session = Registry();
            if (!string.IsNullOrEmpty(binding.HookId))
            {
                try
                {
                    var info = MethodHookHost.Info(new MethodHookInfoRequest { HookId = binding.HookId });
                    var remove = MethodHookHost.PrepareRemove(new MethodHookRemoveRequest
                    {
                        HookId = binding.HookId,
                        EditorSessionId = string.IsNullOrEmpty(info.EditorSessionId) ? UnityCliBridge.EditorSessionId() : info.EditorSessionId,
                        ExpectedRevision = info.Revision
                    }, ProgramOwnerOf(programId));
                    MethodHookHost.ApplyRemove(remove);
                }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("debug_program_binding_detach_failed hookId=" + binding.HookId + " reason=" + reason + " error=" + exception.GetType().Name);
                }
            }
            session.RecordBindingAttachment(programId, binding.BindingId, string.Empty, string.Empty, false);
        }

        /// <summary>释放该 program 占用的会话资源槽位与 phase 现场；重复释放是幂等的。</summary>
        private static void ReleaseRunResource(string programId)
        {
            // phase 现场的 owner 与运行资源同属一个 program，因此在这里一并归还；
            // 释放一个不再声明 phase 的 program 是幂等 no-op。
            DebugPlayerLoopHost.Release(ProgramPhaseOwner(programId));
            string resourceId;
            lock (Sync)
            {
                if (!ActiveResourceByProgram.TryGetValue(programId ?? string.Empty, out resourceId)) return;
                ActiveResourceByProgram.Remove(programId);
            }
            DebugSessionHost.ReleaseResource(resourceId);
        }

        /// <summary>一个 program 的 PlayerLoop phase 现场 owner 身份；与运行资源同生命周期。</summary>
        internal static string ProgramPhaseOwner(string programId)
        {
            return "program-phase:" + (programId ?? string.Empty);
        }

        /// <summary>
        /// 为一个 program 注册唯一的显式清理条目；重复注册同名条目由 session owner 拒绝。
        /// 该条目同时关闭该 program 的全部非终态运行：只撤 detour 会留下永远非终态的 run，
        /// 并让 program.remove 被永久拒绝。
        /// </summary>
        private static void RegisterProgramCleanupStep(UnityCliDebugProgramDefinition definition)
        {
            var order = UnityCliDebugCleanupSteps.OrderOf(UnityCliDebugCleanupSteps.DetachHooks);
            var failure = DebugSessionHost.RegisterCleanupStep(definition.SessionId, order, "program-detach:" + definition.ProgramId, UnityCliDebugFacilityOwners.Program, report =>
            {
                var current = Registry().Find(definition.ProgramId);
                if (current != null) DetachBindings(current);
                CloseProgramRuns(definition.ProgramId, "The owning session stopped, so this run was interrupted.");
                if (report != null) report.RemovedDetours += 0L;
                return true;
            },
                // 撤除 detour 与关闭运行都必须发生在 Unity 主线程上；主线程暂停期间由控制通道服务的 stop 推迟它。
                true);
            // 每个 program 只注册一次同名清理条目；重复注册由 session owner 拒绝，这里只记录事实。
            if (failure != null)
                UnityCliBridge.WriteLog("debug_program_cleanup_step_reused program=" + definition.ProgramId + " reason=" + failure);
            // 关闭与解除条目每个 session 各一条，与 program 数量无关。
            RegisterSessionStopSteps(definition.SessionId);
        }

        // ------------------------------------------------------------------
        // 投影与错误映射
        // ------------------------------------------------------------------

        private static string TargetSummaryOf(MethodHookTargetSpec spec)
        {
            if (spec == null) return string.Empty;
            var parameterText = spec.ParameterTypes == null || spec.ParameterTypes.Count == 0 ? string.Empty : string.Join(",", spec.ParameterTypes);
            return (spec.AssemblyName ?? string.Empty) + "|" + (spec.TypeName ?? string.Empty) + "." + (spec.MethodName ?? string.Empty)
                + "(" + parameterText + ")" + " : " + (spec.ReturnType ?? string.Empty) + (spec.IsStatic ? " static" : " instance");
        }

        /// <summary>
        /// 一次运行 target 字段的唯一推导：它命名的是该运行声明的执行点，而不是调用方猜的位置。
        /// phase 运行说真实 phase 名，有 binding 的运行说该 exact target，只有真的由中央 editor tick
        /// 服务的运行才用中央 tick 措辞；两者都不是时只说声明的执行点，不得冒充中央 tick。
        /// </summary>
        internal static string TargetText(UnityCliDebugProgramEntry entry, UnityCliDebugProgramDefinition definition, string executionSite, string phase)
        {
            if (string.Equals(executionSite, UnityCliDebugProgramExecutionSites.PlayerLoopPhase, StringComparison.Ordinal))
                return "Unity PlayerLoop phase " + (phase ?? string.Empty);
            var binding = definition.Bindings.FirstOrDefault(candidate => string.Equals(candidate.Entry, entry.Name, StringComparison.Ordinal));
            if (binding != null && !string.IsNullOrEmpty(binding.TargetSummary)) return binding.TargetSummary;
            // 无 exact target 的 signal 投递与 editor-tick 一样由中央 tick 取得执行权。
            if (string.Equals(executionSite, UnityCliDebugProgramExecutionSites.EditorTick, StringComparison.Ordinal)
                || string.Equals(executionSite, UnityCliDebugProgramExecutionSites.Signal, StringComparison.Ordinal))
                return "main-thread scheduler";
            return "declared execution site " + (executionSite ?? string.Empty) + " with no exact target";
        }

        private static DebugProgramEntryView SummarizeEntry(UnityCliDebugProgramEntry entry)
        {
            return new DebugProgramEntryView
            {
                Name = entry.Name,
                Category = entry.Category,
                DeclaringType = entry.DeclaringType,
                Signature = entry.Signature
            };
        }

        /// <summary>
        /// 导出该 program 每个声明 binding 的 exact 目标声明与相，供 recipe 的 binding 段使用。
        /// 目标声明只取自已经完成校验与准备的安装计划，不重新解析，也不探测用户构造。
        /// </summary>
        internal static List<Dictionary<string, object>> ExportBindingDeclarations(UnityCliDebugProgramDefinition definition)
        {
            var result = new List<Dictionary<string, object>>();
            if (definition == null) return result;
            Dictionary<string, DebugProgramBindingPlan> plans = null;
            lock (Sync) PreparedBindings.TryGetValue(definition.ProgramId, out plans);
            foreach (var binding in definition.Bindings)
            {
                if (result.Count >= DebugLimits.MaxProgramBindings) break;
                DebugProgramBindingPlan prepared = null;
                if (plans != null) plans.TryGetValue(binding.BindingId, out prepared);
                var spec = prepared == null || prepared.HookPlan == null ? null : prepared.HookPlan.TargetSpec;
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "bindingId", binding.BindingId ?? string.Empty },
                    { "phase", binding.Phase ?? string.Empty },
                    { "entry", binding.Entry ?? string.Empty },
                    { "target", spec == null ? null : spec.ToData() }
                });
            }
            return result;
        }

        private static DebugProgramBindingView SummarizeBinding(UnityCliDebugProgramBinding binding)
        {
            return new DebugProgramBindingView
            {
                BindingId = binding.BindingId,
                Phase = binding.Phase,
                Entry = binding.Entry,
                TargetSummary = binding.TargetSummary,
                HookId = binding.HookId,
                Attached = binding.Attached
            };
        }

        private static DebugProgramSummaryView SummarizeProgram(UnityCliDebugProgramDefinition definition)
        {
            return new DebugProgramSummaryView
            {
                ProgramId = definition.ProgramId,
                SessionId = definition.SessionId,
                Label = definition.Label,
                EntryType = definition.EntryType,
                State = definition.State,
                Revision = definition.Revision,
                SourceSha256 = definition.SourceSha256,
                SourceBytes = definition.SourceBytes,
                StateEpoch = definition.StateEpoch,
                StateBytes = definition.StateBytes,
                StateSchemaBytes = definition.StateSchemaBytes,
                InFlightCalls = definition.InFlightCalls,
                StepDriver = definition.StepDriverPlanted,
                CreatedAtUtc = definition.CreatedAtUtc,
                StoppedAtUtc = definition.StoppedAtUtc,
                AssemblyName = definition.AssemblyName,
                Entries = definition.Entries.Select(SummarizeEntry).ToList(),
                Bindings = definition.Bindings.Select(SummarizeBinding).ToList()
            };
        }

        private static DebugProgramRunView SummarizeRun(UnityCliDebugProgramRun run)
        {
            return new DebugProgramRunView
            {
                ProgramRunId = run.ProgramRunId,
                Entry = run.Entry,
                EntryCategory = run.EntryCategory,
                State = run.State,
                Revision = run.Revision,
                ExecutionSite = run.ExecutionSite,
                Target = run.Target,
                RunId = run.RunId,
                DurationMs = run.DurationMs,
                StartedAtUtc = run.StartedAtUtc,
                CompletedAtUtc = run.CompletedAtUtc,
                StepCount = run.StepCount,
                CheckCount = run.CheckCount,
                DeliveryCount = run.DeliveryCount,
                CancellationRequested = run.CancellationRequested,
                InFlight = run.InFlight,
                ResultTrust = run.ResultTrust,
                FailureCode = run.FailureCode
            };
        }

        private static DebugProgramDriverView SummarizeDriver(string programId)
        {
            var driver = DebugScheduler.DescribeDriver(programId);
            if (driver == null) return new DebugProgramDriverView { Planted = false, Note = "no continuous step driver is planted for this program" };
            return new DebugProgramDriverView
            {
                Planted = true,
                ProgramRunId = driver.ProgramRunId,
                Entry = driver.Entry,
                Phase = driver.Phase,
                StepIndex = driver.StepIndex,
                WaitingOnSignal = driver.WaitSignal,
                WaitingOnCondition = driver.WaitCondition,
                FramesRemaining = driver.FramesRemaining,
                DeadlineMonotonicMs = driver.DeadlineMonotonicMs,
                LastRevision = driver.LastRevision,
                Note = string.Empty
            };
        }

        private static DebugProgramBudgetView BudgetView(UnityCliDebugProgramBudget budget)
        {
            if (budget == null) return new DebugProgramBudgetView();
            return new DebugProgramBudgetView
            {
                ProgramsUsed = budget.ProgramsUsed,
                ProgramsRemaining = budget.ProgramsRemaining,
                MaxProgramsGlobal = budget.MaxProgramsGlobal,
                SessionProgramsUsed = budget.SessionProgramsUsed,
                SessionProgramsRemaining = budget.SessionProgramsRemaining,
                MaxProgramsPerSession = budget.MaxProgramsPerSession,
                ActiveRunsUsed = budget.ActiveRunsUsed,
                ActiveRunsRemaining = budget.ActiveRunsRemaining,
                MaxActiveRunsGlobal = budget.MaxActiveRunsGlobal
            };
        }

        private static object BoundedValue(object value)
        {
            if (value == null) return null;
            return DebugValueProjection.ProjectValueOnly(value);
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            var source = value as IDictionary<string, object>;
            return source == null ? new Dictionary<string, object>(StringComparer.Ordinal) : new Dictionary<string, object>(source, StringComparer.Ordinal);
        }

        private static object ParseJsonText(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return UnityCliSerializer.Parse(json); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 程序事件统一入口；值与参数在入库前已按投影上限物化。
        ///
        /// 帧号规则与 hook 命中路径相同：真实 Unity 帧号只能在可证明的主线程上读取。
        /// 方法 entry 在命中线程（可能是 worker）当场执行，因此 off-main 追加只能引用主线程缓存帧号；
        /// 若在这里无条件调用主线程专属读取，worker 命中会抛异常并静默丢掉整条事件。
        /// 主线程身份、线程 id 与执行点仍按追加线程的真实身份记录。
        ///
        /// 入库失败时把已装配的事件事实交给存储 owner：计数、日志行与“未记录”归因事实由它一次完成，
        /// 因此丢事件不可能只增加一个无法归因的计数。
        /// </summary>
        private static void AppendProgramEvent(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, string kind, string name, object value, string faultCode, int threadId)
        {
            var mainThread = DebugEventClocks.IsCurrentThreadMainThread();
            UnityCliDebugEvent entry = null;
            try
            {
                entry = ProgramEvent(definition, run, kind, name, value, faultCode, threadId, mainThread);
                DebugEventStore.Append(entry);
            }
            catch (Exception exception)
            {
                // 投影在装配时失败时 entry 仍为空：归因事实只用固定标量重新装配，不会在同一原因上再次失败。
                DebugEventStore.ReportAppendFailure(
                    entry ?? ProgramEvent(definition, run, kind, name, null, faultCode, threadId, mainThread),
                    exception.GetType().Name);
            }
        }

        /// <summary>
        /// 程序事件事实的唯一装配点；帧号只有可证明的主线程才真实读取，off-main 只引用主线程缓存帧号。
        /// </summary>
        private static UnityCliDebugEvent ProgramEvent(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, string kind, string name, object value, string faultCode, int threadId, bool mainThread)
        {
            return new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugProgramAbi.EventSource,
                Kind = kind ?? string.Empty,
                ExecutionPoint = run == null ? string.Empty : run.ExecutionSite,
                Revision = definition == null ? 0L : definition.Revision,
                ThreadId = threadId,
                MainThread = mainThread,
                ClockKind = UnityCliDebugClocks.Monotonic,
                ClockValue = DebugEventClocks.MonotonicMilliseconds,
                Frame = mainThread ? DebugEventClocks.ReadUnityFrame() : DebugEventClocks.LastObservedMainFrame,
                HookId = string.Empty,
                Name = name ?? string.Empty,
                Value = value == null ? null : DebugValueProjection.ProjectValueOnly(value),
                FaultCode = faultCode ?? string.Empty,
                MethodIdentity = definition == null ? string.Empty : definition.ProgramId + ":" + (run == null ? string.Empty : run.Entry)
            };
        }

        private static void AppendLifecycle(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, string name, string detail)
        {
            if (definition == null) return;
            AppendProgramEvent(definition, run, "lifecycle", name, string.IsNullOrEmpty(detail) ? null : detail, null, Thread.CurrentThread.ManagedThreadId);
        }

        private static void RecordFault(UnityCliDebugProgramDefinition definition, UnityCliDebugProgramRun run, string code, string message)
        {
            var bounded = UnityCliBridge.BoundedText(message ?? string.Empty, MaxDiagnosticBytes);
            AppendProgramEvent(definition, run, "fault", "fault", null, code, Thread.CurrentThread.ManagedThreadId);
            UnityCliBridge.WriteLog("debug_program_fault code=" + (code ?? string.Empty) + " program=" + (definition == null ? string.Empty : definition.ProgramId) + " detail=" + bounded);
        }

        private static double UtcMilliseconds()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private static BridgeCommandException SchemaInvalid(string field, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "Field " + field + " is invalid: expected " + expected + ".",
                new Dictionary<string, object> { { "path", "$." + field }, { "expected", expected }, { "actual", actual ?? "null" } },
                new[] { "Run tools describe on this program Tool to read the field contract, then submit the corrected input." });
        }

        private static BridgeCommandException StateTooLarge(long bytes)
        {
            return new BridgeCommandException(
                "E_DEBUG_LIMIT_EXCEEDED",
                "The revision state or its schema exceeds the declared per-program byte budget.",
                new Dictionary<string, object> { { "limit", "program-state-bytes" }, { "used", bytes }, { "max", (long)DebugLimits.MaxProgramStateBytes } },
                new[]
                {
                    "Send a smaller state payload.",
                    "Run tools run debug.program-info to read the current revision state size."
                });
        }

        private static BridgeCommandException SourceTooLarge(long bytes)
        {
            return new BridgeCommandException(
                "E_DEBUG_LIMIT_EXCEEDED",
                "The program source exceeds the declared per-compile-unit byte budget.",
                new Dictionary<string, object> { { "limit", "program-source-bytes" }, { "used", bytes }, { "max", (long)DebugLimits.MaxProgramSourceBytes } },
                new[]
                {
                    "Split the program into a smaller source file, or reduce generated code inside it.",
                    "Run tools run debug.program-list to read the current program budget usage."
                });
        }

        private static BridgeCommandException ArgsTooLarge(long bytes)
        {
            return new BridgeCommandException(
                "E_DEBUG_LIMIT_EXCEEDED",
                "The run arguments exceed the declared per-run byte budget.",
                new Dictionary<string, object> { { "limit", "program-args-bytes" }, { "used", bytes }, { "max", (long)DebugLimits.MaxProgramArgsBytes } },
                new[] { "Send smaller args, or read the needed value inside the entry instead." });
        }

        private static BridgeCommandException DurationInvalid(long durationMs)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "durationMs must be inside the declared run duration budget.",
                new Dictionary<string, object>
                {
                    { "path", "$.durationMs" },
                    { "expected", "1.." + DebugLimits.MaxProgramRunMaxDurationMs.ToString(CultureInfo.InvariantCulture) },
                    { "actual", durationMs.ToString(CultureInfo.InvariantCulture) }
                },
                new[] { "Pass a durationMs inside the declared budget." });
        }

        private static BridgeCommandException MissingProgram(string programId)
        {
            return new BridgeCommandException(
                "E_DEBUG_PROGRAM_NOT_FOUND",
                "No loaded program definition has that programId in the current domain.",
                new Dictionary<string, object> { { "programId", programId ?? string.Empty } },
                new[] { "Run tools run debug.program-list to read the currently loaded program definitions." });
        }

        private static BridgeCommandException MissingRun(string programRunId)
        {
            return new BridgeCommandException(
                "E_DEBUG_PROGRAM_RUN_NOT_FOUND",
                "No loaded program run has that programRunId.",
                new Dictionary<string, object> { { "programRunId", programRunId ?? string.Empty } },
                new[] { "Run tools run debug.program-info on the program to read its active runs." });
        }

        private static BridgeCommandException EntryMissing(UnityCliDebugProgramDefinition definition, string entry)
        {
            return new BridgeCommandException(
                "E_DEBUG_PROGRAM_ENTRY_MISSING",
                "The program does not declare that entry name.",
                new Dictionary<string, object>
                {
                    { "programId", definition.ProgramId },
                    { "entry", entry ?? string.Empty },
                    { "declaredEntries", definition.Entries.Select(candidate => candidate.Name).ToList() }
                },
                new[] { "Run tools run debug.program-info to read the declared entry names, then submit the run again." });
        }

        private static BridgeCommandException SiteInvalid(string executionSite, UnityCliDebugProgramDefinition definition)
        {
            return new BridgeCommandException(
                "E_DEBUG_PROGRAM_SITE_INVALID",
                "executionSite is not one of the declared execution points.",
                new Dictionary<string, object>
                {
                    { "executionSite", executionSite ?? string.Empty },
                    { "availableSites", UnityCliDebugProgramExecutionSites.All() },
                    { "programId", definition.ProgramId }
                },
                new[] { "Pass one of the declared executionSite values." });
        }

        private static BridgeCommandException InstallRejection(string failure, string label, string sessionId, long sourceBytes, UnityCliDebugProgramBudget budget)
        {
            if (string.Equals(failure, UnityCliDebugProgramFailures.DuplicateLabel, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_PROGRAM_LABEL_CONFLICT",
                    "The label is already used by another program definition of the same session.",
                    new Dictionary<string, object> { { "label", label ?? string.Empty }, { "sessionId", sessionId ?? string.Empty } },
                    new[] { "Choose a label that no current program definition of this session uses.", "Run tools run debug.program-list to read the current labels." });
            if (string.Equals(failure, UnityCliDebugProgramFailures.SourceTooLarge, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The program source exceeds the declared per-compile-unit byte budget.",
                    new Dictionary<string, object> { { "limit", "program-source-bytes" }, { "used", sourceBytes }, { "max", (long)DebugLimits.MaxProgramSourceBytes } },
                    new[] { "Split the program into a smaller source file, or reduce generated code inside it." });
            return new BridgeCommandException(
                "E_DEBUG_LIMIT_EXCEEDED",
                "The program budget is already reached, so no definition was created.",
                new Dictionary<string, object> { { "limit", failure }, { "budget", budget == null ? new Dictionary<string, object>() : budget.ToData() } },
                new[]
                {
                    "Run tools run debug.program-remove for a stopped program you no longer need, then retry this install.",
                    "Run tools run debug.program-list to read the current program budget usage."
                });
        }

        private static BridgeCommandException ReplaceRejection(string failure, UnityCliDebugProgramDefinition definition, long expectedRevision, string state, long sourceBytes, UnityCliDebugProgramBudget budget)
        {
            if (string.Equals(failure, UnityCliDebugProgramFailures.RevisionConflict, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_PROGRAM_REVISION_CONFLICT",
                    "expectedRevision does not match the current revision, so the previous working configuration was kept.",
                    new Dictionary<string, object>
                    {
                        { "programId", definition.ProgramId },
                        { "expectedRevision", expectedRevision },
                        { "currentRevision", definition.Revision }
                    },
                    new[] { "Run tools run debug.program-info to read the current revision, then submit the replacement with that expectedRevision." });
            if (string.Equals(failure, UnityCliDebugProgramFailures.StateEpochConflict, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_PROGRAM_STATE_CONFLICT",
                    "The revision state epoch changed while the candidate was prepared, so the previous configuration was kept.",
                    new Dictionary<string, object> { { "programId", definition.ProgramId }, { "currentStateEpoch", definition.StateEpoch } },
                    new[] { "Run tools run debug.program-info to read the current state epoch, then submit the replacement again." });
            if (string.Equals(failure, UnityCliDebugProgramFailures.SourceTooLarge, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The candidate source exceeds the declared per-compile-unit byte budget, so the previous configuration was kept.",
                    new Dictionary<string, object> { { "limit", "program-source-bytes" }, { "used", sourceBytes }, { "max", (long)DebugLimits.MaxProgramSourceBytes } },
                    new[] { "Reduce the candidate source size, then submit the replacement again." });
            if (string.Equals(failure, UnityCliDebugProgramFailures.WrongState, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_PROGRAM_WRONG_STATE",
                    "A stopped program cannot accept a replacement.",
                    new Dictionary<string, object> { { "programId", definition.ProgramId }, { "state", state ?? definition.State } },
                    new[] { "Install a new program for this investigation, then run it.", "Run tools run debug.program-remove to delete this stopped definition." });
            return new BridgeCommandException(
                "E_DEBUG_PROGRAM_NOT_FOUND",
                "The program to replace is not loaded in the current domain.",
                new Dictionary<string, object> { { "programId", definition == null ? string.Empty : definition.ProgramId } },
                new[] { "Run tools run debug.program-list to read the currently loaded program definitions." });
        }

        private static BridgeCommandException RunRejection(string failure, UnityCliDebugProgramDefinition definition, string state, string executionSite, UnityCliDebugProgramBudget budget)
        {
            if (string.Equals(failure, UnityCliDebugProgramFailures.RunActive, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_PROGRAM_RUN_ACTIVE",
                    "This program already has one active run; a second concurrent run was refused before any effect.",
                    new Dictionary<string, object> { { "programId", definition.ProgramId }, { "state", state ?? definition.State } },
                    new[]
                    {
                        "Run tools run debug.program-wait for the active run, or tools run debug.program-stop to close it.",
                        "Run tools run debug.program-info to read the active run of this program."
                    });
            if (string.Equals(failure, UnityCliDebugProgramFailures.RunLimitExceeded, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The global active run budget is already reached, so no run was activated.",
                    new Dictionary<string, object> { { "limit", "active-runs" }, { "budget", budget == null ? new Dictionary<string, object>() : budget.ToData() } },
                    new[]
                    {
                        "Run tools run debug.program-stop for a run you no longer need, then submit this run again.",
                        "Run tools run debug.program-list to read the current active run usage."
                    });
            if (string.Equals(failure, UnityCliDebugProgramFailures.WrongState, StringComparison.Ordinal))
                return new BridgeCommandException(
                    "E_DEBUG_PROGRAM_WRONG_STATE",
                    "A stopped program cannot accept a new run.",
                    new Dictionary<string, object> { { "programId", definition.ProgramId }, { "state", state ?? definition.State }, { "executionSite", executionSite ?? string.Empty } },
                    new[] { "Run tools run debug.program-replace to republish the program, or install a new program for this investigation." });
            return new BridgeCommandException(
                "E_DEBUG_PROGRAM_ENTRY_MISSING",
                "The declared entry or execution site cannot be activated.",
                new Dictionary<string, object> { { "programId", definition == null ? string.Empty : definition.ProgramId }, { "failure", failure }, { "executionSite", executionSite ?? string.Empty } },
                new[] { "Run tools run debug.program-info to read the declared entries and their categories." });
        }

        private static BridgeCommandException RemoveRejection(string failure, UnityCliDebugProgramDefinition definition, string state, int activeRuns, int attachedBindings, UnityCliDebugProgramBudget budget)
        {
            if (string.Equals(failure, UnityCliDebugProgramFailures.MissingProgram, StringComparison.Ordinal))
                return MissingProgram(definition == null ? string.Empty : definition.ProgramId);
            var detail = new Dictionary<string, object>
            {
                { "programId", definition == null ? string.Empty : definition.ProgramId },
                { "state", state ?? string.Empty },
                { "activeRuns", (long)activeRuns },
                { "attachedBindings", (long)attachedBindings },
                { "budget", budget == null ? new Dictionary<string, object>() : budget.ToData() }
            };
            return new BridgeCommandException(
                "E_DEBUG_PROGRAM_RUN_ACTIVE",
                "The program still owns an active run or an in-flight call, so it cannot be removed.",
                detail,
                new[]
                {
                    "Run tools run debug.program-stop for the active run of this program.",
                    "Run tools run debug.program-wait to observe the run until it reaches a terminal state, then remove the program."
                });
        }
    }
}
#endif
