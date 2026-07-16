#if UNITY_EDITOR
using System.Collections.Generic;
namespace UnityCli.Cinemachine
{
    public sealed class ExtensionListTool : IUnityCliTool<ExtensionListTool.Request, ExtensionListTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public List<string> Extensions { get; set; } public long Count { get; set; } public string LogPath { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "cinemachine.extension.list", Summary = "List CinemachineExtension components.", Description = "Lists CinemachineExtension-derived components on a virtual camera without modifying the scene.", Group = "cinemachine", Version = "1.0.0", RequiresPackages = new[] { "com.unity.cinemachine" }, SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "List PlayerCam extensions", Input = new Request { Target = "PlayerCam" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default) { var extensions = CinemachineExtensionToolSupport.List(CinemachineExtensionToolSupport.Target(request.Target, "cinemachine.extension.list")); return new(new Result { Extensions = extensions, Count = extensions.Count, LogPath = context.LogPath }); }
    }
}
#endif
