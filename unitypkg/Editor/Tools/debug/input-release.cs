#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.input-release — 释放本 owner 在指定 session 的输入：清除按下状态并结束设备生命周期。
    /// 只有实际确认释放才报 released，否则报 pending 与精确原因。
    /// </summary>
    public class DebugInputReleaseTool : IUnityCliTool<DebugInputReleaseRequest, DebugInputReleaseResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.input-release",
            Summary = "Release this owner's input for one session and report released only for what the device readback actually confirmed.",
            Description = "Release the input this debug owner holds for one session: clear the pressed state, and for scope all also end the session-owned virtual devices by removing them. scope is required: all releases every device and record of that session, keys releases only key records, pointer releases only pointer records. A stopped session is answered from the current records instead of failing. A record is reported released only when the submitted clear was confirmed by reading the device back, or when the device is provably absent from the backend device list; everything else is pending with the exact reason and is completed by the central scheduler tick, which deferredTo names. Calling again re-reads and re-tries the confirmation. Only devices this owner created for that session are touched. Output includes sessionId, scope, releaseState, confirmedCount, pendingCount, pendingReasons, per-record holds and devices with removalReason. An unavailable backend is reported as pending with reason backend-unavailable, never as an error. Rejections: E_DEBUG_SESSION_NOT_FOUND, E_SCHEMA_INVALID.", Notes = DebugDomainNotes.InputDevice,
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
