#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.input-key — 在会话拥有的虚拟键设备上按下一个有显式结束边界的键集合，或显式释放这些键。
    /// 设备后端缺失时只报 unavailable 且不安装任何包。
    /// </summary>
    public class DebugInputKeyTool : IUnityCliTool<DebugInputKeyRequest, DebugInputDispatchResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.input-key",
            Summary = "Press keys on the session-owned virtual key device, each press carrying its own declared end boundary.",
            Description = "Submit key input on a virtual key device that belongs to the named debug session, and report the end boundary and the release state honestly. Inputs: sessionId (required, must be an open session); keys (required, one to the declared per-dispatch limit of exact key control names of the device backend, for example Space, W or LeftShift); endBoundary (required: hold-ms or until-release, with no default, because this tool never creates an unbounded hold); holdMs (required with hold-ms, within the declared millisecond limit; forbidden with until-release). Semantics: this tool presses the declared controls on the session's key device and returns the record it created. Releasing keys is its own Tool: call debug.input-release with scope keys, which reports the per-record release confirmation instead of hiding it inside this dispatch. Ownership and lifetime: the session owns at most one virtual key device, created on the first press and ended by debug.input-release with scope all, by session stop, or by Play exit; a press with the until-release boundary stays pressed until one of those happens, and a press with hold-ms is released when that boundary is reached, measured on the monotonic clock and advanced by the central scheduler tick on the Unity main thread. Release honesty: the release Tool reports released only for a record whose cleared state was actually confirmed by reading the device back; when the state was submitted but the device does not yet report it cleared, or when the readback itself is unavailable, the record is reported as pending with the exact reason, and the remaining work is completed by the central scheduler tick. Conflicts are refused before any effect: a key that this owner already holds under a different end boundary, a device with this owner's exact target name that another owner created, an unreadable device list, an exhausted input plan budget and a full record budget all fail before the effect barrier, so a refused call never pressed anything. This owner never resets, rewrites or takes over a real device or another owner's device. Effects: creating a session-owned virtual device and submitting its state are recorded as side effects, and every dispatch and release appends one trigger-source event (kind input-dispatch, execution point input-dispatch, source trigger) with the dispatch id, controls, end boundary and hold state, so debug.events-read can read the same fact and debug.run-begin can require it with source trigger and kind input-dispatch. A submitted device event never proves that the business read the input: prove acceptance with a hook capture, a program check, a value read or a sealed debug.run window. Output: dispatchId, sessionId, deviceId, deviceKind, deviceName, backend, apiIdentity, action, controls, endBoundary, holdMs, expiresAtMonotonicMs, state, releaseState (held while the press is outstanding), releaseReason, releaseConfirmed, the resolved pointer coordinate space fields (empty for keys), confirmedControls read live from the device at the end of this call, confirmReason, dispatchedAtMonotonicMs, dispatchedThreadId, executionPoint and the current input budget. This tool is only the optional device backend; it does not replace general business command injection. Budget: " + DebugLimits.InputSummary() + " Rejections: E_DEBUG_BINDING_UNSUPPORTED when no device backend is available, with status unavailable, the backend name, the reason, the missing API members and installedByThisOwner false; E_DEBUG_SESSION_NOT_FOUND for an unknown sessionId; E_DEBUG_SESSION_NOT_OPEN when the session is not open; E_DEBUG_RESOURCE_CONFLICT for a hold conflict or a device name another owner already uses; E_DEBUG_TRIGGER_BUDGET_EXHAUSTED when the shared planned injection and input budget is exhausted; E_DEBUG_LIMIT_EXCEEDED when the retained record budget has no released record left to evict; E_SCHEMA_INVALID for a key name or an end boundary outside the declared contract; and E_EXECUTION_TIMEOUT with effectMayBePartial true only when neither the submitted state nor the removal of a device created by that very call could be confirmed.",
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "virtual-input-device" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Press the jump key and let the declared boundary release it, using a sessionId read from the current Editor session.",
                    Input = new DebugInputKeyRequest
                    {
                        SessionId = "dbg-1",
                        Keys = new System.Collections.Generic.List<string> { "Space" },
                        EndBoundary = "hold-ms",
                        HoldMs = 250
                    }
                },
                new ToolExample
                {
                    Description = "Press a key that stays down until an explicit release.",
                    Input = new DebugInputKeyRequest
                    {
                        SessionId = "dbg-1",
                        Keys = new System.Collections.Generic.List<string> { "LeftShift" },
                        EndBoundary = "until-release"
                    }
                }
            }
        };

        public ValueTask<DebugInputDispatchResult> ExecuteAsync(DebugInputKeyRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 后端可用性、结束边界、冲突与预算全部在唯一 effect barrier 之前判定。
            var plan = DebugInputHost.PrepareKey(request);
            ctx.CommitEffect();
            return new ValueTask<DebugInputDispatchResult>(DebugInputHost.ApplyKey(plan));
        }
    }
}
#endif
