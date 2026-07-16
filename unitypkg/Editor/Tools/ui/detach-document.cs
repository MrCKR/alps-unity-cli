#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.detach-document — 从 GameObject 移除 UIDocument 组件。</summary>
    public class UiDetachDocumentTool : IUnityCliTool<UiDetachDocumentTool.Request, UiDetachDocumentTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public string Command { get; set; } public UiElementRef Target { get; set; } public bool Detached { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.detach-document", Summary = "Remove a UIDocument component from a target GameObject.", Description = "Remove UIDocument component. Only affects current scene session. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Detach UIDocument", Input = new Request { Target = "Player" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.detach-document");
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(r.Target);
            if (go == null) throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "ui.detach-document requires a target GameObject.", "Pass --target <path>.");
            var doc = go.GetComponent<UIDocument>();
            if (doc == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument on target: " + r.Target, "Pass a target with a UIDocument.");
            var dryRun = ctx.DryRun;
            if (!dryRun) { UnityCliSceneMutation.DestroyObject(doc); UnityCliSceneMutation.MarkSceneDirty(go.scene); }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.detach-document", Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), Detached = !dryRun, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.detach-document", Target = GameObjectToolSupport.HierarchyPath(go) } } });
        }
    }
}
#endif
