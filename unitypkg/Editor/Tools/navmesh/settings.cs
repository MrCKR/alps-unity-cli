#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>读取 default agent 的 legacy NavMesh bake settings 与持久化文件身份。</summary>
    public sealed class NavMeshSettingsTool : IUnityCliTool<NavMeshSettingsTool.Request, NavMeshSettingsTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public NavMeshSettingsData Settings { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "navmesh.settings",
            Summary = "Read the default agent's persisted NavMesh project settings.",
            Description = "Read-only: returns default-agent radius, height, slope, climb, minimum region area, voxel size/manual mode, and SHA-256 of ProjectSettings/NavMeshAreas.asset. The centralized Unity NavMeshProjectSettings reflection boundary and every required serialized property fail closed.",
            Group = "navmesh",
            DestructiveHint = false,
            SideEffects = System.Array.Empty<string>(),
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read default NavMesh settings.", Input = new Request() } }
        };

        /// <summary>直接从 current SerializedObject 与文件读取，不触发 import、save 或其他副作用。</summary>
        public ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();
            NavMeshToolSupport.EnsureEditorReady("navmesh.settings", false);
            return new ValueTask<Result>(new Result { Settings = NavMeshToolSupport.ReadSettings() });
        }
    }
}
#endif
