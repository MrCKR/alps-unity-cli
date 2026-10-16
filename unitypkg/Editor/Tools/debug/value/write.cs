#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.value.write — 类型检查的结构化字段写入与条件恢复登记。
    ///
    /// 不是属性 setter，也不是方法调用器；所有校验与预算判定都在唯一 effect barrier 之前完成。
    /// </summary>
    public class DebugValueWriteTool : IUnityCliTool<DebugValueWriteRequest, DebugValueWriteResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.value.write",
            Summary = "Write one type-checked field path in the live Editor and register a conditional restore record.",
            Description = "Write one bounded field path in the live Editor as a structured intervention. The root is exactly one of a static field, an exact Unity selector (the same instanceId / assetPath / guid / exact hierarchy path / component resolution as the other object tools) or a current managed object handle; fieldPath is the bounded list of field names and one-dimensional array indexes described by tools describe debug.value.read, and this tool is not a property setter and not a method invoker. Value is converted to the declared field type and must match it, otherwise nothing is written and the call fails with E_SCHEMA_INVALID naming the expected and actual type; an oversized JSON value fails with E_DEBUG_LIMIT_EXCEEDED before any effect. When guardByExpectedValue is true the current value is read first and compared with expectedValue using only members that can be compared without running user code: a mismatch returns ok:true with outcome guard-mismatch, applied false, and no field written, while a member type that cannot be compared safely is refused with E_DEBUG_SCOPE_UNSUPPORTED so the caller can use tools run debug.code.execute. The expectedValue guard and every other pre-visible cause are decided before the single effect barrier, so a mismatch is always reported as a guard result and never as post-effect uncertainty. Writing a const (literal) or readonly (init-only) field is refused before the effect barrier with E_DEBUG_FIELD_NOT_WRITABLE and rule field-is-literal or field-is-init-only, because such a field is not mutable state and no later code execution can change the outcome. Writing a property is refused with rule property-getter-not-executed, a non-array index on an array with array-index-required, and a static field on a type that declares a static constructor with static-type-not-initialized, because a structured write must not trigger type initialisation. When an intermediate path member is a value type the write goes through an explicit boxed copy chain and is written back into its parent, which the result reports as structWriteBack instead of silently losing the update. On success the tool registers a conditional restore record (original value, target identity and write time) with the session named by sessionId, so debug.session.stop can restore the field later: the restore happens only while the target is still valid and the current value still equals the value this session wrote, and any other write is reported as a conflict instead of being overwritten. The record keeps the resolved target identity for the lifetime of the session, so it is itself a recorded intervention owned by this session. The effect category is structured-intervention: one field changes, this is not an automatic rollback, it does not undo other writers, and an explicit code execution performed by the caller is never rolled back. createHandle accepts none, weak or strong: a strong handle keeps the target alive while it exists, so it is recorded as an intervention that changes object lifetime, and both handle modes are bounded to " + DebugLimits.MaxHandlesPerSession + " per session and " + DebugLimits.MaxHandlesGlobal + " in the process with E_DEBUG_LIMIT_EXCEEDED when exhausted; handles and restore records belong to an open session, and a stopping or stopped session is refused with E_DEBUG_CLEANUP_PENDING. A handle or field target that changed runtime, domain generation or object identity is reported with E_DEBUG_TARGET_STALE and its actual state instead of being rebound. Execution domain: the current Editor session, Edit and Play. Target/thread: writes managed field state on the calling (main) thread through reflection. Behaviour: one synchronous bounded write after the single effect barrier; it registers no update callback and waits for nothing. Cancellation: the request deadline and cancellation token bound the call, and a request that is cancelled or disconnects after the effect barrier may leave the declared field written, so the field must be re-read and the restored state verified before retrying; a failure that is only discovered after the barrier is reported as E_EXECUTION_TIMEOUT with effectMayBePartial true instead of as a refusal. Reload and recovery: restore records and handles are runtime state, so a Domain Reload or Play exit invalidates them and the field keeps whatever value it had. Pause routing: naming pauseId executes the write on that pause site's own paused thread instead of the normal path, which is how this write stays reachable while a main-thread pause breakpoint holds the Editor; omitting pauseId keeps the ordinary path and an unknown or already released pauseId returns E_DEBUG_PAUSE_STALE. Side effects: runtime-object-modify. " + DebugLimits.ValueSummary(),
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "runtime-object-modify" },
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Write one private field of a component on an exact hierarchy path with a conditional restore record.", Input = new DebugValueWriteRequest { RootKind = "selector", Path = "Player", Component = "PlayerController", FieldPath = new System.Collections.Generic.List<string> { "_health" }, Value = 100, SessionId = "dbg-1" } },
                new ToolExample { Description = "Write one field only when it still equals the expected value.", Input = new DebugValueWriteRequest { RootKind = "handle", HandleId = "h-1", FieldPath = new System.Collections.Generic.List<string> { "state", "enabled" }, Value = true, GuardByExpectedValue = true, ExpectedValue = false, SessionId = "dbg-1" } }
            }
        };

        public async ValueTask<DebugValueWriteResult> ExecuteAsync(DebugValueWriteRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 全部输入、目标、可写性、类型与前置条件校验都在 barrier 之前完成；barrier 紧邻唯一的结构化写入。
            // 结果 DTO 与 debug.value.read 一样会被框架在同一请求内有预算地多次序列化，但写入结果不含命名逻辑时钟注册表：
            // 其最坏形态（DebugLimits.ValueWriteGetters()）远低于默认请求预算，因此这里使用默认账本而不收紧它。
            if (request != null && !string.IsNullOrWhiteSpace(request.PauseId)) return await ExecutePauseScopedAsync(request, ctx, ct);
            var plan = DebugValueSupport.PrepareWrite(request, ctx.UseReadBudget());
            ctx.CommitEffect();
            return DebugValueSupport.ApplyWrite(plan, request);
        }

        /// <summary>
        /// 命名了 pauseId 的写入：写入路由到该现场的线程，由现场操作自己拿读取预算。
        /// 拒绝仍发生在任何写入之前；用户同步代码不返回时只能报告 pending，绝不断言抢占。
        /// </summary>
        private static async ValueTask<DebugValueWriteResult> ExecutePauseScopedAsync(DebugValueWriteRequest request, UnityCliProjectToolContext ctx, CancellationToken ct)
        {
            var site = DebugPauseHost.FindSite(request.PauseId);
            ctx.UseReadBudget(new UnityCliReadBudgetInput { MaxGetters = DebugLimits.ValueReadGetters() });
            ctx.CommitEffect();
            var operation = DebugPauseHost.EnqueueValueWrite(site, request, Origin(ctx, "debug.value.write"));
            await ctx.Wait.Until(() => operation.Completed, TimeSpan.FromMilliseconds(DebugLimits.PauseOperationTimeoutMs), ct);
            if (operation.Failure != null) throw operation.Failure;
            if (!operation.Completed)
                throw new UnityCli.UnityCliBridge.BridgeCommandException(
                    "E_EXECUTION_TIMEOUT",
                    "The paused thread did not serve this write inside the bounded wait, so the site is still held and no value was returned.",
                    new System.Collections.Generic.Dictionary<string, object> { { "pauseId", site.PauseId }, { "siteStillPaused", site.IsPaused }, { "effectStarted", true } },
                    new[] { "Run tools run debug.pause.list to read the current site state and its executing operation.", "Run tools run debug.pause.resume when the on-site work is no longer needed." });
            return operation.ValueWriteResult;
        }

        private static string Origin(UnityCliProjectToolContext ctx, string tool)
        {
            return (ctx == null || string.IsNullOrEmpty(ctx.RequestId) ? "unknown" : ctx.RequestId) + ":" + tool;
        }
    }
}
#endif
