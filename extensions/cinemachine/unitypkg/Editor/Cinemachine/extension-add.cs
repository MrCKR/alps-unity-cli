#if UNITY_EDITOR
using UnityEditor;
namespace UnityCli.Cinemachine
{
    public sealed class ExtensionAddTool : IUnityCliTool<ExtensionAddTool.Request, ExtensionAddTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string ExtensionType { get; set; } }
        public sealed class Result { public string ExtensionType { get; set; } public string LogPath { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "cinemachine.extension.add", Summary = "Add a CinemachineExtension component.", Description = "Adds one CinemachineExtension-derived component to a virtual camera and marks the scene dirty without saving it.", Group = "cinemachine", Version = "1.0.0", RequiresPackages = new[] { "com.unity.cinemachine" }, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "Add a collider extension", Input = new Request { Target = "PlayerCam", ExtensionType = "CinemachineCollider" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default) { var target = CinemachineExtensionToolSupport.Target(request.Target, "cinemachine.extension.add"); var type = CinemachineExtensionToolSupport.Type(request.ExtensionType); if (target.GetComponent(type) != null) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_EXTENSION_EXISTS", "Extension already present: " + request.ExtensionType, "Use cinemachine.extension.remove before adding it again."); UnityCliSceneMutation.AddComponent(target, type); CinemachineToolSupport.MarkDirty(target); return new(new Result { ExtensionType = request.ExtensionType, LogPath = context.LogPath }); }
    }
}
#endif
