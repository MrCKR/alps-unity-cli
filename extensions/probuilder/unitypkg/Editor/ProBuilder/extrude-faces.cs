#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.extrude-faces — 挤出 ProBuilder 网格的选定面。
    /// </summary>
    public class ExtrudeFacesTool : IUnityCliTool<ExtrudeFacesTool.Request, ExtrudeFacesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
            public double? Distance { get; set; }                  // 可选,默认 0.5
            public string Method { get; set; }                     // 可选,默认 vertex;可选 faceNormal
        }

        public sealed class Result
        {
            public long ModifiedFaceCount { get; set; }
            public long NewVertexCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.extrude-faces",
            Summary = "Extrude selected faces of a ProBuilder mesh by a distance.",
            Description = "Extrude selected faces of a ProBuilder mesh by a given distance. When faceIndices is omitted, all faces are extruded. Method controls extrude direction: vertex (default) or faceNormal.",
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
                new ToolExample { Description = "挤出面 0 0.5 距离", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 }, Distance = 0.5 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("extrude-faces", "No ProBuilderMesh component on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            var distance = (float)(request.Distance ?? 0.5);
            var method = request.Method ?? "vertex";

            var beforeVertexCount = ProBuilderReflection.GetVertexCount(mesh);
            Undo.RecordObject(go, "ProBuilder Extrude Faces");

            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces == null || faces.Length == 0)
                throw ProBuilderErrors.OperationFailed("extrude-faces", "No valid faces found on target.");
            if (!ProBuilderReflection.Extrude(mesh, faces, distance, method))
                throw ProBuilderErrors.OperationFailed("extrude-faces", "ProBuilder 5.x ExtrudeElements.Extrude failed.");
            ProBuilderReflection.RefreshMesh(mesh);

            var afterVertexCount = ProBuilderReflection.GetVertexCount(mesh);
            if (beforeVertexCount < 0 || afterVertexCount <= beforeVertexCount)
                throw ProBuilderErrors.OperationFailed("extrude-faces", "Extrude completed without increasing vertex count.");
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                ModifiedFaceCount = faces?.Length ?? 0,
                NewVertexCount = afterVertexCount - beforeVertexCount
            });
        }
    }
}
#endif
