#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.skybox-set-material — 设置 RenderSettings.skybox 材质。</summary>
    public class GraphicsSkyboxSetMaterialTool : IUnityCliTool<GraphicsSkyboxSetMaterialTool.Request, GraphicsSkyboxSetMaterialTool.Result>
    {
        public sealed class Request { public string Material { get; set; } }
        public sealed class Result { public string Skybox { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-material", Summary = "Set the RenderSettings skybox material from an Assets/*.mat path.", Description = "Set RenderSettings.skybox. Accepts builtin:Default-Skybox to restore the built-in skybox.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set skybox material", Input = new Request { Material = "Assets/Skyboxes/Day.mat" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(r.Material)) throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PATH_REQUIRED", "Pass --material <Assets/*.mat path>.", "Use tools run graphics.skybox-set-material --input '{\"material\":\"Assets/Skyboxes/Day.mat\"}'.");
            var material = GraphicsToolSupport.ResolveSkyboxMaterial(r.Material);
            if (material == null) throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_NOT_FOUND", "Skybox material not found: " + r.Material, "Pass an existing Assets/*.mat path.");
            RenderSettings.skybox = material;
            UnityCliSceneMutation.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Skybox = material.name, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.skybox-set-material", Property = "skybox", Value = r.Material } } });
        }
    }
}
#endif
