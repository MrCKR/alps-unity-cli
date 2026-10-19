#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>
    /// debug.session.create — 建立一个 debug session 定义并返回观察到的身份与剩余预算。
    /// 只建立定义，不注册任何 log listener，也不激活任何资源。
    /// </summary>
    public class DebugSessionCreateTool : IUnityCliTool<DebugSessionCreateRequest, DebugSessionCreateResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "debug.session.create",
            Summary = "Create a labelled debug session definition bound to this Editor session and return its id and budget.",
            Description = "Create one debug session definition that later debug facilities (hook, program, input, pause, run) can attach resources to. The definition is bound to the canonical project path plus the current editorSessionId, so it is never inferred from the current selection, from the previous tool call or from a CLI process. The session id has the form dbg-<n>, is monotonically increasing inside one Editor session and is never reused. Inputs: label (required, unique among loaded definitions), activationScope editor (Edit only), play (Play only) or editor-and-play, clockKind monotonic, editor-tick, unity-frame or project-logical, and an optional initialState JSON object. A scope that cannot be activated in the current editor mode is rejected with E_DEBUG_SESSION_SCOPE_CONFLICT before any effect instead of creating a definition that can never activate. The response returns the session id, the observed editorSessionId, runtimeId (the Play runtime segment currently observed; empty in Edit Mode), domainGeneration, the store publication and the remaining budget. Creating a session does not activate any resource: this call only registers the definition, and it mounts the threaded log listener only when logCapture is enabled. Log capture: pass logCapture to subscribe once to Unity's threaded log callback for this session, with level log, warning, error, exception or all, and an optional messageContains text filter plus an optional signal name. Accepted records become bounded log events in the debug event store with the real callback thread id; the callback thread is not assumed to be the business call boundary. When a record matches messageContains and signal is set, the same callback publishes that host signal, which wakes drivers waiting on it and is recorded as a separate host-signal trigger. The subscription is released when the session cleanup runs, so stopping the session unsubscribes. Limits and rejections: E_DEBUG_LIMIT_EXCEEDED when the session or state byte limit is reached, E_DEBUG_SESSION_LABEL_CONFLICT for a duplicate label, E_DEBUG_SESSION_SCOPE_CONFLICT for an activation scope that conflicts with the current editor mode, and E_DEBUG_STORE_FAILED when the single session file could not be published. The definition is published atomically to the single per-project debug session file, so it survives a Domain Reload inside the same Editor session but never an Editor restart. Side effects: writes this Editor session's session definition file and, when logCapture is enabled, registers one threaded log subscription for the whole debug domain. A definition holds one session slot from this moment until debug.session.remove deletes it: debug.session.stop closes future dispatch and keeps the definition and its evidence, so a stopped definition still occupies its slot, and create returns E_DEBUG_LIMIT_EXCEEDED while every slot is held. Use debug.session.list with state stopped to find reclaimable definitions. " + DebugLimits.SessionSummary() + " " + DebugLimits.TriggerSummary(),
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
