#if UNITY_EDITOR
namespace UnityCli.Cinemachine
{
    public sealed class BrainStatusTool : IUnityCliTool<BrainStatusTool.Request, BrainStatusTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public CinemachinePackageStatus Cinemachine { get; set; } public CinemachineBrainInfo Brain { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "cinemachine.brain-status", Summary = "Read CinemachineBrain status.", Description = "Reports the selected or current CinemachineBrain, active virtual camera, and blend state without modifying the scene.", Group = "cinemachine",RequiresPackages = new[] { "com.unity.cinemachine" }, SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "Read Main Camera brain state", Input = new Request { Target = "Main Camera" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { CinemachineToolSupport.EnsureEditorReady("cinemachine.brain-status"); CinemachineToolSupport.EnsureCinemachineAvailable(); var brain = CinemachineToolSupport.RequireBrainComponent(CinemachineToolSupport.ResolveBrainOwner(request.Target)); return new(new Result { Cinemachine = CinemachineToolSupport.BuildPackageStatus(), Brain = CinemachineToolSupport.BuildBrainInfo(brain) }); }
    }
}
#endif
