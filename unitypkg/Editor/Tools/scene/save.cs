#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.save — 保存激活场景到磁盘。未保存场景需 --path。destructive,支持 dryRun。
    /// </summary>
    public class SceneSaveTool : IUnityCliTool<SceneSaveTool.Request, SceneSaveTool.Result>
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
            public bool? DryRun { get; set; }
            public List<ScenePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.save",
            Summary = "Save the active scene to disk.",
            Description = "Save the active scene to disk. Requires --path for unsaved scenes. Destructive: writes the .unity asset. Supports --dry-run to preview.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-write", "scene-modify" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Save the active scene", Input = new Request { } },
                new ToolExample { Description = "Preview saving to a path", Input = new Request { Path = "Assets/Scenes/Main.unity" }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.save");
            var scene = SceneManager.GetActiveScene();
            var path = SceneToolSupport.ResolveScenePath(request.Path ?? request.Name, false);
            if (string.IsNullOrEmpty(path))
            {
                path = SceneToolSupport.NormalizeAssetPath(scene.path);
            }
            if (string.IsNullOrEmpty(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_PATH_REQUIRED", "Scene save requires --path when the active scene has not been saved yet.", "Pass tools run scene.save --input '{\"path\":\"Assets/Scenes/Main.unity\"}'.");
            }
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Scene = SceneToolSupport.BuildSceneInfo(scene),
                    RequestedPath = path,
                    DryRun = true,
                    PlannedEffects = new List<ScenePlannedEffect>
                    {
                        new ScenePlannedEffect { Type = "scene.save", Path = path, SideEffects = "none" }
                    }
                });
            }
            ctx.CommitEffect();
            SceneToolSupport.CreateAssetDirectory(path);
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_SAVE_FAILED", "Failed to save scene: " + path, "Check Unity Console diagnostics and file permissions.");
            }
            var observedScene = SceneManager.GetActiveScene();
            if (!observedScene.isLoaded || observedScene.path != path || observedScene.isDirty)
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_SAVE_STATE_MISMATCH", "Saved scene readback did not match the requested path or remains dirty.", "Inspect the active scene and disk permissions before retrying.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scene = SceneToolSupport.BuildSceneInfo(observedScene),
                RequestedPath = path,
                PlannedEffects = new List<ScenePlannedEffect>
                {
                    new ScenePlannedEffect { Type = "scene.save", Path = path }
                }
            });
        }
    }
}
#endif
