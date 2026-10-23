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
            Description = "Read the full bounded record of one Hook Definition: exact target, activation, priority, capture configuration, callback hashes, hit and bypass counters, last fault with staleFaults, droppedSupplements, event summary with history gaps, state bytes, adapter and artifact counters, and foreign patch owners on that target. bypassCount counts suppressed re-entries, which produce no frame and no hit event, so it is the only way to tell a suppressed call from one that never happened. A listener summary reports its session owner, trigger, finite hit budget, consumed and filtered hits, thread and instance filters, and every declared condition. Source text is returned only when sourceSlot (prefix or postfix) is passed, at most 32768 UTF-8 bytes per page, continued by sourceOffset while truncated=true. A definition owned by another Editor session cannot be read here. Pure read: it never recovers, recompiles, reattaches, cleans or mutates the definition and its events.", Notes = DebugDomainNotes.HookManage,
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
