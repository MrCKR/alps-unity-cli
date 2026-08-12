#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>分页读取legacy Input Manager批准字段。</summary>
    public sealed class ProjectInputSettingsTool : IUnityCliTool<ProjectInputSettingsRequest, ProjectInputSettingsResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "project.input-settings",
            Summary = "Read a bounded page of legacy Input Manager axis settings.",
            Description = "Returns only axis name, index, sensitivity, gravity, and dead with total/matched counts and pagination. axis is an optional exact case-sensitive name filter; offset defaults to 0 and limit defaults to 50 with a maximum of 200. InputManager scanning fails closed above 4096 entries.",
            Group = "project",
            DestructiveHint = false,
            SupportsDryRun = false,
            SideEffects = System.Array.Empty<string>(),
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the first bounded axis page.", Input = new ProjectInputSettingsRequest() },
                new ToolExample { Description = "Read exact Horizontal axis entries.", Input = new ProjectInputSettingsRequest { Axis = "Horizontal", Limit = 20 } }
            }
        };

        /// <summary>读取有界axis集合，不触发asset写入或import。</summary>
        public System.Threading.Tasks.ValueTask<ProjectInputSettingsResult> ExecuteAsync(ProjectInputSettingsRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new System.Threading.Tasks.ValueTask<ProjectInputSettingsResult>(ProjectSettingsReader.ReadInput(request, "project.input-settings"));
        }
    }
}
#endif
