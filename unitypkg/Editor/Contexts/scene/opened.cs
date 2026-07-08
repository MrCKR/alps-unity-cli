#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace UnityCli.Contexts.Scene
{
    /// <summary>
    /// scene.opened 内置 Context：读取当前 Editor 已加载场景清单。
    /// </summary>
    public class SceneOpenedContext : IUnityCliContext<SceneOpenedContext.Request, SceneOpenedContext.Result>
    {
        public sealed class Request { }

        public sealed class OpenedScene
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public long BuildIndex { get; set; }
            public bool IsLoaded { get; set; }
            public bool IsDirty { get; set; }
        }

        public sealed class Result
        {
            public List<OpenedScene> Scenes { get; set; }
            public long Count { get; set; }
            public string ActiveScene { get; set; }
            public string LogPath { get; set; }
        }

        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "scene.opened",
            Summary = "List currently loaded Editor scenes with active, loaded, dirty, and build index state.",
            Description = "List currently loaded scenes in the Editor with name, path, build index, loaded state, dirty state, and active scene. Read-only; does not load, unload, save, or switch scenes.",
            Category = "scene",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "live"
        };

        /// <summary>
        /// 读取 SceneManager 当前已加载场景；只观察状态，不加载、卸载、保存或切换 active scene。
        /// </summary>
        public Result Read(Request request, UnityCliProjectToolContext ctx)
        {
            var scenes = new List<OpenedScene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new OpenedScene
                {
                    Name = scene.name,
                    Path = SceneToolSupport.NormalizeAssetPath(scene.path),
                    BuildIndex = scene.buildIndex,
                    IsLoaded = scene.isLoaded,
                    IsDirty = scene.isDirty
                });
            }

            return new Result
            {
                Scenes = scenes,
                Count = SceneManager.sceneCount,
                ActiveScene = SceneManager.GetActiveScene().name,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
