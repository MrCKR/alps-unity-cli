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
            if (parent == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PARENT_NOT_FOUND", "UI parent Canvas or RectTransform not found: " + r.Parent, "Create a Canvas first.");
            var name = string.IsNullOrEmpty(r.Name) ? "Button" : r.Name;
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            var image = go.AddComponent<UiImage>();
            image.color = string.IsNullOrEmpty(r.Color) ? Color.white : GraphicsToolSupport.ParseColor(r.Color);
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
            Selection.activeGameObject = go;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Element = UiToolSupport.BuildUiElementRef(go, "Button"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.button.create", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
