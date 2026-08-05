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
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "scene-modify", "component-reference-edit" }, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[] { new ToolExample { Description = "Assign the matrix graph", Input = new Request { Target = "Effects/Emitter", Graph = "Assets/VFX/Explosion.vfx" } } }
        };

        public System.Threading.Tasks.ValueTask<VfxEffectActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var effect = VfxActionToolSupport.RequireEffect(request.Target, "vfx.effect.assign");
            VfxActionToolSupport.PrepareGraphAssignment(effect.GetType());
            var graphPath = VfxToolSupport.RequireVfxGraphPath(request.Graph);
            if (UnityEditor.AssetDatabase.LoadAssetAtPath(graphPath, VfxToolSupport.RequireVfxAssetType()) == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + request.Graph, "Pass an existing Assets/*.vfx path.");
            var gameObjectId = effect.gameObject.GetInstanceID();
            var effectType = effect.GetType();
            ctx.CommitEffect();
            var observedObject = VfxToolSupport.FindGameObjectByTarget(gameObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            effect = observedObject == null ? null : observedObject.GetComponent(effectType);
            if (effect == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", "vfx.effect.assign target/component disappeared after commit.", "Retry after checking the target still exists.");
            VfxActionToolSupport.AssignGraph(effect, graphPath);
            VfxActionToolSupport.VerifyAssignedGraph(effect, graphPath);
            return new System.Threading.Tasks.ValueTask<VfxEffectActionResult>(new VfxEffectActionResult
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(effect.gameObject),
                Action = "assigned",
                Effect = VfxToolSupport.BuildEffectInfo(effect),
                PlannedEffects = new List<VfxPlannedEffect> { new VfxPlannedEffect { Type = "vfx.effect.assign", Target = request.Target, Graph = request.Graph } }
            });
        }
    }
}
#endif
