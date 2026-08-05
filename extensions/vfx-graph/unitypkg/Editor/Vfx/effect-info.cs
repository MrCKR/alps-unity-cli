#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>读取目标 VisualEffect 组件及其图资产绑定。</summary>
    public sealed class VfxEffectInfoTool : IUnityCliTool<VfxEffectInfoTool.Request, VfxEffectInfoTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public GameObjectRef Target { get; set; } public VfxEffectInfo Effect { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.effect.info", Summary = "Read VisualEffect component information.",
            Description = "Report the existing VisualEffect component and assigned VFX Graph without modifying the scene.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new string[0], Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Inspect the matrix emitter", Input = new Request { Target = "Effects/Emitter" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.effect.info");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(effect.gameObject),
                Effect = VfxToolSupport.BuildEffectInfo(effect)
            });
        }
    }
}
#endif
