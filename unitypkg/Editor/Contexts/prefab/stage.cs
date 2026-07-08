#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli.Contexts.Prefab
{
    /// <summary>
    /// prefab.stage 内置 Context：读取当前 Prefab Stage 状态。
    /// </summary>
    public class PrefabStageContext : IUnityCliContext<PrefabStageContext.Request, PrefabStageContext.Result>
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

        // ── desc:只声明人工字段,source=built-in 表示进入内置 Context 表 ──
        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "prefab.stage",
            Description = "Read current Prefab editing stage state: whether a prefab is being edited, its path, and dirty status. This observes Prefab Stage only and does not open, save, close, or modify it.",
            Category = "prefab",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "live"
        };

        /// <summary>
        /// 读取 Prefab Stage 当前状态；只读，不打开、保存、关闭或修改 Prefab。
        /// </summary>
        public Result Read(Request request, UnityCliProjectToolContext ctx)
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null)
            {
                return new Result
                {
                    IsOpen = false,
                    AssetPath = null,
                    PrefabRootName = null,
                    Mode = null,
                    IsDirty = false
                };
            }

            var type = prefabStage.GetType();
            var sceneValue = type.GetProperty("scene")?.GetValue(prefabStage);
            var root = type.GetProperty("prefabContentsRoot")?.GetValue(prefabStage);
            return new Result
            {
                IsOpen = true,
                AssetPath = type.GetProperty("assetPath")?.GetValue(prefabStage)?.ToString(),
                PrefabRootName = root is GameObject gameObject ? gameObject.name : null,
                Mode = type.GetProperty("mode")?.GetValue(prefabStage)?.ToString(),
                IsDirty = sceneValue is UnityEngine.SceneManagement.Scene scene && scene.isDirty
            };
        }
    }
}
#endif
