#if UNITY_EDITOR
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("detach-faces", "No ProBuilderMesh on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("faceIndices is required.");

            Undo.RecordObject(go, "ProBuilder Detach Faces");
            var detached = ProBuilderReflection.DetachFaces(mesh, faceIndices);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            int faceCount = 0;
            if (detached != null)
            {
                var detMesh = ProBuilderReflection.GetProBuilderMesh(detached);
                if (detMesh != null) faceCount = ProBuilderReflection.GetFaceCount(detMesh);
            }

            return new Result
            {
                DetachedObject = detached != null
                    ? new ObjectRef { Id = detached.GetInstanceID(), Name = detached.name, Path = HierarchyPath(detached) }
                    : null,
                FaceCount = faceCount
            };
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
