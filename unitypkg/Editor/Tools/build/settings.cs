#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// build.settings — 读取/修改 PlayerSettings 关键构建配置。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class BuildSettingsTool : IUnityCliTool<BuildSettingsTool.Request, BuildSettingsTool.Result>
    {
        public sealed class Request
        {
            public string Action { get; set; } = "get";
            public string ProductName { get; set; }
            public string BundleVersion { get; set; }
            public string CompanyId { get; set; }
            public string ApplicationIdentifier { get; set; }
            public string ScriptingBackend { get; set; }
            public string ApiCompatibilityLevel { get; set; }
            public bool DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string Action { get; set; }
            public string ProductName { get; set; }
            public string BundleVersion { get; set; }
            public string CompanyId { get; set; }
            public string ApplicationIdentifier { get; set; }
            public string ScriptingBackend { get; set; }
            public string ApiCompatibilityLevel { get; set; }
            public List<string> Changed { get; set; }
            public bool DryRun { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "build.settings",
            Summary = "Read or update PlayerSettings build configuration (productName, bundleVersion, companyId, etc.).",
            Description = "Read or update PlayerSettings build configuration. action=get returns productName, bundleVersion, companyId, applicationIdentifier, scriptingBackend, apiCompatibilityLevel. action=set updates provided fields. Supports dry-run.",
            Group = "build",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "player-settings-write" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read build settings", Input = new Request { Action = "get" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var action = string.IsNullOrEmpty(request.Action) ? "get" : request.Action;
            if (action == "get")
            {
                return BuildResult();
            }
            if (action == "set")
            {
                if (request.DryRun)
                {
                    return new Result { Command = "build.settings", Action = "set", DryRun = true };
                }
                var changes = new List<string>();
                if (!string.IsNullOrEmpty(request.ProductName)) { PlayerSettings.productName = request.ProductName; changes.Add("productName"); }
                if (!string.IsNullOrEmpty(request.BundleVersion)) { PlayerSettings.bundleVersion = request.BundleVersion; changes.Add("bundleVersion"); }
                if (!string.IsNullOrEmpty(request.CompanyId)) { PlayerSettings.companyName = request.CompanyId; changes.Add("companyId"); }
                if (!string.IsNullOrEmpty(request.ApplicationIdentifier)) { PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Unknown, request.ApplicationIdentifier); changes.Add("applicationIdentifier"); }
                if (!string.IsNullOrEmpty(request.ScriptingBackend) && System.Enum.TryParse(request.ScriptingBackend, true, out UnityEditor.ScriptingImplementation sb)) { PlayerSettings.SetScriptingBackend(BuildTargetGroup.Unknown, sb); changes.Add("scriptingBackend"); }
                if (!string.IsNullOrEmpty(request.ApiCompatibilityLevel) && System.Enum.TryParse(request.ApiCompatibilityLevel, true, out ApiCompatibilityLevel api)) { PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Unknown, api); changes.Add("apiCompatibilityLevel"); }
                var result = BuildResult();
                result.Action = "set";
                result.Changed = changes;
                return result;
            }
            throw new UnityCliBridge.BridgeCommandException("E_BUILD_SETTINGS_ACTION", "Unknown action: " + action, "Use get or set.");
        }

        private static Result BuildResult()
        {
            return new Result
            {
                Command = "build.settings",
                ProductName = PlayerSettings.productName,
                BundleVersion = PlayerSettings.bundleVersion,
                CompanyId = PlayerSettings.companyName,
                ApplicationIdentifier = PlayerSettings.applicationIdentifier,
                ScriptingBackend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Unknown).ToString(),
                ApiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Unknown).ToString()
            };
        }
    }
}
#endif
