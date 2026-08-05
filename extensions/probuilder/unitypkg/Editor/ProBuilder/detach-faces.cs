#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class DetachFacesTool : IUnityCliTool<DetachFacesTool.Request, DetachFacesTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public List<long> FaceIndices { get; set; } }
        public sealed class Result { public ObjectRef DetachedObject { get; set; } public long FaceCount { get; set; } }
        public sealed class ObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.detach-faces",
            Summary = "Detach selected faces into a new ProBuilder object.",
            Description = "Detach at least one unique valid face from a ProBuilder mesh.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "分离 strict-domain 面 0", Input = new Request { FaceIndices = new List<long> { 0 }, Target = "TwoIndependentTriangles" } }
            }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var go = ProBuilderInput.ResolveTarget(request.Target); if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("detach-faces", "No ProBuilderMesh on target.");
            var indices = request.FaceIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "faceIndices")).ToArray(); var before = ProBuilderReflection.GetFaceCount(mesh); if (indices == null || indices.Length == 0 || indices.Distinct().Count() != indices.Length || before < 1 || ProBuilderReflection.GetFacesByIndices(mesh, indices) == null) throw ProBuilderErrors.SchemaInvalid("faceIndices must contain unique valid faces.");
            var inverse = Enumerable.Range(0, before).Except(indices).ToArray(); var sourceId = go.GetInstanceID(); ctx.CommitEffect(); GameObject detached = null; var detachedId = 0;
            var undoGroup = -1;
            try { Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("ProBuilder Detach Faces"); ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Detach Faces", false, go.GetComponent<Renderer>()); detached = UnityEngine.Object.Instantiate(go); detachedId = detached.GetInstanceID(); detached.name = go.name + "_Detached"; Undo.RegisterCreatedObjectUndo(detached, "ProBuilder Detach Faces"); var detachedMesh = ProBuilderReflection.GetProBuilderMesh(detached); if (detachedMesh == null || (inverse.Length > 0 && !ProBuilderReflection.DeleteFaces(detachedMesh, inverse)) || !ProBuilderReflection.DeleteFaces(mesh, indices)) throw ProBuilderErrors.OutcomeUnknown("detach-faces", "invoke", "Face deletion while detaching failed."); if (!ProBuilderReflection.RefreshMesh(mesh) || !ProBuilderReflection.RefreshMesh(detachedMesh)) throw ProBuilderErrors.OutcomeUnknown("detach-faces", "readback", "ProBuilder refresh failed."); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("detach-faces", "invoke", ex.Message); }
            try { EditorSceneManager.MarkSceneDirty(go.scene); var freshSource = EditorUtility.InstanceIDToObject(sourceId) as GameObject; var freshDetached = EditorUtility.InstanceIDToObject(detachedId) as GameObject; var sourceMesh = ProBuilderReflection.GetProBuilderMesh(freshSource); var detachedMesh = ProBuilderReflection.GetProBuilderMesh(freshDetached); var detachedFaces = ProBuilderReflection.GetFaceCount(detachedMesh); if (freshSource == null || freshDetached == null || freshSource.scene != freshDetached.scene || ProBuilderReflection.GetFaceCount(sourceMesh) != before - indices.Length || detachedFaces != indices.Length || ProBuilderReflection.GetVertexCount(detachedMesh) < 1) throw ProBuilderErrors.OutcomeUnknown("detach-faces", "postcondition", "Fresh source or detached face counts could not be confirmed."); if (undoGroup >= 0) Undo.CollapseUndoOperations(undoGroup); return new System.Threading.Tasks.ValueTask<Result>(new Result { DetachedObject = new ObjectRef { Id = freshDetached.GetInstanceID(), Name = freshDetached.name, Path = HierarchyPath(freshDetached) }, FaceCount = detachedFaces }); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("detach-faces", "readback", ex.Message); }
        }
        private static string HierarchyPath(GameObject go) { var names = new Stack<string>(); for (var current = go.transform; current != null; current = current.parent) names.Push(current.name); return string.Join("/", names.ToArray()); }
    }
}
#endif
