#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
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
                protected override object Planned(object value) { return new Dictionary<string, object> { { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", "script-compile" } } } } }; }
                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object value)
                {
                    var args = (StartArgs)value;
                    return new Dictionary<string, object> { { "requestId", request.RequestId }, { "editorSessionId", EditorSessionId() }, { "requestDigest", digest }, { "deadline", request.Execution.AbsoluteDeadlineUtc }, { "phase", "queued" }, { "sequence", NextSequenceLocked() }, { "runId", "compile-" + digest.Substring(0, 32) }, { "refresh", args.Refresh ?? true }, { "path", args.Path }, { "forceUpdate", args.ForceUpdate ?? false }, { "importRecursive", args.ImportRecursive ?? false }, { "includeWarnings", args.IncludeWarnings ?? true }, { "diagnostics", new List<object>() } };
                }
                protected override void OnTick(Dictionary<string, object> root)
                {
                    var phase = Text(root, "phase");
                    if (phase == "queued") { Update(x => x["phase"] = "prepared"); return; }
                    if (phase == "prepared") { if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && !IsDomainReloading()) Dispatch(root); return; }
                    if (phase == "cycle_finished" && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !IsDomainReloading()) Complete(root);
                }
                protected override object BuildSuccessData(Dictionary<string, object> root)
                {
                    var diagnostics = root["diagnostics"] as List<object> ?? new List<object>(); var typed = diagnostics.Cast<Dictionary<string, object>>().ToList();
                    var errors = typed.Count(x => Text(x, "severity") == "error"); var warnings = typed.Count(x => Text(x, "severity") == "warning");
                    var assemblies = typed.Where(x => !string.IsNullOrEmpty(Text(x, "assembly"))).Select(x => Text(x, "assembly")).Distinct(StringComparer.Ordinal).Count();
                    var startedAt = Text(root, "startedAt"); var finishedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    var durationMs = DateTimeOffset.TryParse(startedAt, out var started) ? (long)Math.Max(0, (DateTimeOffset.UtcNow - started).TotalMilliseconds) : 0L;
                    var refresh = Bool(root, "refresh"); var path = string.IsNullOrEmpty(Text(root, "path")) ? null : Text(root, "path");
                    var compile = new Dictionary<string, object> { { "completed", true }, { "refresh", refresh }, { "path", path }, { "startedAt", startedAt }, { "finishedAt", finishedAt }, { "durationMs", durationMs }, { "assemblyCount", (long)assemblies }, { "errorCount", (long)errors }, { "warningCount", (long)warnings }, { "status", warnings > 0 ? "warnings" : "clean" } };
                    var effects = new List<object>(); if (path != null) effects.Add(new Dictionary<string, object> { { "type", "asset-import" }, { "path", path } }); else if (refresh) effects.Add(new Dictionary<string, object> { { "type", "asset-refresh" } }); effects.Add(new Dictionary<string, object> { { "type", "script-compile" } }); effects.Add(new Dictionary<string, object> { { "type", "domain-reload" } });
                    var state = new Dictionary<string, object> { { "status", "ready" }, { "compile", new Dictionary<string, object> { { "known", true }, { "value", false } } }, { "import", new Dictionary<string, object> { { "known", true }, { "value", false } } }, { "playMode", new Dictionary<string, object> { { "known", true }, { "value", EditorApplication.isPlaying ? "play" : "edit" } } } };
                    return new Dictionary<string, object> { { "compile", compile }, { "diagnostics", diagnostics }, { "editorState", state }, { "plannedEffects", effects } };
                }
                private void Dispatch(Dictionary<string, object> root)
                {
                    var id = Text(root, "requestId"); if (!string.IsNullOrEmpty(activeDispatchRequestId)) return; activeDispatchRequestId = id;
                    try { Update(x => x["phase"] = "dispatching"); var path = Text(root, "path"); if (!string.IsNullOrEmpty(path)) AssetDatabase.ImportAsset(path); else if (Bool(root, "refresh")) AssetDatabase.Refresh(Bool(root, "forceUpdate") ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default); if (!EditorApplication.isCompiling) CompilationPipeline.RequestScriptCompilation(); Update(x => { if (Text(x, "phase") == "dispatching") x["phase"] = "awaiting_started"; }); }
                    catch (Exception e) { Fail(root, "E_EXECUTION_TIMEOUT", "Compilation dispatch result cannot be confirmed: " + e.Message + "; check the business state."); }
                    finally { activeDispatchRequestId = null; }
                }
                private void Complete(Dictionary<string, object> root) { var diagnostics = root["diagnostics"] as List<object> ?? new List<object>(); if (diagnostics.Cast<Dictionary<string, object>>().Any(x => Text(x, "severity") == "error")) Fail(root, "E_UNITY_COMPILE_FAILED", "Unity reported compiler errors."); else Succeed(root); }
                internal void BeforeReload() { Update(x => x["reloadObserved"] = true); }
                internal long CurrentOwnedCycle() { var root = Load(); return root != null && UnityCliSessionMutationLease.IsOwnedBy(Text(root, "requestId")) ? Long(root, "startedCycle") : 0; }
                internal void OnCompilationStarted(long cycle, DateTime startedAt) { Update(x => { if (Text(x, "phase") == "dispatching" || Text(x, "phase") == "awaiting_started") { x["startedCycle"] = cycle; x["startedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture); x["phase"] = "compiling"; } }); }
                internal void OnAssemblyCompilationFinished(long cycle, List<Dictionary<string, object>> diagnostics) { Update(x => { if (Text(x, "phase") == "compiling" && Long(x, "startedCycle") == cycle) foreach (var diagnostic in diagnostics ?? new List<Dictionary<string, object>>()) ((List<object>)x["diagnostics"]).Add(diagnostic); }); }
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
            internal static void RequestRefreshCompilation(string requestId)
            {
                if (!UnityCliSessionMutationLease.IsOwnedBy(requestId)) throw new BridgeCommandException("E_UNITY_BUSY", "Refresh root does not own the mutation lease.");
                UpdateRefresh(requestId, x => { x["compilationRequested"] = true; x["compilePhase"] = "dispatching"; }); CompilationPipeline.RequestScriptCompilation(); UpdateRefresh(requestId, x => { if (Text(x, "compilePhase") == "dispatching") x["compilePhase"] = "awaiting_started"; });
            }
            internal static void OnCompilationStarted(long cycle, long sequence, DateTime startedAt) { operation.OnCompilationStarted(cycle, startedAt); UpdateRefresh(null, x => { if (Bool(x, "compile")) { x["compileStartedCycle"] = cycle; x["compilePhase"] = "compiling"; x["compilationObserved"] = true; } }); }
            internal static void OnAssemblyCompilationFinished(long cycle, long sequence, string assembly, List<Dictionary<string, object>> diagnostics) { operation.OnAssemblyCompilationFinished(cycle, diagnostics); UpdateRefresh(null, x => { if (Text(x, "compilePhase") == "compiling") foreach (var diagnostic in diagnostics ?? new List<Dictionary<string, object>>()) ((List<object>)x["compileDiagnostics"]).Add(diagnostic); }); }
            internal static void OnCompilationFinished(long cycle, long sequence, DateTime finishedAt) { operation.OnCompilationFinished(cycle, finishedAt); UpdateRefresh(null, x => { if (Text(x, "compilePhase") == "compiling" && Long(x, "compileStartedCycle") == cycle) x["compilePhase"] = "cycle_finished"; }); }
            private static void UpdateRefresh(string id, Action<Dictionary<string, object>> action) { if (!UnityCliActiveOperation.Exists("refresh")) return; var root = UnityCliActiveOperation.Load("refresh"); if (id != null && Text(root, "requestId") != id) return; action(root); UnityCliActiveOperation.Save("refresh", root); }
            private static long NextSequenceLocked() { var path = Path.Combine(ProjectRoot(), "Library", "UnityCli", "compile-sequence.txt"); Directory.CreateDirectory(Path.GetDirectoryName(path)); var value = File.Exists(path) ? long.Parse(File.ReadAllText(path), CultureInfo.InvariantCulture) : 0; value = checked(value + 1); DurableWriteText(path, value.ToString(CultureInfo.InvariantCulture)); return value; }
            private static string Text(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : string.Empty; }
            private static bool Bool(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is bool && (bool)item; }
            private static long Long(IDictionary<string, object> value, string key) { long result; return long.TryParse(Text(value, key), out result) ? result : 0; }
            private sealed class StartArgs { public bool? Refresh { get; set; } public string Path { get; set; } public bool? ForceUpdate { get; set; } public bool? ImportRecursive { get; set; } public bool? IncludeWarnings { get; set; } public string ExpectedEditorSessionId { get; set; } }
        }
    }
}
#endif
