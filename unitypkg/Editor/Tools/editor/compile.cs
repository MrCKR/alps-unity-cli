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
    /// <summary>editor.compile — 请求脚本编译。</summary>
    public class EditorCompileTool : IUnityCliTool<EditorCompileTool.Request, EditorCompileTool.Result>
    {
        public sealed class Request { public bool? Refresh { get; set; } public bool? WaitReady { get; set; } }
        public sealed class Result { public CompileResult Compile { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.compile", Summary = "Request script compilation in the live Unity Editor.", Description = "Request CompilationPipeline.RequestScriptCompilation(). refresh=true (default) refreshes assets first. waitReady blocks until idle.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Compile scripts", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (BuildPipeline.isBuildingPlayer) throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
            var refresh = r.Refresh ?? true;
            var waitReady = r.WaitReady ?? false;
            var wasCompiling = EditorApplication.isCompiling;
            var wasUpdating = EditorApplication.isUpdating;
            var alreadyBusy = wasCompiling || wasUpdating;
            var skippedReason = alreadyBusy ? (wasCompiling ? "already-compiling" : "already-importing") : null;
            var dryRun = ctx.DryRun;
            if (dryRun)
            {
                var effects = new List<EditorPlannedEffect>();
                if (!alreadyBusy) { if (refresh) effects.Add(new EditorPlannedEffect { Type = "asset.refresh" }); effects.Add(new EditorPlannedEffect { Type = "script-compile-request" }); }
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Compile = new CompileResult { DryRun = true, Refresh = refresh, WaitReady = waitReady, WouldRequest = !alreadyBusy, SkippedReason = skippedReason }, PlannedEffects = effects });
            }
            if (!alreadyBusy) { if (refresh) AssetDatabase.Refresh(); CompilationPipeline.RequestScriptCompilation(); }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Compile = new CompileResult { Requested = !alreadyBusy, Refresh = refresh, WaitReady = waitReady, SkippedReason = skippedReason }, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "script-compile-request" } } });
        }
    }
}
#endif
