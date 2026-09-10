#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>修改AudioManager批准字段并证明精确文件持久化。</summary>
    public sealed class ProjectAudioSettingsSetTool : IUnityCliTool<ProjectAudioSettingsSetRequest, ProjectAudioSettingsSetResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "project-settings.audio-settings.set",
            Summary = "Set declared project Audio fields and prove persisted AudioManager readback.",
            Description = "Changes only provided volume, rolloffScale, or dopplerFactor fields. volume accepts 0..1 and the other fields accept non-negative finite Unity floats. Framework dry-run returns the exact planned fields without crossing the effect barrier.",
            Group = "project-settings",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "project-settings-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview an Audio volume change.", Input = new ProjectAudioSettingsSetRequest { Volume = 0.8 }, Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Apply Audio rolloff and doppler settings.", Input = new ProjectAudioSettingsSetRequest { RolloffScale = 1.25, DopplerFactor = 1.0 } }
            }
        };

        /// <summary>一次barrier后只保存AudioManager.asset并fresh读回。</summary>
        public System.Threading.Tasks.ValueTask<ProjectAudioSettingsSetResult> ExecuteAsync(ProjectAudioSettingsSetRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<ProjectAudioSettingsSetResult>(ProjectSettingsToolSupport.SetAudio(request, ctx));
        }
    }
}
#endif
