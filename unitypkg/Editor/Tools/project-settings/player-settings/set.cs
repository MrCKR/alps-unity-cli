#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>修改active build target的PlayerSettings批准字段。</summary>
    public sealed class ProjectPlayerSettingsSetTool : IUnityCliTool<ProjectPlayerSettingsSetRequest, ProjectPlayerSettingsSetResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "project-settings.player-settings.set",
            Summary = "Set declared active-target PlayerSettings and return persisted typed readback.",
            Description = "Changes only provided companyName, productName, bundleVersion, scriptingBackend, or apiCompatibilityLevel. Enum names must exactly match values supported by the current Unity version. Backend or API changes may trigger a domain reload; after timeout callers must read project-settings.player-settings before deciding whether to retry.",
            Group = "project-settings",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "project-settings-write", "domain-reload" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 60000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview changing project identity fields.", Input = new ProjectPlayerSettingsSetRequest { CompanyName = "Example", ProductName = "Game", BundleVersion = "1.0.0" }, Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Set the bundle version.", Input = new ProjectPlayerSettingsSetRequest { BundleVersion = "1.0.1" } }
            }
        };

        /// <summary>写入前冻结active NamedBuildTarget，写入后从同一target fresh读回。</summary>
        public System.Threading.Tasks.ValueTask<ProjectPlayerSettingsSetResult> ExecuteAsync(ProjectPlayerSettingsSetRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<ProjectPlayerSettingsSetResult>(ProjectSettingsToolSupport.SetPlayer(request, ctx));
        }
    }
}
#endif
