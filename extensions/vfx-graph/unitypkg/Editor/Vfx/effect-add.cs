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
            Name = "vfx.effect.add", Summary = "Add a VisualEffect component.",
            Description = "Ensure one VisualEffect component exists on the target and optionally assign an initial existing VFX Graph. Repeated calls are idempotent.",
            Group = "vfx", Version = "1.0.0", RequiresPackages = new[] { "com.unity.visualeffectgraph" },
            SideEffects = new[] { "scene-modify", "component-add", "component-reference-edit" },
            Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "Add VisualEffect to a new emitter", Input = new Request { Target = "Effects/NewEmitter" } },
                new ToolExample { Description = "Add and bind a graph", Input = new Request { Target = "Effects/NewEmitter", Graph = "Assets/VFX/Explosion.vfx" } }
            }
        };

        public System.Threading.Tasks.ValueTask<VfxEffectActionResult> ExecuteAsync(Request request, UnityCliProjectToolContext context, System.Threading.CancellationToken ct = default)
        {
            VfxToolSupport.EnsureEditorReady("vfx.effect.add");
            var effectType = VfxToolSupport.RequireVisualEffectType();
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "vfx.effect.add requires target.");
            var effect = gameObject.GetComponent(effectType);
            var added = effect == null;
            if (added) effect = gameObject.AddComponent(effectType);
            if (!string.IsNullOrWhiteSpace(request.Graph)) VfxActionToolSupport.AssignGraph(effect, request.Graph);
            UnityCliSceneMutation.MarkSceneObjectDirty(effect);
            VfxToolSupport.MarkSceneDirty(gameObject);
            return new System.Threading.Tasks.ValueTask<VfxEffectActionResult>(new VfxEffectActionResult
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Action = added ? "added" : "already-present",
                Effect = VfxToolSupport.BuildEffectInfo(effect),
                PlannedEffects = new List<VfxPlannedEffect> { new VfxPlannedEffect { Type = "vfx.effect.add", Target = GameObjectToolSupport.HierarchyPath(gameObject), Graph = request.Graph } },
                LogPath = context.LogPath
            });
        }
    }
}
#endif
