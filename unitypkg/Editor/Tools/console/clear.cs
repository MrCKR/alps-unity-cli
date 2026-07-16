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
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public bool Cleared { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "console.clear", Summary = "Clear the Unity Console log.", Description = "Clear all Console entries via LogEntries.Clear(). dryRun=true previews without clearing.", Group = "console", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Clear console", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var dryRun = ctx.DryRun;
            if (dryRun) return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "console.clear", DryRun = true, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "console.clear" } } });
            var logEntriesType = GameObjectToolSupport.FindType("UnityEditor.LogEntries");
            var clearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            clearMethod?.Invoke(null, null);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "console.clear", DryRun = false, Cleared = true, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "console.clear" } } });
        }
    }
}
#endif
