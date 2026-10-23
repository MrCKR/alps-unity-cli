#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.input-pointer — 在会话拥有的虚拟指针设备上移动到声明坐标空间的位置，
    /// 按下有显式结束边界的指针按键，或显式释放这些按键。
    /// </summary>
    public class DebugInputPointerTool : IUnityCliTool<DebugInputPointerRequest, DebugInputDispatchResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.input-pointer",
            Summary = "Submit the pointer at a declared position in a declared coordinate space, pressing pointer buttons with their own end boundary or moving only.",
            Description = "Submit pointer input on the virtual pointer device of an open debug session and report the resolved coordinate space, end boundary and release state honestly. targetSpace is screen-pixels or game-view-normalized; x and y are required and must be inside 0..1 for game-view-normalized; buttons (left, right, middle) may be omitted for a move-only submission; endBoundary is required when a button is pressed and forbidden for a move-only submission, and holdMs follows the same rule. The position is resolved against the Game view or screen size observed on the Unity main thread and reported as resolvedWidth, resolvedHeight and resolutionSource; when no size is readable the call is refused before any effect. The origin corner is the backend's own convention and is never mirrored or rescaled, so verify it in your project. Releasing is its own Tool, debug.input-release with scope pointer. Refusals: E_DEBUG_SCOPE_UNSUPPORTED, E_DEBUG_BINDING_UNSUPPORTED, E_DEBUG_SESSION_NOT_FOUND, E_DEBUG_SESSION_NOT_OPEN, E_DEBUG_RESOURCE_CONFLICT, E_DEBUG_TRIGGER_BUDGET_EXHAUSTED, E_DEBUG_LIMIT_EXCEEDED or E_SCHEMA_INVALID.", Notes = DebugDomainNotes.InputDevice,
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
