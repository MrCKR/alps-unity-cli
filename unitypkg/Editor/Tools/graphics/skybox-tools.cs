#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.skybox-get — 读取天空盒/环境/雾/反射设置。</summary>
    public class GraphicsSkyboxGetTool : IUnityCliTool<GraphicsSkyboxGetTool.Request, GraphicsSkyboxGetTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public SkyboxInfo Skybox { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-get", Summary = "Read skybox material, sun, ambient, fog and reflection environment settings.", Description = "Read-only: returns RenderSettings skybox, sun, ambient mode/intensity/color, fog settings, and reflection settings.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read skybox settings", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var skybox = RenderSettings.skybox;
            var sun = RenderSettings.sun;
            return new Result
            {
                Skybox = new SkyboxInfo
                {
                    Skybox = skybox == null ? null : new AssetRef { Name = skybox.name, Path = SceneToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(skybox)), Guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(skybox)) },
                    Sun = sun == null ? null : new SunInfo { Name = sun.name, Path = GameObjectToolSupport.HierarchyPath(sun.gameObject) },
                    Ambient = new AmbientInfo { AmbientMode = RenderSettings.ambientMode.ToString(), AmbientIntensity = RenderSettings.ambientIntensity, AmbientLight = new Vector3Data(new Vector3(RenderSettings.ambientLight.r, RenderSettings.ambientLight.g, RenderSettings.ambientLight.b)) },
                    Fog = new FogInfo { Enabled = RenderSettings.fog, FogMode = RenderSettings.fogMode.ToString(), FogColor = GraphicsToolSupport.ColorHtml(RenderSettings.fogColor), FogDensity = RenderSettings.fogDensity, FogStartDistance = RenderSettings.fogStartDistance, FogEndDistance = RenderSettings.fogEndDistance },
                    Reflection = new ReflectionInfo { AmbientMode = RenderSettings.defaultReflectionMode.ToString(), ReflectionBounces = RenderSettings.reflectionBounces, ReflectionIntensity = RenderSettings.reflectionIntensity }
                }
            };
        }
    }

    /// <summary>graphics.skybox-set-material — 设置 RenderSettings.skybox 材质。</summary>
    public class GraphicsSkyboxSetMaterialTool : IUnityCliTool<GraphicsSkyboxSetMaterialTool.Request, GraphicsSkyboxSetMaterialTool.Result>
    {
        public sealed class Request { public string Material { get; set; } }
        public sealed class Result { public string Skybox { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-material", Summary = "Set the RenderSettings skybox material from an Assets/*.mat path.", Description = "Set RenderSettings.skybox. Accepts builtin:Default-Skybox to restore the built-in skybox.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set skybox material", Input = new Request { Material = "Assets/Skyboxes/Day.mat" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            if (string.IsNullOrEmpty(r.Material)) throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PATH_REQUIRED", "Pass --material <Assets/*.mat path>.", "Use tools run graphics.skybox-set-material --input '{\"material\":\"Assets/Skyboxes/Day.mat\"}'.");
            var material = GraphicsToolSupport.ResolveSkyboxMaterial(r.Material);
            if (material == null) throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_NOT_FOUND", "Skybox material not found: " + r.Material, "Pass an existing Assets/*.mat path.");
            RenderSettings.skybox = material; EditorUtility.SetDirty(RenderSettings.skybox); AssetDatabase.SaveAssets();
            return new Result { Skybox = material.name, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.skybox-set-material", Property = "skybox", Value = r.Material } } };
        }
    }

    /// <summary>graphics.skybox-set-properties — 设置当前 skybox 材质的 shader 属性。</summary>
    public class GraphicsSkyboxSetPropertiesTool : IUnityCliTool<GraphicsSkyboxSetPropertiesTool.Request, GraphicsSkyboxSetPropertiesTool.Result>
    {
        public sealed class Request { public string Property { get; set; } public string Value { get; set; } }
        public sealed class Result { public string Skybox { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-properties", Summary = "Set a shader property on the current skybox material.", Description = "Set a float or color property on RenderSettings.skybox. Requires a skybox to be assigned first.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set exposure", Input = new Request { Property = "_Exposure", Value = "1.5" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
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
            return new Result { Skybox = skybox.name, PlannedEffects = effects };
        }
    }

    /// <summary>graphics.skybox-set-ambient — 设置环境光(mode/intensity/color)。</summary>
    public class GraphicsSkyboxSetAmbientTool : IUnityCliTool<GraphicsSkyboxSetAmbientTool.Request, GraphicsSkyboxSetAmbientTool.Result>
    {
        public sealed class Request { public string Mode { get; set; } public float? Intensity { get; set; } public string Color { get; set; } public string AmbientEquatorColor { get; set; } public string AmbientGroundColor { get; set; } public string AmbientSkyColor { get; set; } }
        public sealed class Result { public string AmbientMode { get; set; } public double AmbientIntensity { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-ambient", Summary = "Set ambient lighting mode, intensity and colors.", Description = "Set RenderSettings ambientMode, ambientIntensity, ambientLight and equator/ground/sky colors.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set ambient intensity", Input = new Request { Intensity = 1.2f } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.Mode) && System.Enum.TryParse<UnityEngine.Rendering.AmbientMode>(r.Mode, true, out var mode)) { RenderSettings.ambientMode = mode; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-ambient", Property = "ambientMode", Value = r.Mode }); }
            if (r.Intensity.HasValue) { RenderSettings.ambientIntensity = r.Intensity.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-ambient", Property = "ambientIntensity", Value = r.Intensity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (!string.IsNullOrEmpty(r.Color)) { var c = GraphicsToolSupport.ParseColor(r.Color); RenderSettings.ambientLight = c; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-ambient", Property = "ambientLight", Value = GraphicsToolSupport.ColorHtml(c) }); }
            if (!string.IsNullOrEmpty(r.AmbientEquatorColor)) RenderSettings.ambientEquatorColor = GraphicsToolSupport.ParseColor(r.AmbientEquatorColor);
            if (!string.IsNullOrEmpty(r.AmbientGroundColor)) RenderSettings.ambientGroundColor = GraphicsToolSupport.ParseColor(r.AmbientGroundColor);
            if (!string.IsNullOrEmpty(r.AmbientSkyColor)) RenderSettings.ambientSkyColor = GraphicsToolSupport.ParseColor(r.AmbientSkyColor);
            return new Result { AmbientMode = RenderSettings.ambientMode.ToString(), AmbientIntensity = RenderSettings.ambientIntensity, PlannedEffects = effects };
        }
    }

    /// <summary>graphics.skybox-set-fog — 设置雾(enabled/mode/color/density/distances)。</summary>
    public class GraphicsSkyboxSetFogTool : IUnityCliTool<GraphicsSkyboxSetFogTool.Request, GraphicsSkyboxSetFogTool.Result>
    {
        public sealed class Request { public bool? Enabled { get; set; } public string Mode { get; set; } public string Color { get; set; } public float? Density { get; set; } public float? StartDistance { get; set; } public float? EndDistance { get; set; } }
        public sealed class Result { public bool Fog { get; set; } public string FogMode { get; set; } public string FogColor { get; set; } public double FogDensity { get; set; } public double FogStartDistance { get; set; } public double FogEndDistance { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-fog", Summary = "Set fog enabled, mode, color, density and distance range.", Description = "Set RenderSettings fog, fogMode, fogColor, fogDensity, fogStartDistance, fogEndDistance.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Enable fog", Input = new Request { Enabled = true } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (r.Enabled.HasValue) { RenderSettings.fog = r.Enabled.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fog", Value = r.Enabled.Value.ToString().ToLowerInvariant() }); }
            if (!string.IsNullOrEmpty(r.Mode) && System.Enum.TryParse<FogMode>(r.Mode, true, out var fm)) { RenderSettings.fogMode = fm; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogMode", Value = r.Mode }); }
            if (!string.IsNullOrEmpty(r.Color)) { var c = GraphicsToolSupport.ParseColor(r.Color); RenderSettings.fogColor = c; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogColor", Value = GraphicsToolSupport.ColorHtml(c) }); }
            if (r.Density.HasValue) { RenderSettings.fogDensity = r.Density.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogDensity", Value = r.Density.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (r.StartDistance.HasValue) { RenderSettings.fogStartDistance = r.StartDistance.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogStartDistance", Value = r.StartDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (r.EndDistance.HasValue) { RenderSettings.fogEndDistance = r.EndDistance.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-fog", Property = "fogEndDistance", Value = r.EndDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            return new Result { Fog = RenderSettings.fog, FogMode = RenderSettings.fogMode.ToString(), FogColor = GraphicsToolSupport.ColorHtml(RenderSettings.fogColor), FogDensity = RenderSettings.fogDensity, FogStartDistance = RenderSettings.fogStartDistance, FogEndDistance = RenderSettings.fogEndDistance, PlannedEffects = effects };
        }
    }

    /// <summary>graphics.skybox-set-reflection — 设置反射(mode/bounces/intensity/resolution)。</summary>
    public class GraphicsSkyboxSetReflectionTool : IUnityCliTool<GraphicsSkyboxSetReflectionTool.Request, GraphicsSkyboxSetReflectionTool.Result>
    {
        public sealed class Request { public string Mode { get; set; } public int? Bounces { get; set; } public float? Intensity { get; set; } public string Resolution { get; set; } }
        public sealed class Result { public string DefaultReflectionMode { get; set; } public long ReflectionBounces { get; set; } public double ReflectionIntensity { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-reflection", Summary = "Set reflection mode, bounces, intensity and resolution.", Description = "Set RenderSettings defaultReflectionMode, reflectionBounces, reflectionIntensity, defaultReflectionResolution.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set reflection bounces", Input = new Request { Bounces = 3 } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var effects = new List<GraphicsPlannedEffect>();
            if (!string.IsNullOrEmpty(r.Mode) && System.Enum.TryParse<UnityEngine.Rendering.DefaultReflectionMode>(r.Mode, true, out var drm)) { RenderSettings.defaultReflectionMode = drm; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "defaultReflectionMode", Value = r.Mode }); }
            if (r.Bounces.HasValue) { RenderSettings.reflectionBounces = r.Bounces.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "reflectionBounces", Value = r.Bounces.Value.ToString() }); }
            if (r.Intensity.HasValue) { RenderSettings.reflectionIntensity = r.Intensity.Value; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "reflectionIntensity", Value = r.Intensity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }); }
            if (!string.IsNullOrEmpty(r.Resolution))
            {
#if UNITY_2023_2_OR_NEWER
                if (System.Enum.TryParse<DefaultReflectionResolution>(r.Resolution, true, out var drr)) { RenderSettings.defaultReflectionResolution = drr; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "defaultReflectionResolution", Value = r.Resolution }); }
#else
                if (int.TryParse(r.Resolution, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ri)) { RenderSettings.defaultReflectionResolution = ri; effects.Add(new GraphicsPlannedEffect { Type = "graphics.skybox-set-reflection", Property = "defaultReflectionResolution", Value = r.Resolution }); }
#endif
            }
            return new Result { DefaultReflectionMode = RenderSettings.defaultReflectionMode.ToString(), ReflectionBounces = RenderSettings.reflectionBounces, ReflectionIntensity = RenderSettings.reflectionIntensity, PlannedEffects = effects };
        }
    }

    /// <summary>graphics.skybox-set-sun — 设置 RenderSettings.sun 方向光。</summary>
    public class GraphicsSkyboxSetSunTool : IUnityCliTool<GraphicsSkyboxSetSunTool.Request, GraphicsSkyboxSetSunTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public string Sun { get; set; } public List<GraphicsPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.skybox-set-sun", Summary = "Set the directional Light used as the scene sun for RenderSettings.", Description = "Set RenderSettings.sun from a target GameObject with a Light component. Pass null/empty to clear.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set sun light", Input = new Request { Target = "DirectionalLight" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            Light sun = null;
            if (!string.IsNullOrEmpty(r.Target))
            {
                var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "graphics.skybox-set-sun requires --target <go>.");
                sun = go.GetComponent<Light>();
                if (sun == null) throw new UnityCliBridge.BridgeCommandException("E_LIGHT_NOT_FOUND", "No Light component on target: " + GameObjectToolSupport.HierarchyPath(go), "Pass a GameObject with a directional Light component.");
            }
            else sun = RenderSettings.sun;
            RenderSettings.sun = sun;
            var sunPath = sun == null ? "null" : GameObjectToolSupport.HierarchyPath(sun.gameObject);
            return new Result { Sun = sunPath, PlannedEffects = new List<GraphicsPlannedEffect> { new GraphicsPlannedEffect { Type = "graphics.skybox-set-sun", Property = "sun", Value = sunPath } } };
        }
    }
}
#endif
