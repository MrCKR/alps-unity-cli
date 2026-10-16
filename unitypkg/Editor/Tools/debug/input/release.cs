#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.input.release — 释放本 owner 在指定 session 的输入：清除按下状态并结束设备生命周期。
    /// 只有实际确认释放才报 released，否则报 pending 与精确原因。
    /// </summary>
    public class DebugInputReleaseTool : IUnityCliTool<DebugInputReleaseRequest, DebugInputReleaseResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.input.release",
            Summary = "Release this owner's input for one session and report released only for what the device readback actually confirmed.",
            Description = "Release the input this debug owner holds for one session: clear the pressed state, and for scope all also end the lifetime of the session-owned virtual devices by removing them. Inputs: sessionId (required; the session must exist, and a stopped session is answered from the current records instead of failing, because release is exactly what a stop already asked for); scope (required: all releases every device and record of that session, keys releases only the key device records, pointer releases only the pointer device records). Release honesty is the whole point of this call: a record is reported released only when the submitted clear was confirmed by reading the device back, or when the device is provably absent from the backend's device list so that no hold can survive. Everything else is reported pending with the exact reason, including a state that was submitted but is not yet applied by the device, an unreadable device list, a refused device removal, and a call that cannot confirm anything because it does not run on the Unity main thread. A pending result is completed by the central scheduler tick on the Unity main thread, and DeferredTo names that path; calling this tool again simply re-reads and re-tries the confirmation. Ownership: only devices this owner created for that session are touched, so this call never resets a real device, never touches another owner's device and never removes a device it did not create. Output: sessionId, scope, releaseState (released only when every targeted record is confirmed, otherwise pending), releasedAtMonotonicMs, releasedThreadId, confirmedCount, pendingCount, pendingReasons, deferredTo, the per-record holds with their state, end boundary, releaseReason and releaseConfirmed, the devices with their state and removalReason, and the current input budget. Effects: clearing state, removing this owner's virtual devices and publishing the release facts as trigger-source events are recorded as side effects. Budget: " + DebugLimits.InputSummary() + " Rejections: E_DEBUG_SESSION_NOT_FOUND for an unknown sessionId and E_SCHEMA_INVALID for a missing sessionId or a scope outside the declared contract. A backend that is not available is not an error here: it is reported as pending with reason backend-unavailable, because this tool never claims a release it cannot confirm and never installs anything.",
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
                    Description = "Release every device and input record this owner holds for one session.",
                    Input = new DebugInputReleaseRequest { SessionId = "dbg-1", Scope = "all" }
                },
                new ToolExample
                {
                    Description = "Release only the key device records of that session.",
                    Input = new DebugInputReleaseRequest { SessionId = "dbg-1", Scope = "keys" }
                }
            }
        };

        public ValueTask<DebugInputReleaseResult> ExecuteAsync(DebugInputReleaseRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // session 存在性与范围合法性在唯一 effect barrier 之前判定。
            var plan = DebugInputHost.PrepareRelease(request);
            ctx.CommitEffect();
            return new ValueTask<DebugInputReleaseResult>(DebugInputHost.ApplyRelease(plan));
        }
    }
}
#endif
