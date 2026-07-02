#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.subdivide — 细分 ProBuilder 网格的选定面。
    /// </summary>
    public class SubdivideTool : IUnityCliTool<SubdivideTool.Request, SubdivideTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long BeforeFaceCount { get; set; }
            public long AfterFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.subdivide",
            Summary = "Subdivide selected faces of a ProBuilder mesh to increase face count.",
            Description = "Subdivide selected faces of a ProBuilder mesh, increasing face count. When faceIndices is omitted, all faces are subdivided.",
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
                new ToolExample { Description = "细分明 0", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("subdivide", "No ProBuilderMesh on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            var beforeCount = ProBuilderReflection.GetFaceCount(mesh);
            Undo.RecordObject(go, "ProBuilder Subdivide");

            if (faceIndices != null && faceIndices.Length > 0)
                ProBuilderReflection.SubdivideFaces(mesh, faceIndices);
            else
                ProBuilderReflection.SubdivideFaces(mesh, Enumerable.Range(0, beforeCount).ToArray());

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var afterCount = ProBuilderReflection.GetFaceCount(mesh);
            return new Result
            {
                BeforeFaceCount = beforeCount,
                AfterFaceCount = afterCount
            };
        }
    }
}
#endif
