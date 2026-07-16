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
    /// <summary>ui.text.create — 创建 uGUI Text 元素。</summary>
    public class UiTextCreateTool : IUnityCliTool<UiTextCreateTool.Request, UiTextCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Parent { get; set; } public string Text { get; set; } public int? FontSize { get; set; } public string Color { get; set; } public float? Width { get; set; } public float? Height { get; set; } public float? X { get; set; } public float? Y { get; set; } }
        public sealed class Result { public UiElementRef Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.text.create", Summary = "Create a uGUI Text element under a Canvas/RectTransform parent.", Description = "Create a Text element with LegacyRuntime font, configurable text/fontSize/color and RectTransform.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Text", Input = new Request { Parent = "Canvas", Text = "Hello" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var parent = UiToolSupport.ResolveUiParent(r.Parent);
            if (parent == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PARENT_NOT_FOUND", "UI parent Canvas or RectTransform not found: " + r.Parent, "Create a Canvas first.");
            var name = string.IsNullOrEmpty(r.Name) ? "Text" : r.Name;
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            var text = go.AddComponent<UiText>();
            text.text = string.IsNullOrEmpty(r.Text) ? name : r.Text;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = r.FontSize ?? 24;
            text.color = string.IsNullOrEmpty(r.Color) ? Color.black : GraphicsToolSupport.ParseColor(r.Color);
            UiRectHelper.ApplyRect(go.GetComponent<RectTransform>(), r.Width, r.Height, r.X, r.Y);
            Selection.activeGameObject = go;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Element = UiToolSupport.BuildUiElementRef(go, "Text"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.text.create", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
