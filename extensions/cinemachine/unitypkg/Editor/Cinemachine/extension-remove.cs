#if UNITY_EDITOR
using UnityEditor;
namespace UnityCli.Cinemachine
{
    public sealed class ExtensionRemoveTool : IUnityCliTool<ExtensionRemoveTool.Request, ExtensionRemoveTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string ExtensionType { get; set; } }
        public sealed class Result { public string ExtensionType { get; set; } public string LogPath { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "cinemachine.extension.remove", Summary = "Remove a CinemachineExtension component.", Description = "Removes one CinemachineExtension-derived component from scene memory, records Undo, and marks the scene dirty without saving it.", Group = "cinemachine", Version = "1.0.0", RequiresPackages = new[] { "com.unity.cinemachine" }, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "Remove a collider extension", Input = new Request { Target = "PlayerCam", ExtensionType = "CinemachineCollider" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default) { var target = CinemachineExtensionToolSupport.Target(request.Target, "cinemachine.extension.remove"); var extension = target.GetComponent(CinemachineExtensionToolSupport.Type(request.ExtensionType)); if (extension == null) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_EXTENSION_NOT_FOUND", "Extension not found: " + request.ExtensionType, "Use cinemachine.extension.list to inspect installed extensions."); UnityCliSceneMutation.DestroyObject(extension); CinemachineToolSupport.MarkDirty(target); return new(new Result { ExtensionType = request.ExtensionType, LogPath = context.LogPath }); }
    }
}
#endif
