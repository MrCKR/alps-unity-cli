#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>给现有 VisualEffect 组件绑定图资产。</summary>
    public sealed class VfxEffectAssignTool : IUnityCliTool<VfxEffectAssignTool.Request, VfxEffectActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } [UnityCliRequired] public string Graph { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.effect.assign", Summary = "Assign a VFX Graph to a VisualEffect.",
            Description = "Bind one existing Assets/*.vfx graph to the existing VisualEffect component on the target.",
            Group = "vfx", Version = "1.0.0", RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "scene-modify", "component-reference-edit" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Assign the matrix graph", Input = new Request { Target = "Effects/Emitter", Graph = "Assets/VFX/Explosion.vfx" } } }
        };

        public System.Threading.Tasks.ValueTask<VfxEffectActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.effect.assign");
            VfxActionToolSupport.AssignGraph(effect, request.Graph);
            return new System.Threading.Tasks.ValueTask<VfxEffectActionResult>(new VfxEffectActionResult
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(effect.gameObject),
                Action = "assigned",
                Effect = VfxToolSupport.BuildEffectInfo(effect),
                PlannedEffects = new List<VfxPlannedEffect> { new VfxPlannedEffect { Type = "vfx.effect.assign", Target = request.Target, Graph = request.Graph } },
                LogPath = context.LogPath
            });
        }
    }
}
#endif
