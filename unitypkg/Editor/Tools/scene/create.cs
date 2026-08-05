#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.create — 在 Assets/*.unity 路径创建新空场景并可加载。拒绝覆盖已存在场景资产。
    /// </summary>
    public class SceneCreateTool : IUnityCliTool<SceneCreateTool.Request, SceneCreateTool.Result>
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
            public List<ScenePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.create",
            Summary = "Create a new empty scene at an Assets/*.unity path and optionally load it.",
            Description = "Create a new empty scene at an Assets/*.unity path and optionally load it. Refuses to overwrite an existing scene asset.",
            Group = "scene",
            DestructiveHint = true,
            SideEffects = new[] { "scene-modify", "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a new scene", Input = new Request { Path = "Assets/Scenes/Main.unity" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.create");
            var path = SceneToolSupport.ResolveScenePath(request.Path ?? request.Name, false);
            if (!string.IsNullOrEmpty(path) && File.Exists(SceneToolSupport.AssetFullPath(path)))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_OUTPUT_EXISTS", "Scene already exists: " + path, "Choose a different Assets/*.unity path or run scene load/save.");
            }
            ctx.CommitEffect();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!string.IsNullOrEmpty(path))
            {
                SceneToolSupport.CreateAssetDirectory(path);
                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_SCENE_SAVE_FAILED", "Failed to save new scene: " + path, "Check destination path and Unity Console diagnostics.");
                }
            }
            var observedScene = SceneManager.GetActiveScene();
            if (!observedScene.IsValid() || !observedScene.isLoaded || (!string.IsNullOrEmpty(path) && observedScene.path != path))
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_CREATE_STATE_MISMATCH", "Created scene readback did not match the requested path.", "Inspect the active scene and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scene = SceneToolSupport.BuildSceneInfo(observedScene),
                RequestedPath = path,
                PlannedEffects = new List<ScenePlannedEffect>
                {
                    new ScenePlannedEffect { Type = "scene.create", Path = path }
                }
            });
        }
    }
}
#endif
