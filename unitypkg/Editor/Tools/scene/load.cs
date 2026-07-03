#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.load — 按 Assets/*.unity 路径加载场景(OpenSceneMode.Single)。
    /// 对齐旧 SceneLoad handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class SceneLoadTool : IUnityCliTool<SceneLoadTool.Request, SceneLoadTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Name { get; set; }
        }

        public sealed class Result
        {
            public SceneInfo Scene { get; set; }
            public string RequestedPath { get; set; }
            public List<PlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.load",
            Summary = "Load a scene by Assets/*.unity path or name.",
            Description = "Load a scene by Assets/*.unity path or name. In Editor this opens the scene; use scene.list-opened to confirm loaded scenes. Triggers import if the scene asset is new.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Load a scene by path", Input = new Request { Path = "Assets/Scenes/Main.unity" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.load");
            var path = SceneToolSupport.ResolveScenePath(request.Path ?? request.Name, true);
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return new Result
            {
                Scene = SceneToolSupport.BuildSceneInfo(scene),
                RequestedPath = path,
                PlannedEffects = new List<PlannedEffect>
                {
                    new PlannedEffect { Type = "scene.load", Path = SceneToolSupport.NormalizeAssetPath(scene.path) ?? path }
                }
            };
        }
    }
}
#endif
