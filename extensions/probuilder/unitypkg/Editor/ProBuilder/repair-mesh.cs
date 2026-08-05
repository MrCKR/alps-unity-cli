#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class RepairMeshTool : IUnityCliTool<RepairMeshTool.Request, RepairMeshTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public long RemovedDegenerateIndexCount { get; set; } public long RemovedUnusedVertexCount { get; set; } public long RemainingDegenerateFaceCount { get; set; } public long RemainingUnusedVertexCount { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.repair-mesh",
            Summary = "Repair degenerate triangles and unused vertices.",
            Description = "Remove readable degenerate triangles and unused vertices from a ProBuilder mesh.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "移除唯一未使用顶点", Input = new Request { Target = "TriangleWithUnusedRaw" } }
            }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var go = ProBuilderInput.ResolveTarget(request.Target); if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("repair-mesh", "No ProBuilderMesh on target.");
            var beforeDegenerate = ProBuilderReflection.CountDegenerateFaces(mesh); var beforeUnused = ProBuilderReflection.CountUnusedVertices(mesh); if (beforeDegenerate < 0 || beforeUnused < 0) throw ProBuilderErrors.OperationFailed("repair-mesh", "Failed to read repairable mesh state."); if (beforeDegenerate == 0 && beforeUnused == 0) throw ProBuilderErrors.OperationFailed("repair-mesh", "Mesh has no readable degenerate triangles or unused vertices to repair."); var id = go.GetInstanceID(); ctx.CommitEffect(); int removedDegenerate; int removedUnused;
            try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Repair Mesh"); removedDegenerate = ProBuilderReflection.RemoveDegenerateTriangles(mesh); removedUnused = ProBuilderReflection.RemoveUnusedVertices(mesh); if (removedDegenerate < 0 || removedUnused < 0) throw ProBuilderErrors.OutcomeUnknown("repair-mesh", "invoke", "Repair API failed."); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("repair-mesh", "invoke", ex.Message); }
            try { if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw ProBuilderErrors.OutcomeUnknown("repair-mesh", "readback", "ProBuilder refresh failed."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(id) as GameObject); var remainingDegenerate = ProBuilderReflection.CountDegenerateFaces(fresh); var remainingUnused = ProBuilderReflection.CountUnusedVertices(fresh); if (remainingDegenerate != 0 || remainingUnused != 0) throw ProBuilderErrors.OutcomeUnknown("repair-mesh", "postcondition", "Fresh repair counts did not show progress."); return new System.Threading.Tasks.ValueTask<Result>(new Result { RemovedDegenerateIndexCount = removedDegenerate, RemovedUnusedVertexCount = removedUnused, RemainingDegenerateFaceCount = remainingDegenerate, RemainingUnusedVertexCount = remainingUnused }); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("repair-mesh", "readback", ex.Message); }
        }
    }
}
#endif
