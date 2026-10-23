#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>将一个 scene prefab instance 的全部 non-default overrides 应用到 source asset。</summary>
    public sealed class PrefabOverridesApplyTool : IUnityCliTool<PrefabOverridesApplyTool.Request, PrefabOverrideResult>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Instance { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.overrides-apply",
            Summary = "Apply one scene prefab instance's non-default overrides to its source asset and prove both identities.",
            Description = "Binds the outermost instance root, source path/GUID and serialized source hash before one effect barrier. Success requires a changed source hash, a connected clean instance, and the same source identity.",
            Group = "prefab",
            DestructiveHint = true,
            SideEffects = new[] { "prefab-asset-write", "scene-modify", "undo" },
            SupportsDryRun = true,
            Permissions = new[] { "project-asset-write", "scene-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Apply overrides from one scene instance.", Input = new Request { Instance = "Environment/Crate" } },
                new ToolExample { Description = "Preview whether one instance has overrides to apply.", Input = new Request { Instance = "Environment/Crate" }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        public ValueTask<PrefabOverrideResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.overrides-apply");
            ct.ThrowIfCancellationRequested();
            var plan = PrefabOverrideToolSupport.PrepareOverride(request.Instance);
            if (ctx.DryRun) return new ValueTask<PrefabOverrideResult>(PrefabOverrideToolSupport.DryRunOverride(plan, "apply"));
            if (!plan.Before.HasOverrides) return new ValueTask<PrefabOverrideResult>(PrefabOverrideToolSupport.NoopOverride(plan, "apply"));
            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            PrefabOverrideToolSupport.ApplyOverrides(plan);
            return new ValueTask<PrefabOverrideResult>(PrefabOverrideToolSupport.ProveOverrides(plan, "apply"));
        }
    }
}
#endif
