#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// material.create — 从指定 shader 创建新 Material 资产。拒绝覆盖(除非 overwrite)。
    /// </summary>
    public class MaterialCreateTool : IUnityCliTool<MaterialCreateTool.Request, MaterialCreateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Shader { get; set; }
            public bool? Overwrite { get; set; }
        }

        public sealed class Result
        {
            public MaterialInfo Material { get; set; }
            public List<MaterialPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "material.create",
            Summary = "Create a new Material asset from a named shader at an Assets/*.mat path.",
            Description = "Create a new Material asset from a named shader at an Assets/*.mat path. Use shader.list or asset.search-built-in to discover a valid shader name first; an unknown shader returns E_SHADER_NOT_FOUND. Refuses to overwrite an existing material unless overwrite=true.",
            Group = "material",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a Standard material", Input = new Request { Path = "Assets/Materials/New.mat", Shader = "Standard" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            MaterialToolSupport.EnsureEditorNotBusy("material.create");
            var path = MaterialToolSupport.RequireMaterialPath(request.Path);
            var shaderName = request.Shader;
            var overwrite = request.Overwrite ?? false;
            if (string.IsNullOrEmpty(shaderName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_REQUIRED", "Material create requires a shader name.", "Pass a Unity shader name such as Standard or Universal Render Pipeline/Lit.");
            }
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SHADER_NOT_FOUND", "Shader not found: " + shaderName, "Check the shader name in Unity or import the required render pipeline package.");
            }
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_OUTPUT_EXISTS", "Material output already exists: " + path, "Pass --overwrite or choose a different Assets/*.mat path.");
            }

            AssetToolSupport.CreateAssetDirectory(path);
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            var material = new Material(shader);
            material.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Material = MaterialToolSupport.BuildMaterialInfo(path, MaterialToolSupport.LoadMaterialAsset(path), null),
                PlannedEffects = new List<MaterialPlannedEffect> { new MaterialPlannedEffect { Type = "material.create", Path = path } }
            });
        }
    }
}
#endif
