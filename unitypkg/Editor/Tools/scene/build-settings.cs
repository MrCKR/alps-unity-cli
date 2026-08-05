#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.build-settings — 列出 Editor Build Settings 中注册的场景(enabled 与路径)。
    /// </summary>
    public class SceneBuildSettingsTool : IUnityCliTool<SceneBuildSettingsTool.Request, SceneBuildSettingsTool.Result>
    {
        public sealed class Request
        {
            // scene.build-settings 无入参
        }

        public sealed class Result
        {
            public System.Collections.Generic.List<BuildSceneEntry> Scenes { get; set; }
            public long Count { get; set; }
        }

        public sealed class BuildSceneEntry
        {
            public string Path { get; set; }
            public bool Enabled { get; set; }
            public long BuildIndex { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.build-settings",
            Summary = "List scenes registered in the Editor Build Settings (enabled scenes and their paths).",
            Description = "List scenes registered in the Editor Build Settings (enabled scenes and their paths). Read-only; use build.scene.add or build.scene.remove to change the build list.",
            Group = "scene",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List build settings scenes", Input = new Request { } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
            var entries = new System.Collections.Generic.List<BuildSceneEntry>();
            for (var index = 0; index < scenes.Length; index++)
            {
                var scene = scenes[index];
                entries.Add(new BuildSceneEntry
                {
                    Path = SceneToolSupport.NormalizeAssetPath(scene.path),
                    Enabled = scene.enabled,
                    BuildIndex = (long)index
                });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scenes = entries,
                Count = (long)scenes.Length
            });
        }
    }
}
#endif
