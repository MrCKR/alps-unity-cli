#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// build.platform — 读取/设置构建平台 (BuildTarget)。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class BuildPlatformTool : IUnityCliTool<BuildPlatformTool.Request, BuildPlatformTool.Result>
    {
        public sealed class Request
        {
            public string Action { get; set; } = "get";
            public string Target { get; set; }
            public bool DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string Action { get; set; }
            public string ActiveTarget { get; set; }
            public string ActiveTargetGroup { get; set; }
            public string Target { get; set; }
            public bool Switched { get; set; }
            public bool DryRun { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "build.platform",
            Summary = "Read or switch the active Unity build target (BuildTarget).",
            Description = "Read or switch the active build target. action=get returns activeTarget and activeTargetGroup. action=set switches the build target (session-affecting, may take time). Supports dry-run for set.",
            Group = "build",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "build-target-switch" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Get active build target", Input = new Request { Action = "get" } },
                new ToolExample { Description = "Switch to Windows 64", Input = new Request { Action = "set", Target = "StandaloneWindows64" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var action = string.IsNullOrEmpty(request.Action) ? "get" : request.Action;
            if (action == "get")
            {
                return new Result
                {
                    Command = "build.platform",
                    Action = "get",
                    ActiveTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    ActiveTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString()
                };
            }
            if (action == "set")
            {
                var target = request.Target;
                if (string.IsNullOrEmpty(target))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_BUILD_TARGET_REQUIRED", "build.platform set requires target.", "Pass a BuildTarget like StandaloneWindows64, Android, iOS.");
                }
                BuildTarget bt;
                if (!System.Enum.TryParse(target, true, out bt))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_BUILD_TARGET_UNKNOWN", "Unknown BuildTarget: " + target, "Pass a valid BuildTarget enum name.");
                }
                if (request.DryRun)
                {
                    return new Result { Command = "build.platform", Action = "set", Target = target, DryRun = true };
                }
                var switched = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Unknown, bt);
                return new Result { Command = "build.platform", Action = "set", Target = target, Switched = switched };
            }
            throw new UnityCliBridge.BridgeCommandException("E_BUILD_PLATFORM_ACTION", "Unknown action: " + action, "Use get or set.");
        }
    }
}
#endif
