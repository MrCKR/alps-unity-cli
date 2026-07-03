#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.set-active — 在已加载场景中设置激活场景。目标场景必须已加载。
    /// 对齐旧 SceneSetActive handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class SceneSetActiveTool : IUnityCliTool<SceneSetActiveTool.Request, SceneSetActiveTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
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
            Name = "scene.set-active",
            Summary = "Set the active scene among currently loaded scenes.",
            Description = "Set the active scene among currently loaded scenes. The target scene must already be loaded (use scene.load first). Affects which scene new GameObjects are created in.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "editor-session" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set the active scene", Input = new Request { Path = "Assets/Scenes/Main.unity" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.set-active");
            var path = SceneToolSupport.ResolveScenePath(request.Path ?? request.Name, true);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(SceneToolSupport.NormalizeAssetPath(scene.path), path, System.StringComparison.OrdinalIgnoreCase))
                {
                    EditorSceneManager.SetActiveScene(scene);
                    return new Result
                    {
                        Scene = SceneToolSupport.BuildSceneInfo(scene),
                        RequestedPath = path,
                        PlannedEffects = new List<PlannedEffect>
                        {
                            new PlannedEffect { Type = "scene.set-active", Path = SceneToolSupport.NormalizeAssetPath(scene.path) ?? path }
                        }
                    };
                }
            }
            throw new UnityCliBridge.BridgeCommandException("E_SCENE_NOT_OPEN", "Scene is not currently open: " + path, "Run context read scene.opened, then scene.load if needed.");
        }
    }
}
#endif
