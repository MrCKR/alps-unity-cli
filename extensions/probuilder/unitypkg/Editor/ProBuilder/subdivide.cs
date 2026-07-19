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
            SideEffects = new[] { "scene-modify", "undo-stack" },
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("subdivide", "No ProBuilderMesh on target.");

            var beforeCount = ProBuilderReflection.GetFaceCount(mesh);
            if (beforeCount < 1)
                throw ProBuilderErrors.OperationFailed("subdivide", "Target has no readable faces.");
            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).Distinct().ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                faceIndices = Enumerable.Range(0, beforeCount).ToArray();
            if (ProBuilderReflection.GetFacesByIndices(mesh, faceIndices) == null)
                throw ProBuilderErrors.SchemaInvalid("faceIndices contains an invalid face index.");
            var before = ProBuilderOperationProof.Capture(mesh, faceIndices: faceIndices);
            if (before == null) throw ProBuilderErrors.OperationFailed("subdivide", "Requested face geometry is not readable.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Subdivide");
            if (!ProBuilderReflection.SubdivideFaces(mesh, faceIndices))
                throw ProBuilderErrors.OperationFailed("subdivide", "ProBuilder 5.x Subdivision.Subdivide failed.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            var afterCount = ProBuilderReflection.GetFaceCount(freshMesh);
            var after = ProBuilderOperationProof.Capture(freshMesh, faceIndices: Enumerable.Range(0, Math.Max(0, afterCount)).ToArray());
            if (!ProBuilderOperationProof.HasSubdividedRequestedFaces(before, after) || afterCount <= beforeCount)
                throw ProBuilderErrors.OperationFailed("subdivide", "Subdivide did not produce readable requested mesh topology.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                BeforeFaceCount = beforeCount,
                AfterFaceCount = afterCount
            });
        }
    }
}
#endif
