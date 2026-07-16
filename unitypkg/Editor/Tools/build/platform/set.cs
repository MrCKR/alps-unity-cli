#if UNITY_EDITOR
using UnityEditor;
namespace UnityCli
{
    public class BuildPlatformSetTool : IUnityCliTool<BuildPlatformSetTool.Request, BuildPlatformResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "build.platform.set", Summary = "Switch the active Unity build target, with dry-run support.", Description = "Switches the Editor active BuildTarget and can trigger a lengthy platform reload. dryRun=true previews the switch. This changes Editor session state but does not write project settings; it is available in Edit and Play mode.", Group = "build", Version = "1.0.0", SupportsDryRun = true, SideEffects = new[] { "build-target-switch" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Preview Windows 64 switch", Input = new Request { Target = "StandaloneWindows64" }, Execution = new ToolExampleExecution { DryRun = true } }, new ToolExample { Description = "Use Windows 64 target", Input = new Request { Target = "StandaloneWindows64" } } } };
        public System.Threading.Tasks.ValueTask<BuildPlatformResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { BuildToolSupport.EnsureEditorNotBusy("build.platform.set"); if (string.IsNullOrEmpty(request.Target)) throw new UnityCliBridge.BridgeCommandException("E_BUILD_TARGET_REQUIRED", "build.platform.set requires target.", "Pass a BuildTarget such as StandaloneWindows64, Android, or iOS."); if (!System.Enum.TryParse(request.Target, true, out BuildTarget target)) throw new UnityCliBridge.BridgeCommandException("E_BUILD_TARGET_UNKNOWN", "Unknown BuildTarget: " + request.Target, "Pass a valid BuildTarget enum name."); return new(ctx.DryRun ? new BuildPlatformResult { Target = request.Target, DryRun = true } : new BuildPlatformResult { Target = request.Target, Switched = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Unknown, target) }); }
    }
}
#endif
