#if UNITY_EDITOR
using System;
using UnityEditor;

namespace UnityCli
{
    public class BuildPlatformSetTool : IUnityCliTool<BuildPlatformSetTool.Request, BuildPlatformResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }

        public ToolDescriptor Describe() => new() { Name = "build.platform.set", Summary = "Switch the active Unity build target and return its stable readback, with dry-run support.", Description = "Switch the Editor active BuildTarget, absorb platform reload/import, and return only after EditorUserBuildSettings.activeBuildTarget equals the requested target in a stable generation. dryRun=true previews the switch.", Group = "build", Version = "1.0.0", SupportsDryRun = true, SideEffects = new[] { "build-target-switch" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Preview Windows 64 switch", Input = new Request { Target = "StandaloneWindows64" }, Execution = new ToolExampleExecution { DryRun = true } }, new ToolExample { Description = "Use Windows 64 target", Input = new Request { Target = "StandaloneWindows64" } } } };

        /// <summary>首次调用执行 switch；reload redelivery 只验证 active target，不重复切换。</summary>
        public async System.Threading.Tasks.ValueTask<BuildPlatformResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.Target))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_TARGET_REQUIRED", "build.platform.set requires target.", "Pass a BuildTarget such as StandaloneWindows64, Android, or iOS.");
            if (!Enum.TryParse(request.Target, true, out BuildTarget target))
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_TARGET_UNKNOWN", "Unknown BuildTarget: " + request.Target, "Pass a valid BuildTarget enum name.");
            if (ctx.DryRun) return new BuildPlatformResult { Target = request.Target, DryRun = true };
            var continuation = ctx.ProofOnly ? ctx.RequireProofContinuation("build-platform-v1") : ctx.ProofContinuation;
            object expected;
            if (continuation == null || !continuation.TryGetValue("expected", out expected) || !string.Equals(expected as string, request.Target, StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Build platform continuation does not match the requested target.", "Inspect the request journal before submitting a new request.");

            if (!ctx.ProofOnly)
            {
                BuildToolSupport.EnsureEditorNotBusy("build.platform.set");
                if (EditorUserBuildSettings.activeBuildTarget != target)
                {
                    ctx.CommitEffect();
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(target), target))
                        throw new UnityCliBridge.BridgeCommandException("E_BUILD_TARGET_SWITCH_FAILED", "Unity rejected the build target switch to " + target + ".", "Inspect Unity Console and installed platform modules.");
                }
            }
            var ready = await ctx.Wait.UntilEditorReady(TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct);
            var active = EditorUserBuildSettings.activeBuildTarget;
            if (!ready || active != target)
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_TARGET_PROOF_MISSING", "The active build target does not match the requested target after recovery.", "Inspect editor.state and installed platform support before submitting a new request.");
            return new BuildPlatformResult
            {
                Target = request.Target,
                Switched = true,
                ActiveTarget = active.ToString(),
                ActiveTargetGroup = BuildPipeline.GetBuildTargetGroup(active).ToString()
            };
        }
    }
}
#endif
