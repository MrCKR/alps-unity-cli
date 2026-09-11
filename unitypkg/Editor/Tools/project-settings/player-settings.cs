#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>读取active build target的PlayerSettings批准字段。</summary>
    public sealed class ProjectSettingsPlayerSettingsTool : IUnityCliTool<ProjectSettingsReadRequest, ProjectPlayerSettingsSnapshot>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "project-settings.player-settings",
            Summary = "Read bounded PlayerSettings for the active named build target.",
            Description = "Returns companyName, productName, bundleVersion, scriptingBackend, and apiCompatibilityLevel for the active build target plus the verified ProjectSettings file identity. It does not change build target or project state.",
            Group = "project-settings",
            DestructiveHint = false,
            SupportsDryRun = false,
            SideEffects = System.Array.Empty<string>(),
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[] { new ToolExample { Description = "Read active-target PlayerSettings.", Input = new ProjectSettingsReadRequest() } }
        };

        /// <summary>通过NamedBuildTarget读取当前PlayerSettings。</summary>
        public System.Threading.Tasks.ValueTask<ProjectPlayerSettingsSnapshot> ExecuteAsync(ProjectSettingsReadRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<ProjectPlayerSettingsSnapshot>(ProjectSettingsReader.ReadPlayer("project-settings.player-settings"));
        }
    }
}
#endif
