#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// build.scenes — 读取/修改 Build Settings scenes 列表。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class BuildScenesTool : IUnityCliTool<BuildScenesTool.Request, BuildScenesTool.Result>
    {
        public sealed class Request
        {
            public string Action { get; set; } = "get";
            public string Path { get; set; }
            public bool DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string Action { get; set; }
            public long Count { get; set; }
            public List<BuildSceneEntry> Scenes { get; set; }
            public string Path { get; set; }
        }

        public sealed class BuildSceneEntry
        {
            public string Path { get; set; }
            public bool Enabled { get; set; }
            public long BuildIndex { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "build.scenes",
            Summary = "Read or modify the EditorBuildSettings scenes list (get, add, remove, enable, disable).",
            Description = "Read or modify the Build Settings scenes list. action=get returns the current scenes with path/enabled/buildIndex. action=add/remove/enable/disable mutates the list. Supports dry-run for mutation actions.",
            Group = "build",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List build scenes", Input = new Request { Action = "get" } },
                new ToolExample { Description = "Add a scene to build", Input = new Request { Action = "add", Path = "Assets/Scenes/Main.unity" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var action = string.IsNullOrEmpty(request.Action) ? "get" : request.Action;
            if (action == "get")
            {
                var scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
                var entries = new List<BuildSceneEntry>();
                for (var i = 0; i < scenes.Length; i++)
                {
                    entries.Add(new BuildSceneEntry
                    {
                        Path = BuildToolSupport.NormalizeAssetPath(scenes[i].path),
                        Enabled = scenes[i].enabled,
                        BuildIndex = i
                    });
                }
                return new Result { Command = "build.scenes", Action = "get", Count = scenes.Length, Scenes = entries };
            }

            if (action == "add" || action == "remove" || action == "enable" || action == "disable")
            {
                if (request.DryRun)
                {
                    return new Result { Command = "build.scenes", Action = action, Path = request.Path };
                }
                var path = request.Path;
                var list = (EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0]).ToList();
                if (action == "add" && !string.IsNullOrEmpty(path))
                {
                    if (!list.Any(s => s.path == path)) list.Add(new EditorBuildSettingsScene(path, true));
                }
                else if (action == "remove" && !string.IsNullOrEmpty(path))
                {
                    list.RemoveAll(s => s.path == path);
                }
                else if ((action == "enable" || action == "disable") && !string.IsNullOrEmpty(path))
                {
                    var idx = list.FindIndex(s => s.path == path);
                    if (idx >= 0) list[idx] = new EditorBuildSettingsScene(path, action == "enable");
                }
                EditorBuildSettings.scenes = list.ToArray();
                return new Result { Command = "build.scenes", Action = action, Path = path, Count = list.Count };
            }

            throw new UnityCliBridge.BridgeCommandException("E_BUILD_SCENES_ACTION", "Unknown action: " + action, "Use get, add, remove, enable, disable.");
        }
    }
}
#endif
