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
    /// <summary>console.clear — 清空 Console。</summary>
    public class ConsoleClearTool : IUnityCliTool<ConsoleClearTool.Request, ConsoleClearTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool DryRun { get; set; } public bool Cleared { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "console.clear", Summary = "Clear the Unity Console log.", Description = "Clear all Console entries via LogEntries.Clear(). dryRun=true previews without clearing.", Group = "console",DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "editor-console-state" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Clear console", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = true, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "console.clear" } } });
            var logEntriesType = GameObjectToolSupport.FindType("UnityEditor.LogEntries");
            if (logEntriesType == null)
                throw new UnityCliBridge.BridgeCommandException("E_CONSOLE_CLEAR_UNSUPPORTED", "UnityEditor.LogEntries is unavailable.", "Use a Unity Editor version that exposes Console log entries.");
            var clearMethod = logEntriesType.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (clearMethod == null || getCountMethod == null)
                throw new UnityCliBridge.BridgeCommandException("E_CONSOLE_CLEAR_UNSUPPORTED", "UnityEditor.LogEntries.Clear or GetCount is unavailable.", "Use a Unity Editor version that exposes Console log entry controls.");
            ctx.CommitEffect();
            clearMethod.Invoke(null, null);
            var count = Convert.ToInt32(getCountMethod.Invoke(null, null));
            if (count != 0)
                throw new UnityCliBridge.BridgeCommandException("E_CONSOLE_CLEAR_STATE_MISMATCH", "Unity Console still contains " + count + " entries after clearing.", "Inspect the Unity Console and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = false, Cleared = true, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "console.clear" } } });
        }
    }
}
#endif
