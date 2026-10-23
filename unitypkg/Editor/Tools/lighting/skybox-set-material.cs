#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>lighting.skybox-set-material — 设置 RenderSettings.skybox 材质。</summary>
    public class LightingSkyboxSetMaterialTool : IUnityCliTool<LightingSkyboxSetMaterialTool.Request, LightingSkyboxSetMaterialTool.Result>
    {
        public sealed class Request { public string Material { get; set; } }
        public sealed class Result { public string Skybox { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "lighting.skybox-set-material", Summary = "Set the RenderSettings skybox material from an Assets/*.mat path.", Description = "Set RenderSettings.skybox. Accepts builtin:Default-Skybox to restore the built-in skybox.", Group = "lighting",DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set skybox material", Input = new Request { Material = "Assets/Skyboxes/Day.mat" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(r.Material)) throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PATH_REQUIRED", "Pass --material <Assets/*.mat path>.", "Use tools run lighting.skybox-set-material --input '{\"material\":\"Assets/Skyboxes/Day.mat\"}'.");
            var material = GraphicsToolSupport.ResolveSkyboxMaterial(r.Material);
            if (material == null) throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_NOT_FOUND", "Skybox material not found: " + r.Material, "Pass an existing Assets/*.mat path.");
            var materialPath = AssetDatabase.GetAssetPath(material);
            var materialGuid = string.IsNullOrEmpty(materialPath) ? null : AssetDatabase.AssetPathToGUID(materialPath);
            ctx.CommitEffect();
            RenderSettings.skybox = material;
            UnityCliSceneMutation.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            var observed = RenderSettings.skybox;
            var observedPath = observed == null ? null : AssetDatabase.GetAssetPath(observed);
            var observedGuid = string.IsNullOrEmpty(observedPath) ? null : AssetDatabase.AssetPathToGUID(observedPath);
            if (observed == null || !string.Equals(observedGuid, materialGuid, System.StringComparison.Ordinal) || (materialGuid == null && observed != material)) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_MATERIAL_STATE_MISMATCH", "Skybox material GUID readback did not match the request.", "Retry after the editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Skybox = observed.name, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "lighting.skybox-set-material", Property = "skybox", Value = r.Material } } });
        }
    }
}
#endif
