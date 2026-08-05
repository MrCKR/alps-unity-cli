#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>asset.refresh 的跨 domain reload 重导入协调器。</summary>
        internal static class UnityCliAssetRefreshCoordinator
        {
            private static readonly Coordinator Instance = new Coordinator();
            internal static bool IsCommand(string command) { return Instance.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { Instance.Register(registry); }
            internal static Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return Instance.Execute(request, raw, accept, ct); }
            internal static void Initialize() { }
            internal static void Shutdown() { }
            internal static void BeforeReload() { Instance.Update(root => root["reloadObserved"] = true); }
            internal static void Tick() { Instance.Tick(); }

            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return "asset-refresh"; } }
                protected override string StartCommand { get { return "internal.asset.refresh.start"; } }
                protected override string StatusCommand { get { return "internal.asset.refresh.status"; } }
                protected override string OperationDisplayName { get { return "Asset refresh"; } }
                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<Args>(); }
                protected override void ValidateStart(object value)
                {
                    var args = (Args)value;
                    if (args == null || string.IsNullOrEmpty(args.Path ?? args.AssetPath ?? args.Target))
                        throw new BridgeCommandException("E_ASSET_REFRESH_PROOF_MISSING", "asset.refresh requires one asset path because whole-database refresh has no attributable completion proof.", "Pass an Assets/* path to reimport one observable asset.");
                    if (EditorApplication.isPlaying || IsPlayModeChanging()) throw new BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "asset.refresh is editor-only and requires stable Edit Mode.");
                }
                protected override object Planned(object value)
                {
                    var args = (Args)value; var path = args.Path ?? args.AssetPath ?? args.Target; var options = Options(args);
                    return new Result { DryRun = true, Path = path, Options = options.ToString(), PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.import", Path = path, SideEffects = options.ToString() } } };
                }
                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object value)
                {
                    var args = (Args)value; var path = AssetToolSupport.RequireAssetPath(args.Path ?? args.AssetPath ?? args.Target); var fullPath = AssetToolSupport.AssetFullPath(path);
                    return new Dictionary<string, object> { { "requestId", request.RequestId }, { "editorSessionId", EditorSessionId() }, { "requestDigest", digest }, { "deadline", request.Execution.AbsoluteDeadlineUtc }, { "phase", "queued" }, { "path", path }, { "options", Options(args).ToString() }, { "beforeHash", File.Exists(fullPath) ? FileSha256(fullPath) : null } };
                }
                protected override void OnTick(Dictionary<string, object> root)
                {
                    var phase = Text(root, "phase");
                    if (phase == "queued") { if (BusyEditor()) return; Update(x => x["phase"] = "dispatched"); return; }
                    if (phase == "dispatched")
                    {
                        // ImportAsset 可能在返回前触发 domain reload；必须先落盘，避免重载后重复执行副作用。
                        Update(x => x["phase"] = "waiting_stable");
                        try { AssetDatabase.ImportAsset(Text(root, "path"), (ImportAssetOptions)Enum.Parse(typeof(ImportAssetOptions), Text(root, "options"))); }
                        catch (Exception exception) { Fail(root, "E_ASSET_REFRESH_PROOF_MISSING", "The reimported asset could not be dispatched: " + exception.Message); }
                        return;
                    }
                    if (phase != "waiting_stable" || BusyEditor()) return;
                    var fullPath = AssetToolSupport.AssetFullPath(Text(root, "path"));
                    if (File.Exists(fullPath) && string.Equals(FileSha256(fullPath), Text(root, "beforeHash"), StringComparison.Ordinal) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(Text(root, "path")))) Succeed(root);
                }
                protected override object BuildSuccessData(Dictionary<string, object> root)
                {
                    return new Result { Path = Text(root, "path"), Options = Text(root, "options"), Refreshed = true, PlannedEffects = new List<AssetPlannedEffect> { new AssetPlannedEffect { Type = "asset.import", Path = Text(root, "path") } } };
                }
                private static ImportAssetOptions Options(Args args) { var options = ImportAssetOptions.Default; if (args.ForceUpdate ?? false) options |= ImportAssetOptions.ForceUpdate; if (args.ImportRecursive ?? false) options |= ImportAssetOptions.ImportRecursive; return options; }
                private static bool BusyEditor() { return EditorApplication.isUpdating || EditorApplication.isCompiling || BuildPipeline.isBuildingPlayer || IsPlayModeChanging(); }
                private static string FileSha256(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return string.Concat(Array.ConvertAll(sha.ComputeHash(stream), value => value.ToString("x2"))); }
            }
            private sealed class Args { public string Path { get; set; } public string AssetPath { get; set; } public string Target { get; set; } public bool? ForceUpdate { get; set; } public bool? ImportRecursive { get; set; } }
            private sealed class Result { public string Path { get; set; } public string Options { get; set; } public bool? DryRun { get; set; } public bool Refreshed { get; set; } public List<AssetPlannedEffect> PlannedEffects { get; set; } }
        }
    }
}
#endif
