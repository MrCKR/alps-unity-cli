#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.link-stylesheet — 链接 USS 到 UXML。</summary>
    public class UiLinkStylesheetTool : IUnityCliTool<UiLinkStylesheetTool.Request, UiLinkStylesheetTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public string Uss { get; set; } public string StyleSheet { get; set; } }
        public sealed class Result { public bool DryRun { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.link-stylesheet", Summary = "Link a .uss stylesheet to a .uxml document by appending a Style element.", Description = "Append <Style src=\"...\" /> to the .uxml file. dryRun=true previews.", Group = "ui",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Link stylesheet", Input = new Request { Path = "Assets/UI/Main.uxml", Uss = "Assets/UI/Main.uss" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var uxmlPath = SceneToolSupport.NormalizeAssetPath(r.Path);
            var ussPath = SceneToolSupport.NormalizeAssetPath(r.Uss ?? r.StyleSheet);
            if (string.IsNullOrEmpty(uxmlPath) || string.IsNullOrEmpty(ussPath)) throw new UnityCliBridge.BridgeCommandException("E_UI_PATH_REQUIRED", "ui.link-stylesheet requires --path (uxml) and --uss.", "Pass both uxml and uss paths.");
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = true, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.link-stylesheet", Path = uxmlPath, Uss = ussPath } } });
            var uxmlFullPath = SceneToolSupport.AssetFullPath(uxmlPath);
            var requestedStyleSheet = UiToolSupport.LoadStyleSheet(ussPath);
            var content = File.ReadAllText(uxmlFullPath);
            var styleLine = "  <Style src=\"" + Path.GetFileName(ussPath) + "\" />\n";
            if (!content.Contains(styleLine.Trim()))
            {
                var insertIdx = content.LastIndexOf("</ui:UXML>");
                if (insertIdx < 0) insertIdx = content.LastIndexOf("<");
                if (insertIdx >= 0) content = content.Insert(insertIdx, styleLine);
                else content += styleLine;
            }
            ctx.CommitEffect();
            File.WriteAllText(uxmlFullPath, content, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            var observedContent = File.ReadAllText(uxmlFullPath, System.Text.Encoding.UTF8);
            var observedDocument = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var observedStyleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
            var expectedSource = Path.GetFileName(AssetDatabase.GetAssetPath(requestedStyleSheet));
            var parsed = System.Xml.Linq.XDocument.Parse(observedContent);
            var linked = false;
            foreach (var element in parsed.Descendants())
            {
                var source = element.Attribute("src");
                if (element.Name.LocalName == "Style" && source != null && source.Value == expectedSource) { linked = true; break; }
            }
            if (observedDocument == null || observedStyleSheet == null || !linked) throw new UnityCliBridge.BridgeCommandException("E_UI_STYLESHEET_READBACK_FAILED", "Stylesheet link could not be verified: " + uxmlPath, "Check Unity Console for UXML import errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = false, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.link-stylesheet", Path = uxmlPath, Uss = ussPath } } });
        }
    }
}
#endif
