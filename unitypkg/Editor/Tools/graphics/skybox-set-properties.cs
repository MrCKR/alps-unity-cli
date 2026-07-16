#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.skybox-set-properties — 设置当前 skybox 材质的 shader 属性。</summary>
    public class GraphicsSkyboxSetPropertiesTool : IUnityCliTool<GraphicsSkyboxSetPropertiesTool.Request, GraphicsSkyboxSetPropertiesTool.Result>
    {
        public sealed class Request { public string Property { get; set; } public string Value { get; set; } }
        public sealed class Result { public string Skybox { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-properties", Summary = "Set a shader property on the current skybox material.", Description = "Set a float or color property on RenderSettings.skybox. Requires a skybox to be assigned first.", Group = "graphics", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set exposure", Input = new Request { Property = "_Exposure", Value = "1.5" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var skybox = RenderSettings.skybox;
            if (skybox == null) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_NOT_SET", "No skybox material is assigned to RenderSettings.", "Use graphics.skybox-set-material first.");
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.Property) && !string.IsNullOrEmpty(r.Value))
            {
                if (skybox.HasProperty(r.Property))
                {
#if UNITY_2023_2_OR_NEWER
                    var pt = skybox.GetPropertyType(r.Property);
                    if (pt == UnityEngine.Rendering.ShaderPropertyType.Color) skybox.SetColor(r.Property, GraphicsToolSupport.ParseColor(r.Value));
                    else skybox.SetFloat(r.Property, GameObjectToolSupport.ParseFloat(r.Value, r.Property));
#else
                    var shader = skybox.shader; var idx = shader.FindPropertyIndex(r.Property); var pt = UnityEditor.ShaderUtil.GetPropertyType(shader, idx);
                    if (pt == UnityEditor.ShaderUtil.ShaderPropertyType.Color) skybox.SetColor(r.Property, GraphicsToolSupport.ParseColor(r.Value));
                    else skybox.SetFloat(r.Property, GameObjectToolSupport.ParseFloat(r.Value, r.Property));
#endif
                }
                effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-properties", Property = r.Property, Value = r.Value });
            }
            EditorUtility.SetDirty(skybox); AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Skybox = skybox.name, PlannedEffects = effects });
        }
    }
}
#endif
