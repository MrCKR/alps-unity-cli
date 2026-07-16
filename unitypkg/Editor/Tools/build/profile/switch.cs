#if UNITY_EDITOR && UNITY_6000_0_OR_NEWER
namespace UnityCli
{
    public class BuildProfileSwitchTool : IUnityCliTool<BuildProfileSwitchTool.Request, BuildProfileMutationResult>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "build.profile.switch", Summary = "Set the active Unity Build Profile, with dry-run support.", Description = "Selects an existing Build Profile as active. This changes Editor session state and may affect subsequent builds. dryRun=true previews the selection. Available in Edit and Play mode only where Unity exposes activeBuildProfile.", Group = "build", Version = "1.0.0", SupportsDryRun = true, SideEffects = new[] { "build-profile-switch" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Activate Windows profile", Input = new Request { Path = "Assets/BuildProfiles/Windows.asset" }, Execution = new ToolExampleExecution { DryRun = true } } } };
        public System.Threading.Tasks.ValueTask<BuildProfileMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { var type = BuildToolSupport.RequireBuildProfileType("build.profile.switch"); var profile = BuildToolSupport.LoadProfile(type, request.Path, "build.profile.switch"); if (ctx.DryRun) return new(new BuildProfileMutationResult { Available = true, Path = request.Path, ActivePath = request.Path, DryRun = true }); BuildToolSupport.SetActiveBuildProfile(type, profile); return new(new BuildProfileMutationResult { Available = true, Path = request.Path, ActivePath = request.Path }); }
    }
}
#endif
