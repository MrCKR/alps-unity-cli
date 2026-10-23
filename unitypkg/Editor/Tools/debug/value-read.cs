#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.value-read — 精确根加有界字段路径的纯读取。
    ///
    /// 不执行属性 getter、不调用任意 ToString、不运行用户序列化器或枚举器，也不触发类型初始化；
    /// 无法证明安全时以稳定错误拒绝并路由到显式代码执行。
    /// </summary>
    public class DebugValueReadTool : IUnityCliTool<DebugValueReadRequest, DebugValueReadResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.value-read",
            Summary = "Read one exact field path in the live Editor and report its value, identity, thread and clock provenance.",
            Description = "Read one bounded field path in the live Editor. The root is exactly one of: a static field (rootKind static-field with typeName and fieldName), an exact Unity selector (rootKind selector, reusing the same instanceId / assetPath / guid / exact hierarchy path / component resolution as the other object tools, so an ambiguous or missing selector fails instead of guessing), or a current managed object handle (rootKind handle). fieldPath is a bounded list of field names and one-dimensional array indexes, at most " + DebugLimits.MaxValuePathLength + " segments, resolved by reflection on the declared type of each previous segment. A pure read never runs user code: it does not execute property getters, does not call arbitrary ToString, does not run user serializers or enumerators, and does not enumerate containers that are not framework-owned. Requests that cannot be proven safe are refused with E_DEBUG_SCOPE_UNSUPPORTED and a rule naming the exact boundary: a property member in the path (property-getter-not-executed), a non-array index or member name on an array (array-index-required), or a static field on a type that declares a static constructor (static-type-not-initialized, because a pure read cannot prove the type initialiser already ran). The route for such a read is explicit code execution with tools run debug.code-execute. Unsupported or unavailable members are reported as E_DEBUG_SCOPE_UNSUPPORTED with rule member-not-a-field, a missing type as E_TYPE_NOT_FOUND, and a selector that matches nothing or matches more than one object with the existing E_OBJECTREF_* codes. A handle is bound to the runtime and domain generation observed when it was created: after a runtime change, a Domain Reload, collection or Unity destruction it reports E_DEBUG_TARGET_STALE with its state and reason, and a stale handle is never reported as a null field value, while a genuinely null field reports valueIsNull true with nullReason actual-null and an intermediate null reports nullAtPath. The result adds provenance: rootKind, rootIdentity, rootTypeName, path, declaringTypeName, the declared value type, the projection category (bounded-projection, identity-only when a reference type outside the safe set is reported by type only, or unity-object-identity), threadId, mainThread, and the four clock provenance records. Clock kinds are monotonic elapsed time, the debug domain Editor tick, the real Unity frame, and a named project logical clock published by naming logicalClockName on a numeric field; a worker thread never reads the Unity frame and instead reports the last frame the main thread published, so isUnityFrameCount and the source text always state what the reading actually is. Sequence and ordinary Console or reflection queries do not need a session; pass sessionId only when createHandle asks for a weak handle, which is recorded in this Editor session's in-memory handle table and released by debug.session-stop. Handles are bounded to " + DebugLimits.MaxHandlesPerSession + " per session and " + DebugLimits.MaxHandlesGlobal + " in the process, and exceeding that returns E_DEBUG_LIMIT_EXCEEDED with the current occupancy. Execution domain: the current Editor session, Edit and Play. Target/thread: reads managed state and Unity object identity on the calling (main) thread; a non-main caller still gets monotonic and cached-frame provenance instead of a main-thread API call. Behaviour: one synchronous bounded read; it registers no update callback and waits for nothing. Consistency: unsynchronized. Cancellation: the request deadline and cancellation token bound the read. Reload and recovery: nothing is persisted, handles invalidate on a Domain Reload or runtime change, and named logical clocks from an earlier domain generation are labelled with that generation. Pause routing: naming pauseId executes the read on that pause site's own paused thread instead of the normal path, which is how this read stays reachable while a main-thread pause breakpoint holds the Editor; omitting pauseId keeps the ordinary path and an unknown or already released pauseId returns E_DEBUG_PAUSE_STALE. " + DebugLimits.ValueSummary(),
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read a private field of one component on an exact hierarchy path.", Input = new DebugValueReadRequest { RootKind = "selector", Path = "Player", Component = "PlayerController", FieldPath = new System.Collections.Generic.List<string> { "_health" } } },
                new ToolExample { Description = "Read a field from a handle taken in an earlier read of the same runtime.", Input = new DebugValueReadRequest { RootKind = "handle", HandleId = "h-1", FieldPath = new System.Collections.Generic.List<string> { "value" } } }
            }
        };

        public async ValueTask<DebugValueReadResult> ExecuteAsync(DebugValueReadRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 纯读取：不提交 effect、不写存储、不注册回调。
            // 结果 DTO 会被框架在同一请求内多次有预算地读取，因此这里声明由 DebugLimits 单点推导的读取预算，
            // 而不是依赖默认上限；推导值保证一次读取（含命名时钟快照与句柄视图）始终落在默认请求预算内。
            var budget = ctx.UseReadBudget(new UnityCliReadBudgetInput { MaxGetters = DebugLimits.ValueReadGetters() });
            if (request != null && !string.IsNullOrWhiteSpace(request.PauseId))
            {
                // 命名了 pauseId：读取路由到该现场的线程，与 debug.pause-inspect 共用同一有界现场 mailbox 与 pending 语义。
                var site = DebugPauseHost.FindSite(request.PauseId);
                var operation = DebugPauseHost.EnqueueValueRead(site, request, Origin(ctx, "debug.value-read"));
                await ctx.Wait.Until(() => operation.Completed, TimeSpan.FromMilliseconds(DebugLimits.PauseOperationTimeoutMs), ct);
                if (operation.Failure != null) throw operation.Failure;
                if (!operation.Completed)
                    throw new UnityCli.UnityCliBridge.BridgeCommandException(
                        "E_EXECUTION_TIMEOUT",
                        "The paused thread did not serve this read inside the bounded wait, so no value was returned and the site is still held.",
                        new System.Collections.Generic.Dictionary<string, object> { { "pauseId", site.PauseId }, { "siteStillPaused", site.IsPaused } },
                        new[] { "Run tools run debug.pause-list to read the current site state and its executing operation.", "Run tools run debug.pause-resume when the on-site work is no longer needed." });
                return operation.ValueReadResult;
            }
            return DebugValueSupport.Read(request, budget);
        }

        private static string Origin(UnityCliProjectToolContext ctx, string tool)
        {
            return (ctx == null || string.IsNullOrEmpty(ctx.RequestId) ? "unknown" : ctx.RequestId) + ":" + tool;
        }
    }
}
#endif
