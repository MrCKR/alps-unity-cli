#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>lighting.skybox.set-properties — 设置当前 skybox 材质的 shader 属性。</summary>
    public class GraphicsSkyboxSetPropertiesTool : IUnityCliTool<GraphicsSkyboxSetPropertiesTool.Request, GraphicsSkyboxSetPropertiesTool.Result>
    {
        public sealed class Request { public string Property { get; set; } public string Value { get; set; } }
        public sealed class Result { public string Skybox { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "lighting.skybox.set-properties", Summary = "Set a shader property on the current skybox material.", Description = "Set a float or color property on RenderSettings.skybox. Requires a skybox to be assigned first.", Group = "rendering",DestructiveHint = true, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set exposure", Input = new Request { Property = "_Exposure", Value = "1.5" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var skybox = RenderSettings.skybox;
            if (skybox == null) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_NOT_SET", "No skybox material is assigned to RenderSettings.", "Use lighting.skybox.set-material first.");
            var effects = new List<GraphicsPlannedEffect>();
            if (string.IsNullOrEmpty(r.Property) != string.IsNullOrEmpty(r.Value)) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_PROPERTY_INPUT_INVALID", "property and value must be provided together.", "Pass a material property and value.");
            if (!string.IsNullOrEmpty(r.Property))
            {
                if (!skybox.HasProperty(r.Property)) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_PROPERTY_NOT_FOUND", "Skybox material does not contain property: " + r.Property, "Inspect the assigned skybox shader properties.");
#if UNITY_2023_2_OR_NEWER
                var pt = skybox.GetPropertyType(r.Property).ToString();
#else
                var shader = skybox.shader; var idx = shader.FindPropertyIndex(r.Property); var pt = UnityEditor.ShaderUtil.GetPropertyType(shader, idx).ToString();
#endif
                var color = pt == "Color" ? (Color?)GraphicsToolSupport.ParseColor(r.Value) : (Color?)null;
                var number = pt == "Float" || pt == "Range" ? (float?)GraphicsToolSupport.ParseFinite(r.Value, r.Property, -1000000f, 1000000f) : (float?)null;
                int parsedInt = 0;
                var isInt = pt == "Int";
                if (isInt && !int.TryParse(r.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsedInt)) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_PROPERTY_INPUT_INVALID", "Invalid integer value for " + r.Property + ".", "Pass an integer value for an Int shader property.");
                if (!color.HasValue && !number.HasValue && !isInt) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_PROPERTY_TYPE_UNSUPPORTED", "Skybox property " + r.Property + " has unsupported shader type " + pt + ".", "Only Color, Float, Range, and Int properties can be set by this command.");
                var skyboxPath = AssetDatabase.GetAssetPath(skybox);
                var skyboxGuid = string.IsNullOrEmpty(skyboxPath) ? null : AssetDatabase.AssetPathToGUID(skyboxPath);
                if (string.IsNullOrEmpty(skyboxPath) || string.IsNullOrEmpty(skyboxGuid) || !skyboxPath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_ASSET_PATH_INVALID", "Skybox material must be a writable Assets/ material for verified property writes.", "Assign an Assets/*.mat skybox material first.");
                ctx.CommitEffect();
                if (color.HasValue) skybox.SetColor(r.Property, color.Value); else if (number.HasValue) skybox.SetFloat(r.Property, number.Value); else skybox.SetInt(r.Property, parsedInt);
                EditorUtility.SetDirty(skybox);
                AssetDatabase.SaveAssetIfDirty(skybox);
                var observed = GraphicsToolSupport.ReloadAsset<Material>(skyboxPath, skyboxGuid);
                if (observed == null || (color.HasValue && !GraphicsToolSupport.SameColor(observed.GetColor(r.Property), color.Value)) || (number.HasValue && !Mathf.Approximately(observed.GetFloat(r.Property), number.Value)) || (isInt && observed.GetInt(r.Property) != parsedInt)) throw new UnityCliBridge.BridgeCommandException("E_SKYBOX_PROPERTY_STATE_MISMATCH", "Skybox property readback did not match the request.", "Retry after the editor is idle.");
                skybox = observed;
                effects.Add(new GraphicsPlannedEffect { Type = "lighting.skybox.set-properties", Property = r.Property, Value = r.Value });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Skybox = skybox.name, PlannedEffects = effects });
        }
    }
}
#endif
