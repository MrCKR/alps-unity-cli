#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.detach-faces — 将选定面分离到新 GameObject。
    /// </summary>
    public class DetachFacesTool : IUnityCliTool<DetachFacesTool.Request, DetachFacesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public List<long> FaceIndices { get; set; }
        }

        public sealed class Result
        {
            public ObjectRef DetachedObject { get; set; }
            public long FaceCount { get; set; }
        }

        public sealed class ObjectRef
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.detach-faces",
            Summary = "Detach selected faces from a ProBuilder mesh into a new GameObject.",
            Description = "Detach selected faces from a ProBuilder mesh into a new GameObject. Requires at least one face index.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "分离面 0", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("detach-faces", "No ProBuilderMesh on target.");
            if (ProBuilderReflection.GetProBuilderMeshType() == null)
                throw ProBuilderErrors.PackageNotAvailable();

            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).Distinct().ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("faceIndices is required.");
            var sourceFaceCount = ProBuilderReflection.GetFaceCount(mesh);
            if (sourceFaceCount < 1 || ProBuilderReflection.GetFacesByIndices(mesh, faceIndices) == null)
                throw ProBuilderErrors.SchemaInvalid("faceIndices contains an invalid face index.");
            var before = ProBuilderOperationProof.Capture(mesh, faceIndices: faceIndices);
            if (before == null) throw ProBuilderErrors.OperationFailed("detach-faces", "Requested face geometry is not readable.");

            var sourceInstanceId = go.GetInstanceID();
            var selected = new HashSet<int>(faceIndices);
            var inverse = Enumerable.Range(0, sourceFaceCount).Where(index => !selected.Contains(index)).ToArray();
            ctx.CommitEffect();
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("ProBuilder Detach Faces");

            try
            {
                Undo.RecordObject(go, "ProBuilder Detach Faces");
                var detached = UnityEngine.Object.Instantiate(go);
                var detachedInstanceId = detached.GetInstanceID();
                detached.name = go.name + "_Detached";
                Undo.RegisterCreatedObjectUndo(detached, "ProBuilder Detach Faces");

                var detachedMesh = ProBuilderReflection.GetProBuilderMesh(detached);
                if (detachedMesh == null)
                    throw ProBuilderErrors.OperationFailed("detach-faces", "Cloned object has no ProBuilderMesh component.");

                // DeleteFaces 和 RefreshMesh 会改写 ProBuilder 组件及其关联 Unity Mesh；必须在首次拓扑修改前完整记录。
                var sourceProBuilderMesh = mesh as UnityEngine.Object;
                var detachedProBuilderMesh = detachedMesh as UnityEngine.Object;
                if (sourceProBuilderMesh == null || detachedProBuilderMesh == null)
                    throw ProBuilderErrors.OperationFailed("detach-faces", "ProBuilderMesh component is not undoable.");
                var undoTargets = new List<UnityEngine.Object>
                {
                    sourceProBuilderMesh,
                    detachedProBuilderMesh,
                    go.GetComponent<MeshFilter>()?.sharedMesh,
                    detached.GetComponent<MeshFilter>()?.sharedMesh
                }.Where(target => target != null).Distinct().ToArray();
                Undo.RegisterCompleteObjectUndo(undoTargets, "ProBuilder Detach Faces");

                if (inverse.Length > 0 && !ProBuilderReflection.DeleteFaces(detachedMesh, inverse))
                    throw ProBuilderErrors.OperationFailed("detach-faces", "Failed to retain selected faces on the detached object.");
                if (!ProBuilderReflection.DeleteFaces(mesh, faceIndices))
                    throw ProBuilderErrors.OperationFailed("detach-faces", "Failed to remove selected faces from the source object.");

                ProBuilderReflection.RefreshMesh(mesh);
                ProBuilderReflection.RefreshMesh(detachedMesh);
                var freshSource = EditorUtility.InstanceIDToObject(sourceInstanceId) as GameObject;
                var freshDetached = EditorUtility.InstanceIDToObject(detachedInstanceId) as GameObject;
                var freshSourceMesh = freshSource == null ? null : ProBuilderReflection.GetProBuilderMesh(freshSource);
                var freshDetachedMesh = freshDetached == null ? null : ProBuilderReflection.GetProBuilderMesh(freshDetached);
                var after = ProBuilderOperationProof.Capture(freshSourceMesh, faceIndices: Enumerable.Range(0, Math.Max(0, ProBuilderReflection.GetFaceCount(freshSourceMesh))).ToArray());
                var detachedFaceCount = ProBuilderReflection.GetFaceCount(freshDetachedMesh);
                var remainingFaceCount = ProBuilderReflection.GetFaceCount(freshSourceMesh);
                if (!ProBuilderOperationProof.HasRemovedRequestedFaces(before, after) || !ProBuilderReflection.HasReadableGeometry(freshDetachedMesh)
                    || !ProBuilderOperationProof.ContainsFaceSignatures(freshDetachedMesh, before.Faces)
                    || freshSource == null || freshSource.GetInstanceID() != sourceInstanceId || freshDetached == null || freshDetached.GetInstanceID() != detachedInstanceId
                    || freshSource.scene != freshDetached.scene || detachedFaceCount != faceIndices.Length || remainingFaceCount != sourceFaceCount - faceIndices.Length)
                    throw ProBuilderErrors.OperationFailed("detach-faces", "Requested faces were not split into readable source and detached topology.");

                EditorSceneManager.MarkSceneDirty(freshSource.scene);
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DetachedObject = new ObjectRef { Id = freshDetached.GetInstanceID(), Name = freshDetached.name, Path = HierarchyPath(freshDetached) },
                    FaceCount = detachedFaceCount
                });
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var cur = go.transform;
            while (cur != null) { names.Push(cur.name); cur = cur.parent; }
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
