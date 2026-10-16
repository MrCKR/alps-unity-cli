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
    /// <summary>export 的 effect 前提炼结果：目标 session 与分页参数已经确定。</summary>
    internal sealed class DebugRecipeExportPlan
    {
        internal string SessionId = string.Empty;
        internal string Cursor = string.Empty;
        internal int PageBytes;
    }

    /// <summary>import 的 effect 前提炼结果：已读入并校验的 recipe 与其分节。</summary>
    internal sealed class DebugRecipeImportPlan
    {
        internal string File = string.Empty;
        internal string Label = string.Empty;
        internal string ActivationScope = string.Empty;
        internal string ClockKind = string.Empty;
        internal DebugSessionCreatePlan CreatePlan;
        internal Dictionary<string, object> InitialState;
        internal long InitialStateBytes;
        internal long RecipeBytes;
        internal string RecipeChecksum = string.Empty;
        internal List<Dictionary<string, object>> Programs = new List<Dictionary<string, object>>();
        internal List<Dictionary<string, object>> Hooks = new List<Dictionary<string, object>>();
        internal int CheckCount;
        internal List<string> ClockNames = new List<string>();
    }

    /// <summary>
    /// recipe 导出与导入的唯一 host。
    ///
    /// 边界：
    /// - export 只读当前不可变 publication，不产生 artifact、不写文件；分页只读同一个内存快照。
    /// - import 只校验、编译候选源码并登记未激活定义；不执行 startup/cleanup 代码，也不重放任何操作。
    /// - 会话定义、Hook 定义与 program 定义的真值仍由各自 owner 持有，本类不建立第二套真值。
    /// </summary>
    internal static class DebugRecipeHost
    {
        private const int MaxDiagnosticBytes = 512;
        private const string IncludedSectionsText = "session, programs, hooks, checks, clocks";
        private const string ExcludedSectionsText = "pause sites, object handles, Thread/Task/delegate objects and unfinished commands";

        // ------------------------------------------------------------------
        // export
        // ------------------------------------------------------------------

        /// <summary>export 的 effect 前校验：session 必须存在，cursor 与页大小必须合法。</summary>
        internal static DebugRecipeExportPlan PrepareExport(DebugSessionExportRequest request)
        {
            var sessionId = request == null ? null : request.SessionId;
            var definition = DebugSessionHost.Definition(sessionId);
            if (definition == null)
                throw new BridgeCommandException(
                    "E_DEBUG_SESSION_NOT_FOUND",
                    "The session to export does not exist in the current Editor session.",
                    new Dictionary<string, object> { { "sessionId", sessionId }, { "path", "$.sessionId" } },
                    new[] { "Run tools run debug.session.list to read the loaded session definitions." });
            var pageBytes = request.PageBytes.HasValue && request.PageBytes.Value > 0 ? request.PageBytes.Value : DebugLimits.RecipePageDefaultBytes;
            if (pageBytes > DebugLimits.RecipePageMaxBytes)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field pageBytes is above the maximum recipe page size.",
                    new Dictionary<string, object> { { "path", "$.pageBytes" }, { "maxBytes", DebugLimits.RecipePageMaxBytes } },
                    new[] { "Submit pageBytes within the published recipe page bound." });
            return new DebugRecipeExportPlan { SessionId = sessionId, Cursor = request.Cursor ?? string.Empty, PageBytes = pageBytes };
        }

        /// <summary>
        /// export 的唯一 effect：无 cursor 时冻结当前 publication 并建立快照，有 cursor 时读同一快照的下一页。
        /// 本方法不写任何文件，也不改变 Unity 或 session 状态。
        /// </summary>
        internal static DebugSessionExportResult Export(DebugRecipeExportPlan plan)
        {
            DebugRecipePageResult page;
            if (string.IsNullOrEmpty(plan.Cursor))
            {
                var document = BuildDocument(plan.SessionId);
                var publication = DebugSessionHost.Definition(plan.SessionId).Revision;
                page = DebugRecipeStore.Publish(plan.SessionId, publication, DebugSessionHost.DomainGeneration(), document, plan.PageBytes);
            }
            else
            {
                string failure;
                string message;
                page = DebugRecipeStore.ReadPage(plan.SessionId, plan.Cursor, plan.PageBytes, out failure, out message);
                if (page == null)
                    throw new BridgeCommandException(
                        failure == DebugRecipeFailures.SnapshotMissing ? "E_DEBUG_SESSION_NOT_FOUND" : "E_DEBUG_EXPORT_CURSOR_EXPIRED",
                        message,
                        new Dictionary<string, object> { { "sessionId", plan.SessionId }, { "cursor", plan.Cursor }, { "reason", failure } },
                        new[] { "Run tools run debug.session.export again without cursor to freeze a new snapshot of the current publication." });
            }
            return new DebugSessionExportResult
            {
                SessionId = page.SessionId,
                SnapshotId = page.SnapshotId,
                Publication = page.Publication,
                Offset = page.Offset,
                TotalBytes = page.TotalBytes,
                PageBytes = page.PageBytes,
                Complete = page.Complete,
                HasMore = page.HasMore,
                Cursor = page.Cursor,
                Fragment = page.Fragment,
                Document = page.Document,
                ContentSha256 = page.Complete ? ComputeFragmentChecksum(page) : string.Empty,
                IncludedSections = IncludedSectionsText,
                ExcludedSections = ExcludedSectionsText,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DebugSessionHost.DomainGeneration(),
                RuntimeId = DebugSessionHost.RuntimeId(),
                MaxRecipeBytes = DebugLimits.MaxRecipeBytes,
                Note = "Export is a read-only snapshot of the current debug publication. It writes no artifact and no file; the caller owns any file it writes. Later cursors read the same snapshot, a new export of the same session replaces it and invalidates older cursors, and the snapshot never contains pause sites, object handles, Thread/Task/delegate objects or unfinished commands."
            };
        }

        private static string ComputeFragmentChecksum(DebugRecipePageResult page)
        {
            var document = page.Document;
            if (document == null) return string.Empty;
            var unsigned = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in document)
            {
                if (string.Equals(pair.Key, DebugRecipeStore.ChecksumField, StringComparison.Ordinal)) continue;
                unsigned[pair.Key] = pair.Value;
            }
            try { return UnityCliDebugSessionStorePayload.Checksum(unsigned); }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 构造当前 publication 的不可变 recipe 文档。只收录可复用的配置事实；
        /// 活动现场与托管对象身份由构造过程本身排除，不依赖后置过滤：
        /// hook 段使用 <see cref="MethodHookDefinition.ToData"/> 的声明投影，现场线程身份在该投影里就不存在。
        /// </summary>
        private static Dictionary<string, object> BuildDocument(string sessionId)
        {
            var definition = DebugSessionHost.Definition(sessionId);
            if (definition == null)
                throw new BridgeCommandException(
                    "E_DEBUG_SESSION_NOT_FOUND",
                    "The session disappeared before its recipe could be built.",
                    new Dictionary<string, object> { { "sessionId", sessionId } },
                    new[] { "Run tools run debug.session.list to read the loaded session definitions." });
            var programs = new List<object>();
            foreach (var program in DebugProgramHost.Registry().ProgramsFor(sessionId).OrderBy(item => item.ProgramId, StringComparer.Ordinal))
            {
                programs.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "label", program.Label ?? string.Empty },
                    { "entryType", program.EntryType ?? string.Empty },
                    { "source", program.Source ?? string.Empty },
                    { "sourceSha256", program.SourceSha256 ?? string.Empty },
                    { "sourceBytes", program.SourceBytes },
                    { "revision", program.Revision },
                    { "stateSchema", program.StateSchema },
                    { "entries", program.Entries.Select(entry => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "name", entry.Name ?? string.Empty },
                            { "category", entry.Category ?? string.Empty },
                            { "declaringType", entry.DeclaringType ?? string.Empty }
                        }).ToList() },
                    { "bindings", DebugProgramHost.ExportBindingDeclarations(program).Select(item => (object)item).ToList() }
                });
            }
            var hooks = MethodHookHost.ExportDefinitions(sessionId).Cast<object>().ToList();
            var checks = DebugRunHost.ExportCheckDeclarations(sessionId).Cast<object>().ToList();
            var clocks = DebugLogicalClocks.Snapshot().Select(clock => (object)new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", clock.Name ?? string.Empty },
                { "value", clock.Value },
                { "rawText", clock.RawText ?? string.Empty },
                { "source", clock.Source ?? string.Empty }
            }).ToList();
            var document = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { DebugRecipeStore.FormatField, DebugRecipeStore.Format },
                { "createdAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { DebugRecipeStore.ProjectPathField, UnityCliBridge.ProjectRoot() },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "domainGeneration", DebugSessionHost.DomainGeneration() },
                { "runtimeId", DebugSessionHost.RuntimeId() },
                { DebugRecipeStore.SessionField, new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { DebugRecipeStore.SessionIdField, definition.SessionId },
                        { "label", definition.Label ?? string.Empty },
                        { "activationScope", definition.ActivationScope ?? string.Empty },
                        { "clockKind", definition.ClockKind ?? string.Empty },
                        { "revision", definition.Revision },
                        { "initialStateBytes", definition.InitialStateBytes },
                        { "initialState", definition.InitialState }
                    } },
                { "programs", programs },
                { "hooks", hooks },
                { "checks", checks },
                { "clocks", clocks }
            };
            document[DebugRecipeStore.ChecksumField] = DebugRecipeStore.Checksum(document);
            return document;
        }

        // ------------------------------------------------------------------
        // import
        // ------------------------------------------------------------------

        /// <summary>
        /// import 的 effect 前校验：读入文件、校验内容/格式/checksum/路径/预算，并解析分节。
        /// 失败在这里抛出，且必须如实报告已经加载而不可回收的候选程序集。
        /// </summary>
        internal static DebugRecipeImportPlan PrepareImport(DebugSessionImportRequest request)
        {
            var file = request == null || request.File == null ? string.Empty : request.File.Trim();
            if (file.Length == 0)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field file is required: import reads a recipe the caller saved.",
                    new Dictionary<string, object> { { "path", "$.file" } },
                    new[] { "Pass the path of the recipe file previously written by the caller." });
            string text;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) throw new FileNotFoundException("the declared recipe file does not exist");
                if (info.Length > DebugLimits.MaxRecipeBytes) throw InvalidRecipe("over-budget", "The recipe file is larger than the single-recipe byte budget.");
                text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (BridgeCommandException) { throw; }
            catch (Exception exception)
            {
                throw InvalidRecipe("file-unreadable", "The declared recipe file could not be read: " + UnityCliBridge.BoundedText(exception.Message, MaxDiagnosticBytes));
            }
            var document = DebugRecipeStore.Parse(text);
            var validation = DebugRecipeStore.Validate(document, UnityCliBridge.ProjectRoot());
            if (!validation.Success)
                throw InvalidRecipe(validation.Failure, validation.Message);
            var session = DebugRecipeStore.Value(document, DebugRecipeStore.SessionField) as IDictionary<string, object>;
            var label = request.Label == null ? string.Empty : request.Label.Trim();
            if (label.Length == 0) label = DebugRecipeStore.Text(session, "label");
            var scope = request.ActivationScope == null ? string.Empty : request.ActivationScope.Trim();
            if (scope.Length == 0) scope = DebugRecipeStore.Text(session, "activationScope");
            if (scope.Length == 0) scope = UnityCliDebugActivationScopes.EditorAndPlay;
            var plan = new DebugRecipeImportPlan
            {
                File = file,
                Label = label,
                ActivationScope = scope,
                ClockKind = Empty(DebugRecipeStore.Text(session, "clockKind"), UnityCliDebugClocks.Monotonic),
                InitialState = DebugRecipeStore.Value(session, "initialState") as Dictionary<string, object>,
                RecipeBytes = validation.Bytes,
                RecipeChecksum = DebugRecipeStore.Text(document, DebugRecipeStore.ChecksumField)
            };
            plan.InitialStateBytes = plan.InitialState == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(plan.InitialState));
            // session 上限、label 冲突与 activation scope 冲突在 effect barrier 之前判定：
            // 被拒绝的 import 不建立任何定义，也不加载任何候选程序集。
            plan.CreatePlan = DebugSessionHost.PrepareCreate(new DebugSessionCreateRequest
            {
                Label = plan.Label,
                ActivationScope = plan.ActivationScope,
                ClockKind = plan.ClockKind,
                InitialState = plan.InitialState
            });
            plan.Programs = Sections(document, "programs");
            plan.Hooks = Sections(document, "hooks");
            plan.CheckCount = Sections(document, "checks").Count;
            foreach (var clock in Sections(document, "clocks"))
            {
                var name = DebugRecipeStore.Text(clock, "name");
                if (name.Length != 0) plan.ClockNames.Add(name);
            }
            return plan;
        }

        private static string Empty(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static List<Dictionary<string, object>> Sections(Dictionary<string, object> document, string key)
        {
            var result = new List<Dictionary<string, object>>();
            var items = DebugRecipeStore.Value(document, key) as IEnumerable<object>;
            if (items == null) return result;
            foreach (var item in items)
            {
                var dictionary = item as IDictionary<string, object>;
                if (dictionary == null) continue;
                result.Add(new Dictionary<string, object>(dictionary, StringComparer.Ordinal));
            }
            return result;
        }

        /// <summary>
        /// import 的唯一 effect：原子建立一个新的未激活 session，编译候选 program 源码并登记未激活定义。
        ///
        /// 顺序固定：建立定义 → 逐个 program 准备/登记 → 逐个 Hook 登记 dormant → 返回事实。
        /// 任一步失败都回滚本次建立的定义并报告已经加载的不可回收候选程序集；
        /// 不执行 startup 或 cleanup 代码，不重放 recipe 里的任何操作。
        /// </summary>
        internal static async ValueTask<DebugSessionImportResult> ApplyImportAsync(DebugRecipeImportPlan plan, IUnityCliWaiter waiter, TimeSpan timeout, CancellationToken ct)
        {
            var createPlan = plan.CreatePlan;
            var created = DebugSessionHost.ApplyCreate(createPlan, true);
            var installedPrograms = new List<string>();
            var importedHooks = new List<string>();
            try
            {
                foreach (var program in plan.Programs)
                {
                    var installRequest = new DebugProgramInstallRequest
                    {
                        SessionId = created.SessionId,
                        Label = DebugRecipeStore.Text(program, "label"),
                        EntryType = Empty(DebugRecipeStore.Text(program, "entryType"), "code"),
                        Source = DebugRecipeStore.Text(program, "source"),
                        StateSchema = DebugRecipeStore.Value(program, "stateSchema") as Dictionary<string, object>,
                        Entries = ProgramEntries(program),
                        Bindings = ProgramBindings(program)
                    };
                    var installPlan = await DebugProgramHost.PrepareInstallAsync(installRequest, waiter, timeout, ct);
                    var installed = DebugProgramHost.ApplyInstall(installPlan);
                    installedPrograms.Add(installed.ProgramId);
                }
                foreach (var hook in plan.Hooks)
                {
                    // recipe 的 Hook 段全部来自被导出的 session：它声明的 session 引用必须重建为本次新建的 identity。
                    // 沿用旧 identity 会让导入的定义被另一个（或不存在的）session 的派发闸门挡住，
                    // 并把它的命中/计划注入错误地记到那个 session 的预算上。
                    ReestablishSessionReferences(hook, created.SessionId);
                    var imported = MethodHookHost.ImportDormant(hook);
                    if (imported == null)
                        throw InvalidRecipe("hook-content-invalid", "A hook section could not be read as a hook definition.");
                    importedHooks.Add(imported.HookId);
                }
            }
            catch (Exception)
            {
                RollBackImport(created.SessionId, installedPrograms, importedHooks);
                throw;
            }
            return new DebugSessionImportResult
            {
                SessionId = created.SessionId,
                Label = created.Label,
                ActivationScope = created.ActivationScope,
                ClockKind = created.ClockKind,
                State = created.State,
                RestoredWithoutActivation = true,
                ActiveResourceCount = 0L,
                ProgramDefinitions = installedPrograms.Count,
                HookDefinitions = importedHooks.Count,
                CheckDeclarations = plan.CheckCount,
                ClockNames = plan.ClockNames,
                ProgramIds = installedPrograms,
                HookIds = importedHooks,
                RecipeFile = plan.File,
                RecipeChecksum = plan.RecipeChecksum,
                RecipeBytes = plan.RecipeBytes,
                ReplayedOperations = new List<string>(),
                LoadedCandidateAssemblyCount = DebugCompilerOwner.Budget().Count,
                LoadedCandidateAssemblyBytes = DebugCompilerOwner.Budget().Bytes,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DebugSessionHost.DomainGeneration(),
                RuntimeId = DebugSessionHost.RuntimeId(),
                StorePublication = created.StorePublication,
                Note = "Import validated the recipe, compiled the declared program sources as candidate assemblies and registered inactive definitions. Every declared hook keeps its configuration, and its declared session reference is re-established as the new session so the new session owns it; no reference to the exported session survives. A listener's live thread identity is never part of a recipe: the imported listener keeps its declared trigger, conditions and hit budget and matches any managed thread until the caller declares a new thread filter. It executed no startup or cleanup code, attached no detour, armed no listener, replayed no operation and activated no resource; the new session stays inactive until an explicit debug.program.run or debug.hook.enable call in this domain."
            };
        }

        /// <summary>
        /// 把 recipe 中指向被导出 session 的声明引用重建为新的 session identity。
        /// listener 与 breakpoint 的 sessionId 是 Hook 归属的唯一真值（同一判定见 MethodHookHost.SessionIdOf），
        /// 因此导入只改这两个已声明的引用，不新增第二份 owner 字段。
        /// </summary>
        private static void ReestablishSessionReferences(Dictionary<string, object> hook, string sessionId)
        {
            ReestablishSessionReference(hook, "listener", sessionId);
            ReestablishSessionReference(hook, "breakpoint", sessionId);
        }

        /// <summary>单个 session 引用的重建；未声明该段时没有引用可重建。</summary>
        private static void ReestablishSessionReference(Dictionary<string, object> hook, string section, string sessionId)
        {
            var declaration = DebugRecipeStore.Value(hook, section) as IDictionary<string, object>;
            if (declaration == null) return;
            declaration["sessionId"] = sessionId;
        }

        private static List<DebugProgramEntrySpec> ProgramEntries(Dictionary<string, object> program)
        {
            var result = new List<DebugProgramEntrySpec>();
            foreach (var entry in Sections(program, "entries"))
            {
                result.Add(new DebugProgramEntrySpec
                {
                    Name = DebugRecipeStore.Text(entry, "name"),
                    Category = DebugRecipeStore.Text(entry, "category"),
                    DeclaringType = DebugRecipeStore.Text(entry, "declaringType")
                });
            }
            return result;
        }

        private static List<DebugProgramBindingSpec> ProgramBindings(Dictionary<string, object> program)
        {
            var result = new List<DebugProgramBindingSpec>();
            foreach (var binding in Sections(program, "bindings"))
            {
                var target = MethodHookTargetSpec.FromData(DebugRecipeStore.Value(binding, "target"));
                // 目标声明缺失时绑定无法重建：接受已校验合同下的失败，而不是静默丢掉一个绑定。
                if (target == null)
                    throw InvalidRecipe("binding-content-invalid", "A program binding section carries no readable target declaration.");
                result.Add(new DebugProgramBindingSpec
                {
                    BindingId = DebugRecipeStore.Text(binding, "bindingId"),
                    Phase = DebugRecipeStore.Text(binding, "phase"),
                    Entry = DebugRecipeStore.Text(binding, "entry"),
                    Target = target
                });
            }
            return result;
        }

        /// <summary>
        /// 回滚一次失败的 import：删除本次登记的 dormant Hook 定义与 program 定义，再停止并删除本次建立的 session。
        /// 回滚只动本次导入建立的 identity，不触碰用户已有定义。
        /// 新建的 session 处于 open 状态，必须先走同一 stop 管线再 remove：remove 只接受已停止的 session，
        /// 跳过 stop 会让回滚被拒绝并留下半个可激活 session。
        /// </summary>
        private static void RollBackImport(string sessionId, List<string> installedPrograms, List<string> importedHooks)
        {
            foreach (var hookId in importedHooks)
            {
                try { MethodHookHost.RemoveImportedDormant(hookId); }
                catch (Exception exception) { UnityCliBridge.WriteLog("debug_recipe_import_hook_rollback_failed " + exception.GetType().Name); }
            }
            foreach (var programId in installedPrograms)
            {
                try
                {
                    var controller = DebugProgramHost.PrepareRemove(new DebugProgramRemoveRequest { ProgramId = programId });
                    DebugProgramHost.ApplyRemove(controller);
                }
                catch (Exception exception) { UnityCliBridge.WriteLog("debug_recipe_import_program_rollback_failed " + exception.GetType().Name); }
            }
            try
            {
                DebugSessionHost.ApplyStop(DebugSessionHost.PrepareStop(new DebugSessionStopRequest { SessionId = sessionId }));
                DebugSessionHost.ApplyRemove(DebugSessionHost.PrepareRemove(new DebugSessionRemoveRequest { SessionId = sessionId }));
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("debug_recipe_import_session_rollback_failed " + exception.GetType().Name);
            }
        }

        /// <summary>
        /// 构造一个稳定的 recipe 校验失败错误；details 始终包含已经加载而不可回收的候选程序集事实。
        /// </summary>
        private static BridgeCommandException InvalidRecipe(string reason, string message)
        {
            var budget = DebugCompilerOwner.Budget();
            return new BridgeCommandException(
                "E_DEBUG_RECIPE_INVALID",
                "The recipe was rejected (" + reason + "): " + message,
                new Dictionary<string, object>
                {
                    { "reason", reason },
                    { "loadedCandidateAssemblyCount", (long)budget.Count },
                    { "loadedCandidateAssemblyBytes", budget.Bytes },
                    { "maxCompiledArtifacts", DebugLimits.MaxCompiledArtifacts }
                },
                new[]
                {
                    "Fix or re-export the recipe with tools run debug.session.export, then submit debug.session.import again.",
                    "Candidate assemblies already loaded by this attempt stay loaded for this domain; loaded bytes are reported above and are not reclaimed by stopping the session."
                });
        }
    }
}
#endif
