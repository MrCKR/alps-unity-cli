#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class DeleteFacesTool : IUnityCliTool<DeleteFacesTool.Request, DeleteFacesTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public List<long> FaceIndices { get; set; } }
        public sealed class Result { public long DeletedCount { get; set; } public long RemainingFaceCount { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.delete-faces",
            Summary = "Delete selected faces from a ProBuilder mesh.",
            Description = "Delete at least one unique valid face index from a ProBuilder mesh.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "删除唯一三角面", Input = new Request { FaceIndices = new List<long> { 0 }, Target = "SingleTriangle" } }
            }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var go = ProBuilderInput.ResolveTarget(request.Target); if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("delete-faces", "No ProBuilderMesh on target.");
            var indices = request.FaceIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "faceIndices")).ToArray(); var before = ProBuilderReflection.GetFaceCount(mesh); if (indices == null || indices.Length == 0 || indices.Distinct().Count() != indices.Length || before < 0 || ProBuilderReflection.GetFacesByIndices(mesh, indices) == null) throw ProBuilderErrors.SchemaInvalid("faceIndices must contain unique valid faces."); var id = go.GetInstanceID(); ctx.CommitEffect();
            try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Delete Faces"); if (!ProBuilderReflection.DeleteFaces(mesh, indices)) throw ProBuilderErrors.OutcomeUnknown("delete-faces", "invoke", "DeleteFaces failed."); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("delete-faces", "invoke", ex.Message); }
            try { if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw ProBuilderErrors.OutcomeUnknown("delete-faces", "readback", "ProBuilder refresh failed."); EditorSceneManager.MarkSceneDirty(go.scene); var freshMesh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(id) as GameObject); var after = ProBuilderReflection.GetFaceCount(freshMesh); if (after < 0 || after != before - indices.Length) throw ProBuilderErrors.OutcomeUnknown("delete-faces", "postcondition", "Fresh face count did not confirm deletion."); return new System.Threading.Tasks.ValueTask<Result>(new Result { DeletedCount = indices.Length, RemainingFaceCount = after }); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("delete-faces", "readback", ex.Message); }
        }
    }
}
#endif
