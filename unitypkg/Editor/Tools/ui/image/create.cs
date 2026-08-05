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
        public ToolDescriptor Describe() => new() { Name = "ui.image.create", Summary = "Create a uGUI Image element with optional sprite.", Description = "Create an Image element. Pass --sprite for an Assets/* sprite path.", Group = "ui",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Image", Input = new Request { Parent = "Canvas" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var parent = UiToolSupport.ResolveUiParent(r.Parent);
            if (parent == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PARENT_NOT_FOUND", "UI parent Canvas or RectTransform not found: " + r.Parent, "Create a Canvas first.");
            var name = string.IsNullOrEmpty(r.Name) ? "Image" : r.Name;
            var requestedColor = string.IsNullOrEmpty(r.Color) ? Color.white : GraphicsToolSupport.ParseColor(r.Color);
            var spritePath = SceneToolSupport.NormalizeAssetPath(r.Sprite);
            var requestedSprite = string.IsNullOrEmpty(spritePath) ? null : AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            if (!string.IsNullOrEmpty(spritePath) && requestedSprite == null)
                throw new UnityCliBridge.BridgeCommandException("E_UI_SPRITE_NOT_FOUND", "Sprite asset not found: " + spritePath, "Pass an Assets/* sprite path or omit --sprite.");
            UiToolSupport.ValidateUguiRequest(null, r.Width, r.Height, r.X, r.Y);
            ctx.CommitEffect();
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            var image = go.AddComponent<UiImage>();
            image.color = requestedColor;
            image.sprite = requestedSprite;
            UiRectHelper.ApplyRect(go.GetComponent<RectTransform>(), r.Width, r.Height, r.X, r.Y);
            var observedGo = EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject;
            var observedImage = observedGo == null ? null : observedGo.GetComponent<UiImage>();
            var observedRect = observedGo == null ? null : observedGo.GetComponent<RectTransform>();
            if (observedGo == null || observedGo.transform.parent != parent.transform || observedRect == null || (r.Width.HasValue && !Mathf.Approximately(observedRect.sizeDelta.x, r.Width.Value)) || (r.Height.HasValue && !Mathf.Approximately(observedRect.sizeDelta.y, r.Height.Value)) || (r.X.HasValue && !Mathf.Approximately(observedRect.anchoredPosition.x, r.X.Value)) || (r.Y.HasValue && !Mathf.Approximately(observedRect.anchoredPosition.y, r.Y.Value)) || observedImage == null || observedImage.sprite != requestedSprite || observedImage.color != requestedColor)
                throw new UnityCliBridge.BridgeCommandException("E_UI_IMAGE_READBACK_FAILED", "Created Image does not match the requested sprite or color.", "Check Unity Console for scene mutation errors.");
            Selection.activeGameObject = go;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Element = UiToolSupport.BuildUiElementRef(go, "Image"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.image.create", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
