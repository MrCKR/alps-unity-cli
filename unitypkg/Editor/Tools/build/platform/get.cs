#if UNITY_EDITOR
using UnityEditor;
namespace UnityCli
{
    public class BuildPlatformGetTool : IUnityCliTool<BuildPlatformGetTool.Request, BuildPlatformResult>
    {
        public sealed class Request { }
        public ToolDescriptor Describe() => new() { Name = "build.platform.get", Summary = "Read the active Unity build target and target group.", Description = "Returns the active BuildTarget and selected BuildTargetGroup without modifying project or session state. Available in Edit and Play mode.", Group = "build", Version = "1.0.0", Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read active build target", Input = new Request() } } };
        public System.Threading.Tasks.ValueTask<BuildPlatformResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) => new(new BuildPlatformResult { ActiveTarget = EditorUserBuildSettings.activeBuildTarget.ToString(), ActiveTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString() });
    }
}
#endif
