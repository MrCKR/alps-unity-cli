#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.move-vertices — 按偏移量移动 ProBuilder 网格的选定顶点。
    /// </summary>
    public class MoveVerticesTool : IUnityCliTool<MoveVerticesTool.Request, MoveVerticesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public List<long> VertexIndices { get; set; }
            public double X { get; set; }                            // 默认 0
            public double Y { get; set; }                            // 默认 0
            public double Z { get; set; }                            // 默认 0
        }

        public sealed class Result
        {
            public long MovedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.move-vertices",
            Summary = "Move selected vertices of a ProBuilder mesh by an offset.",
            Description = "Move selected vertices on a ProBuilder mesh by an X/Y/Z offset. Requires at least one vertex index.",
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
                new ToolExample { Description = "沿 Y 轴移动顶点 0", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0 }, Y = 0.5 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("move-vertices", "No ProBuilderMesh on target.");

            int[] vertexIndices = request.VertexIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "vertexIndices")).ToArray();
            if (vertexIndices == null || vertexIndices.Length == 0)
                throw ProBuilderErrors.SchemaInvalid("vertexIndices is required for move-vertices.");

            var offset = ProBuilderOperationProof.CheckedVector3(request.X, request.Y, request.Z, "offset");

            var beforePositions = ProBuilderRequestProof.CapturePositions(mesh);
            if (beforePositions == null || vertexIndices.Any(index => index < 0 || index >= beforePositions.Count))
                throw ProBuilderErrors.SchemaInvalid("vertexIndices contains an invalid vertex index.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Move Vertices");
            var moved = ProBuilderReflection.MoveVerticesByOffset(mesh, vertexIndices, offset);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshGo = ProBuilderInput.ResolveTarget(request.Target);
            var freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            if (!ProBuilderRequestProof.VerticesMovedByRequestedOffset(freshMesh, beforePositions, vertexIndices, offset))
                throw ProBuilderErrors.OperationFailed("move-vertices", "Every requested vertex did not move by the requested offset.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { MovedCount = moved });
        }
    }
}
#endif
