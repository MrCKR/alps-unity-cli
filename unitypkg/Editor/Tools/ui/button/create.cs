#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    /// <summary>ui.button.create — 创建 uGUI Button 元素。</summary>
    public class UiButtonCreateTool : IUnityCliTool<UiButtonCreateTool.Request, UiButtonCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Parent { get; set; } public string Text { get; set; } public int? FontSize { get; set; } public string Color { get; set; } public float? Width { get; set; } public float? Height { get; set; } public float? X { get; set; } public float? Y { get; set; } }
        public sealed class Result { public UiElementRef Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.button.create", Summary = "Create a uGUI Button with Image and Text label.", Description = "Create a Button element with Image background and a centered Text label child.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Button", Input = new Request { Parent = "Canvas", Text = "Click Me" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var parent = UiToolSupport.ResolveUiParent(r.Parent);
            if (parent == null || parent.GetComponent<RectTransform>() == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PARENT_NOT_FOUND", "UI parent Canvas or RectTransform not found: " + r.Parent, "Create a Canvas first.");
            var parentId = parent.GetInstanceID();
            var freshParent = EditorUtility.InstanceIDToObject(parentId) as GameObject;
            if (freshParent == null || freshParent.GetInstanceID() != parentId) throw new UnityCliBridge.BridgeCommandException("E_UI_PARENT_NOT_FOUND", "UI parent could not be fresh-resolved: " + r.Parent, "Retry after the Editor is idle.");
            parent = freshParent;
            var name = string.IsNullOrEmpty(r.Name) ? "Button" : r.Name;
            var requestedColor = string.IsNullOrEmpty(r.Color) ? Color.white : GraphicsToolSupport.ParseColor(r.Color);
            UiToolSupport.ValidateUguiRequest(r.FontSize, r.Width, r.Height, r.X, r.Y);
            ctx.CommitEffect();
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            var image = go.AddComponent<UiImage>();
            image.color = requestedColor;
            go.AddComponent<UiButton>();
            var labelGo = new GameObject("Text", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);
            UiRectHelper.StretchRect(labelGo.GetComponent<RectTransform>());
            var label = labelGo.AddComponent<UiText>();
            label.text = string.IsNullOrEmpty(r.Text) ? name : r.Text;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = r.FontSize ?? 24;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.black;
            UiRectHelper.ApplyRect(go.GetComponent<RectTransform>(), r.Width, r.Height, r.X, r.Y);
            var observedGo = EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject;
            var observedButton = observedGo == null ? null : observedGo.GetComponent<UiButton>();
            var observedImage = observedGo == null ? null : observedGo.GetComponent<UiImage>();
            var observedLabel = observedGo == null ? null : observedGo.GetComponentInChildren<UiText>();
            var observedRect = observedGo == null ? null : observedGo.GetComponent<RectTransform>();
            if (observedGo == null || observedGo.transform.parent != parent.transform || observedRect == null || (r.Width.HasValue && !Mathf.Approximately(observedRect.sizeDelta.x, r.Width.Value)) || (r.Height.HasValue && !Mathf.Approximately(observedRect.sizeDelta.y, r.Height.Value)) || (r.X.HasValue && !Mathf.Approximately(observedRect.anchoredPosition.x, r.X.Value)) || (r.Y.HasValue && !Mathf.Approximately(observedRect.anchoredPosition.y, r.Y.Value)) || observedButton == null || observedImage == null || observedImage.color != requestedColor || observedLabel == null || observedLabel.text != label.text || observedLabel.fontSize != label.fontSize)
                throw new UnityCliBridge.BridgeCommandException("E_UI_BUTTON_READBACK_FAILED", "Created Button does not match the requested values.", "Check Unity Console for scene mutation errors.");
            Selection.activeGameObject = go;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Element = UiToolSupport.BuildUiElementRef(go, "Button"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.button.create", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
