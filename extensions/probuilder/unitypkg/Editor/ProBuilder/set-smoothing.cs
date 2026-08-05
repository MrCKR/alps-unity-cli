#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class SetSmoothingTool : IUnityCliTool<SetSmoothingTool.Request, SetSmoothingTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public List<long> FaceIndices { get; set; } [UnityCliRequired] public ushort SmoothingGroup { get; set; } }
        public sealed class Result { public long ModifiedFaceCount { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.set-smoothing", Summary = "Set the smoothing group for selected faces of a ProBuilder mesh.", Description = "Set selected face smoothing groups in the bounded 0..65535 domain.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "将面 0 设为平滑组 1", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 }, SmoothingGroup = 1 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var target = request.Target; var go = ProBuilderInput.ResolveTarget(target); if (go == null) throw ProBuilderErrors.TargetNotFound(target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("set-smoothing", "No ProBuilderMesh on target."); var group = (int)request.SmoothingGroup;
            var indexes = request.FaceIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "faceIndices")).ToArray(); if (indexes == null || indexes.Length == 0) indexes = Enumerable.Range(0, ProBuilderReflection.GetFaceCount(mesh)).ToArray(); if (ProBuilderReflection.GetFacesByIndices(mesh, indexes) == null || indexes.Distinct().Count() != indexes.Length) throw ProBuilderErrors.SchemaInvalid("faceIndices must identify unique valid faces."); if (ProBuilderAtomicReadback.RequestedFacesHaveSmoothingGroup(mesh, indexes, group)) return new System.Threading.Tasks.ValueTask<Result>(new Result { ModifiedFaceCount = 0 });
            ctx.CommitEffect(); try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Set Smoothing"); if (!ProBuilderReflection.ApplySmoothingGroup(mesh, indexes, group)) throw ProBuilderErrors.OutcomeUnknown("set-smoothing", "invoke", "Failed to set Face.smoothingGroup."); if (!ProBuilderReflection.RefreshMesh(mesh)) throw ProBuilderErrors.OutcomeUnknown("set-smoothing", "readback", "ProBuilder refresh failed after the effect."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject); if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("set-smoothing", "readback", "Fresh target mesh is unreadable."); if (!ProBuilderAtomicReadback.RequestedFacesHaveSmoothingGroup(fresh, indexes, group)) throw ProBuilderErrors.OutcomeUnknown("set-smoothing", "postcondition", "The requested faces do not have the requested smoothing group."); return new System.Threading.Tasks.ValueTask<Result>(new Result { ModifiedFaceCount = indexes.Length }); } catch (Exception exception) when (!(exception is UnityCliBridge.BridgeCommandException)) { throw ProBuilderErrors.OutcomeUnknown("set-smoothing", "invoke", exception.Message); }
        }
    }
}
#endif
