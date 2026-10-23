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
            Description = "Press exact key control names on the virtual key device of an open debug session and report the end boundary and release state honestly. sessionId is required and must be an open session; keys accept 1..8 control names such as Space, W or LeftShift; endBoundary is required (hold-ms or until-release) because this Tool never creates an unbounded hold; holdMs is required with hold-ms and forbidden with until-release. The press is submitted to the session's key device and the created record is returned; releasing is its own Tool, debug.input-release with scope keys, which reports the per-record confirmation. Output includes dispatchId, deviceId, controls, endBoundary, expiresAtMonotonicMs, state, releaseState, releaseConfirmed, confirmedControls read live from the device, and the current input budget. Refusals: E_DEBUG_BINDING_UNSUPPORTED, E_DEBUG_SESSION_NOT_FOUND, E_DEBUG_SESSION_NOT_OPEN, E_DEBUG_RESOURCE_CONFLICT, E_DEBUG_TRIGGER_BUDGET_EXHAUSTED, E_DEBUG_LIMIT_EXCEEDED or E_SCHEMA_INVALID.", Notes = DebugDomainNotes.InputDevice,
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
