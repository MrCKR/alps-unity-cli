#if UNITY_EDITOR
using UnityEditor;
namespace UnityCli.Cinemachine
{
    public sealed class ExtensionRemoveTool : IUnityCliTool<ExtensionRemoveTool.Request, ExtensionRemoveTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string ExtensionType { get; set; } }
        public sealed class Result { public bool Removed { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "cinemachine.extension-remove", Summary = "Remove a CinemachineExtension component.", Description = "Removes one CinemachineExtension-derived component from scene memory, re-enumerates extensions to verify it is absent, records Undo, and marks the scene dirty without saving it.", Group = "cinemachine",RequiresPackages = new[] { "com.unity.cinemachine" }, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "Remove a collider extension", Input = new Request { Target = "PlayerCam", ExtensionType = "CinemachineCollider" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { var target = CinemachineExtensionToolSupport.Target(request.Target, "cinemachine.extension-remove"); var type = CinemachineExtensionToolSupport.Type(request.ExtensionType); var extension = target.GetComponent(type); if (extension == null) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_EXTENSION_NOT_FOUND", "Extension not found: " + request.ExtensionType, "Use cinemachine.extension-list to inspect installed extensions."); var targetId = target.GetInstanceID(); ctx.CommitEffect(); UnityCliSceneMutation.DestroyObject(extension); if (CinemachineExtensionToolSupport.Contains(targetId, type, out var observedTarget)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_EXTENSION_VERIFY_FAILED", "Extension remained after removal: " + request.ExtensionType, "Check the target virtual camera and retry."); CinemachineToolSupport.MarkDirty(observedTarget); return new(new Result { Removed = true }); }
    }
}
#endif
