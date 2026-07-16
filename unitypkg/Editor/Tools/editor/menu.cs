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
    /// <summary>editor.menu — 执行 Unity Editor 菜单项。</summary>
    public class EditorMenuTool : IUnityCliTool<EditorMenuTool.Request, EditorMenuTool.Response>
    {
        public sealed class Request { public string Path { get; set; } }
        public sealed class Response { public MenuResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.menu", Summary = "Execute a Unity Editor menu item by path.", Description = "Call EditorApplication.ExecuteMenuItem(path). Pass exact menu path like File/Save.", Group = "editor", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "arbitrary-code-execution" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Save scene", Input = new Request { Path = "File/Save" } } } };
        public System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.menu");
            if (string.IsNullOrWhiteSpace(r.Path)) throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", "Editor menu path is required.", "Pass an exact Unity menu path like File/Save.");
            var startedAt = DateTime.UtcNow;
            var executed = EditorApplication.ExecuteMenuItem(r.Path);
            if (!executed) throw new UnityCliBridge.BridgeCommandException("E_MENU_NOT_FOUND", "Unity menu item was not found or could not be executed.", "Use the exact Unity Editor menu path.");
            var elapsedMs = (long)Math.Round((DateTime.UtcNow - startedAt).TotalMilliseconds);
            return new System.Threading.Tasks.ValueTask<Response>(new Response { Result = new MenuResult { Path = r.Path, Executed = true, ElapsedMs = elapsedMs }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.menu", Path = r.Path } } });
        }
    }
}
#endif
