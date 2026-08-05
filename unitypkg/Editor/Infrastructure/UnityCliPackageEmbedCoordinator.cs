#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>package.embed 的跨 domain reload Package Manager 协调器。</summary>
        internal static class UnityCliPackageEmbedCoordinator
        {
            private static readonly Coordinator Instance = new Coordinator();
            private static readonly Dictionary<string, object> ActiveRequests = new Dictionary<string, object>(StringComparer.Ordinal);
            internal static bool IsCommand(string command) { return Instance.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { Instance.Register(registry); }
            internal static Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return Instance.Execute(request, raw, accept, ct); }
            internal static void Initialize() { ActiveRequests.Clear(); }
            internal static void Shutdown() { ActiveRequests.Clear(); }
            internal static void BeforeReload() { ActiveRequests.Clear(); Instance.Update(root => root["reloadObserved"] = true); }
            internal static void Tick() { Instance.Tick(); }

            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return "package-embed"; } }
                protected override string StartCommand { get { return "internal.package.embed.start"; } }
                protected override string StatusCommand { get { return "internal.package.embed.status"; } }
                protected override string OperationDisplayName { get { return "Package embed"; } }
                protected override int CommandTimeoutMs { get { return 120000; } }
                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<Args>(); }
                protected override void ValidateStart(object value)
                {
                    var args = (Args)value;
                    if (args == null || string.IsNullOrEmpty(args.Package)) throw new BridgeCommandException("E_PACKAGE_ID_REQUIRED", "package.embed requires --package <id>.", "Pass a package id like com.unity.x.");
                    if (EditorApplication.isPlaying || IsPlayModeChanging()) throw new BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "package.embed is editor-only and requires stable Edit Mode.");
                }
                protected override object Planned(object value)
                {
                    var args = (Args)value;
                    return new Result { PackageId = args.Package, EmbeddedPath = "Packages/" + args.Package, PlannedEffects = Effects(args.Package) };
                }
                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object value)
                {
                    var args = (Args)value;
                    return new Dictionary<string, object> { { "requestId", request.RequestId }, { "editorSessionId", EditorSessionId() }, { "requestDigest", digest }, { "deadline", request.Execution.AbsoluteDeadlineUtc }, { "phase", "queued" }, { "package", args.Package } };
                }
                protected override void OnTick(Dictionary<string, object> root)
                {
                    var phase = Text(root, "phase");
                    if (phase == "queued")
                    {
                        if (BusyEditor()) return;
                        var method = typeof(UnityEditor.PackageManager.Client).GetMethod("Embed", BindingFlags.Static | BindingFlags.Public);
                        if (method == null) { Fail(root, "E_PACKAGE_EMBED_UNSUPPORTED", "PackageClient.Embed is not available in this Unity version."); return; }
                        // Embed 可能在返回前触发 domain reload；先记录已发起，重载后只重新观察业务结果。
                        Update(x => x["phase"] = "embed_started");
                        try
                        {
                            ActiveRequests[Text(root, "requestId")] = method.Invoke(null, new object[] { Text(root, "package") });
                            Update(x => x["phase"] = "waiting_completed");
                        }
                        catch (Exception exception) { Fail(root, "E_PACKAGE_EMBED_FAILED", exception.InnerException == null ? exception.Message : exception.InnerException.Message); }
                        return;
                    }
                    if (phase == "embed_started")
                    {
                        // domain reload 后 request 对象已失效，直接转入可持久重建的 PackageInfo/磁盘观察。
                        Update(x => x["phase"] = "waiting_stable");
                        return;
                    }
                    if (phase == "waiting_completed")
                    {
                        object request;
                        if (!ActiveRequests.TryGetValue(Text(root, "requestId"), out request) || request == null)
                        {
                            Update(x => x["phase"] = "waiting_stable");
                            return;
                        }
                        var requestType = request.GetType();
                        var completed = requestType.GetProperty("IsCompleted");
                        if (completed == null || !(bool)completed.GetValue(request, null)) return;
                        var status = requestType.GetProperty("Status");
                        var value = status == null ? null : status.GetValue(request, null);
                        if (value != null && string.Equals(value.ToString(), "Failure", StringComparison.OrdinalIgnoreCase))
                        {
                            var error = requestType.GetProperty("Error");
                            var errorValue = error == null ? null : error.GetValue(request, null);
                            var message = errorValue == null ? null : errorValue.GetType().GetProperty("message")?.GetValue(errorValue, null) as string;
                            ActiveRequests.Remove(Text(root, "requestId"));
                            Fail(root, "E_PACKAGE_EMBED_FAILED", string.IsNullOrEmpty(message) ? "Package Manager failed to embed " + Text(root, "package") + "." : message);
                            return;
                        }
                        ActiveRequests.Remove(Text(root, "requestId"));
                        Update(x => x["phase"] = "waiting_stable");
                        return;
                    }
                    if (phase != "waiting_stable" || BusyEditor()) return;
                    try { BuildEmbeddedResult(Text(root, "package")); Succeed(root); }
                    catch (BridgeCommandException) { /* 业务状态尚未稳定，后续 Tick 继续观察。 */ }
                }
                protected override object BuildSuccessData(Dictionary<string, object> root) { return BuildEmbeddedResult(Text(root, "package")); }
                private static bool BusyEditor() { return EditorApplication.isUpdating || EditorApplication.isCompiling || BuildPipeline.isBuildingPlayer || IsPlayModeChanging(); }
            }
            private sealed class Args { public string Package { get; set; } }
            private sealed class Result { public string PackageId { get; set; } public string EmbeddedPath { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
            private static List<EditorPlannedEffect> Effects(string packageId) { return new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "package.embed", Path = packageId } }; }
            /// <summary>只从 PackageManager registry 与磁盘构造 terminal proof，不触发 resolve/embed。</summary>
            private static Result BuildEmbeddedResult(string packageId)
            {
                var assetPath = "Packages/" + packageId;
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                var expected = Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Packages", packageId));
                var actual = info == null || string.IsNullOrEmpty(info.resolvedPath) ? null : Path.GetFullPath(info.resolvedPath);
                if (info == null || info.source != UnityEditor.PackageManager.PackageSource.Embedded || !string.Equals(info.name, packageId, StringComparison.Ordinal) || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(actual))
                    throw new BridgeCommandException("E_PACKAGE_EMBED_PROOF_MISSING", "Package Manager does not report a verified embedded package at " + assetPath + ".", "Inspect Packages/ and Package Manager diagnostics before submitting a new request.");
                return new Result { PackageId = packageId, EmbeddedPath = actual, PlannedEffects = Effects(packageId) };
            }
        }
    }
}
#endif
