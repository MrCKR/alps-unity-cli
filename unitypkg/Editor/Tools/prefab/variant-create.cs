#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>从 base prefab 在 preview scene 中创建新 variant asset，并证明 base/output identity。</summary>
    public sealed class PrefabVariantCreateTool : IUnityCliTool<PrefabVariantCreateTool.Request, PrefabVariantCreateResult>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Base { get; set; }

            [UnityCliRequired]
            public string Path { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.variant-create",
            Summary = "Create a prefab variant in an isolated preview scene and prove its base path, GUID, and Variant asset type.",
            Description = "Requires an existing base prefab, a new Assets/*.prefab output, and an existing parent folder. The active scene is not touched. Success requires the output GUID to resolve to a Variant whose source is the unchanged base prefab.",
            Group = "prefab",
            DestructiveHint = true,
            SideEffects = new[] { "prefab-asset-create", "asset-write" },
            SupportsDryRun = true,
            Permissions = new[] { "project-asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create one variant from a base prefab.", Input = new Request { Base = "Assets/Prefabs/Base.prefab", Path = "Assets/Prefabs/BaseVariant.prefab" } },
                new ToolExample { Description = "Preview one variant output without creating an asset.", Input = new Request { Base = "Assets/Prefabs/Base.prefab", Path = "Assets/Prefabs/BaseVariant.prefab" }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        public ValueTask<PrefabVariantCreateResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.variant-create");
            ct.ThrowIfCancellationRequested();
            var plan = PrefabOverrideToolSupport.PrepareVariant(request.Base, request.Path);
            if (ctx.DryRun) return new ValueTask<PrefabVariantCreateResult>(PrefabOverrideToolSupport.DryRunVariant(plan));
            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            PrefabOverrideToolSupport.CreateVariant(plan);
            return new ValueTask<PrefabVariantCreateResult>(PrefabOverrideToolSupport.ProveVariant(plan));
        }
    }
}
#endif
