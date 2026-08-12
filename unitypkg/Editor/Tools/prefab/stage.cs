#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli.Tools.Prefab
{
    /// <summary>
    /// prefab.stage 只读 Tool：读取当前 Prefab Stage 状态。
    /// </summary>
    public class PrefabStageTool : IUnityCliTool<PrefabStageTool.Request, PrefabStageTool.Result>
    {
        public sealed class Request { }

        public sealed class Result
        {
            public bool IsOpen { get; set; }
            public string AssetPath { get; set; }
            public string PrefabRootName { get; set; }
            public string Mode { get; set; }
            public bool IsDirty { get; set; }
        }

        // ── desc:只声明人工字段,source=built-in 表示进入只读 Tool 表 ──
        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.stage", Summary = "Read current Prefab editing stage state: whether a prefab is being edited, its path, and dirty status.",
            Description = "Read current Prefab editing stage state: whether a prefab is being edited, its path, and dirty status. This observes Prefab Stage only and does not open, save, close, or modify it.",
            DestructiveHint = false,
            Group = "prefab",
            Target = "editor",
            PlayModeSupport = "supported",
            Examples = new[] { new ToolExample { Description = "Read the current Prefab Stage", Input = new Request { } } }
        };

        /// <summary>
        /// 读取 Prefab Stage 当前状态；只读，不打开、保存、关闭或修改 Prefab。
    /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    IsOpen = false,
                    AssetPath = null,
                    PrefabRootName = null,
                    Mode = null,
                    IsDirty = false
                });
            }

            var type = prefabStage.GetType();
            var sceneValue = type.GetProperty("scene")?.GetValue(prefabStage);
            var root = type.GetProperty("prefabContentsRoot")?.GetValue(prefabStage);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                IsOpen = true,
                AssetPath = type.GetProperty("assetPath")?.GetValue(prefabStage)?.ToString(),
                PrefabRootName = root is GameObject gameObject ? gameObject.name : null,
                Mode = type.GetProperty("mode")?.GetValue(prefabStage)?.ToString(),
                IsDirty = sceneValue is UnityEngine.SceneManagement.Scene scene && scene.isDirty
            });
        }
    }
}
#endif
