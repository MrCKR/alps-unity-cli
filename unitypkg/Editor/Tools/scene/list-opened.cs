#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.list-opened — 列出当前已加载(打开)场景:name/path/buildIndex/loaded/dirty/isActive。
    /// 只读工具,对齐旧 SceneListOpened handler。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class SceneListOpenedTool : IUnityCliTool<SceneListOpenedTool.Request, SceneListOpenedTool.Result>
    {
        public sealed class Request
        {
            // scene.list-opened 无入参
        }

        public sealed class Result
        {
            public List<OpenedScene> Scenes { get; set; }
            public long Count { get; set; }
            public string ActiveScene { get; set; }
        }

        public sealed class OpenedScene
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public long BuildIndex { get; set; }
            public bool IsLoaded { get; set; }
            public bool IsDirty { get; set; }
            public bool IsActive { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.list-opened",
            Summary = "List currently loaded (opened) scenes with name, path, build index, loaded/dirty state and active flag.",
            Description = "List currently loaded (opened) scenes with name, path, build index, loaded/dirty state and active flag. Read-only.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List opened scenes", Input = new Request { } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var scenes = new List<OpenedScene>();
            var active = SceneManager.GetActiveScene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new OpenedScene
                {
                    Name = scene.name,
                    Path = SceneToolSupport.NormalizeAssetPath(scene.path),
                    BuildIndex = scene.buildIndex,
                    IsLoaded = scene.isLoaded,
                    IsDirty = scene.isDirty,
                    IsActive = scene == active
                });
            }
            return new Result
            {
                Scenes = scenes,
                Count = (long)SceneManager.sceneCount,
                ActiveScene = active.name
            };
        }
    }
}
#endif
