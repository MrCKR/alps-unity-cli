#if UNITY_EDITOR && UNITY_6000_0_OR_NEWER
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public class BuildProfileCreateTool : IUnityCliTool<BuildProfileCreateTool.Request, BuildProfileMutationResult>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } public string Name { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "build.profile.create", Summary = "Create a persistent Unity Build Profile asset, with dry-run support.",
            Description = "Creates a Build Profile asset at path and optionally assigns its name. dryRun=true previews without writing. Available in Edit and Play mode when the Unity Build Profile API exists.",
            Group = "build",DestructiveHint = true, SupportsDryRun = true,
            SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Create Windows profile", Input = new Request { Path = "Assets/BuildProfiles/Windows.asset", Name = "Windows" }, Execution = new ToolExampleExecution { DryRun = true } } }
        };

        /// <summary>创建 Build Profile，并通过 AssetDatabase 重新加载证明路径、类型和名称终态。</summary>
        public System.Threading.Tasks.ValueTask<BuildProfileMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var type = BuildToolSupport.RequireBuildProfileType("build.profile.create");
            BuildToolSupport.RequireProfilePath(request.Path, "build.profile.create");
            if (AssetDatabase.LoadMainAssetAtPath(request.Path) != null)
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_ALREADY_EXISTS", "An asset already exists at " + request.Path + ".", "Pass a new Assets/*.asset path.");
            if (ctx.DryRun)
                return new(new BuildProfileMutationResult { Available = true, Path = request.Path, Name = request.Name, DryRun = true });

            ctx.CommitEffect();
            var profile = ScriptableObject.CreateInstance(type);
            if (!string.IsNullOrEmpty(request.Name)) profile.name = request.Name;
            AssetDatabase.CreateAsset(profile, request.Path);
            AssetDatabase.SaveAssets();

            var observed = AssetDatabase.LoadAssetAtPath(request.Path, type);
            if (observed == null || (!string.IsNullOrEmpty(request.Name) && observed.name != request.Name))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_CREATE_MISMATCH", "Build Profile readback did not match the requested path and name.", "Inspect build.profile.list and the target asset before retrying.");
            return new(new BuildProfileMutationResult { Available = true, Path = request.Path, Name = observed.name });
        }
    }
}
#endif
