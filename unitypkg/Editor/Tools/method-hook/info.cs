#if UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// method-hook.info — 只读精查一个定义；默认不返回源码，按 slot 分页读取回调源码。
    /// </summary>
    public class MethodHookInfoTool : IUnityCliTool<MethodHookInfoRequest, MethodHookInfoResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "method-hook.info",
            Summary = "Read one hook definition's target, revision, state, faults and paged callback source.",
            Description = "Read the full bounded record of one Hook Definition: exact target summary, activation, priority, capture configuration, callback hashes, hit and bypass counters, last fault (code, message, revision, invocation id, owner), event summary with history gaps, State bytes, adapter and compiled-artifact counters, and a bounded summary of foreign patch owners coexisting on that target. Source text is not returned by default; pass sourceSlot (prefix or postfix) and an optional sourceOffset to read at most " + MethodHookLimits.MaxInfoSourcePageBytes + " UTF-8 bytes per page, and continue with the next offset while the returned page reports truncated=true. A definition that belongs to another Editor session cannot be read through this session. This is a pure read: it never recovers, recompiles, reattaches or cleans state, and it never mutates the definition or its events. Side effects: none.",
            Group = "method-hook",
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
