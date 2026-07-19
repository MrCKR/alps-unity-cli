#if UNITY_EDITOR && UNITY_6000_0_OR_NEWER
using System;
using UnityEditor;

namespace UnityCli
{
    public class BuildProfileSwitchTool : IUnityCliTool<BuildProfileSwitchTool.Request, BuildProfileMutationResult>
    {
        public sealed class Request { [UnityCliRequired] public string Path { get; set; } }

        public ToolDescriptor Describe() => new() { Name = "build.profile.switch", Summary = "Set the active Unity Build Profile and return its stable readback, with dry-run support.", Description = "Select an existing Build Profile, absorb any Editor reload/import, and return only after activeBuildProfile resolves to the requested asset path. dryRun=true previews the selection. Available only where Unity exposes activeBuildProfile.", Group = "build", Version = "1.0.0", SupportsDryRun = true, SideEffects = new[] { "build-profile-switch" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Activate Windows profile", Input = new Request { Path = "Assets/BuildProfiles/Windows.asset" }, Execution = new ToolExampleExecution { DryRun = true } } } };

        /// <summary>reload redelivery 只读取 activeBuildProfile；不会再次设置 profile。</summary>
        public async System.Threading.Tasks.ValueTask<BuildProfileMutationResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var type = BuildToolSupport.RequireBuildProfileType("build.profile.switch");
            var profile = BuildToolSupport.LoadProfile(type, request.Path, "build.profile.switch");
            if (ctx.DryRun) return new BuildProfileMutationResult { Available = true, Path = request.Path, ActivePath = AssetDatabase.GetAssetPath(profile), DryRun = true };
            var continuation = ctx.ProofOnly ? ctx.RequireProofContinuation("build-profile-v1") : ctx.ProofContinuation;
            object expectedPath;
            if (continuation == null || !continuation.TryGetValue("expectedPath", out expectedPath) || !string.Equals(expectedPath as string, request.Path, StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Build Profile continuation does not match the requested path.", "Inspect the request journal before submitting a new request.");
            if (!ctx.ProofOnly)
            {
                ctx.CommitEffect();
                BuildToolSupport.SetActiveBuildProfile(type, profile);
            }
            var ready = await ctx.Wait.UntilEditorReady(TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct);
            var active = BuildToolSupport.ActiveBuildProfile(type);
            var activePath = active == null ? null : AssetDatabase.GetAssetPath(active);
            if (!ready || active != profile || !string.Equals(activePath, request.Path, StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_PROOF_MISSING", "The active Build Profile does not match the requested profile after recovery.", "Run build.profile.list and inspect Editor state before submitting a new request.");
            return new BuildProfileMutationResult { Available = true, Path = request.Path, ActivePath = activePath };
        }
    }
}
#endif
