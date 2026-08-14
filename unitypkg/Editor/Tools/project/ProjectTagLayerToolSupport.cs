#if UNITY_EDITOR
using System.Collections.Generic;
namespace UnityCli
{
    public sealed class ProjectTagLayerResult { public bool Changed { get; set; } public string NoOpReason { get; set; } public long? LayerIndex { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
    public static class ProjectTagLayerToolSupport
    {
        public static System.Threading.Tasks.ValueTask<ProjectTagLayerResult> Apply(bool layer, bool add, string name, int? index, string toolName, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy(toolName);
            var plan = EditorActionToolSupport.PrepareTagLayerChange(layer, add, name, index);
            if (!plan.Changed)
            {
                var (_, existingLayerIndex) = EditorActionToolSupport.ApplyTagLayerChange(plan);
                return new(new ProjectTagLayerResult { Changed = false, NoOpReason = add ? "already-present" : "already-absent", LayerIndex = layer && existingLayerIndex >= 0 ? existingLayerIndex : (long?)null, PlannedEffects = new List<EditorPlannedEffect>() });
            }
            ctx.CommitEffect();
            var (changed, layerIndex) = EditorActionToolSupport.ApplyTagLayerChange(plan);
            return new(new ProjectTagLayerResult { Changed = changed, LayerIndex = layer && layerIndex >= 0 ? layerIndex : (long?)null, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = toolName, Action = add ? "add" : "remove", Name = name } } });
        }
    }
}
#endif
