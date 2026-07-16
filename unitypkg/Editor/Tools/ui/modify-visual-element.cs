#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.modify-visual-element — 修改 VisualElement 属性。</summary>
    public class UiModifyVisualElementTool : IUnityCliTool<UiModifyVisualElementTool.Request, UiModifyVisualElementTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string ElementName { get; set; } public string Text { get; set; } public string Display { get; set; } public float? Width { get; set; } public float? Height { get; set; } public string AddClass { get; set; } public string RemoveClass { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public string ElementName { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.modify-visual-element", Summary = "Modify a VisualElement in a live UIDocument by name.", Description = "Find a VisualElement by name in a UIDocument and modify text/display/size/classes. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set text", Input = new Request { Target = "UIDocument", ElementName = "label", Text = "Updated" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = UiToolSupport.ResolveGameObjectTarget(r.Target);
            var doc = go.GetComponent<UIDocument>();
            if (doc == null || doc.rootVisualElement == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument with a visual tree on target.", "Pass a target with a UIDocument.");
            var dryRun = ctx.DryRun;
            var element = doc.rootVisualElement.Q(r.ElementName);
            if (element == null) throw new UnityCliBridge.BridgeCommandException("E_UI_ELEMENT_NOT_FOUND", "VisualElement not found: " + r.ElementName, "Pass a valid element name.");
            if (!dryRun)
            {
                if (!string.IsNullOrEmpty(r.Text))
                {
                    var textElement = element as TextElement;
                    if (textElement == null)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_UI_ELEMENT_TEXT_UNSUPPORTED", "VisualElement does not support text: " + r.ElementName, "Target a Label/TextElement or omit the text field.");
                    }
                    textElement.text = r.Text;
                }
                if (!string.IsNullOrEmpty(r.Display)) if (System.Enum.TryParse<DisplayStyle>(r.Display, true, out var ds)) element.style.display = ds;
                if (r.Width.HasValue) element.style.width = r.Width.Value;
                if (r.Height.HasValue) element.style.height = r.Height.Value;
                if (!string.IsNullOrEmpty(r.AddClass)) element.AddToClassList(r.AddClass);
                if (!string.IsNullOrEmpty(r.RemoveClass)) element.RemoveFromClassList(r.RemoveClass);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.modify-visual-element", DryRun = dryRun, ElementName = r.ElementName, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.modify-visual-element", Target = r.ElementName } } });
        }
    }
}
#endif
