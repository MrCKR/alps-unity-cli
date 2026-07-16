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
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var skybox = RenderSettings.skybox;
            var sun = RenderSettings.sun;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Skybox = new SkyboxInfo
                {
                    Skybox = skybox == null ? null : new AssetRef { Name = skybox.name, Path = SceneToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(skybox)), Guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(skybox)) },
                    Sun = sun == null ? null : new SunInfo { Name = sun.name, Path = GameObjectToolSupport.HierarchyPath(sun.gameObject) },
                    Ambient = new AmbientInfo { AmbientMode = RenderSettings.ambientMode.ToString(), AmbientIntensity = RenderSettings.ambientIntensity, AmbientLight = new Vector3Data(new Vector3(RenderSettings.ambientLight.r, RenderSettings.ambientLight.g, RenderSettings.ambientLight.b)) },
                    Fog = new FogInfo { Enabled = RenderSettings.fog, FogMode = RenderSettings.fogMode.ToString(), FogColor = GraphicsToolSupport.ColorHtml(RenderSettings.fogColor), FogDensity = RenderSettings.fogDensity, FogStartDistance = RenderSettings.fogStartDistance, FogEndDistance = RenderSettings.fogEndDistance },
                    Reflection = new ReflectionInfo { AmbientMode = RenderSettings.defaultReflectionMode.ToString(), ReflectionBounces = RenderSettings.reflectionBounces, ReflectionIntensity = RenderSettings.reflectionIntensity }
                }
            });
        }
    }
}
#endif
