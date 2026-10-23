#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// debug.pause-inspect — 在暂停线程上读取有界调用栈、参数、实例/静态字段与结果。
    ///
    /// 读取请求被投递到现场 mailbox，由暂停线程自己执行；调用方只等待答案。
    /// 本 Tool 不解除暂停、不运行用户代码、不改变业务状态。
    /// </summary>
    public class DebugPauseInspectTool : IUnityCliTool<DebugPauseInspectRequest, DebugPauseInspectResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.pause-inspect",
            Summary = "Read a bounded stack, arguments, instance and static fields of one pause site on its own paused thread.",
            Description = "Read the paused invocation from the paused thread itself. Because the site is a real stopped hook hit, the stack and arguments are the real ones: the request enqueues a bounded read into the site mailbox and waits for the answer, so it never reads user objects from another thread. It can report a bounded real call stack (includeStack or stackDepth, capped by the frame limit), the arguments of the paused invocation, the instance identity with an optional bounded projection, the fields named in fields, and the result at a postfix site. It reports unavailable instead of guessing: locals of frames other than the paused invocation, the result at a prefix site because the original body has not returned, and an exception object, which ends the invocation through the original-exception lifecycle event; fields that cannot be read carry their own unavailableReason. Reading a site that already resumed, timed out or was released returns E_DEBUG_PAUSE_STALE and never revives it.", Notes = DebugDomainNotes.PauseRead,
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read the paused stack, arguments and instance.", Input = new DebugPauseInspectRequest { PauseId = "pause-1" } },
                new ToolExample { Description = "Read two named fields without the stack.", Input = new DebugPauseInspectRequest { PauseId = "pause-1", IncludeStack = false, Fields = new List<DebugPauseFieldRequest> { new DebugPauseFieldRequest { FieldName = "health" }, new DebugPauseFieldRequest { FieldName = "state" } } } }
            }
        };

        public async ValueTask<DebugPauseInspectResult> ExecuteAsync(DebugPauseInspectRequest request, UnityCliProjectToolContext ctx, CancellationToken ct)
        {
            // 纯读取：不跨效果屏障。现场操作只读取暂停线程上的真实帧。
            ctx.UseReadBudget(new UnityCliReadBudgetInput
            {
                MaxGetters = UnityCliRequestBudget.AbsoluteMaxGetters,
                MaxReflectionCalls = UnityCliRequestBudget.AbsoluteMaxReflectionCalls,
                MaxScannedRecords = UnityCliRequestBudget.AbsoluteMaxScannedRecords,
                MaxSerializedUtf8Bytes = UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes
            });
            var site = DebugPauseHost.FindSite(request == null ? null : request.PauseId);
            var waitMs = request != null && request.WaitMs != null ? request.WaitMs.Value : DebugLimits.PauseOperationTimeoutMs;
            var operation = DebugPauseHost.EnqueueInspect(site, request, Origin(ctx, "debug.pause-inspect"));
            await ctx.Wait.Until(() => operation.Completed, TimeSpan.FromMilliseconds(waitMs), ct);
            if (operation.Failure != null) throw operation.Failure;
            if (!operation.Completed)
                throw new BridgeCommandException(
                    "E_EXECUTION_TIMEOUT",
                    "The paused thread did not serve this read inside the bounded wait, so no value was returned.",
                    new Dictionary<string, object>
                    {
                        { "pauseId", site.PauseId },
                        { "waitMs", (long)waitMs },
                        { "executing", site.Executing == null ? string.Empty : site.Executing.Kind },
                        { "siteStillPaused", site.IsPaused }
                    },
                    new[]
                    {
                        "Run tools run debug.pause-list to read the current site state and its executing operation.",
                        "Submit the read again, or resume the site with debug.pause-resume when the on-site work is no longer needed."
                    });
            return operation.InspectResult;
        }

        private static string Origin(UnityCliProjectToolContext ctx, string tool)
        {
            var requestId = ctx == null || string.IsNullOrEmpty(ctx.RequestId) ? "unknown" : ctx.RequestId;
            return requestId + ":" + tool;
        }
    }
}
#endif
