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
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public string Path { get; set; } public string Uss { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.link-stylesheet", Summary = "Link a .uss stylesheet to a .uxml document by appending a Style element.", Description = "Append <Style src=\"...\" /> to the .uxml file. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Link stylesheet", Input = new Request { Path = "Assets/UI/Main.uxml", Uss = "Assets/UI/Main.uss" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var uxmlPath = SceneToolSupport.NormalizeAssetPath(r.Path);
            var ussPath = SceneToolSupport.NormalizeAssetPath(r.Uss ?? r.StyleSheet);
            if (string.IsNullOrEmpty(uxmlPath) || string.IsNullOrEmpty(ussPath)) throw new UnityCliBridge.BridgeCommandException("E_UI_PATH_REQUIRED", "ui.link-stylesheet requires --path (uxml) and --uss.", "Pass both uxml and uss paths.");
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.link-stylesheet", DryRun = true, Path = uxmlPath, Uss = ussPath, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.link-stylesheet", Path = uxmlPath, Uss = ussPath } } });
            var content = File.ReadAllText(SceneToolSupport.AssetFullPath(uxmlPath));
            var styleLine = "  <Style src=\"" + Path.GetFileName(ussPath) + "\" />\n";
            if (!content.Contains(styleLine.Trim()))
            {
                var insertIdx = content.LastIndexOf("</ui:UXML>");
                if (insertIdx < 0) insertIdx = content.LastIndexOf("<");
                if (insertIdx >= 0) content = content.Insert(insertIdx, styleLine);
                else content += styleLine;
            }
            File.WriteAllText(SceneToolSupport.AssetFullPath(uxmlPath), content, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "ui.link-stylesheet", DryRun = false, Path = uxmlPath, Uss = ussPath, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.link-stylesheet", Path = uxmlPath, Uss = ussPath } } });
        }
    }
}
#endif
