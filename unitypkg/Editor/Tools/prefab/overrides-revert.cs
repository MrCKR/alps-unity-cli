#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>回退一个 scene prefab instance 的全部 non-default overrides，并保持 source asset 不变。</summary>
    public sealed class PrefabOverridesRevertTool : IUnityCliTool<PrefabOverridesRevertTool.Request, PrefabOverrideResult>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Instance { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.overrides-revert",
            Summary = "Revert one scene prefab instance's non-default overrides and prove a clean connected instance.",
            Description = "Binds the outermost instance root and source identity before one effect barrier. Success requires the same source hash and GUID, a connected instance, no remaining non-default overrides, and a dirty scene readback.",
            Group = "prefab",
            DestructiveHint = true,
            SideEffects = new[] { "scene-modify", "undo" },
            SupportsDryRun = true,
            Permissions = new[] { "scene-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Revert overrides on one scene instance.", Input = new Request { Instance = "Environment/Crate" } },
                new ToolExample { Description = "Preview whether one instance has overrides to revert.", Input = new Request { Instance = "Environment/Crate" }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        public ValueTask<PrefabOverrideResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.overrides-revert");
            ct.ThrowIfCancellationRequested();
            var plan = PrefabOverrideToolSupport.PrepareOverride(request.Instance);
            if (ctx.DryRun) return new ValueTask<PrefabOverrideResult>(PrefabOverrideToolSupport.DryRunOverride(plan, "revert"));
            if (!plan.Before.HasOverrides) return new ValueTask<PrefabOverrideResult>(PrefabOverrideToolSupport.NoopOverride(plan, "revert"));
            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            PrefabOverrideToolSupport.RevertOverrides(plan);
            return new ValueTask<PrefabOverrideResult>(PrefabOverrideToolSupport.ProveOverrides(plan, "revert"));
        }
    }
}
#endif
