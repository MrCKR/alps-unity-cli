#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
namespace UnityCli
{
    public class GraphicsBakeReflectionProbeTool : IUnityCliTool<GraphicsBakeReflectionProbeTool.Request, GraphicsBakeReflectionProbeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Path { get; set; } }
        public sealed class Result { public ReflectionProbeInfo Probe { get; set; } public bool Baked { get; set; } public string ArtifactPath { get; set; } public long SizeBytes { get; set; } public string Sha256 { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "lighting.bake.reflection-probe", Summary = "Bake a single ReflectionProbe to an .exr cubemap asset.", Description = "Bake a ReflectionProbe to an .exr path. The parent Assets folder must already exist; a missing parent fails before Unity starts the bake.", Group = "rendering",DestructiveHint = true, SideEffects = new[] { "bake", "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Bake one reflection probe", Input = new Request { Target = "ReflectionProbe", Path = "Assets/Reflections/ReflectionProbe.exr" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var probe = PhysicsToolSupport.ResolveReflectionProbe(r.Target); if (probe == null) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_NOT_FOUND", "ReflectionProbe not found: " + r.Target, "Pass an existing ReflectionProbe target.");
            var probePath = GraphicsToolSupport.NormalizeAssetPathRequired(r.Path ?? ("Assets/" + probe.gameObject.name + "_reflection.exr"), ".exr"); var outputPath = AssetToolSupport.AssetFullPath(probePath);
            // Unity 的 bake API 会为不存在的父目录弹出原生阻断窗口，必须在 effect barrier 前拒绝。
            var parentAssetPath = Path.GetDirectoryName(probePath)?.Replace('\\', '/');
            var parentOutputPath = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(parentAssetPath) || string.IsNullOrEmpty(parentOutputPath) || !Directory.Exists(parentOutputPath))
                throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_ASSET_DIRECTORY_NOT_FOUND", "ReflectionProbe output parent folder does not exist: " + parentAssetPath, "Run tools run asset.create with path and type=folder input, then retry with a new output path.");
            var oldExists = File.Exists(outputPath); var oldBytes = oldExists ? File.ReadAllBytes(outputPath) : null; var oldDigest = oldBytes == null ? null : GraphicsToolSupport.FileSha256(oldBytes); var started = System.DateTime.UtcNow;
            ctx.CommitEffect();
            if (!Lightmapping.BakeReflectionProbe(probe, probePath)) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_BAKE_FAILED", "Unity did not complete the ReflectionProbe bake: " + probePath, "Check lighting settings and retry.");
            AssetDatabase.ImportAsset(probePath, ImportAssetOptions.ForceSynchronousImport);
            var texture = AssetDatabase.LoadAssetAtPath<Cubemap>(probePath); var info = new FileInfo(outputPath); if (texture == null || !info.Exists || info.Length < 4) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_BAKE_ARTIFACT_MISSING", "Bake completed without a readable cubemap artifact.", "Check the Unity Console and retry.");
            var bytes = File.ReadAllBytes(outputPath); var digest = GraphicsToolSupport.FileSha256(bytes); var exr = bytes[0] == 0x76 && bytes[1] == 0x2f && bytes[2] == 0x31 && bytes[3] == 0x01;
            if (!exr) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_BAKE_ARTIFACT_INVALID", "Bake did not write a valid EXR artifact.", "Use an .exr output path.");
            if (oldExists && oldDigest == digest) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_BAKE_ARTIFACT_UNPROVEN", "The EXR digest did not change, so this bake cannot be attributed to this request.", "Choose a new output path or remove the old artifact before retrying.");
            if (info.LastWriteTimeUtc < started || info.Length != bytes.LongLength) throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_PROBE_BAKE_ARTIFACT_UNPROVEN", "The EXR metadata cannot be attributed to this request.", "Choose a new output path and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Probe = GraphicsToolSupport.BuildReflectionProbeInfo(probe), Baked = true, ArtifactPath = outputPath, SizeBytes = bytes.LongLength, Sha256 = digest, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "lighting.bake.reflection-probe", Property = "probe", Value = GameObjectToolSupport.HierarchyPath(probe.gameObject) } } });
        }
    }
}
#endif
