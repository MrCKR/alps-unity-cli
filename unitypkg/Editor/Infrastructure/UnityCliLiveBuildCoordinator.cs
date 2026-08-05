#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>BuildPlayer 的进行中状态只保留在 active-build 文件，终态立即删除。</summary>
        internal static class UnityCliLiveBuildCoordinator
        {
            internal const string Command = "internal.build.run.start";
            private const string Scope = "build";
            internal static bool IsCommand(string command) { return command == Command; }
            internal static void Register(BridgeCommandRegistry registry)
            {
                registry.Register<BridgeRequest>(Command, "internal", true, 600000, (r, c) => { throw new InvalidOperationException("Build is coordinator-owned."); });
            }

            internal static async Task<string> Execute(BridgeRequest request, byte[] rawRequestBytes, Func<bool> tryAccept, CancellationToken cancellationToken)
            {
                var args = request.DeserializeArgs<Args>();
                if (request.Execution != null && request.Execution.DryRun == true)
                {
                    Validate(args);
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "dryRun", true }, { "plannedEffects", args.Targets.Select(x => (object)new Dictionary<string, object> { { "type", "build-player" }, { "target", x.Target }, { "outputPath", x.OutputPath } }).ToList() } }));
                }
                bool newlyAcquiredLease;
                var digest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
                if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out newlyAcquiredLease)) return Busy(request.RequestId);
                try
                {
                    Dictionary<string, object> active;
                    try { active = UnityCliActiveOperation.Load(Scope); }
                    catch (Exception ex) { UnityCliActiveOperation.Clear(Scope); return Terminal(request.RequestId, ProtocolEnvelopeFactory.Error(request.RequestId, "E_EXECUTION_TIMEOUT", "Build active state is corrupt: " + ex.Message, null, new[] { "Inspect editor.state and retry." })); }
                    if (active != null)
                    {
                        // BuildPlayer 无法在 reload 后重放；清理遗留状态并把恢复请求归约为超时。
                        UnityCliActiveOperation.Clear(Scope);
                        return Terminal(request.RequestId, ProtocolEnvelopeFactory.Error(request.RequestId, "E_EXECUTION_TIMEOUT", "A previous build did not reach a terminal result before reload or timeout.", null, new[] { "Inspect build output and editor.state before retrying." }));
                    }
                    if (tryAccept == null || !tryAccept()) throw new OperationCanceledException();
                    Validate(args);
                    var prepared = args.Targets.Select(Prepare).ToList();
                    active = NewActive(request, digest, args);
                    UnityCliActiveOperation.Save(Scope, active);
                    cancellationToken.ThrowIfCancellationRequested();
                    active["phase"] = "running";
                    UnityCliActiveOperation.Save(Scope, active);
                    var reports = new List<object>();
                    foreach (var item in prepared) reports.Add(Build(item));
                    var data = new Dictionary<string, object> { { "targets", reports } };
                    var failed = reports.Cast<Dictionary<string, object>>().Any(x => !string.Equals(x["result"] as string, "Succeeded", StringComparison.Ordinal));
                    object envelope = failed ? (object)ProtocolEnvelopeFactory.Error(request.RequestId, "E_BUILD_FAILED", "One or more BuildPlayer reports were not successful.", data, new[] { "Inspect the returned BuildReport summaries and Unity Console." }) : ProtocolEnvelopeFactory.Success(request.RequestId, data);
                    UnityCliActiveOperation.Clear(Scope);
                    return Terminal(request.RequestId, envelope);
                }
                catch (OperationCanceledException)
                {
                    UnityCliActiveOperation.Clear(Scope);
                    return Terminal(request.RequestId, ProtocolEnvelopeFactory.Error(request.RequestId, "E_EXECUTION_TIMEOUT", "Build did not complete before the request deadline.", null, new[] { "Inspect editor.state and retry." }));
                }
                catch (Exception ex)
                {
                    UnityCliActiveOperation.Clear(Scope);
                    return Terminal(request.RequestId, SerializeException(request.RequestId, ex));
                }
                finally { UnityCliSessionMutationLease.Release(request.RequestId); await Task.FromResult(0); }
            }

            private static Dictionary<string, object> NewActive(BridgeRequest request, string digest, Args args) { return new Dictionary<string, object> { { "requestId", request.RequestId }, { "requestDigest", digest }, { "editorSessionId", EditorSessionId() }, { "phase", "accepted" }, { "startedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, { "targets", args.Targets.Select(x => (object)new Dictionary<string, object> { { "target", x.Target }, { "outputPath", x.OutputPath } }).ToList() } }; }
            private static string Busy(string id) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(id, "E_UNITY_BUSY", "Another mutation owns the selected Unity instance.", null, new[] { "Wait for the current mutation to reach its terminal result, then retry." }, true, 250)); }
            private static string Terminal(string id, object envelope) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(id, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", envelope } })); }
            private static object SerializeException(string id, Exception ex) { return ProtocolEnvelopeFactory.Error(id, "E_BRIDGE_COMMAND_FAILED", ex.Message, null, new[] { "Check Unity Console and UnityCliBridge.log." }); }
            private static Prepared Prepare(TargetArgs input)
            {
                if (input == null || string.IsNullOrWhiteSpace(input.Target) || string.IsNullOrWhiteSpace(input.OutputPath)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Each build target requires target and outputPath.");
                BuildTarget target; if (!Enum.TryParse(input.Target, false, out target)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Unsupported Unity BuildTarget: " + input.Target);
                var scenes = EditorBuildSettings.scenes.Where(x => x.enabled && File.Exists(x.path)).Select(x => x.path).ToArray(); if (scenes.Length == 0) throw new BridgeCommandException("E_BUILD_SCENES_EMPTY", "No enabled existing build scenes are available.");
                var publicPath = input.OutputPath.Replace('\\', '/'); if (Path.IsPathRooted(input.OutputPath) || publicPath.Split('/').Any(x => x == "..")) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build outputPath must be project-relative.");
                var projectRoot = Path.GetFullPath(ProjectRoot()); var outputPath = Path.GetFullPath(Path.Combine(projectRoot, input.OutputPath)); if (!outputPath.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(outputPath, projectRoot, StringComparison.Ordinal)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build outputPath escapes the project.");
                return new Prepared { Target = target, TargetName = input.Target, OutputPath = outputPath, PublicOutputPath = publicPath, Scenes = scenes, Options = ParseOptions(input.Options) };
            }
            private static BuildOptions ParseOptions(List<string> values) { var result = BuildOptions.None; foreach (var value in values ?? new List<string>()) { BuildOptions option; if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse(value, false, out option)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Unsupported BuildOptions value: " + value); result |= option; } return result; }
            private static Dictionary<string, object> Build(Prepared p)
            {
                var started = DateTimeOffset.UtcNow; var parent = Path.GetDirectoryName(p.OutputPath); if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                var report = BuildPipeline.BuildPlayer(p.Scenes, p.OutputPath, p.Target, p.Options); var finished = DateTimeOffset.UtcNow; var artifact = ReadArtifact(p.OutputPath, p.PublicOutputPath);
                if (report.summary.result == BuildResult.Succeeded && !SuccessfulArtifactProof(artifact)) throw new BridgeCommandException("E_BUILD_ARTIFACT_MISSING", "BuildReport succeeded but its fresh output artifact has no non-empty SHA-256 proof.");
                var messages = report.steps.SelectMany(step => step.messages ?? new BuildStepMessage[0]).Where(message => !string.IsNullOrWhiteSpace(message.content)).Select(message => (object)new Dictionary<string, object> { { "type", message.type.ToString() }, { "message", message.content } }).Take(100).ToList();
                return new Dictionary<string, object> { { "target", p.TargetName }, { "result", report.summary.result.ToString() }, { "outputPath", p.PublicOutputPath }, { "totalSize", report.summary.totalSize }, { "totalErrors", (long)report.summary.totalErrors }, { "totalWarnings", (long)report.summary.totalWarnings }, { "startedAt", started.ToString("O", CultureInfo.InvariantCulture) }, { "finishedAt", finished.ToString("O", CultureInfo.InvariantCulture) }, { "durationMs", (long)(finished - started).TotalMilliseconds }, { "messages", messages }, { "artifact", artifact } };
            }
            private static Dictionary<string, object> ReadArtifact(string path, string publicPath) { var roots = new List<string>(); if (File.Exists(path)) roots.Add(path); if (Directory.Exists(path)) roots.Add(path); var sidecar = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + "_Data"); if (Directory.Exists(sidecar)) roots.Add(sidecar); var files = roots.SelectMany(root => File.Exists(root) ? new[] { root } : Directory.GetFiles(root, "*", SearchOption.AllDirectories)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(); var size = files.Sum(x => new FileInfo(x).Length); var treeRoot = Path.GetDirectoryName(path) ?? path; return new Dictionary<string, object> { { "outputPath", publicPath }, { "exists", files.Length > 0 }, { "hash", File.Exists(path) ? HashFile(path) : null }, { "treeHash", files.Length > 0 ? HashTree(files, treeRoot) : null }, { "fileCount", (long)files.Length }, { "size", size } }; }
            private static bool SuccessfulArtifactProof(Dictionary<string, object> artifact) { object exists; object count; object size; object hash; object treeHash; artifact.TryGetValue("exists", out exists); artifact.TryGetValue("fileCount", out count); artifact.TryGetValue("size", out size); artifact.TryGetValue("hash", out hash); artifact.TryGetValue("treeHash", out treeHash); return exists is bool && (bool)exists && count is long && (long)count > 0 && size is long && (long)size > 0 && (IsSha256(hash as string) || IsSha256(treeHash as string)); }
            private static string HashFile(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
            private static string HashTree(IEnumerable<string> files, string root) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", files.Select(x => Path.GetRelativePath(root, x).Replace('\\', '/') + "\0" + HashFile(x)))))).Replace("-", "").ToLowerInvariant(); }
            private static void Validate(Args args) { if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId) || args.Targets == null || args.Targets.Count == 0) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build targets and expectedEditorSessionId are required."); if (args.Targets.Any(x => x == null || string.IsNullOrWhiteSpace(x.Target) || string.IsNullOrWhiteSpace(x.OutputPath))) throw new BridgeCommandException("E_SCHEMA_INVALID", "Each build target requires target and outputPath."); if (args.Targets.Select(x => x.Target).Distinct(StringComparer.Ordinal).Count() != args.Targets.Count) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build targets must not contain duplicates."); if (args.Targets.Select(x => x.OutputPath.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != args.Targets.Count) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build output paths must not conflict."); if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before build execution."); if (EditorApplication.isPlaying || IsPlayModeChanging()) throw new BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "build.run is editor-only and requires stable Edit Mode.", new Dictionary<string, object> { { "toolName", "build.run" }, { "playModeSupport", "editor-only" } }, new[] { "Exit Play Mode before running build.run." }); if (EditorUtility.scriptCompilationFailed) throw new BridgeCommandException("E_UNITY_COMPILE_FAILED", "Unity has unresolved script compilation errors and cannot start BuildPlayer.", "Run editor.compile and fix all diagnostics before retrying build.run."); }
            private sealed class Args { public List<TargetArgs> Targets { get; set; } public string ExpectedEditorSessionId { get; set; } }
            private sealed class TargetArgs { public string Target { get; set; } public string OutputPath { get; set; } public List<string> Options { get; set; } }
            private sealed class Prepared { public BuildTarget Target; public string TargetName; public string OutputPath; public string PublicOutputPath; public string[] Scenes; public BuildOptions Options; }
        }
    }
}
#endif
