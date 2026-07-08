#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityCli.Samples
{
    /// <summary>
    /// 新形态自定义 Context 样板(IUnityCliContext&lt;TReq,TRes&gt; 泛型接口自包含)。
    ///
    /// 设计依据:.plan/Context形态.md
    /// - Request/Result 嵌套 Context 类内
    /// - Describe() 只声明人写字段,schema 由框架从 Request/Result 反射派生
    /// - Read(Request, ctx): Result,只读观察,不得修改 Unity 状态、资产、场景或 PlayMode
    /// </summary>
    public class SampleContext : IUnityCliContext<SampleContext.Request, SampleContext.Result>
    {
        // ── 协议类型:嵌套 ──
        public sealed class Request
        {
            public bool IncludeDisabled { get; set; }
        }

        public sealed class Result
        {
            public string UnityVersion { get; set; }
            public string ActiveScene { get; set; }
            public string RenderPipeline { get; set; }
            public string BuildTarget { get; set; }
            public bool IncludeDisabled { get; set; }
        }

        // ── desc:只声明人工字段,不含 inputSchema/outputSchema ──
        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "sample.project-summary",
            Description = "Returns a read-only summary of the Unity project: version, active scene, render pipeline, and build target.",
            Category = "sample",
            Version = "1.0.0",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "onDemand"
        };

        /// <summary>
        /// 读取项目配置摘要。只读访问 Editor 状态,不写文件、不改场景、不触发导入或 PlayMode。
        /// </summary>
        public Result Read(Request request, UnityCliProjectToolContext ctx)
        {
            var activeScene = SceneManager.GetActiveScene();
            var buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            var renderPipeline = GraphicsSettings.currentRenderPipeline != null
                ? GraphicsSettings.currentRenderPipeline.name
                : "Built-in";

            return new Result
            {
                UnityVersion = Application.unityVersion,
                ActiveScene = activeScene.path ?? string.Empty,
                RenderPipeline = renderPipeline,
                BuildTarget = buildTarget,
                IncludeDisabled = request != null && request.IncludeDisabled
            };
        }
    }
}
#endif
