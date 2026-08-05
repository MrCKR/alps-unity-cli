#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
namespace UnityCli.ProBuilder
{
    /// <summary>probuilder.center-pivot — 将轴心点居中到包围盒中心。</summary>
    public class CenterPivotTool : IUnityCliTool<CenterPivotTool.Request, CenterPivotTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public Vec3 PivotOffset { get; set; } public Vec3 NewPosition { get; set; } }
        public sealed class Vec3 { public double X { get; set; } public double Y { get; set; } public double Z { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.center-pivot", Summary = "Center a ProBuilder mesh pivot to its renderer bounds center.", Description = "Center a mesh pivot to its renderer bounds center while preserving readable world geometry.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "居中轴心", Input = new Request { Target = "MyCube" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var target = request.Target; var go = ProBuilderInput.ResolveTarget(target); if (go == null) throw ProBuilderErrors.TargetNotFound(target); var mesh = ProBuilderReflection.GetProBuilderMesh(go) as ProBuilderMesh; if (mesh == null) throw ProBuilderErrors.OperationFailed("center-pivot", "No ProBuilderMesh on target.");
            var renderer = go.GetComponent<Renderer>(); if (renderer == null) throw ProBuilderErrors.OperationFailed("center-pivot", "Target has no Renderer to determine bounds center."); var pivot = renderer.bounds.center; if (float.IsNaN(pivot.x) || float.IsInfinity(pivot.x) || float.IsNaN(pivot.y) || float.IsInfinity(pivot.y) || float.IsNaN(pivot.z) || float.IsInfinity(pivot.z)) throw ProBuilderErrors.SchemaInvalid("Target bounds center must be finite."); var old = go.transform.position;
            if (go.transform.position == pivot) return new System.Threading.Tasks.ValueTask<Result>(new Result { PivotOffset = new Vec3(), NewPosition = new Vec3 { X = pivot.x, Y = pivot.y, Z = pivot.z } }); var worldPositions = ProBuilderAtomicReadback.CaptureWorldPositions(go, mesh); if (worldPositions == null) throw ProBuilderErrors.OperationFailed("center-pivot", "Raw vertex positions are unreadable.");
            var instanceId = go.GetInstanceID();
            ctx.CommitEffect();
            try
            {
                ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Center Pivot", true);
                mesh.CenterPivot(null);
                EditorSceneManager.MarkSceneDirty(go.scene);
                var freshGo = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                var fresh = ProBuilderReflection.GetProBuilderMesh(freshGo);
                if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("center-pivot", "readback", "Fresh target mesh is unreadable.");
                if (!ProBuilderAtomicReadback.PivotPreservesWorldGeometry(freshGo, fresh, pivot, worldPositions, true)) throw ProBuilderErrors.OutcomeUnknown("center-pivot", "postcondition", "Center pivot or world geometry did not match the request.");
                var offset = pivot - old;
                return new System.Threading.Tasks.ValueTask<Result>(new Result { PivotOffset = new Vec3 { X = offset.x, Y = offset.y, Z = offset.z }, NewPosition = new Vec3 { X = pivot.x, Y = pivot.y, Z = pivot.z } });
            }
            catch (Exception exception) when (!(exception is UnityCliBridge.BridgeCommandException))
            {
                throw ProBuilderErrors.OutcomeUnknown("center-pivot", "invoke", exception.Message);
            }
        }
    }
}
#endif
