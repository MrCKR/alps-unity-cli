#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    public class BuildSettingsSetTool : IUnityCliTool<BuildSettingsSetTool.Request, BuildSettingsMutationResult>
    {
        public sealed class Request
        {
            public string ProductName { get; set; }
            public string BundleVersion { get; set; }
            public string CompanyId { get; set; }
            public string ApplicationIdentifier { get; set; }
            public string ScriptingBackend { get; set; }
            public string ApiCompatibilityLevel { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "build.settings.set", Summary = "Update persistent Unity PlayerSettings build configuration, with dry-run support.",
            Description = "Updates provided PlayerSettings fields: productName, bundleVersion, companyId, applicationIdentifier, scriptingBackend, and apiCompatibilityLevel. dryRun=true previews without writing. Available in Edit and Play mode.",
            Group = "build",DestructiveHint = true, SupportsDryRun = true,
            SideEffects = new[] { "player-settings-write" }, Target = "editor", PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Set product name", Input = new Request { ProductName = "My Game" }, Execution = new ToolExampleExecution { DryRun = true } } }
        };

        /// <summary>先解析全部枚举和空请求，再以单一 commit boundary 写入并读回 PlayerSettings。</summary>
        public System.Threading.Tasks.ValueTask<BuildSettingsMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var hasValue = !string.IsNullOrEmpty(request.ProductName)
                || !string.IsNullOrEmpty(request.BundleVersion)
                || !string.IsNullOrEmpty(request.CompanyId)
                || !string.IsNullOrEmpty(request.ApplicationIdentifier)
                || !string.IsNullOrEmpty(request.ScriptingBackend)
                || !string.IsNullOrEmpty(request.ApiCompatibilityLevel);
            if (!hasValue)
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SETTINGS_EMPTY", "build.settings.set requires at least one setting.", "Pass one or more documented PlayerSettings fields.");

            ScriptingImplementation scriptingBackend = default;
            if (!string.IsNullOrEmpty(request.ScriptingBackend) && !Enum.TryParse(request.ScriptingBackend, true, out scriptingBackend))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SETTINGS_ENUM_INVALID", "Unknown scriptingBackend: " + request.ScriptingBackend, "Pass a Unity ScriptingImplementation enum name.");
            ApiCompatibilityLevel compatibilityLevel = default;
            if (!string.IsNullOrEmpty(request.ApiCompatibilityLevel) && !Enum.TryParse(request.ApiCompatibilityLevel, true, out compatibilityLevel))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SETTINGS_ENUM_INVALID", "Unknown apiCompatibilityLevel: " + request.ApiCompatibilityLevel, "Pass a Unity ApiCompatibilityLevel enum name.");

            if (ctx.DryRun)
                return new(new BuildSettingsMutationResult { Changed = new List<string> { "productName", "bundleVersion", "companyId", "applicationIdentifier", "scriptingBackend", "apiCompatibilityLevel" }.FindAll(name => (name == "productName" && !string.IsNullOrEmpty(request.ProductName)) || (name == "bundleVersion" && !string.IsNullOrEmpty(request.BundleVersion)) || (name == "companyId" && !string.IsNullOrEmpty(request.CompanyId)) || (name == "applicationIdentifier" && !string.IsNullOrEmpty(request.ApplicationIdentifier)) || (name == "scriptingBackend" && !string.IsNullOrEmpty(request.ScriptingBackend)) || (name == "apiCompatibilityLevel" && !string.IsNullOrEmpty(request.ApiCompatibilityLevel))), DryRun = true });

            ctx.CommitEffect();
            var changed = new List<string>();
            if (!string.IsNullOrEmpty(request.ProductName)) { PlayerSettings.productName = request.ProductName; changed.Add("productName"); }
            if (!string.IsNullOrEmpty(request.BundleVersion)) { PlayerSettings.bundleVersion = request.BundleVersion; changed.Add("bundleVersion"); }
            if (!string.IsNullOrEmpty(request.CompanyId)) { PlayerSettings.companyName = request.CompanyId; changed.Add("companyId"); }
            if (!string.IsNullOrEmpty(request.ApplicationIdentifier)) { PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Unknown, request.ApplicationIdentifier); changed.Add("applicationIdentifier"); }
            if (!string.IsNullOrEmpty(request.ScriptingBackend)) { PlayerSettings.SetScriptingBackend(BuildTargetGroup.Unknown, scriptingBackend); changed.Add("scriptingBackend"); }
            if (!string.IsNullOrEmpty(request.ApiCompatibilityLevel)) { PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Unknown, compatibilityLevel); changed.Add("apiCompatibilityLevel"); }

            var result = BuildSettingsToolSupport.Read();
            var mismatch = (!string.IsNullOrEmpty(request.ProductName) && result.ProductName != request.ProductName)
                || (!string.IsNullOrEmpty(request.BundleVersion) && result.BundleVersion != request.BundleVersion)
                || (!string.IsNullOrEmpty(request.CompanyId) && result.CompanyId != request.CompanyId)
                || (!string.IsNullOrEmpty(request.ApplicationIdentifier) && result.ApplicationIdentifier != request.ApplicationIdentifier)
                || (!string.IsNullOrEmpty(request.ScriptingBackend) && !string.Equals(result.ScriptingBackend, scriptingBackend.ToString(), StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(request.ApiCompatibilityLevel) && !string.Equals(result.ApiCompatibilityLevel, compatibilityLevel.ToString(), StringComparison.OrdinalIgnoreCase));
            if (mismatch)
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_SETTINGS_STATE_MISMATCH", "PlayerSettings readback did not match every requested value.", "Inspect build.settings.get before retrying.");
            return new(new BuildSettingsMutationResult { Changed = changed });
        }
    }
}
#endif
