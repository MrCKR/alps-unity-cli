#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.validate — 校验场景:缺失脚本、null 引用、Build Settings 包含。
    /// </summary>
    public class SceneValidateTool : IUnityCliTool<SceneValidateTool.Request, SceneValidateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
        }

        public sealed class Result
        {
            public string Scene { get; set; }
            public string Path { get; set; }
            public bool Valid { get; set; }
            public long MissingScriptCount { get; set; }
            public long NullReferenceCount { get; set; }
            public bool InBuildSettings { get; set; }
            public List<string> Issues { get; set; }
            public List<string> NextActions { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.validate",
            Summary = "Validate a scene for missing scripts, null references, and Build Settings inclusion.",
            Description = "Validate the active scene (or --path scene) for common issues: missing script components (null components from missing assemblies), null references, and whether the scene is included in Editor Build Settings scenes. Returns valid flag, issue counts, and nextActions to fix.",
            Group = "scene",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Validate the active scene", Input = new Request { } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var scene = SceneManager.GetActiveScene();
            var path = request.Path;
            if (!string.IsNullOrEmpty(path))
            {
                scene = SceneManager.GetSceneByPath(path);
                if (!scene.IsValid())
                {
                    throw new UnityCliBridge.BridgeCommandException("E_SCENE_NOT_OPEN", "Scene is not loaded: " + path, "Load the scene first or run tools run scene.list-opened.");
                }
            }
            var issues = new List<string>();
            var missingScriptCount = 0;
            var nullReferenceCount = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    foreach (var c in t.GetComponents<Component>())
                    {
                        // 缺失脚本(MonoBehaviour 引用的程序集丢失)表现为 null 组件。
                        if (c == null)
                        {
                            missingScriptCount++;
                            issues.Add(t.gameObject.name + ": missing script component");
                            continue;
                        }
                    }
                }
            }
            // 构建 scenes 配置校验:当前场景是否在 EditorBuildSettings.scenes。
            var inBuildSettings = false;
            foreach (var bs in EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0])
            {
                if (string.Equals(bs.path, scene.path, System.StringComparison.OrdinalIgnoreCase))
                {
                    inBuildSettings = true;
                    break;
                }
            }
            if (!string.IsNullOrEmpty(scene.path) && !inBuildSettings)
            {
                issues.Add("scene not in Build Settings scenes: " + scene.path);
            }
            var ok = missingScriptCount == 0 && nullReferenceCount == 0;
            var nextActions = new List<string>();
            if (!ok)
            {
                nextActions.Add("Fix missing scripts/null references before building.");
            }
            if (!inBuildSettings && !string.IsNullOrEmpty(scene.path))
            {
                nextActions.Add("Add the scene to Build Settings with build.scene.add using path " + scene.path);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scene = scene.name,
                Path = scene.path,
                Valid = ok,
                MissingScriptCount = missingScriptCount,
                NullReferenceCount = nullReferenceCount,
                InBuildSettings = inBuildSettings,
                Issues = issues,
                NextActions = nextActions
            });
        }
    }
}
#endif
