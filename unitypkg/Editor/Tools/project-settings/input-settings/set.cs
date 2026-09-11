#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>按唯一axis名称修改legacy Input Manager批准字段。</summary>
    public sealed class ProjectSettingsInputSettingsSetTool : IUnityCliTool<ProjectInputSettingsSetRequest, ProjectInputSettingsSetResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "project-settings.input-settings.set",
            Summary = "Set declared fields on one exact legacy Input Manager axis and prove persistence.",
            Description = "Requires one exact case-sensitive axis name and changes only provided sensitivity, gravity, or dead. If the legacy Input Manager contains duplicate names, the first configured entry is selected and its index/name identity is frozen before the effect. InputManager scanning fails closed above 4096 entries. Framework dry-run returns the exact planned fields without writing InputManager.asset.",
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
                new ToolExample { Description = "Preview a Horizontal sensitivity change.", Input = new ProjectInputSettingsSetRequest { Axis = "Horizontal", Sensitivity = 3.0 }, Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Apply gravity and dead values to one exact axis.", Input = new ProjectInputSettingsSetRequest { Axis = "Horizontal", Gravity = 3.0, Dead = 0.001 } }
            }
        };

        /// <summary>冻结唯一axis index/name，单次barrier写入并按同一identity读回。</summary>
        public System.Threading.Tasks.ValueTask<ProjectInputSettingsSetResult> ExecuteAsync(ProjectInputSettingsSetRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<ProjectInputSettingsSetResult>(ProjectSettingsToolSupport.SetInput(request, ctx));
        }
    }
}
#endif
