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
    /// <summary>ui.image.create — 创建 uGUI Image 元素。</summary>
    public class UiImageCreateTool : IUnityCliTool<UiImageCreateTool.Request, UiImageCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Parent { get; set; } public string Sprite { get; set; } public string Color { get; set; } public float? Width { get; set; } public float? Height { get; set; } public float? X { get; set; } public float? Y { get; set; } }
        public sealed class Result { public UiElementRef Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.image.create", Summary = "Create a uGUI Image element with optional sprite.", Description = "Create an Image element. Pass --sprite for an Assets/* sprite path.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Image", Input = new Request { Parent = "Canvas" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Element = UiToolSupport.BuildUiElementRef(go, "Image"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.image.create", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
