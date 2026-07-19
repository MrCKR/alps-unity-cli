#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.set-active — 在已加载场景中设置激活场景。目标场景必须已加载。
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
            public List<ScenePlannedEffect> PlannedEffects { get; set; }
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

        // 切换后读取 Unity 当前活动场景，确认写入已生效。
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.set-active");
            var path = SceneToolSupport.ResolveScenePath(request.Path ?? request.Name, true);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(SceneToolSupport.NormalizeAssetPath(scene.path), path, System.StringComparison.OrdinalIgnoreCase))
                {
                    ctx.CommitEffect();
                    EditorSceneManager.SetActiveScene(scene);
                    // 写后读取激活场景，确认场景切换已由 Unity 接受。
                    var observedScene = SceneManager.GetActiveScene();
                    if (observedScene.path != scene.path || observedScene.handle != scene.handle)
                        throw new UnityCliBridge.BridgeCommandException("E_SCENE_ACTIVE_STATE_MISMATCH", "Unity did not set the requested active scene.", "Inspect the active scene and retry.");
                    return new System.Threading.Tasks.ValueTask<Result>(new Result
                    {
                        Scene = SceneToolSupport.BuildSceneInfo(observedScene),
                        RequestedPath = path,
                        PlannedEffects = new List<ScenePlannedEffect>
                        {
                            new ScenePlannedEffect { Type = "scene.set-active", Path = SceneToolSupport.NormalizeAssetPath(scene.path) ?? path }
                        }
                    });
                }
            }
            throw new UnityCliBridge.BridgeCommandException("E_SCENE_NOT_OPEN", "Scene is not currently open: " + path, "Run context read scene.opened, then scene.load if needed.");
        }
    }
}
#endif
