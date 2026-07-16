#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.load — 按 Assets/*.unity 路径或唯一场景资产名加载场景(OpenSceneMode.Single)。
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
            public List<ScenePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.load",
            Summary = "Load or reload one scene; EditMode reload discards unsaved in-memory scene state.",
            Description = "Load a scene by exact Assets/*.unity path or unique scene asset filename. A name is matched case-insensitively without the .unity suffix and fails when missing or ambiguous. In EditMode this uses OpenSceneMode.Single, replacing the currently open scene state without saving scene assets. When a task started from a clean scene and must discard all temporary in-memory changes, call this Tool once with the exact active scene path after finishing verification and deleting temporary objects; this avoids traversing unrelated shared Undo history. Only call it when discarding every current unsaved scene change is intended. Use scene.list-opened to confirm loaded scenes.",
            Group = "scene",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Load a scene by path", Input = new Request { Path = "Assets/Scenes/Main.unity" } },
                new ToolExample { Description = "Load a uniquely named scene asset", Input = new Request { Name = "Main" } }
            }
        };

        /// <summary>解析 path/name 后以 Single 模式打开；name 必须在项目中唯一。</summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            SceneToolSupport.EnsureEditorNotBusy("scene.load");
            request = request ?? new Request();
            var path = SceneToolSupport.ResolveExistingScenePath(request.Path, request.Name);
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scene = SceneToolSupport.BuildSceneInfo(scene),
                RequestedPath = path,
                PlannedEffects = new List<ScenePlannedEffect>
                {
                    new ScenePlannedEffect { Type = "scene.load", Path = SceneToolSupport.NormalizeAssetPath(scene.path) ?? path }
                }
            });
        }
    }
}
#endif
