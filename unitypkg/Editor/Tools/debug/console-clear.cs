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
    /// <summary>debug.console-clear — 清空 Console。</summary>
    public class ConsoleClearTool : IUnityCliTool<ConsoleClearTool.Request, ConsoleClearTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public bool DryRun { get; set; } public bool Cleared { get; set; } public long PreviousCount { get; set; } public long RemainingCount { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "debug.console-clear", Summary = "Clear every Unity Console entry and return count readback.", Description = "Clear all Console entries in Edit or Play mode. dryRun previews without clearing. A real call reads the count before the effect, invokes LogEntries.Clear once, and succeeds only after fresh GetCount readback reaches zero.", Group = "debug",DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "editor-console-state" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Preview clearing the Console", Input = new Request { }, Execution = new ToolExampleExecution { DryRun = true } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = true, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "debug.console-clear" } } });
            var logEntriesType = GameObjectToolSupport.FindType("UnityEditor.LogEntries");
            if (logEntriesType == null)
                throw new UnityCliBridge.BridgeCommandException("E_CONSOLE_CLEAR_UNSUPPORTED", "UnityEditor.LogEntries is unavailable.", "Use a Unity Editor version that exposes Console log entries.");
            var clearMethod = logEntriesType.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getCountMethod = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (clearMethod == null || getCountMethod == null)
                throw new UnityCliBridge.BridgeCommandException("E_CONSOLE_CLEAR_UNSUPPORTED", "UnityEditor.LogEntries.Clear or GetCount is unavailable.", "Use a Unity Editor version that exposes Console log entry controls.");
            var previousCount = Convert.ToInt32(getCountMethod.Invoke(null, null));
            ctx.CommitEffect();
            clearMethod.Invoke(null, null);
            var remainingCount = Convert.ToInt32(getCountMethod.Invoke(null, null));
            if (remainingCount != 0)
                throw new UnityCliBridge.BridgeCommandException("E_CONSOLE_CLEAR_STATE_MISMATCH", "Unity Console still contains " + remainingCount + " entries after clearing.", "Inspect the Unity Console and retry.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = false, Cleared = true, PreviousCount = previousCount, RemainingCount = remainingCount, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "debug.console-clear" } } });
        }
    }
}
#endif
