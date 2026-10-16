#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.input.pointer — 在会话拥有的虚拟指针设备上移动到声明坐标空间的位置，
    /// 按下有显式结束边界的指针按键，或显式释放这些按键。
    /// </summary>
    public class DebugInputPointerTool : IUnityCliTool<DebugInputPointerRequest, DebugInputDispatchResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.input.pointer",
            Summary = "Submit the pointer at a declared position in a declared coordinate space, pressing pointer buttons with their own end boundary or moving only.",
            Description = "Submit pointer input on a virtual pointer device that belongs to the named debug session, and report the resolved coordinate space, the end boundary and the release state honestly. Inputs: sessionId (required, must be an open session); targetSpace (required: screen-pixels or game-view-normalized); x and y (required); buttons (left, right or middle; omit or leave empty to move the pointer without pressing anything); endBoundary (required when at least one button is declared: hold-ms or until-release, with no default; forbidden for a move-only submission because nothing is held); holdMs (required with hold-ms, within the declared millisecond limit; forbidden with until-release). Coordinate space: this call resolves the declared position against the Game view or screen size it observes on the Unity main thread and reports the observed width and height in resolvedWidth, resolvedHeight and resolutionSource, so the caller never has to guess the size. screen-pixels submits the declared pixel values unchanged, and game-view-normalized requires x and y inside 0 and 1 and multiplies them by the observed size. Which corner of that space is the origin is the device backend's own convention; this tool never mirrors or rescales the values beyond that declared multiplication, so verify the origin convention in your project instead of assuming one. When no Game view or screen size is readable the call is refused before any effect instead of submitting against an assumed size. Releasing pressed buttons is its own Tool: call debug.input.release with scope pointer, which reports the per-record release confirmation. Ownership and lifetime are the same as for key input: the session owns at most one virtual pointer device, the device is created on the first submission and ended by debug.input.release with scope all, by session stop or by Play exit, and the release confirmation and conflict rules are identical: released is reported only when the device readback confirms the buttons are no longer pressed, otherwise pending with the exact reason, and a conflicting hold, a device name owned elsewhere, an unreadable device list, an exhausted plan budget or a full record budget is refused before the effect barrier. This owner never resets, rewrites or takes over a real device or another owner's device. Effects: creating a session-owned virtual device and submitting its state are recorded as side effects, and every submission and release appends one trigger-source event (kind input-dispatch, execution point input-dispatch, source trigger) carrying the dispatch id, buttons, end boundary, declared space, resolved size and hold state. A submitted device event never proves that the business read the input: prove acceptance with a hook capture, a program check, a value read or a sealed debug.run window. Output: dispatchId, sessionId, deviceId, deviceKind, deviceName, backend, apiIdentity, action (press or move), controls, endBoundary, holdMs, expiresAtMonotonicMs, pointerSpace, the submitted pointerX and pointerY, resolvedWidth, resolvedHeight, resolutionSource, state, releaseState (released for a move-only submission, otherwise held until confirmed), releaseReason, releaseConfirmed, confirmedControls read live from the device at the end of this call, confirmReason, dispatchedAtMonotonicMs, dispatchedThreadId, executionPoint and the current input budget. This tool is only the optional device backend; it does not replace general business command injection. Budget: " + DebugLimits.InputSummary() + " Rejections: E_DEBUG_BINDING_UNSUPPORTED when no device backend is available, with status unavailable, the backend name, the reason, the missing API members and installedByThisOwner false; E_DEBUG_SESSION_NOT_FOUND for an unknown sessionId; E_DEBUG_SESSION_NOT_OPEN when the session is not open; E_DEBUG_SCOPE_UNSUPPORTED when the call does not run on the Unity main thread or no Game view or screen size is readable, with rule target-space-unresolved in the latter case; E_DEBUG_RESOURCE_CONFLICT for a hold conflict or a device name another owner already uses; E_DEBUG_TRIGGER_BUDGET_EXCEEDED when the shared planned injection and input budget is exhausted; E_DEBUG_LIMIT_EXCEEDED when the retained record budget has no released record left to evict; E_SCHEMA_INVALID for an unknown space, an unknown button, a coordinate outside 0..1 for game-view-normalized, a missing end boundary for a pressed button or a declared end boundary for a move-only submission; and E_EXECUTION_TIMEOUT with effectMayBePartial true only when neither the submitted state nor the removal of a device created by that very call could be confirmed.",
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
                    Description = "Press the left pointer button at the center of the observed Game view and let the declared boundary release it, using a sessionId read from the current Editor session.",
                    Input = new DebugInputPointerRequest
                    {
                        SessionId = "dbg-1",
                        TargetSpace = "game-view-normalized",
                        X = 0.5d,
                        Y = 0.5d,
                        Buttons = new System.Collections.Generic.List<string> { "left" },
                        EndBoundary = "hold-ms",
                        HoldMs = 250
                    }
                },
                new ToolExample
                {
                    Description = "Move the pointer to a pixel position without pressing anything.",
                    Input = new DebugInputPointerRequest
                    {
                        SessionId = "dbg-1",
                        TargetSpace = "screen-pixels",
                        X = 320d,
                        Y = 240d
                    }
                }
            }
        };

        public ValueTask<DebugInputDispatchResult> ExecuteAsync(DebugInputPointerRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 坐标空间解析、后端可用性、结束边界、冲突与预算全部在唯一 effect barrier 之前判定。
            var plan = DebugInputHost.PreparePointer(request);
            ctx.CommitEffect();
            return new ValueTask<DebugInputDispatchResult>(DebugInputHost.ApplyPointer(plan));
        }
    }
}
#endif
