#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>读取TimeManager批准的持久化字段。</summary>
    public sealed class ProjectTimeSettingsTool : IUnityCliTool<ProjectSettingsReadRequest, ProjectTimeSettingsSnapshot>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "project-settings.time-settings",
            Summary = "Read persisted project Time settings from the verified TimeManager surface.",
            Description = "Reads only fixedDeltaTime, maximumDeltaTime, and timeScale from TimeManager.asset. Values are project configuration rather than transient Play Mode runtime state, and the result includes the exact file SHA-256.",
            Group = "project-settings",
            DestructiveHint = false,
            SupportsDryRun = false,
            SideEffects = System.Array.Empty<string>(),
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[] { new ToolExample { Description = "Read persisted Time settings.", Input = new ProjectSettingsReadRequest() } }
        };

        /// <summary>读取TimeManager asset，不读取PlayMode瞬时Time值。</summary>
        public System.Threading.Tasks.ValueTask<ProjectTimeSettingsSnapshot> ExecuteAsync(ProjectSettingsReadRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<ProjectTimeSettingsSnapshot>(ProjectSettingsReader.ReadTime("project-settings.time-settings"));
        }
    }
}
#endif
