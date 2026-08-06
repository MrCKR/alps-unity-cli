#if UNITY_EDITOR
using UnityEditor;

namespace UnityCli
{
    public class BuildProfileModifyTool : IUnityCliTool<BuildProfileModifyTool.Request, BuildProfileMutationResult>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } [UnityCliRequired] public string Name { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "build.profile.modify", Summary = "Rename a persistent Unity Build Profile asset, with dry-run support.",
            Description = "Updates the name of an existing Build Profile asset. dryRun=true previews without writing. Available in Edit and Play mode when the Unity Build Profile API exists.",
            Group = "build",DestructiveHint = true, SupportsDryRun = true,
            SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Rename profile", Input = new Request { Path = "Assets/BuildProfiles/Windows.asset", Name = "Windows Release" }, Execution = new ToolExampleExecution { DryRun = true } } }
        };

        /// <summary>修改 Build Profile 名称，并重新加载资产验证持久化终态。</summary>
        public System.Threading.Tasks.ValueTask<BuildProfileMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var type = BuildToolSupport.RequireBuildProfileType("build.profile.modify");
            var profile = BuildToolSupport.LoadProfile(type, request.Path, "build.profile.modify");
            if (string.IsNullOrEmpty(request.Name))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_NAME_REQUIRED", "build.profile.modify requires name.", "Pass the new profile name.");
            if (ctx.DryRun)
                return new(new BuildProfileMutationResult { Available = true, Path = request.Path, Name = request.Name, DryRun = true });

            ctx.CommitEffect();
            profile.name = request.Name;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var observed = AssetDatabase.LoadAssetAtPath(request.Path, type);
            if (observed == null || observed.name != request.Name)
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_MODIFY_MISMATCH", "Build Profile name readback did not match the requested value.", "Inspect build.profile.list and the target asset before retrying.");
            return new(new BuildProfileMutationResult { Available = true, Path = request.Path, Name = observed.name });
        }
    }
}
#endif
