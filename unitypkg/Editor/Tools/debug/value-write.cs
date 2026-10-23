#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.value-write — 类型检查的结构化字段写入与条件恢复登记。
    ///
    /// 不是属性 setter，也不是方法调用器；所有校验与预算判定都在唯一 effect barrier 之前完成。
    /// </summary>
    public class DebugValueWriteTool : IUnityCliTool<DebugValueWriteRequest, DebugValueWriteResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.value-write",
            Summary = "Write one type-checked field path in the live Editor and register a conditional restore record.",
            Description = "Write one bounded field path in the live Editor. The root is exactly one of a static field, an exact Unity selector (instanceId, assetPath, guid, exact hierarchy path or component resolution) or a current managed object handle; fieldPath is the bounded list of field names and one-dimensional array indexes. Value is converted to the declared field type and must match it, otherwise nothing is written and the call fails with E_SCHEMA_INVALID; With guardByExpectedValue the current value is read first and compared without running user code: a mismatch returns ok:true with outcome guard-mismatch, applied false and no write, while a member that cannot be compared safely is refused with E_DEBUG_SCOPE_UNSUPPORTED. Writing a const or readonly field is refused with E_DEBUG_FIELD_NOT_WRITABLE and rule field-is-literal or field-is-init-only; a property is refused with property-getter-not-executed, a non-array index on an array with array-index-required, and a static field on a type that declares a static constructor with static-type-not-initialized. A target that changed runtime, domain generation or object identity returns E_DEBUG_TARGET_STALE.", Notes = DebugDomainNotes.ValueWriteFacts,
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
            // 结果 DTO 与 debug.value-read 一样会被框架在同一请求内有预算地多次序列化，但写入结果不含命名逻辑时钟注册表：
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
            var operation = DebugPauseHost.EnqueueValueWrite(site, request, Origin(ctx, "debug.value-write"));
            await ctx.Wait.Until(() => operation.Completed, TimeSpan.FromMilliseconds(DebugLimits.PauseOperationTimeoutMs), ct);
            if (operation.Failure != null) throw operation.Failure;
            if (!operation.Completed)
                throw new UnityCli.UnityCliBridge.BridgeCommandException(
                    "E_EXECUTION_TIMEOUT",
                    "The paused thread did not serve this write inside the bounded wait, so the site is still held and no value was returned.",
                    new System.Collections.Generic.Dictionary<string, object> { { "pauseId", site.PauseId }, { "siteStillPaused", site.IsPaused }, { "effectStarted", true } },
                    new[] { "Run tools run debug.pause-list to read the current site state and its executing operation.", "Run tools run debug.pause-resume when the on-site work is no longer needed." });
            return operation.ValueWriteResult;
        }

        private static string Origin(UnityCliProjectToolContext ctx, string tool)
        {
            return (ctx == null || string.IsNullOrEmpty(ctx.RequestId) ? "unknown" : ctx.RequestId) + ":" + tool;
        }
    }
}
#endif
