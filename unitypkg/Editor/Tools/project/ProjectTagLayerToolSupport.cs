#if UNITY_EDITOR
using System.Collections.Generic;
namespace UnityCli
{
    public sealed class ProjectTagLayerResult { public string Name { get; set; } public bool Changed { get; set; } public long? LayerIndex { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
    public static class ProjectTagLayerToolSupport
    {
        public static System.Threading.Tasks.ValueTask<ProjectTagLayerResult> Apply(bool layer, bool add, string name, int? index, string toolName, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy(toolName);
            var (changed, layerIndex) = EditorActionToolSupport.ApplyTagLayerChange(layer, add, name, index);
            return new(new ProjectTagLayerResult { Name = name, Changed = changed, LayerIndex = layer && layerIndex >= 0 ? layerIndex : (long?)null, PlannedEffects = changed ? new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = toolName, Action = add ? "add" : "remove", Name = name } } : new List<EditorPlannedEffect>() });
        }
    }
}
#endif
