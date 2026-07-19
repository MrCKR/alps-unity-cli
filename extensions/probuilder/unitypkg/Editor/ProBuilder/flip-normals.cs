#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.flip-normals — 翻转 ProBuilder 网格选定面的法线。
    /// </summary>
    public class FlipNormalsTool : IUnityCliTool<FlipNormalsTool.Request, FlipNormalsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long FlippedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.flip-normals",
            Summary = "Flip normals of selected faces on a ProBuilder mesh.",
            Description = "Flip the normals of selected faces on a ProBuilder mesh. When faceIndices is omitted, all faces are flipped.",
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
                new ToolExample { Description = "翻转面 0 法线", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("flip-normals", "No ProBuilderMesh on target.");

            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            if (faceCount < 1)
                throw ProBuilderErrors.OperationFailed("flip-normals", "Target has no readable faces.");
            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).Distinct().ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                faceIndices = Enumerable.Range(0, faceCount).ToArray();
            if (ProBuilderReflection.GetFacesByIndices(mesh, faceIndices) == null)
                throw ProBuilderErrors.SchemaInvalid("faceIndices contains an invalid face index.");

            var before = ProBuilderOperationProof.Capture(mesh, faceIndices: faceIndices);
            if (before == null) throw ProBuilderErrors.OperationFailed("flip-normals", "Requested face geometry is not readable.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Flip Normals");
            if (!ProBuilderReflection.FlipFaces(mesh, faceIndices))
                throw ProBuilderErrors.OperationFailed("flip-normals", "ProBuilder 5.x Face.Reverse failed.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            if (!ProBuilderOperationProof.HasFlippedRequestedFaces(before, freshMesh, faceIndices))
                throw ProBuilderErrors.OperationFailed("flip-normals", "Requested faces did not reverse readable winding.");

            var flipped = faceIndices.Length;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { FlippedCount = flipped });
        }
    }
}
#endif
