#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>editor.compile 的单 scope 进行中操作协调器。</summary>
        internal static class UnityCliCompileCoordinator
        {
            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return "compile"; } }
                protected override string StartCommand { get { return "internal.editor.compile.start"; } }
                protected override string StatusCommand { get { return "internal.editor.compile.status"; } }
                protected override string OperationDisplayName { get { return "Compile"; } }
                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<StartArgs>(); }
                protected override void ValidateStart(object value)
                {
                    var args = (StartArgs)value;
                    if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Compile input and expectedEditorSessionId are required.");
                    if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed after compile preflight.");
                }
                protected override void ValidateLoadedRoot(Dictionary<string, object> root)
                {
                    ValidateRootContract(root, new[] { "sequence", "runId", "refresh", "path", "forceUpdate", "importRecursive", "includeWarnings", "compilationObserved", "compileObservationSequence", "startedSequence", "diagnostics", "diagnosticErrorCount", "diagnosticWarningCount", "diagnosticInfoCount", "diagnosticsTruncated", "stableFrames", "startedCycle", "startedAt", "finishedAt", "reloadObserved", "rootContractDigest" },
                        "queued", "prepared", "dispatching", "awaiting_started", "compiling", "cycle_finished");
                    object sequence; object refresh; object forceUpdate; object recursive; object warnings; object observed; object diagnostics; object errorCount; object warningCount; object infoCount; object truncated; object stableFrames;
                    if (!root.TryGetValue("sequence", out sequence) || !(sequence is long) || (long)sequence <= 0
                        || string.IsNullOrWhiteSpace(Text(root, "runId"))
                        || !root.TryGetValue("refresh", out refresh) || !(refresh is bool)
                        || !root.TryGetValue("forceUpdate", out forceUpdate) || !(forceUpdate is bool)
                        || !root.TryGetValue("importRecursive", out recursive) || !(recursive is bool)
                        || !root.TryGetValue("includeWarnings", out warnings) || !(warnings is bool)
                        || !root.TryGetValue("compilationObserved", out observed) || !(observed is bool)
                        || !root.TryGetValue("compileObservationSequence", out sequence) || !(sequence is long) || (long)sequence < 0
                        || !root.TryGetValue("startedSequence", out sequence) || !(sequence is long) || (long)sequence < 0
                        || !root.TryGetValue("diagnostics", out diagnostics) || !(diagnostics is List<object>)
                        || !root.TryGetValue("diagnosticErrorCount", out errorCount) || !(errorCount is long) || (long)errorCount < 0 || (long)errorCount > 1000000
                        || !root.TryGetValue("diagnosticWarningCount", out warningCount) || !(warningCount is long) || (long)warningCount < 0 || (long)warningCount > 1000000
                        || !root.TryGetValue("diagnosticInfoCount", out infoCount) || !(infoCount is long) || (long)infoCount < 0 || (long)infoCount > 1000000
                        || !root.TryGetValue("diagnosticsTruncated", out truncated) || !(truncated is long) || (long)truncated < 0 || (long)truncated > 1000000
                        || !root.TryGetValue("stableFrames", out stableFrames) || !(stableFrames is long) || (long)stableFrames < 0
                        || string.IsNullOrWhiteSpace(Text(root, "startedAt")))
                        throw new InvalidDataException("Compile active root fields are invalid.");
                    if (!string.Equals(Text(root, "rootContractDigest"), CompileRootContractDigest(root), StringComparison.Ordinal))
                        throw new InvalidDataException("Compile immutable root contract changed.");
                    ValidateBoundedDiagnostics(root, "diagnostics", "diagnosticErrorCount", "diagnosticWarningCount", "diagnosticInfoCount", "diagnosticsTruncated", (bool)warnings);
                    var phase = Text(root, "phase");
                    var fenced = HasExactEffectFence("compile", root, "asset-refresh-dispatch");
                    if ((phase == "queued" || phase == "prepared") == fenced)
                        throw new InvalidDataException("Compile effect fence is inconsistent with phase.");
                    if ((bool)observed != (phase == "compiling" || phase == "cycle_finished"))
                        throw new InvalidDataException("Compile observation state is inconsistent with phase.");
                    if ((bool)observed && (Long(root, "startedCycle") <= 0 || Long(root, "startedSequence") <= Long(root, "compileObservationSequence")))
                        throw new InvalidDataException("Compile active cycle identity is invalid.");
                }

                private static string CompileRootContractDigest(Dictionary<string, object> root)
                {
                    return UnityCliSerializer.Sha256Canonical(new Dictionary<string, object>
                    {
                        { "sequence", root["sequence"] }, { "runId", root["runId"] }, { "refresh", root["refresh"] },
                        { "path", root["path"] }, { "forceUpdate", root["forceUpdate"] }, { "importRecursive", root["importRecursive"] }, { "includeWarnings", root["includeWarnings"] }
                    });
                }

                protected override object Planned(object value)
                {
                    var args = (StartArgs)value;
                    var effects = new List<object>();
                    if (!string.IsNullOrEmpty(args.Path)) effects.Add(new Dictionary<string, object> { { "type", "asset-import" }, { "path", args.Path } });
                    else effects.Add(new Dictionary<string, object> { { "type", "asset-refresh" } });
                    effects.Add(new Dictionary<string, object> { { "type", "script-compile" } });
                    effects.Add(new Dictionary<string, object> { { "type", "domain-reload" } });
                    return new Dictionary<string, object> { { "plannedEffects", effects } };
                }
                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object value)
                {
                    var args = (StartArgs)value;
                    var root = new Dictionary<string, object> { { "requestId", request.RequestId }, { "editorSessionId", EditorSessionId() }, { "requestDigest", digest }, { "deadline", request.Execution.AbsoluteDeadlineUtc }, { "phase", "queued" }, { "sequence", NextSequenceLocked() }, { "runId", "compile-" + digest.Substring(0, 32) }, { "refresh", string.IsNullOrEmpty(args.Path) }, { "path", args.Path }, { "forceUpdate", args.ForceUpdate ?? false }, { "importRecursive", args.ImportRecursive ?? false }, { "includeWarnings", args.IncludeWarnings ?? true }, { "compilationObserved", false }, { "compileObservationSequence", 0L }, { "startedSequence", 0L }, { "diagnostics", new List<object>() }, { "diagnosticErrorCount", 0L }, { "diagnosticWarningCount", 0L }, { "diagnosticInfoCount", 0L }, { "diagnosticsTruncated", 0L }, { "stableFrames", 0L }, { "startedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, { "reloadObserved", false } };
                    root["rootContractDigest"] = CompileRootContractDigest(root);
                    return root;
                }
                protected override void OnTick(Dictionary<string, object> root)
                {
                    if (UnityCliCompilationEventBroker.SequenceCorrupt) { Fail(root, "E_EXECUTION_TIMEOUT", "Compilation event sequence is corrupt; attributed outcome is unknown."); return; }
                    var phase = Text(root, "phase");
                    if (phase == "queued") { Update(x => x["phase"] = "prepared"); return; }
                    if (phase == "prepared") { if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && !IsDomainReloading()) Dispatch(root); return; }
                    if (phase == "awaiting_started" && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !IsDomainReloading())
                    {
                        var frames = Long(root, "stableFrames") + 1L;
                        root["stableFrames"] = frames;
                        Update(x => x["stableFrames"] = frames);
                        if (frames >= 3L) Complete(root);
                        return;
                    }
                    if (phase == "cycle_finished" && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !IsDomainReloading()) Complete(root);
                }
                protected override object BuildSuccessData(Dictionary<string, object> root)
                {
                    var diagnostics = root["diagnostics"] as List<object> ?? new List<object>(); var typed = diagnostics.Cast<Dictionary<string, object>>().ToList();
                    var errors = Long(root, "diagnosticErrorCount"); var warnings = Long(root, "diagnosticWarningCount");
                    var assemblies = typed.Where(x => !string.IsNullOrEmpty(Text(x, "assembly"))).Select(x => Text(x, "assembly")).Distinct(StringComparer.Ordinal).Count();
                    var startedAt = Text(root, "startedAt"); var finishedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    var durationMs = DateTimeOffset.TryParse(startedAt, out var started) ? (long)Math.Max(0, (DateTimeOffset.UtcNow - started).TotalMilliseconds) : 0L;
                    var refresh = Bool(root, "refresh"); var path = string.IsNullOrEmpty(Text(root, "path")) ? null : Text(root, "path"); var compilationTriggered = Bool(root, "compilationObserved");
                    var status = compilationTriggered ? (warnings > 0 ? "warnings" : "clean") : "unchanged";
                    var compile = new Dictionary<string, object> { { "completed", true }, { "compilationTriggered", compilationTriggered }, { "noOpReason", compilationTriggered ? null : "no-script-changes" }, { "refresh", refresh }, { "path", path }, { "startedAt", startedAt }, { "finishedAt", finishedAt }, { "durationMs", durationMs }, { "assemblyCount", (long)assemblies }, { "errorCount", (long)errors }, { "warningCount", (long)warnings }, { "status", status } };
                    var effects = new List<object>(); if (path != null) effects.Add(new Dictionary<string, object> { { "type", "asset-import" }, { "path", path } }); else if (refresh) effects.Add(new Dictionary<string, object> { { "type", "asset-refresh" } }); if (compilationTriggered) { effects.Add(new Dictionary<string, object> { { "type", "script-compile" } }); effects.Add(new Dictionary<string, object> { { "type", "domain-reload" } }); }
                    var state = new Dictionary<string, object> { { "status", "ready" }, { "compile", new Dictionary<string, object> { { "known", true }, { "value", false } } }, { "import", new Dictionary<string, object> { { "known", true }, { "value", false } } }, { "playMode", new Dictionary<string, object> { { "known", true }, { "value", EditorApplication.isPlaying ? "play" : "edit" } } } };
                    return new Dictionary<string, object> { { "compile", compile }, { "diagnostics", diagnostics }, { "editorState", state }, { "plannedEffects", effects } };
                }
                /// <summary>刷新前冻结事件序号；刷新后的编译是否发生完全由 Unity Asset Pipeline 决定。</summary>
                private void Dispatch(Dictionary<string, object> root)
                {
                    var id = Text(root, "requestId"); if (!string.IsNullOrEmpty(activeDispatchRequestId)) return; activeDispatchRequestId = id;
                    try
                    {
                        var observationSequence = UnityCliCompilationEventBroker.CurrentCompilationEventSequence();
                        if (UnityCliCompilationEventBroker.SequenceCorrupt) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Compilation event sequence is corrupt; refresh is forbidden.");
                        Update(x => { x["compileObservationSequence"] = observationSequence; x["stableFrames"] = 0L; });
                        root["compileObservationSequence"] = observationSequence;
                        root = CrossEffectFence(root, "asset-refresh-dispatch", "dispatching");
                        var path = Text(root, "path");
                        if (!string.IsNullOrEmpty(path)) AssetDatabase.ImportAsset(path);
                        else AssetDatabase.Refresh(Bool(root, "forceUpdate") ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
                        Update(x => { if (Text(x, "phase") == "dispatching") x["phase"] = "awaiting_started"; });
                    }
                    catch (Exception e) { Fail(root, "E_EXECUTION_TIMEOUT", "Compilation refresh result cannot be confirmed: " + e.Message + "; check the business state."); }
                    finally { activeDispatchRequestId = null; }
                }
                private void Complete(Dictionary<string, object> root) { if (Long(root, "diagnosticErrorCount") > 0) Fail(root, "E_UNITY_COMPILE_FAILED", "Unity reported compiler errors.", BuildCompileFailureDetails(root), new[] { "Fix the listed compiler diagnostics before retrying." }); else Succeed(root); }
                private static object BuildCompileFailureDetails(Dictionary<string, object> root)
                {
                    return new Dictionary<string, object>
                    {
                        { "compile", new Dictionary<string, object> { { "completed", true }, { "compilationTriggered", true }, { "noOpReason", null }, { "errorCount", Long(root, "diagnosticErrorCount") }, { "warningCount", Long(root, "diagnosticWarningCount") }, { "status", "errors" }, { "diagnosticsTruncated", Long(root, "diagnosticsTruncated") } } },
                        { "diagnostics", root["diagnostics"] }
                    };
                }
                internal void BeforeReload() { Update(x => x["reloadObserved"] = true); }
                internal long CurrentOwnedCycle() { var root = Load(); return root != null && UnityCliSessionMutationLease.IsOwnedBy(Text(root, "requestId"), Text(root, "requestDigest")) ? Long(root, "startedCycle") : 0; }
                internal void OnCompilationStarted(long cycle, long sequence, DateTime startedAt) { Update(x => { if ((Text(x, "phase") == "dispatching" || Text(x, "phase") == "awaiting_started") && sequence > Long(x, "compileObservationSequence")) { x["startedCycle"] = cycle; x["startedSequence"] = sequence; x["compilationObserved"] = true; x["stableFrames"] = 0L; x["phase"] = "compiling"; } }); }
                internal void OnAssemblyCompilationFinished(long cycle, List<Dictionary<string, object>> diagnostics) { Update(x => { if (Text(x, "phase") == "compiling" && Long(x, "startedCycle") == cycle) AppendBoundedDiagnostics(x, "diagnostics", "diagnosticErrorCount", "diagnosticWarningCount", "diagnosticInfoCount", "diagnosticsTruncated", Bool(x, "includeWarnings"), diagnostics); }); }
                internal void OnCompilationFinished(long cycle, DateTime finishedAt) { Update(x => { if (Text(x, "phase") == "compiling" && Long(x, "startedCycle") == cycle) { x["finishedAt"] = finishedAt.ToString("O", CultureInfo.InvariantCulture); x["phase"] = "cycle_finished"; } }); }
            }
            private static readonly Coordinator operation = new Coordinator();
            private static string activeDispatchRequestId;
            internal static bool IsCommand(string command) { return operation.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { operation.Register(registry); }
            internal static System.Threading.Tasks.Task<string> Execute(BridgeRequest request, byte[] rawBytes, Func<bool> tryAccept, System.Threading.CancellationToken cancellationToken) { return operation.Execute(request, rawBytes, tryAccept, cancellationToken); }
            internal static void Initialize() { }
            internal static void Shutdown() { activeDispatchRequestId = null; }
            internal static void BeforeReload() { operation.BeforeReload(); }
            internal static void Tick() { operation.Tick(); }
            internal static long CurrentOwnedCycle() { return operation.CurrentOwnedCycle(); }
            internal static void OnCompilationStarted(long cycle, long sequence, DateTime startedAt)
            {
                operation.OnCompilationStarted(cycle, sequence, startedAt);
                UpdateRefresh(x =>
                {
                    var phase = Text(x, "compilePhase");
                    if (Bool(x, "compile") && Bool(x, "compilationObservationStarted")
                        && phase == "awaiting_started"
                        && sequence > Long(x, "compileObservationSequence"))
                    {
                        x["compileStartedCycle"] = cycle; x["compileStartedSequence"] = sequence;
                        x["compilePhase"] = "compiling"; x["compilationObserved"] = true; x["stableFrames"] = 0L;
                    }
                });
            }
            internal static void OnAssemblyCompilationFinished(long cycle, long sequence, string assembly, List<Dictionary<string, object>> diagnostics)
            {
                operation.OnAssemblyCompilationFinished(cycle, diagnostics);
                UpdateRefresh(x =>
                {
                    if (Text(x, "compilePhase") == "compiling" && Long(x, "compileStartedCycle") == cycle
                        && sequence > Long(x, "compileStartedSequence"))
                        AppendBoundedDiagnostics(x, "compileDiagnostics", "compileErrorCount", "compileWarningCount", "compileInfoCount", "compileDiagnosticsTruncated", true, diagnostics);
                });
            }
            internal static void OnCompilationFinished(long cycle, long sequence, DateTime finishedAt)
            {
                operation.OnCompilationFinished(cycle, finishedAt);
                UpdateRefresh(x =>
                {
                    if (Text(x, "compilePhase") == "compiling" && Long(x, "compileStartedCycle") == cycle
                        && sequence > Long(x, "compileStartedSequence"))
                    { x["compileFinishedSequence"] = sequence; x["compilePhase"] = "cycle_finished"; }
                });
            }
            private static void UpdateRefresh(Action<Dictionary<string, object>> action)
            {
                if (!UnityCliActiveOperation.Exists("refresh")) return;
                var root = UnityCliActiveOperation.Load("refresh");
                if (!UnityCliSessionMutationLease.IsOwnedBy(Text(root, "requestId"), Text(root, "requestDigest"))) return;
                action(root);
                UnityCliActiveOperation.Save("refresh", root);
            }
            /// <summary>回调诊断只写入固定sample预算；aggregate计数始终覆盖全部归属消息。</summary>
            private static void AppendBoundedDiagnostics(Dictionary<string, object> root, string listKey, string errorKey, string warningKey, string infoKey, string truncatedKey, bool includeWarnings, IEnumerable<Dictionary<string, object>> diagnostics)
            {
                var target = root[listKey] as List<object>;
                if (target == null) throw new InvalidDataException("Compilation diagnostic list is invalid.");
                foreach (var source in diagnostics ?? new List<Dictionary<string, object>>())
                {
                    var severity = Text(source, "severity");
                    if (severity == "error") root[errorKey] = Long(root, errorKey) + 1;
                    else if (severity == "warning") root[warningKey] = Long(root, warningKey) + 1;
                    else root[infoKey] = Long(root, infoKey) + 1;
                    if (severity == "warning" && !includeWarnings) continue;
                    var bounded = new Dictionary<string, object>
                    {
                        { "assembly", BoundedDiagnosticText(Text(source, "assembly"), 1024) }, { "severity", severity },
                        { "code", BoundedDiagnosticText(Text(source, "code"), 64) }, { "message", BoundedDiagnosticText(Text(source, "message"), 4096) },
                        { "file", BoundedDiagnosticText(Text(source, "file"), 2048) },
                        { "line", DiagnosticNumber(source, "line") }, { "column", DiagnosticNumber(source, "column") }
                    };
                    if (target.Count >= 200 || Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(target.Concat(new object[] { bounded }).ToList())) > 512 * 1024)
                    { root[truncatedKey] = Long(root, truncatedKey) + 1; continue; }
                    target.Add(bounded);
                }
            }

            /// <summary>reload后按精确schema、预算和aggregate关系校验diagnostics，损坏root必须fail closed。</summary>
            internal static void ValidateBoundedDiagnostics(Dictionary<string, object> root, string listKey, string errorKey, string warningKey, string infoKey, string truncatedKey, bool includeWarnings)
            {
                var values = root[listKey] as List<object>;
                if (values == null || values.Count > 200 || Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(values)) > 512 * 1024)
                    throw new InvalidDataException("Compilation diagnostic sample exceeds its fixed budget.");
                long sampledErrors = 0; long sampledWarnings = 0; long sampledInfo = 0;
                var allowed = new HashSet<string>(new[] { "assembly", "severity", "code", "message", "file", "line", "column" }, StringComparer.Ordinal);
                foreach (var value in values)
                {
                    var item = value as Dictionary<string, object>;
                    object assembly; object severityValue; object code; object message; object file; object line; object column;
                    if (item == null || item.Keys.Any(key => !allowed.Contains(key)) || item.Count != allowed.Count
                        || !item.TryGetValue("assembly", out assembly) || !(assembly is string) || ((string)assembly).Length > 1024
                        || !item.TryGetValue("severity", out severityValue) || !(severityValue is string)
                        || !item.TryGetValue("code", out code) || !(code is string) || ((string)code).Length > 64
                        || !item.TryGetValue("message", out message) || !(message is string) || ((string)message).Length > 4096
                        || !item.TryGetValue("file", out file) || !(file is string) || ((string)file).Length > 2048
                        || !item.TryGetValue("line", out line) || !(line is long) || (long)line < 0
                        || !item.TryGetValue("column", out column) || !(column is long) || (long)column < 0)
                        throw new InvalidDataException("Compilation diagnostic entry is invalid.");
                    var severity = Text(item, "severity");
                    if (severity == "error") sampledErrors++;
                    else if (severity == "warning") sampledWarnings++;
                    else if (severity == "info") sampledInfo++;
                    else throw new InvalidDataException("Compilation diagnostic severity is invalid.");
                }
                var errors = Long(root, errorKey); var warnings = Long(root, warningKey); var info = Long(root, infoKey); var truncated = Long(root, truncatedKey);
                var excludedWarnings = includeWarnings ? 0 : warnings;
                if (sampledErrors > errors || sampledWarnings > warnings || sampledInfo > info || !includeWarnings && sampledWarnings != 0
                    || errors + warnings + info != sampledErrors + sampledWarnings + sampledInfo + truncated + excludedWarnings)
                    throw new InvalidDataException("Compilation diagnostic aggregate is inconsistent with its sample.");
            }
            private static long DiagnosticNumber(Dictionary<string, object> source, string key) { object value; return source.TryGetValue(key, out value) && value is long && (long)value >= 0 ? (long)value : 0L; }
            private static string BoundedDiagnosticText(string value, int maxLength) { value = value ?? string.Empty; return value.Length <= maxLength ? value : value.Substring(0, maxLength); }

            private static long NextSequenceLocked() { var path = Path.Combine(ProjectRoot(), "Library", "UnityCli", "compile-sequence.txt"); Directory.CreateDirectory(Path.GetDirectoryName(path)); string text; try { text = File.ReadAllText(path); } catch (FileNotFoundException) { text = "0"; } catch (DirectoryNotFoundException) { text = "0"; } var value = long.Parse(text, CultureInfo.InvariantCulture); value = checked(value + 1); DurableWriteText(path, value.ToString(CultureInfo.InvariantCulture)); return value; }
            private static string Text(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : string.Empty; }
            private static bool Bool(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is bool && (bool)item; }
            private static long Long(IDictionary<string, object> value, string key) { long result; return long.TryParse(Text(value, key), out result) ? result : 0; }
            private sealed class StartArgs { public string Path { get; set; } public bool? ForceUpdate { get; set; } public bool? ImportRecursive { get; set; } public bool? IncludeWarnings { get; set; } public string ExpectedEditorSessionId { get; set; } }
        }
    }
}
#endif
