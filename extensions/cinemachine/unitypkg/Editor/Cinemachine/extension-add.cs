#if UNITY_EDITOR
using UnityEditor;
namespace UnityCli.Cinemachine
{
    public sealed class ExtensionAddTool : IUnityCliTool<ExtensionAddTool.Request, ExtensionAddTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string ExtensionType { get; set; } }
        public sealed class Result { public bool Added { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "cinemachine.extension-add", Summary = "Add a CinemachineExtension component.", Description = "Adds one CinemachineExtension-derived component to a virtual camera, re-enumerates extensions to verify it exists, and marks the scene dirty without saving it.", Group = "cinemachine",RequiresPackages = new[] { "com.unity.cinemachine" }, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "Add a collider extension", Input = new Request { Target = "PlayerCam", ExtensionType = "CinemachineCollider" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { var target = CinemachineExtensionToolSupport.Target(request.Target, "cinemachine.extension-add"); var type = CinemachineExtensionToolSupport.Type(request.ExtensionType); if (target.GetComponent(type) != null) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_EXTENSION_EXISTS", "Extension already present: " + request.ExtensionType, "Use cinemachine.extension-remove before adding it again."); var targetId = target.GetInstanceID(); ctx.CommitEffect(); UnityCliSceneMutation.AddComponent(target, type); if (!CinemachineExtensionToolSupport.Contains(targetId, type, out var observedTarget)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_EXTENSION_VERIFY_FAILED", "Extension was not present after adding: " + request.ExtensionType, "Check the target virtual camera and retry."); CinemachineToolSupport.MarkDirty(observedTarget); return new(new Result { Added = true }); }
    }
}
#endif
