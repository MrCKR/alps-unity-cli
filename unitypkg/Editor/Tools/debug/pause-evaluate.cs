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
            Description = "Execute C# source or a declared program entry on the paused thread at exactly one pause site, then report the value it returned and the interventions it actually performed. Exactly one source must be given: sourceText (inline C# whose body is the place of the wrapped Run(string argsJson)), with args as the JSON handed to that Run method, or programEntry naming an installed program id plus entry that is already attached to this exact target and phase. Compilation uses the single shared compiler owner and the same reference snapshot, artifact budget and temporary directory as hook callbacks and debug.code-execute, so this is not a second compiler; while the main thread is paused the compile lane is advanced by the pause service itself and by nothing else. Interventions are staged on the paused invocation and applied at its boundary: the code can read the instance and arguments through UnityCliPauseContext.Current, set arguments (honoured at a prefix site), skip the original, write the result (written back at the applicable boundary), write fields and emit events. The response lists what actually happened (argument-write, skip-original, result-write, field-writes, emitted) so an intervention is never inferred from the fact that the call returned; argument writes at a postfix site are reported as ignored because the original method already received its arguments. Execution and honesty: the operation runs on the paused thread, so if this synchronous code never returns, the site stays paused and this Tool reports executionState=pending with completed=false instead of claiming the thread was resumed; resume releases the wait, it does not preempt running code. returnValueTruncated reports whether the returned value was cut by the shared projection bounds, so a caller can always distinguish a complete value from a bounded fragment (the value itself is then only that fragment). Only the paused thread is guaranteed stopped, other threads and object mutation continue. Process stdout is not captured or redirected, so return a value instead of printing. Mailbox rules: one operation executes at a time, at most the declared number of requests may wait, and the paused thread does not enter a nested pause while serving this request; the request origin is recorded on any hit that is suppressed for that reason. A stale pauseId returns E_DEBUG_PAUSE_STALE and the failure paths (compiler busy, compile failure, unbound entry, artifact budget) all leave the site paused and inspectable. Execution domain: this Editor session, Edit and Editor Play. Target/thread: executes on the paused thread named by pauseId. Behaviour: cross-frame from the caller's point of view, synchronous on the paused thread. Return value projection: depth, item count and per-string bytes are bounded by the same owner as value reads, and returnValueTruncated states whether that bound was hit; a code source uses the identical wrapper documented by debug.code-execute. Cancellation: the request deadline and cancellation token end the wait; they do not cancel the synchronous user code. Reload and recovery: the site and the compiled artifact belong to this Editor domain generation; a Domain Reload, Play exit or Editor restart ends the pause site, and loaded artifacts are only reclaimed by a Domain Reload. Risks: this runs caller-supplied code inside the paused business call, can deadlock if that code waits on something the paused thread owns, and a main-thread pause stops the Editor UI and normal ticks while the operation runs. Side effects: it pauses and releases one thread, executes caller code and can modify the paused invocation's arguments, result and reachable object state. " + DebugLimits.PauseSummary(),
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
