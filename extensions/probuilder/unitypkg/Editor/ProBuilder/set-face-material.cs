#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.set-face-material — 为 ProBuilder 网格的选定面分配材质。
    /// </summary>
    public class SetFaceMaterialTool : IUnityCliTool<SetFaceMaterialTool.Request, SetFaceMaterialTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
            [UnityCliRequired] public string MaterialPath { get; set; }
        }

        public sealed class Result
        {
            public long AssignedFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.set-face-material",
            Summary = "Assign a material asset to selected faces of a ProBuilder mesh.",
            Description = "Assign a material asset to selected faces with the ProBuilder 5.x ProBuilderMesh.SetMaterial API. The material is loaded from materialPath. When faceIndices is omitted, all faces are assigned.",
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
                new ToolExample { Description = "为面 0 分配材质", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 }, MaterialPath = "Assets/Materials/Red.mat" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("set-face-material", "No ProBuilderMesh on target.");

            var materialPath = request.MaterialPath;
            if (string.IsNullOrEmpty(materialPath))
                throw ProBuilderErrors.SchemaInvalid("materialPath is required for set-face-material.");

            // 加载材质
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
                throw ProBuilderErrors.OperationFailed("set-face-material", "Material not found at path: " + materialPath);

            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();
            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);
            if (faces == null || faces.Length == 0)
                throw ProBuilderErrors.OperationFailed("set-face-material", "No faces found on target.");

            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Set Face Material");
            if (!ProBuilderReflection.SetFaceMaterial(mesh, faces, material))
                throw ProBuilderErrors.OperationFailed("set-face-material", "ProBuilder 5.x ProBuilderMesh.SetMaterial failed.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshGo = ProBuilderInput.ResolveTarget(request.Target);
            var freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            if (!ProBuilderRequestProof.RequestedFacesUseMaterial(freshMesh, faceIndices ?? Enumerable.Range(0, faces.Length).ToArray(), material))
                throw ProBuilderErrors.OperationFailed("set-face-material", "Every requested face did not retain the requested material.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result { AssignedFaceCount = faces.Length });
        }
    }
}
#endif
