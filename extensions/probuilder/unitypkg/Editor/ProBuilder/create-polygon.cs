#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class CreatePolygonTool : IUnityCliTool<CreatePolygonTool.Request, CreatePolygonTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public List<long> VertexIndices { get; set; } public bool? Unordered { get; set; } }
        public sealed class Result { public long NewFaceIndex { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.create-polygon", Summary = "Create a polygon face from selected vertices.", Description = "Create a polygon from at least three valid vertex indices. unordered defaults to true.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "创建多边形", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0, 1, 2 } } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var go = ProBuilderInput.ResolveTarget(request.Target); if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("create-polygon", "No ProBuilderMesh on target.");
            var indices = request.VertexIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "vertexIndices")).ToArray(); var vertexCount = ProBuilderReflection.GetVertexCount(mesh); var beforeCount = ProBuilderReflection.GetFaceCount(mesh);
            if (indices == null || indices.Length < 3 || indices.Distinct().Count() != indices.Length || vertexCount < 0 || beforeCount < 0 || indices.Any(value => value < 0 || value >= vertexCount)) throw ProBuilderErrors.SchemaInvalid("vertexIndices must contain at least 3 unique valid vertices.");
            var unordered = request.Unordered ?? true; var id = go.GetInstanceID(); ctx.CommitEffect();
            try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Create Polygon"); if (!ProBuilderReflection.CreatePolygon(mesh, indices, unordered)) throw ProBuilderErrors.OutcomeUnknown("create-polygon", "invoke", "AppendElements.CreatePolygon failed."); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("create-polygon", "invoke", ex.Message); }
            try { if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw ProBuilderErrors.OutcomeUnknown("create-polygon", "readback", "ProBuilder refresh failed."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = EditorUtility.InstanceIDToObject(id) as GameObject; var freshMesh = ProBuilderReflection.GetProBuilderMesh(fresh); if (!ProBuilderAtomicReadback.PolygonWasCreatedFromRequestedVertices(freshMesh, indices, beforeCount + 1)) throw ProBuilderErrors.OutcomeUnknown("create-polygon", "postcondition", "Created polygon could not be confirmed."); return new System.Threading.Tasks.ValueTask<Result>(new Result { NewFaceIndex = beforeCount }); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("create-polygon", "readback", ex.Message); }
        }
    }
}
#endif
