#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
namespace UnityCli.ProBuilder
{
    public class MergeFacesTool : IUnityCliTool<MergeFacesTool.Request, MergeFacesTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public List<long> FaceIndices { get; set; } }
        public sealed class Result { public long BeforeFaceCount { get; set; } public long AfterFaceCount { get; set; } public long MergedSourceFaceCount { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.merge-faces",
            Summary = "Merge selected ProBuilder faces.",
            Description = "Merge at least two unique valid faces into one ProBuilder face.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "合并 strict-domain 面 0 和 1", Input = new Request { FaceIndices = new List<long> { 0, 1 }, Target = "SharedEdgeTriangles" } }
            }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var go = ProBuilderInput.ResolveTarget(request.Target); if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("merge-faces", "No ProBuilderMesh on target.");
            var indices = request.FaceIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "faceIndices")).ToArray(); var before = ProBuilderReflection.GetFaceCount(mesh); if (indices == null || indices.Length < 2 || indices.Distinct().Count() != indices.Length || before < 0 || ProBuilderReflection.GetFacesByIndices(mesh, indices) == null) throw ProBuilderErrors.SchemaInvalid("faceIndices must contain at least 2 unique valid faces."); var id = go.GetInstanceID(); ctx.CommitEffect();
            try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Merge Faces"); var merged = MergeElements.Merge(mesh as ProBuilderMesh, ProBuilderReflection.GetFacesByIndices(mesh, indices).Cast<Face>()); if (merged == null) throw ProBuilderErrors.OutcomeUnknown("merge-faces", "invoke", "MergeElements.Merge failed."); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("merge-faces", "invoke", ex.Message); }
            try { if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw ProBuilderErrors.OutcomeUnknown("merge-faces", "readback", "ProBuilder refresh failed."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(id) as GameObject); var after = ProBuilderReflection.GetFaceCount(fresh); if (after < 0 || after != before - indices.Length + 1) throw ProBuilderErrors.OutcomeUnknown("merge-faces", "postcondition", "Fresh face count did not confirm merge."); return new System.Threading.Tasks.ValueTask<Result>(new Result { BeforeFaceCount = before, AfterFaceCount = after, MergedSourceFaceCount = indices.Length }); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("merge-faces", "readback", ex.Message); }
        }
    }
}
#endif
