#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>预检并写入一个 asset importer，reimport 后以 fresh importer/GUID/member 值证明终态。</summary>
    public sealed class AssetImportSettingsSetTool : IUnityCliTool<AssetImportSettingsSetTool.Request, AssetImportSettingsSetTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }

            [UnityCliRequired, UnityCliAnyJson]
            public Dictionary<string, object> Settings { get; set; }

            [UnityCliStringEnum("Default", "Standalone", "iOS", "Android", "WebGL", "tvOS")]
            public string Platform { get; set; }
        }

        public sealed class Result
        {
            public AssetImportSettingsSnapshot ImportSettings { get; set; }
            public bool Changed { get; set; }
            public bool DryRun { get; set; }
            public List<AssetImportSettingChange> Changes { get; set; }
            public List<AssetPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.import-settings-set",
            Summary = "Set validated default or platform importer settings and prove them after synchronous reimport.",
            Description = "Prevalidates every supplied setting before one effect barrier. Default targets public importer members and Audio sample settings; platform overrides support TextureImporter and AudioImporter. Unknown, conflicting, reload-risk, or unconvertible settings fail without partial writes.",
            Group = "asset",
            DestructiveHint = true,
            SideEffects = new[] { "asset-meta-write", "asset-import" },
            SupportsDryRun = true,
            Permissions = new[] { "project-asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Make one texture readable and enable mipmaps.",
                    Input = new Request
                    {
                        Path = "Assets/Textures/Icon.png",
                        Platform = "Default",
                        Settings = new Dictionary<string, object> { { "isReadable", true }, { "mipmapEnabled", true } }
                    }
                },
                new ToolExample
                {
                    Description = "Preview an Android texture override without writing importer state.",
                    Input = new Request
                    {
                        Path = "Assets/Textures/Icon.png",
                        Platform = "Android",
                        Settings = new Dictionary<string, object> { { "maxTextureSize", 1024L }, { "overridden", true } }
                    },
                    Execution = new ToolExampleExecution { DryRun = true }
                }
            }
        };

        /// <summary>先冻结全部 member/value，再按 dry-run/no-op/effect 三条路径返回统一 typed result。</summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            AssetToolSupport.EnsureEditorNotBusy("asset.import-settings-set");
            ct.ThrowIfCancellationRequested();
            var plan = AssetImportSettingsToolSupport.Prepare(request.Path, request.Platform, request.Settings);
            var plannedEffects = plan.Changed
                ? new List<AssetPlannedEffect>
                {
                    new AssetPlannedEffect
                    {
                        Type = "asset.import-settings-set",
                        Path = plan.Path,
                        SideEffects = "asset-meta-write,asset-import"
                    }
                }
                : new List<AssetPlannedEffect>();
            if (ctx.DryRun)
            {
                return new ValueTask<Result>(new Result
                {
                    ImportSettings = AssetImportSettingsToolSupport.Read(plan.Path, plan.Platform),
                    Changed = plan.Changed,
                    DryRun = true,
                    Changes = plan.Changes,
                    PlannedEffects = plannedEffects
                });
            }
            if (!plan.Changed)
            {
                return new ValueTask<Result>(new Result
                {
                    ImportSettings = AssetImportSettingsToolSupport.Read(plan.Path, plan.Platform),
                    Changed = false,
                    DryRun = false,
                    Changes = plan.Changes,
                    PlannedEffects = plannedEffects
                });
            }

            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            AssetImportSettingsToolSupport.Apply(plan);
            var readback = AssetImportSettingsToolSupport.Prove(plan);
            return new ValueTask<Result>(new Result
            {
                ImportSettings = readback,
                Changed = true,
                DryRun = false,
                Changes = plan.Changes,
                PlannedEffects = plannedEffects
            });
        }
    }
}
#endif
