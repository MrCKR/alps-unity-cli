#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>读取AudioManager批准字段和磁盘身份。</summary>
    public sealed class ProjectSettingsAudioSettingsTool : IUnityCliTool<ProjectSettingsReadRequest, ProjectAudioSettingsSnapshot>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "project-settings.audio-settings",
            Summary = "Read bounded project Audio settings with exact settings-file identity.",
            Description = "Reads only volume, rolloffScale, and dopplerFactor from the verified AudioManager surface for the current Unity version. The result includes the exact ProjectSettings file SHA-256 and performs no write, import, or reload.",
            Group = "project-settings",
            DestructiveHint = false,
            SupportsDryRun = false,
            SideEffects = System.Array.Empty<string>(),
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[] { new ToolExample { Description = "Read current project Audio settings.", Input = new ProjectSettingsReadRequest() } }
        };

        /// <summary>返回当前Unity版本已验证的AudioManager字段。</summary>
        public System.Threading.Tasks.ValueTask<ProjectAudioSettingsSnapshot> ExecuteAsync(ProjectSettingsReadRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<ProjectAudioSettingsSnapshot>(ProjectSettingsReader.ReadAudio("project-settings.audio-settings"));
        }
    }
}
#endif
