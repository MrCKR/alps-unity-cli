#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.create-polygon — 从选定顶点创建多边形面。
    /// </summary>
    public class CreatePolygonTool : IUnityCliTool<CreatePolygonTool.Request, CreatePolygonTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public List<long> VertexIndices { get; set; }
            public bool? Unordered { get; set; }                    // 可选,默认 true
        }

        public sealed class Result
        {
            public long NewFaceIndex { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.create-polygon",
            Summary = "Create a new polygon face from selected vertices on a ProBuilder mesh.",
            Description = "Create a new polygon face from selected vertices on a ProBuilder mesh. Requires at least 3 vertex indices. When unordered is true (default), ProBuilder attempts to order the vertices into a valid polygon.",
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
                new ToolExample { Description = "从 3 个顶点创建三角面", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0, 1, 2 } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("create-polygon", "No ProBuilderMesh on target.");

            int[] vertexIndices = request.VertexIndices?.Select(i => (int)i).ToArray();
            if (vertexIndices == null || vertexIndices.Length < 3)
                throw ProBuilderErrors.SchemaInvalid("At least 3 vertexIndices required for create-polygon.");

            var unordered = request.Unordered ?? true;

            Undo.RecordObject(go, "ProBuilder Create Polygon");
            var created = ProBuilderReflection.CreatePolygon(mesh, vertexIndices, unordered);
            if (!created)
                throw ProBuilderErrors.OperationFailed("create-polygon", "AppendElements.CreatePolygon method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            return new Result { NewFaceIndex = faceCount - 1 };
        }
    }
}
#endif
