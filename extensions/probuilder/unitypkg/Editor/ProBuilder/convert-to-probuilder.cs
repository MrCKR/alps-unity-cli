#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.convert-to-probuilder — 将普通网格转换为 ProBuilder 网格。
    /// </summary>
    public class ConvertToProBuilderTool : IUnityCliTool<ConvertToProBuilderTool.Request, ConvertToProBuilderTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
        }

        public sealed class Result
        {
            public bool Converted { get; set; }
            public long VertexCount { get; set; }
            public long FaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.convert-to-probuilder",
            Summary = "Convert a regular Unity mesh into a ProBuilder mesh.",
            Description = "Convert a regular Unity mesh to a ProBuilder mesh by adding a ProBuilderMesh component and importing via MeshImporter. Fails if the target already has a ProBuilderMesh component.",
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
                new ToolExample { Description = "转换普通网格", Input = new Request { Target = "MyPrimitive" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var existingMesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (existingMesh != null)
                throw ProBuilderErrors.OperationFailed("convert-to-probuilder", "Target already has a ProBuilderMesh component.");

            Undo.RecordObject(go, "ProBuilder Convert to ProBuilder");

            var pbMesh = ProBuilderReflection.ConvertToProBuilderMesh(go);
            if (pbMesh == null)
                throw ProBuilderErrors.OperationFailed("convert-to-probuilder", "ProBuilder 5.x MeshImporter failed.");

            var vertexCount = ProBuilderReflection.GetVertexCount(pbMesh);
            var faceCount = ProBuilderReflection.GetFaceCount(pbMesh);
            if (vertexCount < 1 || faceCount < 1)
                throw ProBuilderErrors.OperationFailed("convert-to-probuilder", "Imported ProBuilderMesh contains no geometry.");

            ProBuilderReflection.RefreshMesh(pbMesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Converted = true,
                VertexCount = vertexCount,
                FaceCount = faceCount
            });
        }
    }
}
#endif
