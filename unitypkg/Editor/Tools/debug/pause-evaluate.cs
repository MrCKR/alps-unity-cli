#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// debug.pause-evaluate — 在指定暂停现场执行显式调试代码或已安装 program 入口。
    ///
    /// 现场操作串行执行：一次一个，resume 信号不排队。
    /// 若同步用户代码不返回，本 Tool 只报告 pending，绝不声称现场线程已被强行恢复。
    /// </summary>
    public class DebugPauseEvaluateTool : IUnityCliTool<DebugPauseEvaluateRequest, DebugPauseEvaluateResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.pause-evaluate",
            Summary = "Run explicit debug code or an installed program entry on one pause site and report the interventions it made.",
            Description = "Execute C# source or a declared program entry on the paused thread at exactly one pause site, then report the returned value and the interventions actually performed. Exactly one source is required: sourceText (inline C# wrapped as the body of Run(string argsJson)) with args as its JSON, or programEntry naming an installed program id plus entry already attached to this exact target and phase. Compilation uses the shared compiler owner, reference snapshot and artifact budget of hook callbacks and debug.code-execute, and while the main thread is paused only the pause service advances that lane. Interventions are staged on the paused invocation and applied at its boundary: read instance and arguments through UnityCliPauseContext.Current, set arguments (honoured at a prefix site), skip the original, write the result, write fields and emit events. The response lists what actually happened (argument-write, skip-original, result-write, field-writes, emitted). If this synchronous code never returns, the site stays paused and the Tool reports executionState=pending with completed=false instead of claiming a resume. A stale pauseId returns E_DEBUG_PAUSE_STALE.", Notes = DebugDomainNotes.PauseWrite,
            Group = "debug",
            DestructiveHint = true,
            SideEffects = new[] { "debug-pause", "user-code-execution" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 120000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Read the paused instance and skip the original method.",
                    Input = new DebugPauseEvaluateRequest { PauseId = "pause-1", SourceText = "var invocation = UnityCliPauseContext.Current; invocation.SkipOriginal(42); return invocation.Instance;" }
                },
                new ToolExample
                {
                    Description = "Run an installed program entry at this site.",
                    Input = new DebugPauseEvaluateRequest { PauseId = "pause-1", ProgramEntry = new DebugPauseProgramEntrySpec { ProgramId = "prog-1", Entry = "onHit" } }
                }
            }
        };

        public async ValueTask<DebugPauseEvaluateResult> ExecuteAsync(DebugPauseEvaluateRequest request, UnityCliProjectToolContext ctx, CancellationToken ct)
        {
            // 形状与现场存在性在 effect 之前判定：拒绝时不产生任何现场改动。
            var site = DebugPauseHost.FindSite(request == null ? null : request.PauseId);
            var hasCode = request != null && (!string.IsNullOrWhiteSpace(request.SourceText) || !string.IsNullOrWhiteSpace(request.Code));
            var hasEntry = request != null && request.ProgramEntry != null && !string.IsNullOrWhiteSpace(request.ProgramEntry.ProgramId);
            if (hasCode == hasEntry)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field evaluate requires exactly one of sourceText/code or programEntry.",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "path", "$.programEntry" },
                        { "hasCode", hasCode },
                        { "hasProgramEntry", hasEntry }
                    },
                    new[] { "Submit explicit debug code with sourceText, or name an installed program entry with programEntry." });
            var waitMs = request.WaitMs != null ? request.WaitMs.Value : DebugLimits.PauseOperationTimeoutMs;
            // 用户代码执行与现场干预是真实 effect：跨过效果屏障后才入队。
            ctx.CommitEffect();
            var operation = DebugPauseHost.EnqueueEvaluate(site, request, Origin(ctx));
            await ctx.Wait.Until(() => operation.Completed, TimeSpan.FromMilliseconds(waitMs), ct);
            if (operation.Completed)
            {
                if (operation.Failure != null) throw operation.Failure;
                return operation.EvaluateResult;
            }
            // 同步用户代码没有返回：如实报告 pending，绝不声称线程已被强行恢复。
            return DebugPauseHost.PendingEvaluateResult(site, operation, hasEntry);
        }

        private static string Origin(UnityCliProjectToolContext ctx)
        {
            var requestId = ctx == null || string.IsNullOrEmpty(ctx.RequestId) ? "unknown" : ctx.RequestId;
            return requestId + ":debug.pause-evaluate";
        }
    }
}
#endif
