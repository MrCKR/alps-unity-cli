#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>给目标 GameObject 添加 VisualEffect 组件。</summary>
    public sealed class VfxEffectAddTool : IUnityCliTool<VfxEffectAddTool.Request, VfxEffectActionResult>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public string Graph { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "vfx.effect-add", Summary = "Add a VisualEffect component.",
            Description = "Ensure one VisualEffect component exists on the target and optionally assign an initial existing VFX Graph. Repeated calls are idempotent.",
            Group = "vfx",RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "scene-modify", "component-add", "component-reference-edit" },
            Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Add VisualEffect to a new emitter", Input = new Request { Target = "Effects/NewEmitter" } },
                new ToolExample { Description = "Add and bind a graph", Input = new Request { Target = "Effects/NewEmitter", Graph = "Assets/VFX/Explosion.vfx" } }
            }
        };

        public System.Threading.Tasks.ValueTask<VfxEffectActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.effect-add");
            var effectType = VfxToolSupport.RequireVisualEffectType();
            if (!string.IsNullOrWhiteSpace(request.Graph)) VfxActionToolSupport.PrepareGraphAssignment(effectType);
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.effect-add requires target.");
            var effect = gameObject.GetComponent(effectType);
            if (!string.IsNullOrWhiteSpace(request.Graph) && AssetDatabase.LoadAssetAtPath(VfxToolSupport.RequireVfxGraphPath(request.Graph), VfxToolSupport.RequireVfxAssetType()) == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + request.Graph, "Pass an existing Assets/*.vfx path.");
            var gameObjectId = gameObject.GetInstanceID();
            ctx.CommitEffect();
            var observedObject = VfxToolSupport.FindGameObjectByTarget(gameObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (observedObject == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_OBSERVED_TARGET_MISSING", "vfx.effect-add target disappeared after commit.", "Retry after checking the target still exists.");
            effect = observedObject.GetComponent(effectType);
            var added = effect == null;
            if (added) effect = observedObject.AddComponent(effectType);
            if (!string.IsNullOrWhiteSpace(request.Graph)) { VfxActionToolSupport.AssignGraph(effect, request.Graph); VfxActionToolSupport.VerifyAssignedGraph(effect, request.Graph); }
            effect = observedObject.GetComponent(effectType);
            if (effect == null) throw new UnityCliBridge.BridgeCommandException("E_VFX_STATE_UNOBSERVABLE", "VisualEffect component add could not be read back.", "Retry after checking the target.");
            UnityCliSceneMutation.MarkSceneObjectDirty(effect);
            VfxToolSupport.MarkSceneDirty(observedObject);
            return new System.Threading.Tasks.ValueTask<VfxEffectActionResult>(new VfxEffectActionResult
            {
                Action = added ? "added" : "already-present",
                Effect = VfxToolSupport.BuildEffectInfo(effect),
                PlannedEffects = new List<VfxPlannedEffect> { new VfxPlannedEffect { Type = "vfx.effect-add", Target = GameObjectToolSupport.HierarchyPath(gameObject), Graph = request.Graph } }
            });
        }
    }
}
#endif
