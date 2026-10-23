#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.hook-info — 只读精查一个定义；默认不返回源码，按 slot 分页读取回调源码。
    /// </summary>
    public class MethodHookInfoTool : IUnityCliTool<MethodHookInfoRequest, MethodHookInfoResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.hook-info",
            Summary = "Read one hook definition's target, revision, state, faults and paged callback source for hits on any supported managed thread.",
            Description = "Read the full bounded record of one Hook Definition: exact target summary, activation, priority, capture configuration, callback hashes, hit and bypass counters, last fault (code, message, revision, invocation id, owner), staleFaults (callback faults recorded on a revision that was already superseded, which never change the current revision), droppedSupplements, event summary with history gaps, State bytes, adapter and compiled-artifact counters, and a bounded summary of foreign patch owners coexisting on that target. bypassCount counts callbacks that re-entered their own hooked target and were suppressed: a suppressed re-entry produces no frame and no hit event, so bypassCount is the only way to tell 'suppressed' from 'never happened'. When the definition declares a listener, the listener summary reports its session owner, trigger, finite hit budget, consumed hits and filtered hits, the thread and instance filters, and every declared condition; filtered hits are calls that reached the hook but were rejected by the listener, so they are reported separately from bypassCount and from real hits. Program-owned bindings are reported with the same fields; their revision can only be changed through debug.program-replace. Hits may arrive on any supported managed thread, and events report the thread that made the original call. Source text is not returned by default; pass sourceSlot (prefix or postfix) and an optional sourceOffset to read at most " + DebugLimits.MaxInfoSourcePageBytes + " UTF-8 bytes per page, and continue with the next offset while the returned page reports truncated=true. A definition that belongs to another Editor session cannot be read through this session. This is a pure read: it never recovers, recompiles, reattaches or cleans state, and it never mutates the definition or its events. Side effects: none.",
            Group = "debug",
            DestructiveHint = false,
            SupportsDryRun = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Read one definition without source text.", Input = new MethodHookInfoRequest { HookId = "hook-0000000000000000" } },
                new ToolExample { Description = "Read the first page of the prefix callback source.", Input = new MethodHookInfoRequest { HookId = "hook-0000000000000000", SourceSlot = "prefix", SourceOffset = 0 } }
            }
        };

        public ValueTask<MethodHookInfoResult> ExecuteAsync(MethodHookInfoRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            return new ValueTask<MethodHookInfoResult>(MethodHookHost.Info(request));
        }
    }
}
#endif
