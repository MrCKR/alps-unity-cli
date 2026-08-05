#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli.Contexts.Scene
{
    /// <summary>
    /// scene.renderer-features 内置 Context：读取 URP renderer feature 可用性和摘要。
    /// </summary>
    public class SceneRendererFeaturesContext : IUnityCliContext<SceneRendererFeaturesContext.Request, SceneRendererFeaturesContext.Result>
    {
        public sealed class Request { }

        public sealed class Result
        {
            public bool Available { get; set; }
            public string Renderer { get; set; }
            public string[] Features { get; set; }
            public int Count { get; set; }
            public string Diagnostics { get; set; }
            public string[] NextActions { get; set; }
        }

        // ── desc:requiresPackages 用于发现阶段提示 URP 依赖 ──
        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "scene.renderer-features",
            Summary = "Report URP renderer feature availability and configured renderer data names.",
            Description = "Read URP renderer feature availability and configured feature names for the active/default render pipeline. If URP or a pipeline asset is unavailable, returns available=false with diagnostics and nextActions.",
            Category = "scene",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            Examples = new[] { new ContextExample { Description = "Read URP renderer features", Input = new Request { } } },
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" }
        };

        /// <summary>
        /// 通过反射读取 URP renderer data 列表；URP 或 pipeline asset 缺失时返回可恢复诊断。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var rendererDataType = FindType("UnityEngine.Rendering.Universal.RendererData");
            var pipelineAssetType = FindType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset");

            if (pipelineAssetType == null && rendererDataType == null)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Available = false,
                    Features = new string[0],
                    Count = 0,
                    Diagnostics = "URP package (com.unity.render-pipelines.universal) is not installed. Renderer features context requires URP.",
                    NextActions = new[] { "Install the URP package via Package Manager." }
                });
            }

            var pipelineAsset = GraphicsSettings.defaultRenderPipeline;
            if (pipelineAsset == null)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Available = false,
                    Features = new string[0],
                    Count = 0,
                    Diagnostics = "No URP render pipeline asset is assigned in Graphics Settings.",
                    NextActions = new[] { "Assign a UniversalRenderPipelineAsset in Graphics Settings." }
                });
            }

            var rendererDataListProp = pipelineAsset.GetType().GetProperty("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? pipelineAsset.GetType().GetProperty("rendererDataList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var rendererList = rendererDataListProp?.GetValue(pipelineAsset) as IEnumerable<UnityEngine.Object>;

            var features = new List<string>();
            if (rendererList != null)
            {
                foreach (var rendererObj in rendererList)
                {
                    if (rendererObj == null) continue;
                    features.Add(rendererObj.name + " (" + rendererObj.GetType().Name + ")");
                }
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Available = true,
                Renderer = pipelineAsset.name,
                Features = features.ToArray(),
                Count = features.Count,
                Diagnostics = null,
                NextActions = null
            });
        }

        // 查找可选 URP 类型，避免 Core 对 URP 建立编译期依赖。
        private static Type FindType(string fullName)
        {
            var type = Type.GetType(fullName);
            if (type != null) return type;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch
                {
                    // 忽略无法反射的编辑器程序集，继续查找其它程序集。
                }
            }
            return null;
        }
    }
}
#endif
