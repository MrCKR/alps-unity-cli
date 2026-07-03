#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    /// <summary>ui.canvas.create — 创建 uGUI Canvas。</summary>
    public class UiCanvasCreateTool : IUnityCliTool<UiCanvasCreateTool.Request, UiCanvasCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } }
        public sealed class Result { public UiElementRef Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.canvas.create", Summary = "Create a uGUI Canvas with CanvasScaler and GraphicRaycaster.", Description = "Create a ScreenSpaceOverlay Canvas with ScaleWithScreenSize mode (1920x1080 reference).", Group = "ui", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Canvas", Input = new Request { Name = "MainCanvas" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var name = string.IsNullOrEmpty(r.Name) ? "Canvas" : r.Name;
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(UiCanvasScaler), typeof(UiGraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<UiCanvasScaler>();
            scaler.uiScaleMode = UiCanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            Selection.activeGameObject = go;
            return new Result { Element = UiToolSupport.BuildUiElementRef(go, "Canvas"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.canvas.create", Target = GameObjectToolSupport.HierarchyPath(go) } } };
        }
    }

    /// <summary>ui.text.create — 创建 uGUI Text 元素。</summary>
    public class UiTextCreateTool : IUnityCliTool<UiTextCreateTool.Request, UiTextCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Parent { get; set; } public string Text { get; set; } public int? FontSize { get; set; } public string Color { get; set; } public float? Width { get; set; } public float? Height { get; set; } public float? X { get; set; } public float? Y { get; set; } }
        public sealed class Result { public UiElementRef Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.text.create", Summary = "Create a uGUI Text element under a Canvas/RectTransform parent.", Description = "Create a Text element with LegacyRuntime font, configurable text/fontSize/color and RectTransform.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Text", Input = new Request { Parent = "Canvas", Text = "Hello" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
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
            return new Result { Element = UiToolSupport.BuildUiElementRef(go, "Text"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.text.create", Target = GameObjectToolSupport.HierarchyPath(go) } } };
        }
    }

    /// <summary>ui.button.create — 创建 uGUI Button 元素。</summary>
    public class UiButtonCreateTool : IUnityCliTool<UiButtonCreateTool.Request, UiButtonCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Parent { get; set; } public string Text { get; set; } public int? FontSize { get; set; } public string Color { get; set; } public float? Width { get; set; } public float? Height { get; set; } public float? X { get; set; } public float? Y { get; set; } }
        public sealed class Result { public UiElementRef Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.button.create", Summary = "Create a uGUI Button with Image and Text label.", Description = "Create a Button element with Image background and a centered Text label child.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Button", Input = new Request { Parent = "Canvas", Text = "Click Me" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
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
            return new Result { Element = UiToolSupport.BuildUiElementRef(go, "Button"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.button.create", Target = GameObjectToolSupport.HierarchyPath(go) } } };
        }
    }

    /// <summary>ui.image.create — 创建 uGUI Image 元素。</summary>
    public class UiImageCreateTool : IUnityCliTool<UiImageCreateTool.Request, UiImageCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Parent { get; set; } public string Sprite { get; set; } public string Color { get; set; } public float? Width { get; set; } public float? Height { get; set; } public float? X { get; set; } public float? Y { get; set; } }
        public sealed class Result { public UiElementRef Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.image.create", Summary = "Create a uGUI Image element with optional sprite.", Description = "Create an Image element. Pass --sprite for an Assets/* sprite path.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Image", Input = new Request { Parent = "Canvas" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var parent = UiToolSupport.ResolveUiParent(r.Parent);
            if (parent == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PARENT_NOT_FOUND", "UI parent Canvas or RectTransform not found: " + r.Parent, "Create a Canvas first.");
            var name = string.IsNullOrEmpty(r.Name) ? "Image" : r.Name;
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            var image = go.AddComponent<UiImage>();
            image.color = string.IsNullOrEmpty(r.Color) ? Color.white : GraphicsToolSupport.ParseColor(r.Color);
            var spritePath = SceneToolSupport.NormalizeAssetPath(r.Sprite);
            if (!string.IsNullOrEmpty(spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite == null) { UnityEngine.Object.DestroyImmediate(go); throw new UnityCliBridge.BridgeCommandException("E_UI_SPRITE_NOT_FOUND", "Sprite asset not found: " + spritePath, "Pass an Assets/* sprite path or omit --sprite."); }
                image.sprite = sprite;
            }
            UiRectHelper.ApplyRect(go.GetComponent<RectTransform>(), r.Width, r.Height, r.X, r.Y);
            Selection.activeGameObject = go;
            return new Result { Element = UiToolSupport.BuildUiElementRef(go, "Image"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.image.create", Target = GameObjectToolSupport.HierarchyPath(go) } } };
        }
    }

    // ── uGUI helper ──
    internal static class UiRectHelper
    {
        public static void UiRectHelper.ApplyRect(RectTransform rt, float? width, float? height, float? x, float? y)
        {
            if (width.HasValue) rt.sizeDelta = new Vector2(width.Value, rt.sizeDelta.y);
            if (height.HasValue) rt.sizeDelta = new Vector2(rt.sizeDelta.x, height.Value);
            if (x.HasValue || y.HasValue) rt.anchoredPosition = new Vector2(x ?? rt.anchoredPosition.x, y ?? rt.anchoredPosition.y);
        }
        public static void UiRectHelper.StretchRect(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero; }
    }
}
#endif
