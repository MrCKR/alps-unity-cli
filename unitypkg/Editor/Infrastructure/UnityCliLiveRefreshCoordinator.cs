#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>editor.refresh 的单 scope 进行中操作协调器（统一骨架收编）。</summary>
        internal static class UnityCliLiveRefreshCoordinator
        {
            private static readonly Coordinator Instance = new Coordinator();
            internal const string Command = "internal.editor.refresh.start";
            internal static bool IsCommand(string command) { return Instance.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { Instance.Register(registry); }
            internal static Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return Instance.Execute(request, raw, accept, ct); }
            internal static void Initialize() { }
            internal static void Shutdown() { }
            internal static void BeforeReload() { Instance.Update(x => x["reloadObserved"] = true); }
            internal static void Tick() { Instance.Tick(); }

            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return "refresh"; } }
                protected override string StartCommand { get { return Command; } }
                protected override string StatusCommand { get { return "internal.editor.refresh.status"; } }
                protected override string OperationDisplayName { get { return "Refresh"; } }
                protected override bool IncludeAcceptedInStatus { get { return true; } }
                protected override string SessionChangedMessage { get { return "Refresh result cannot be confirmed after the Editor session changed; check the business state."; } }
                protected override string DeadlineMessage { get { return "Refresh did not reach a confirmed terminal state before the deadline; check the business state."; } }
                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<Args>(); }
                protected override void ValidateStart(object value)
                {
                    var args = (Args)value;
                    if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId)) throw new BridgeCommandException("E_SCHEMA_INVALID", "expectedEditorSessionId is required.");
                    if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before refresh execution.");
                    if (EditorApplication.isPlaying || IsPlayModeChanging()) throw new BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "editor.refresh is editor-only and requires stable Edit Mode.");
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
                    return new Dictionary<string, object> { { "requestId", request.RequestId }, { "editorSessionId", EditorSessionId() }, { "requestDigest", digest }, { "deadline", request.Execution.AbsoluteDeadlineUtc }, { "phase", "queued" }, { "compile", args.Compile }, { "compilationRequested", false }, { "compilationObserved", false }, { "compilePhase", "idle" }, { "compileStartedCycle", 0L }, { "compileDiagnostics", new List<object>() }, { "stableFrames", 0L }, { "beforeSourceFingerprint", before.SourceFingerprint }, { "beforeDependencyVersion", before.DependencyVersion } };
                }
                protected override void OnTick(Dictionary<string, object> root)
                {
                    var phase = Text(root, "phase");
                    if (phase == "queued") { if (BusyEditor()) return; Update(x => x["phase"] = "prepared"); return; }
                    if (phase == "prepared") { Update(x => x["phase"] = "dispatching"); try { AssetDatabase.Refresh(); Update(x => x["phase"] = "refresh_returned"); } catch (Exception e) { Fail(root, "E_EXECUTION_TIMEOUT", "Refresh result cannot be confirmed: " + e.Message + "; check the business state."); } return; }
                    if (phase == "refresh_returned")
                    {
                        if (Bool(root, "compile") && !Bool(root, "compilationObserved") && !Bool(root, "compilationRequested")) { if (BusyEditor()) return; UnityCliCompileCoordinator.RequestRefreshCompilation(Text(root, "requestId")); return; }
                        Update(x => x["phase"] = "waiting_stable"); return;
                    }
                    if (phase != "waiting_stable" || BusyEditor()) return;
                    if (Bool(root, "compile") && (!Bool(root, "compilationObserved") || Text(root, "compilePhase") != "cycle_finished")) { Fail(root, "E_EXECUTION_TIMEOUT", "Refresh compilation result cannot be confirmed; check the business state."); return; }
                    var frames = Long(root, "stableFrames") + 1; Update(x => x["stableFrames"] = frames); if (frames >= 3) Succeed(root);
                }
                protected override object BuildSuccessData(Dictionary<string, object> root)
                {
                    var after = CaptureProof();
                    var changed = !Same(Text(root, "beforeSourceFingerprint"), after.SourceFingerprint) || !Same(Text(root, "beforeDependencyVersion"), after.DependencyVersion);
                    return new Dictionary<string, object>
                    {
                        { "changed", changed }, { "compilationTriggered", Bool(root, "compilationObserved") },
                        { "sourceFileCount", (long)after.Paths.Count }, { "sourceFingerprint", after.SourceFingerprint }, { "dependencyVersion", after.DependencyVersion },
                        { "editorState", new Dictionary<string, object> { { "status", "ready" }, { "compile", false }, { "import", false } } }
                    };
                }
                private sealed class Proof { internal List<string> Paths; internal string SourceFingerprint; internal string DependencyVersion; }
                /// <summary>Refresh 的业务证明：Assets 全量文件指纹 + AssetDatabase 依赖哈希版本。</summary>
                private static Proof CaptureProof()
                {
                    var rootPath = Path.Combine(ProjectRoot(), "Assets");
                    var files = Directory.Exists(rootPath) ? Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray() : new string[0];
                    var lines = new List<string>(); var paths = new List<string>(); var dependencies = new List<string>();
                    foreach (var file in files)
                    {
                        var relative = "Assets/" + Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                        var info = new FileInfo(file);
                        paths.Add(relative);
                        lines.Add(relative + "\0" + info.Length.ToString(CultureInfo.InvariantCulture) + "\0" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\0" + HashFile(file));
                        if (!relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) dependencies.Add(relative + "\0" + AssetDatabase.GetAssetDependencyHash(relative).ToString());
                    }
                    return new Proof { Paths = paths, SourceFingerprint = Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", lines))), DependencyVersion = Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", dependencies))) };
                }
                private static string HashFile(string path) { using (var sha = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
                private static bool BusyEditor() { return EditorApplication.isUpdating || EditorApplication.isCompiling || BuildPipeline.isBuildingPlayer || IsPlayModeChanging(); }
                private static bool Same(string left, string right) { return !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal); }
                private sealed class Args { public bool Compile { get; set; } public string ExpectedEditorSessionId { get; set; } }
            }
        }
    }
}
#endif
