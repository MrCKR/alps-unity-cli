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
    /// <summary>ui.canvas.create — 创建 uGUI Canvas。</summary>
    public class UiCanvasCreateTool : IUnityCliTool<UiCanvasCreateTool.Request, UiCanvasCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } }
        public sealed class Result { public UiElementRef Element { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.canvas.create", Summary = "Create a uGUI Canvas with CanvasScaler and GraphicRaycaster.", Description = "Create a ScreenSpaceOverlay Canvas with ScaleWithScreenSize mode (1920x1080 reference).", Group = "ui",DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create Canvas", Input = new Request { Name = "MainCanvas" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var name = string.IsNullOrEmpty(r.Name) ? "Canvas" : r.Name;
            ctx.CommitEffect();
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(UiCanvasScaler), typeof(UiGraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<UiCanvasScaler>();
            scaler.uiScaleMode = UiCanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            var observedGo = EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject;
            var observedCanvas = observedGo == null ? null : observedGo.GetComponent<Canvas>();
            var observedScaler = observedGo == null ? null : observedGo.GetComponent<UiCanvasScaler>();
            var observedRaycaster = observedGo == null ? null : observedGo.GetComponent<UiGraphicRaycaster>();
            if (observedGo == null || observedGo.transform.parent != null || observedGo.GetComponent<RectTransform>() == null || observedCanvas == null || observedCanvas.renderMode != RenderMode.ScreenSpaceOverlay || observedScaler == null || observedScaler.uiScaleMode != UiCanvasScaler.ScaleMode.ScaleWithScreenSize || observedScaler.referenceResolution != new Vector2(1920, 1080) || observedRaycaster == null)
                throw new UnityCliBridge.BridgeCommandException("E_UI_CANVAS_READBACK_FAILED", "Created Canvas does not match required components and settings.", "Check Unity Console for scene mutation errors.");
            Selection.activeGameObject = observedGo;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Element = UiToolSupport.BuildUiElementRef(go, "Canvas"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.canvas.create", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
