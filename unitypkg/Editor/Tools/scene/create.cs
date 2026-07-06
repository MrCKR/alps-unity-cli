#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.create — 在 Assets/*.unity 路径创建新空场景并可加载。拒绝覆盖已存在场景资产。
    /// 对齐旧 SceneCreate handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "asset-write" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a new scene", Input = new Request { Path = "Assets/Scenes/Main.unity" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.create");
            var path = SceneToolSupport.ResolveScenePath(request.Path ?? request.Name, false);
            if (!string.IsNullOrEmpty(path) && File.Exists(SceneToolSupport.AssetFullPath(path)))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_OUTPUT_EXISTS", "Scene already exists: " + path, "Choose a different Assets/*.unity path or run scene load/save.");
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!string.IsNullOrEmpty(path))
            {
                SceneToolSupport.CreateAssetDirectory(path);
                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_SCENE_SAVE_FAILED", "Failed to save new scene: " + path, "Check destination path and Unity Console diagnostics.");
                }
            }
            return new Result
            {
                Scene = SceneToolSupport.BuildSceneInfo(SceneManager.GetActiveScene()),
                RequestedPath = path,
                PlannedEffects = new List<ScenePlannedEffect>
                {
                    new ScenePlannedEffect { Type = "scene.create", Path = path }
                }
            };
        }
    }
}
#endif
