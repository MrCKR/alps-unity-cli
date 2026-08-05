#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
namespace UnityCli.ProBuilder
{
    /// <summary>probuilder.set-pivot — 将 ProBuilder 网格轴心设到指定位置。</summary>
    public class SetPivotTool : IUnityCliTool<SetPivotTool.Request, SetPivotTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public double? X { get; set; } public double? Y { get; set; } public double? Z { get; set; } }
        public sealed class Result { public Vec3 NewPivotPosition { get; set; } }
        public sealed class Vec3 { public double X { get; set; } public double Y { get; set; } public double Z { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.set-pivot", Summary = "Set the pivot point of a ProBuilder mesh to a specific position.", Description = "Set a root object's world pivot point.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "将轴心设到原点", Input = new Request { Target = "MyCube", X = 0, Y = 0, Z = 0 } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var target = request.Target; var go = ProBuilderInput.ResolveTarget(target); if (go == null) throw ProBuilderErrors.TargetNotFound(target); var mesh = ProBuilderReflection.GetProBuilderMesh(go) as ProBuilderMesh; if (mesh == null) throw ProBuilderErrors.OperationFailed("set-pivot", "No ProBuilderMesh on target.");
            var position = go.transform.position; var pivot = new Vector3(request.X.HasValue ? ProBuilderOperationProof.CheckedFloat(request.X.Value, "x") : position.x, request.Y.HasValue ? ProBuilderOperationProof.CheckedFloat(request.Y.Value, "y") : position.y, request.Z.HasValue ? ProBuilderOperationProof.CheckedFloat(request.Z.Value, "z") : position.z);
            if (go.transform.position == pivot) return new System.Threading.Tasks.ValueTask<Result>(new Result { NewPivotPosition = new Vec3 { X = pivot.x, Y = pivot.y, Z = pivot.z } }); var worldPositions = ProBuilderAtomicReadback.CaptureWorldPositions(go, mesh); if (worldPositions == null) throw ProBuilderErrors.OperationFailed("set-pivot", "Raw vertex positions are unreadable.");
            var instanceId = go.GetInstanceID();
            ctx.CommitEffect();
            try
            {
                ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Set Pivot", true);
                mesh.SetPivot(pivot);
                EditorSceneManager.MarkSceneDirty(go.scene);
                var freshGo = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                var fresh = ProBuilderReflection.GetProBuilderMesh(freshGo);
                if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("set-pivot", "readback", "Fresh target mesh is unreadable.");
                if (!ProBuilderAtomicReadback.PivotPreservesWorldGeometry(freshGo, fresh, pivot, worldPositions, false)) throw ProBuilderErrors.OutcomeUnknown("set-pivot", "postcondition", "Pivot or world geometry did not match the request.");
                return new System.Threading.Tasks.ValueTask<Result>(new Result { NewPivotPosition = new Vec3 { X = pivot.x, Y = pivot.y, Z = pivot.z } });
            }
            catch (Exception exception) when (!(exception is UnityCliBridge.BridgeCommandException))
            {
                throw ProBuilderErrors.OutcomeUnknown("set-pivot", "invoke", exception.Message);
            }
        }
    }
}
#endif
