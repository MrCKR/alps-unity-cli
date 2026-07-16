#if UNITY_EDITOR && UNITY_6000_0_OR_NEWER
using UnityEditor;
namespace UnityCli
{
    public class BuildProfileModifyTool : IUnityCliTool<BuildProfileModifyTool.Request, BuildProfileMutationResult>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } [UnityCliRequired] public string Name { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "build.profile.modify", Summary = "Rename a persistent Unity Build Profile asset, with dry-run support.", Description = "Updates the name of an existing Build Profile asset. Requires --yes because it writes the asset. dryRun=true previews without writing. Available in Edit and Play mode when the Unity Build Profile API exists.", Group = "build", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Rename profile", Input = new Request { Path = "Assets/BuildProfiles/Windows.asset", Name = "Windows Release" }, Execution = new ToolExampleExecution { DryRun = true } } } };
        public System.Threading.Tasks.ValueTask<BuildProfileMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { var type = BuildToolSupport.RequireBuildProfileType("build.profile.modify"); var profile = BuildToolSupport.LoadProfile(type, request.Path, "build.profile.modify"); if (string.IsNullOrEmpty(request.Name)) throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_NAME_REQUIRED", "build.profile.modify requires name.", "Pass the new profile name."); if (ctx.DryRun) return new(new BuildProfileMutationResult { Available = true, Path = request.Path, Name = request.Name, DryRun = true }); profile.name = request.Name; EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets(); return new(new BuildProfileMutationResult { Available = true, Path = request.Path, Name = profile.name }); }
    }
}
#endif
