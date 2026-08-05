#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>probuilder.move-vertices — 按偏移量移动 ProBuilder 网格的选定顶点。</summary>
    public class MoveVerticesTool : IUnityCliTool<MoveVerticesTool.Request, MoveVerticesTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public List<long> VertexIndices { get; set; } public double X { get; set; } public double Y { get; set; } public double Z { get; set; } }
        public sealed class Result { public long MovedCount { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.move-vertices", Summary = "Move selected vertices of a ProBuilder mesh by an offset.", Description = "Move unique raw vertex indices by an X/Y/Z offset. Move validated raw vertex indices by a finite X/Y/Z offset.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "沿 Y 轴移动顶点 0", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0 }, Y = 0.5 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable();
            var target = request.Target; var go = ProBuilderInput.ResolveTarget(target); if (go == null) throw ProBuilderErrors.TargetNotFound(target);
            var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("move-vertices", "No ProBuilderMesh on target.");
            var indexes = request.VertexIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "vertexIndices")).ToArray();
            var offset = ProBuilderOperationProof.CheckedVector3(request.X, request.Y, request.Z, "offset");
            ProBuilderOperationProof.ValidateVertexIndices(mesh, indexes);
            if (offset == Vector3.zero) return new System.Threading.Tasks.ValueTask<Result>(new Result { MovedCount = 0 });
            var before = ProBuilderAtomicReadback.CapturePositions(mesh); if (before == null) throw ProBuilderErrors.OperationFailed("move-vertices", "Raw vertex positions are unreadable.");
            ctx.CommitEffect();
            try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Move Vertices"); var moved = ProBuilderReflection.MoveVerticesByOffset(mesh, indexes, offset); if (moved != indexes.Length) throw ProBuilderErrors.OutcomeUnknown("move-vertices", "invoke", "The frozen raw selection was not fully moved."); if (!ProBuilderReflection.RefreshMesh(mesh)) throw ProBuilderErrors.OutcomeUnknown("move-vertices", "readback", "ProBuilder refresh failed after the effect."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject); if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("move-vertices", "readback", "Fresh target mesh is unreadable."); if (!ProBuilderAtomicReadback.VerticesMovedByOffset(fresh, before, indexes, offset)) throw ProBuilderErrors.OutcomeUnknown("move-vertices", "postcondition", "The requested vertices did not have the requested offset."); return new System.Threading.Tasks.ValueTask<Result>(new Result { MovedCount = moved }); }
            catch (Exception exception) when (!(exception is UnityCliBridge.BridgeCommandException)) { throw ProBuilderErrors.OutcomeUnknown("move-vertices", "invoke", exception.Message); }
        }
    }
}
#endif
