#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
namespace UnityCli.ProBuilder
{
    public class SetFaceUvsTool : IUnityCliTool<SetFaceUvsTool.Request, SetFaceUvsTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public List<long> FaceIndices { get; set; } public double? Scale { get; set; } public double? OffsetU { get; set; } public double? OffsetV { get; set; } public double? Rotation { get; set; } public bool FlipU { get; set; } public bool FlipV { get; set; } }
        public sealed class Result { public long UpdatedFaceCount { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.set-face-uvs", Summary = "Set UV parameters for selected faces on a ProBuilder mesh.", Description = "Set AutoUnwrapSettings fields on selected faces.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "将面 0 UV 缩放设为 2", Input = new Request { Target = "MyTriangle", FaceIndices = new List<long> { 0 }, Scale = 2 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var target = request.Target; var flipU = request.FlipU; var flipV = request.FlipV; var go = ProBuilderInput.ResolveTarget(target); if (go == null) throw ProBuilderErrors.TargetNotFound(target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("set-face-uvs", "No ProBuilderMesh on target.");
            var indexes = request.FaceIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "faceIndices")).ToArray(); if (indexes == null || indexes.Length == 0) indexes = Enumerable.Range(0, ProBuilderReflection.GetFaceCount(mesh)).ToArray(); var faces = ProBuilderReflection.GetFacesByIndices(mesh, indexes); var scale = request.Scale.HasValue ? (float?)ProBuilderOperationProof.CheckedFloat(request.Scale.Value, "scale") : null; var u = request.OffsetU.HasValue ? (float?)ProBuilderOperationProof.CheckedFloat(request.OffsetU.Value, "offsetU") : null; var v = request.OffsetV.HasValue ? (float?)ProBuilderOperationProof.CheckedFloat(request.OffsetV.Value, "offsetV") : null; var rotation = request.Rotation.HasValue ? (float?)ProBuilderOperationProof.CheckedFloat(request.Rotation.Value, "rotation") : null;
            if (faces == null || indexes.Distinct().Count() != indexes.Length) throw ProBuilderErrors.SchemaInvalid("faceIndices must identify unique valid faces."); if (ProBuilderAtomicReadback.RequestedFacesHaveUv(mesh, indexes, scale, u, v, rotation, flipU, flipV)) return new System.Threading.Tasks.ValueTask<Result>(new Result { UpdatedFaceCount = 0 });
            var faceType = ProBuilderReflection.GetFaceType(); var uv = faceType?.GetProperty("uv", BindingFlags.Instance | BindingFlags.Public); var refreshType = faceType == null ? null : typeof(IEnumerable<>).MakeGenericType(faceType); var refresh = refreshType == null ? null : ProBuilderReflection.GetProBuilderMeshType()?.GetMethod("RefreshUV", BindingFlags.Instance | BindingFlags.Public, null, new[] { refreshType }, null); if (uv == null || !uv.CanRead || !uv.CanWrite || refresh == null) throw ProBuilderErrors.OperationFailed("set-face-uvs", "Required ProBuilder 5.2.4 UV members are unavailable.");
            ctx.CommitEffect(); try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Set Face UVs"); foreach (var face in faces) { var value = (AutoUnwrapSettings)uv.GetValue(face); if (scale.HasValue) value.scale = new Vector2(scale.Value, scale.Value); if (u.HasValue || v.HasValue) value.offset = new Vector2(u ?? 0f, v ?? 0f); if (rotation.HasValue) value.rotation = rotation.Value; value.flipU = flipU; value.flipV = flipV; uv.SetValue(face, value); } refresh.Invoke(mesh, new object[] { faces }); if (!ProBuilderReflection.RefreshMesh(mesh)) throw ProBuilderErrors.OutcomeUnknown("set-face-uvs", "readback", "ProBuilder refresh failed after the effect."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject); if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("set-face-uvs", "readback", "Fresh target mesh is unreadable."); if (!ProBuilderAtomicReadback.RequestedFacesHaveUv(fresh, indexes, scale, u, v, rotation, flipU, flipV)) throw ProBuilderErrors.OutcomeUnknown("set-face-uvs", "postcondition", "The requested faces do not have the requested UV settings."); return new System.Threading.Tasks.ValueTask<Result>(new Result { UpdatedFaceCount = faces.Length }); } catch (Exception exception) when (!(exception is UnityCliBridge.BridgeCommandException)) { throw ProBuilderErrors.OutcomeUnknown("set-face-uvs", "invoke", exception.Message); }
        }
    }
}
#endif
