#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCli
{
    /// <summary>editor.active-tool.set — 设置 Editor 工具(Move/Rotate/Scale 等)。</summary>
    public class EditorActiveToolSetTool : IUnityCliTool<EditorActiveToolSetTool.Request, EditorActiveToolSetTool.Result>
    {
        public sealed class Request { public string Tool { get; set; } }
        public sealed class Result { public string Command { get; set; } public string Tool { get; set; } public string EditorTool { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.active-tool.set", Summary = "Set the active Unity Editor tool (Move, Rotate, Scale, etc.).", Description = "Set Tools.current. Accepts View, Move, Rotate, Scale, Rect, Transform.", Group = "editor", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set Move tool", Input = new Request { Tool = "Move" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(r.Tool)) throw new UnityCliBridge.BridgeCommandException("E_TOOL_REQUIRED", "editor.active-tool.set requires --tool.", "Pass View, Move, Rotate, Scale, Rect, Transform.");
            Tool editorTool;
            switch (r.Tool.ToLowerInvariant()) { case "view": case "pan": editorTool = Tool.View; break; case "move": case "translate": editorTool = Tool.Move; break; case "rotate": case "rotation": editorTool = Tool.Rotate; break; case "scale": editorTool = Tool.Scale; break; case "rect": editorTool = Tool.Rect; break; case "transform": editorTool = Tool.Transform; break; default: throw new UnityCliBridge.BridgeCommandException("E_TOOL_UNKNOWN", "Unknown editor tool: " + r.Tool, "Pass View, Move, Rotate, Scale, Rect, or Transform."); }
            Tools.current = editorTool;
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "editor.active-tool.set", Tool = r.Tool, EditorTool = editorTool.ToString() });
        }
    }
}
#endif
