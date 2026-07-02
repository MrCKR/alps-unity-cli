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
            Description = "Assign a material to selected faces of a ProBuilder mesh. The material is loaded from MaterialPath via AssetDatabase. When faceIndices is omitted, all faces are assigned. Falls back to applying the material to the MeshRenderer when per-face assignment is unavailable.",
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
                new ToolExample { Description = "为面 0 分配材质", Input = new Request { Target = "MyCube", FaceIndices = new List<long> { 0 }, MaterialPath = "Assets/Materials/Red.mat" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
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

            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);

            Undo.RecordObject(go, "ProBuilder Set Face Material");

            // 通过反射调用 ProBuilderMesh.SetMaterial 进行逐面材质分配
            var faces = faceIndices != null && faceIndices.Length > 0
                ? ProBuilderReflection.GetFacesByIndices(mesh, faceIndices)
                : ProBuilderReflection.GetAllFaces(mesh);

            if (faces != null && faces.Length > 0)
            {
                var setOk = ProBuilderReflection.SetFaceMaterial(mesh, faces, material);
                if (!setOk)
                {
                    // 回退:将材质应用到整个 renderer
                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer != null)
                    {
                        var materials = renderer.sharedMaterials;
                        if (materials.Length == 0)
                            materials = new Material[] { material };
                        else
                            materials[0] = material;
                        renderer.sharedMaterials = materials;
                    }
                }
            }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var assigned = faces != null ? faces.Length : (faceIndices != null ? faceIndices.Length : faceCount);
            return new Result { AssignedFaceCount = assigned };
        }
    }
}
#endif
