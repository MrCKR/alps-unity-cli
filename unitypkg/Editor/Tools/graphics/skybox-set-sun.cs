#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.skybox-set-sun — 设置 RenderSettings.sun 方向光。</summary>
    public class GraphicsSkyboxSetSunTool : IUnityCliTool<GraphicsSkyboxSetSunTool.Request, GraphicsSkyboxSetSunTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public string Sun { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-sun", Summary = "Set the directional Light used as the scene sun for RenderSettings.", Description = "Set RenderSettings.sun from a target GameObject with a Light component. Pass null/empty to clear.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set sun light", Input = new Request { Target = "DirectionalLight" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            Light sun = null;
            var expectedId = 0;
            if (!string.IsNullOrEmpty(r.Target))
            {
                var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "graphics.skybox-set-sun requires --target <go>.");
                sun = go.GetComponent<Light>();
                if (sun == null || sun.type != LightType.Directional) throw new UnityCliBridge.BridgeCommandException("E_DIRECTIONAL_LIGHT_REQUIRED", "Target must have a Directional Light component: " + GameObjectToolSupport.HierarchyPath(go), "Pass a GameObject with a directional Light component.");
                expectedId = sun.GetInstanceID();
            }
            ctx.CommitEffect();
            RenderSettings.sun = sun;
            var observed = RenderSettings.sun;
            if ((expectedId == 0 && observed != null) || (expectedId != 0 && (observed == null || observed.GetInstanceID() != expectedId))) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_SUN_STATE_MISMATCH", "Skybox sun readback did not match the requested target.", "Retry after the editor is idle.");
            var sunPath = observed == null ? "null" : GameObjectToolSupport.HierarchyPath(observed.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Sun = sunPath, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.skybox-set-sun", Property = "sun", Value = sunPath } } });
        }
    }
}
#endif
