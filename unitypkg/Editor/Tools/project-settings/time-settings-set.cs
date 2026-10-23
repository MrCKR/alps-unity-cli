#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>修改TimeManager批准字段并证明持久化。</summary>
    public sealed class ProjectSettingsTimeSettingsSetTool : IUnityCliTool<ProjectTimeSettingsSetRequest, ProjectTimeSettingsSetResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "project-settings.time-settings-set",
            Summary = "Set declared project Time fields and prove persisted TimeManager readback.",
            Description = "Changes only provided fixedDeltaTime, maximumDeltaTime, or timeScale. Delta values must be positive, maximumDeltaTime must not be below the effective fixedDeltaTime, and timeScale accepts 0..100. Framework dry-run returns the exact planned fields without writing TimeManager.asset.",
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
                new ToolExample { Description = "Preview changing the fixed timestep.", Input = new ProjectTimeSettingsSetRequest { FixedDeltaTime = 0.016666667 }, Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Set maximum timestep and time scale.", Input = new ProjectTimeSettingsSetRequest { MaximumDeltaTime = 0.25, TimeScale = 1.0 } }
            }
        };

        /// <summary>一次barrier后只保存TimeManager.asset并验证float终态。</summary>
        public System.Threading.Tasks.ValueTask<ProjectTimeSettingsSetResult> ExecuteAsync(ProjectTimeSettingsSetRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<ProjectTimeSettingsSetResult>(ProjectSettingsToolSupport.SetTime(request, ctx));
        }
    }
}
#endif
