#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>editor.refresh 的单 scope durable operation；proof 只使用 Unity-owned artifact generations。</summary>
        internal static class UnityCliLiveRefreshCoordinator
        {
            private static readonly Coordinator Instance = new Coordinator();
            internal const string Command = "internal.editor.refresh.start";
            internal static bool IsCommand(string command) { return Instance.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { Instance.Register(registry); }
            internal static Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return Instance.Execute(request, raw, accept, ct); }
            internal static void Initialize() { }
            internal static void Shutdown() { }
            internal static void BeforeReload() { Instance.Update(root => root["reloadObserved"] = true); }
            internal static void Tick() { Instance.Tick(); }

            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return "refresh"; } }
                protected override string StartCommand { get { return Command; } }
                protected override string StatusCommand { get { return "internal.editor.refresh.status"; } }
                protected override string OperationDisplayName { get { return "Refresh"; } }
                protected override bool IncludeAcceptedInStatus { get { return true; } }
                protected override string SessionChangedMessage { get { return "Refresh result cannot be confirmed after the Editor session changed; inspect editor.state and asset state."; } }
                protected override string DeadlineMessage { get { return "Refresh did not reach a confirmed terminal state before the shared deadline; inspect editor.state and asset state."; } }
                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<Args>(); }

                protected override void ValidateStart(object value)
                {
                    var args = (Args)value;
                    if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId)) throw new BridgeCommandException("E_SCHEMA_INVALID", "expectedEditorSessionId is required.");
                    if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before refresh execution.");
                    if (EditorApplication.isPlaying || IsPlayModeChanging()) throw new BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "editor.refresh is editor-only and requires stable Edit Mode.");
                }

                protected override void ValidateLoadedRoot(Dictionary<string, object> root)
                {
                    ValidateRootContract(root, new[]
                    {
                        "compile", "compilationRequested", "compilationObserved", "compilePhase", "compileDispatchSequence", "compileStartedSequence", "compileFinishedSequence", "compileStartedCycle",
                        "compileDiagnostics", "compileErrorCount", "compileWarningCount", "compileInfoCount", "compileDiagnosticsTruncated", "stableFrames",
                        "beforeDependencyGeneration", "beforeProcessedGeneration", "importObserved", "reloadObserved", "rootContractDigest"
                    }, "queued", "prepared", "dispatching", "refresh_returned", "waiting_stable");
                    object compile; object requested; object observed; object diagnostics; object errorCount; object warningCount; object infoCount; object truncated; object stableFrames;
                    if (!root.TryGetValue("compile", out compile) || !(compile is bool)
                        || !root.TryGetValue("compilationRequested", out requested) || !(requested is bool)
                        || !root.TryGetValue("compilationObserved", out observed) || !(observed is bool)
                        || !root.TryGetValue("compileDiagnostics", out diagnostics) || !(diagnostics is List<object>)
                        || !root.TryGetValue("compileErrorCount", out errorCount) || !(errorCount is long) || (long)errorCount < 0L || (long)errorCount > 1000000L
                        || !root.TryGetValue("compileWarningCount", out warningCount) || !(warningCount is long) || (long)warningCount < 0L || (long)warningCount > 1000000L
                        || !root.TryGetValue("compileInfoCount", out infoCount) || !(infoCount is long) || (long)infoCount < 0L || (long)infoCount > 1000000L
                        || !root.TryGetValue("compileDiagnosticsTruncated", out truncated) || !(truncated is long) || (long)truncated < 0L || (long)truncated > 1000000L
                        || !root.TryGetValue("stableFrames", out stableFrames) || !(stableFrames is long) || (long)stableFrames < 0L
                        || !(root["beforeDependencyGeneration"] is long) || (long)root["beforeDependencyGeneration"] < 0L
                        || !(root["beforeProcessedGeneration"] is long) || (long)root["beforeProcessedGeneration"] < 0L
                        || !(root["importObserved"] is bool))
                        throw new InvalidDataException("Refresh active root fields are invalid.");
                    if (!string.Equals(Text(root, "rootContractDigest"), RefreshRootContractDigest(root), StringComparison.Ordinal)) throw new InvalidDataException("Refresh immutable root contract changed.");
                    UnityCliCompileCoordinator.ValidateBoundedDiagnostics(root, "compileDiagnostics", "compileErrorCount", "compileWarningCount", "compileInfoCount", "compileDiagnosticsTruncated", true);
                    var compilePhase = Text(root, "compilePhase");
                    if (Array.IndexOf(new[] { "idle", "dispatching", "awaiting_started", "compiling", "cycle_finished" }, compilePhase) < 0) throw new InvalidDataException("Refresh compile phase is invalid.");
                    var refreshFenced = HasExactEffectFence("refresh", root, "refresh-dispatch");
                    var phase = Text(root, "phase");
                    if ((phase == "queued" || phase == "prepared") == refreshFenced) throw new InvalidDataException("Refresh effect fence is inconsistent with phase.");
                    var compileFenced = HasExactEffectFence("refresh", root, "compile-dispatch");
                    if ((compilePhase == "idle") == compileFenced) throw new InvalidDataException("Refresh compile fence is inconsistent with phase.");
                    if (Bool(root, "compilationObserved") && (Long(root, "compileStartedCycle") <= 0L || Long(root, "compileStartedSequence") <= Long(root, "compileDispatchSequence")))
                        throw new InvalidDataException("Refresh compile attribution is invalid.");
                }

                private static string RefreshRootContractDigest(Dictionary<string, object> root)
                {
                    return UnityCliSerializer.Sha256Canonical(new Dictionary<string, object>
                    {
                        { "compile", root["compile"] }, { "beforeDependencyGeneration", root["beforeDependencyGeneration"] }, { "beforeProcessedGeneration", root["beforeProcessedGeneration"] }
                    });
                }

                protected override object Planned(object value)
                {
                    var args = (Args)value;
                    var effects = new List<object> { new Dictionary<string, object> { { "type", "asset-refresh" } } };
                    if (args.Compile) effects.Add(new Dictionary<string, object> { { "type", "script-compile" } });
                    return new Dictionary<string, object> { { "dryRun", true }, { "plannedEffects", effects } };
                }

                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object value)
                {
                    var args = (Args)value;
                    var before = CaptureProof();
                    var root = new Dictionary<string, object>
                    {
                        { "requestId", request.RequestId }, { "editorSessionId", EditorSessionId() }, { "requestDigest", digest }, { "deadline", request.Execution.AbsoluteDeadlineUtc },
                        { "phase", "queued" }, { "compile", args.Compile }, { "compilationRequested", false }, { "compilationObserved", false }, { "compilePhase", "idle" },
                        { "compileDispatchSequence", 0L }, { "compileStartedSequence", 0L }, { "compileFinishedSequence", 0L }, { "compileStartedCycle", 0L },
                        { "compileDiagnostics", new List<object>() }, { "compileErrorCount", 0L }, { "compileWarningCount", 0L }, { "compileInfoCount", 0L }, { "compileDiagnosticsTruncated", 0L },
                        { "stableFrames", 0L }, { "beforeDependencyGeneration", before.DependencyGeneration }, { "beforeProcessedGeneration", before.ProcessedGeneration },
                        { "importObserved", false }, { "reloadObserved", false }
                    };
                    root["rootContractDigest"] = RefreshRootContractDigest(root);
                    return root;
                }

                protected override void OnTick(Dictionary<string, object> root)
                {
                    if (UnityCliCompilationEventBroker.SequenceCorrupt && Bool(root, "compile")) { Fail(root, "E_EXECUTION_TIMEOUT", "Refresh compilation event sequence is corrupt; attributed outcome is unknown."); return; }
                    var phase = Text(root, "phase");
                    if (phase != "queued" && phase != "prepared") ObserveImport(root);
                    if (phase == "queued") { if (BusyEditor()) return; Update(value => value["phase"] = "prepared"); return; }
                    if (phase == "prepared")
                    {
                        root = CrossEffectFence(root, "refresh-dispatch", "dispatching");
                        try { AssetDatabase.Refresh(); Update(value => value["phase"] = "refresh_returned"); }
                        catch (Exception exception) { Fail(root, "E_EXECUTION_TIMEOUT", "Refresh result cannot be confirmed: " + exception.Message + "; inspect asset state."); }
                        return;
                    }
                    if (phase == "refresh_returned")
                    {
                        if (Bool(root, "compile") && !Bool(root, "compilationObserved") && !Bool(root, "compilationRequested"))
                        {
                            if (BusyEditor()) return;
                            UnityCliCompileCoordinator.RequestRefreshCompilation(Text(root, "requestId"), Text(root, "requestDigest"));
                            return;
                        }
                        Update(value => value["phase"] = "waiting_stable");
                        return;
                    }
                    if (phase != "waiting_stable" || BusyEditor()) return;
                    if (Bool(root, "compile") && (!Bool(root, "compilationObserved") || Text(root, "compilePhase") != "cycle_finished"))
                    {
                        Fail(root, "E_EXECUTION_TIMEOUT", "Refresh compilation result cannot be confirmed; inspect editor.state and compile diagnostics.");
                        return;
                    }
                    if (Long(root, "compileErrorCount") > 0L)
                    {
                        var details = new Dictionary<string, object>
                        {
                            { "compile", new Dictionary<string, object> { { "completed", true }, { "errorCount", Long(root, "compileErrorCount") }, { "warningCount", Long(root, "compileWarningCount") }, { "status", "errors" }, { "diagnosticsTruncated", Long(root, "compileDiagnosticsTruncated") } } },
                            { "diagnostics", root["compileDiagnostics"] }
                        };
                        Fail(root, "E_UNITY_COMPILE_FAILED", "Refresh compilation reported compiler errors.", details, new[] { "Fix the listed compiler diagnostics before retrying refresh." });
                        return;
                    }
                    var frames = Long(root, "stableFrames") + 1L;
                    Update(value => value["stableFrames"] = frames);
                    if (frames >= 3L) Succeed(root);
                }

                protected override object BuildSuccessData(Dictionary<string, object> root)
                {
                    var after = CaptureProof();
                    var changed = Long(root, "beforeDependencyGeneration") != after.DependencyGeneration || Long(root, "beforeProcessedGeneration") != after.ProcessedGeneration;
                    return new Dictionary<string, object>
                    {
                        { "changed", changed }, { "importObserved", Bool(root, "importObserved") || changed }, { "compilationTriggered", Bool(root, "compilationObserved") },
                        { "generation", new Dictionary<string, object> { { "dependencyBefore", Long(root, "beforeDependencyGeneration") }, { "dependencyAfter", after.DependencyGeneration }, { "processedBefore", Long(root, "beforeProcessedGeneration") }, { "processedAfter", after.ProcessedGeneration } } },
                        { "editorState", new Dictionary<string, object> { { "status", "ready" }, { "compile", false }, { "import", false } } }
                    };
                }

                private void ObserveImport(Dictionary<string, object> root)
                {
                    if (Bool(root, "importObserved")) return;
                    var current = CaptureProof();
                    if (!EditorApplication.isUpdating && Long(root, "beforeDependencyGeneration") == current.DependencyGeneration && Long(root, "beforeProcessedGeneration") == current.ProcessedGeneration) return;
                    root["importObserved"] = true;
                    Update(value => value["importObserved"] = true);
                }

                private sealed class Proof { internal long DependencyGeneration; internal long ProcessedGeneration; }

                /// <summary>O(1) Unity-owned generation proof；不读取 Assets 文件，不计算 per-file dependency hash。</summary>
                private static Proof CaptureProof()
                {
                    return new Proof
                    {
                        DependencyGeneration = Math.Max(0L, (long)AssetDatabase.GlobalArtifactDependencyVersion),
                        ProcessedGeneration = Math.Max(0L, (long)AssetDatabase.GlobalArtifactProcessedVersion)
                    };
                }

                private static bool BusyEditor() { return EditorApplication.isUpdating || EditorApplication.isCompiling || BuildPipeline.isBuildingPlayer || IsPlayModeChanging(); }
                private sealed class Args { public bool Compile { get; set; } public string ExpectedEditorSessionId { get; set; } }
            }
        }
    }
}
#endif
