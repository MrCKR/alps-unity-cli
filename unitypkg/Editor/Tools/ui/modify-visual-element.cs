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
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public UiVisualElementInfo Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.modify-visual-element", Summary = "Modify a VisualElement in a live UIDocument by name.", Description = "Find a VisualElement by name in a UIDocument and modify text/display/size/classes. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "ui-document-session-state" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set text", Input = new Request { Target = "UIDocument", ElementName = "label", Text = "Updated" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var resolvedGo = UiToolSupport.ResolveGameObjectTarget(r.Target);
            var targetId = resolvedGo.GetInstanceID();
            var go = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            var doc = go == null ? null : go.GetComponent<UIDocument>();
            if (go == null || go.GetInstanceID() != targetId || doc == null || doc.rootVisualElement == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument with a visual tree on target.", "Pass a target with a UIDocument.");
            var dryRun = ctx.DryRun;
            var element = doc.rootVisualElement.Q(r.ElementName);
            if (element == null) throw new UnityCliBridge.BridgeCommandException("E_UI_ELEMENT_NOT_FOUND", "VisualElement not found: " + r.ElementName, "Pass a valid element name.");
            var textElement = element as TextElement;
            if (r.Text != null && textElement == null)
                throw new UnityCliBridge.BridgeCommandException("E_UI_ELEMENT_TEXT_UNSUPPORTED", "VisualElement does not support text: " + r.ElementName, "Target a Label/TextElement or omit the text field.");
            DisplayStyle requestedDisplay = default;
            if (!string.IsNullOrEmpty(r.Display) && !System.Enum.TryParse(r.Display, true, out requestedDisplay))
                throw new UnityCliBridge.BridgeCommandException("E_UI_DISPLAY_INVALID", "Unsupported display value: " + r.Display, "Pass a valid DisplayStyle value.");
            UiToolSupport.ValidateVisualElementSize(r.Width, r.Height);
            var hasRequestedMutation = r.Text != null || !string.IsNullOrEmpty(r.Display) || r.Width.HasValue || r.Height.HasValue || !string.IsNullOrEmpty(r.AddClass) || !string.IsNullOrEmpty(r.RemoveClass);
            if (!dryRun && hasRequestedMutation)
            {
                ctx.CommitEffect();
                if (r.Text != null)
                {
                    textElement.text = r.Text;
                }
                if (!string.IsNullOrEmpty(r.Display)) element.style.display = requestedDisplay;
                if (r.Width.HasValue) element.style.width = r.Width.Value;
                if (r.Height.HasValue) element.style.height = r.Height.Value;
                if (!string.IsNullOrEmpty(r.AddClass)) element.AddToClassList(r.AddClass);
                if (!string.IsNullOrEmpty(r.RemoveClass)) element.RemoveFromClassList(r.RemoveClass);

                // 写入后按实例 ID 重新解析场景对象和文档，确保读回来自当前对象。
                var observedGo = EditorUtility.InstanceIDToObject(targetId) as GameObject;
                var observedDocument = observedGo == null || observedGo.GetInstanceID() != targetId ? null : observedGo.GetComponent<UIDocument>();
                var observed = observedDocument == null || observedDocument.rootVisualElement == null ? null : observedDocument.rootVisualElement.Q(r.ElementName);
                if (observed == null ||
                    (r.Text != null && (!(observed is TextElement observedText) || observedText.text != r.Text)) ||
                    (!string.IsNullOrEmpty(r.Display) && observed.style.display.value != requestedDisplay) ||
                    (r.Width.HasValue && !Mathf.Approximately(observed.style.width.value.value, r.Width.Value)) ||
                    (r.Height.HasValue && !Mathf.Approximately(observed.style.height.value.value, r.Height.Value)) ||
                    (!string.IsNullOrEmpty(r.AddClass) && !observed.ClassListContains(r.AddClass)) ||
                    (!string.IsNullOrEmpty(r.RemoveClass) && observed.ClassListContains(r.RemoveClass)))
                    throw new UnityCliBridge.BridgeCommandException("E_UI_ELEMENT_READBACK_FAILED", "VisualElement does not match the requested changes: " + r.ElementName, "Check the UIDocument visual tree and retry.");
                element = observed;
            }
            else if (!hasRequestedMutation)
            {
                // 空请求是只读操作；重新解析确保返回当前场景对象的状态而不提交 effect。
                var observedGo = EditorUtility.InstanceIDToObject(targetId) as GameObject;
                var observedDocument = observedGo == null || observedGo.GetInstanceID() != targetId ? null : observedGo.GetComponent<UIDocument>();
                var observed = observedDocument == null || observedDocument.rootVisualElement == null ? null : observedDocument.rootVisualElement.Q(r.ElementName);
                if (observed == null)
                    throw new UnityCliBridge.BridgeCommandException("E_UI_ELEMENT_READBACK_FAILED", "VisualElement could not be re-resolved: " + r.ElementName, "Check the UIDocument visual tree and retry.");
                element = observed;
            }
            var observedTextElement = element as TextElement;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.modify-visual-element", DryRun = dryRun, Element = new UiVisualElementInfo { Name = element.name, Text = observedTextElement == null ? null : observedTextElement.text, Display = element.style.display.value.ToString(), Width = element.style.width.value.value, Height = element.style.height.value.value, Classes = new List<string>(element.GetClasses()).ToArray() }, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.modify-visual-element", Target = r.ElementName } } });
        }
    }
}
#endif
