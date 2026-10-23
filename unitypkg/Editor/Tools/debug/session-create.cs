#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session-create — 建立一个 debug session 定义并返回观察到的身份与剩余预算。
    /// 只建立定义，不注册任何 log listener，也不激活任何资源。
    /// </summary>
    public class DebugSessionCreateTool : IUnityCliTool<DebugSessionCreateRequest, DebugSessionCreateResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session-create",
            Summary = "Create a labelled debug session definition bound to this Editor session and return its id and budget.",
            Description = "Create one debug session definition that later debug facilities (hook, program, input, pause, run) attach resources to. The id has the form dbg-<n>, increases monotonically inside one Editor session and is never reused. Inputs: label (required, unique among loaded definitions), activationScope editor, play or editor-and-play, clockKind monotonic, editor-tick, unity-frame or project-logical, and an optional initialState JSON object. A scope that cannot activate in the current editor mode is rejected with E_DEBUG_SESSION_SCOPE_CONFLICT before any effect instead of creating a definition that can never activate. Optional logCapture subscribes once to Unity's threaded log callback with level log, warning, error, exception or all, an optional messageContains filter and an optional signal name; accepted records become bounded log events carrying the real callback thread id, and a match on messageContains publishes that host signal as a separate host-signal trigger. Creating a session activates no resource. Rejections: E_DEBUG_LIMIT_EXCEEDED, E_DEBUG_SESSION_LABEL_CONFLICT, E_DEBUG_SESSION_SCOPE_CONFLICT, E_DEBUG_STORE_FAILED.", Notes = DebugDomainNotes.SessionCreate,
            Group = "debug",
            DestructiveHint = false,
            SideEffects = new[] { "debug-session", "log-subscription" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 300000,
            Examples = new[]
            {
                new ToolExample { Description = "Create an edit-and-play session with no initial state.", Input = new DebugSessionCreateRequest { Label = "input-debug", ActivationScope = "editor-and-play", ClockKind = "editor-tick" } },
                new ToolExample { Description = "Create a Play-only session that starts with recorded state.", Input = new DebugSessionCreateRequest { Label = "attack-loop", ActivationScope = "play", ClockKind = "unity-frame", InitialState = new System.Collections.Generic.Dictionary<string, object> { { "enabled", true } } } },
                new ToolExample { Description = "Create a session that captures warning and error logs and wakes the signal named attack-start.", Input = new DebugSessionCreateRequest { Label = "attack-logs", ActivationScope = "editor-and-play", ClockKind = "editor-tick", LogCapture = new DebugLogCaptureSpec { Enabled = true, Level = "warning", Signal = "attack-start" } } }
            }
        };

        public ValueTask<DebugSessionCreateResult> ExecuteAsync(DebugSessionCreateRequest request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            // 输入、占用与范围冲突全部在 effect barrier 之前判定，失败时不产生任何定义。
            var plan = DebugSessionHost.PrepareCreate(request);
            ctx.CommitEffect();
            return new ValueTask<DebugSessionCreateResult>(DebugSessionHost.ApplyCreate(plan));
        }
    }
}
#endif
