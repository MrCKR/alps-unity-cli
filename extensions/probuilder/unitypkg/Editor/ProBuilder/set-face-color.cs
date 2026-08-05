#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class SetFaceColorTool : IUnityCliTool<SetFaceColorTool.Request, SetFaceColorTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public List<long> FaceIndices { get; set; } public double? R { get; set; } public double? G { get; set; } public double? B { get; set; } public double? A { get; set; } }
        public sealed class Result { public long ColoredFaceCount { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.set-face-color", Summary = "Set vertex color of selected faces on a ProBuilder mesh.", Description = "Set selected face vertex colors.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "将面 0 设为红色", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 }, R = 1, G = 0, B = 0, A = 1 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var target = request.Target; var go = ProBuilderInput.ResolveTarget(target); if (go == null) throw ProBuilderErrors.TargetNotFound(target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("set-face-color", "No ProBuilderMesh on target.");
            var color = new Color(ProBuilderOperationProof.CheckedFloat(request.R ?? 1d, "r"), ProBuilderOperationProof.CheckedFloat(request.G ?? 1d, "g"), ProBuilderOperationProof.CheckedFloat(request.B ?? 1d, "b"), ProBuilderOperationProof.CheckedFloat(request.A ?? 1d, "a")); if (color.r < 0 || color.r > 1 || color.g < 0 || color.g > 1 || color.b < 0 || color.b > 1 || color.a < 0 || color.a > 1) throw ProBuilderErrors.SchemaInvalid("Color channels must be between 0 and 1.");
            var indexes = request.FaceIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "faceIndices")).ToArray(); if (indexes == null || indexes.Length == 0) indexes = Enumerable.Range(0, ProBuilderReflection.GetFaceCount(mesh)).ToArray(); var faces = ProBuilderReflection.GetFacesByIndices(mesh, indexes); if (faces == null || indexes.Distinct().Count() != indexes.Length) throw ProBuilderErrors.SchemaInvalid("faceIndices must identify unique valid faces."); if (ProBuilderAtomicReadback.RequestedFacesHaveColor(mesh, indexes, color)) return new System.Threading.Tasks.ValueTask<Result>(new Result { ColoredFaceCount = 0 });
            ctx.CommitEffect(); try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Set Face Color"); if (!ProBuilderReflection.SetFaceColor(mesh, faces, color)) throw ProBuilderErrors.OutcomeUnknown("set-face-color", "invoke", "ProBuilderMesh.SetFaceColor failed."); if (!ProBuilderReflection.RefreshMesh(mesh)) throw ProBuilderErrors.OutcomeUnknown("set-face-color", "readback", "ProBuilder refresh failed after the effect."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject); if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("set-face-color", "readback", "Fresh target mesh is unreadable."); if (!ProBuilderAtomicReadback.RequestedFacesHaveColor(fresh, indexes, color)) throw ProBuilderErrors.OutcomeUnknown("set-face-color", "postcondition", "The requested faces do not have the requested color."); return new System.Threading.Tasks.ValueTask<Result>(new Result { ColoredFaceCount = faces.Length }); } catch (Exception exception) when (!(exception is UnityCliBridge.BridgeCommandException)) { throw ProBuilderErrors.OutcomeUnknown("set-face-color", "invoke", exception.Message); }
        }
    }
}
#endif
