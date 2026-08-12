#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>配置当前Editor session的中央scheduler auto-tick状态。</summary>
    public sealed class EditorAutoTickSetTool : IUnityCliTool<EditorAutoTickSetRequest, EditorAutoTickSetResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "editor.autotick.set",
            Summary = "Configure session-only unfocused Editor ticking through the central scheduler.",
            Description = "Enables or disables central-scheduler SignalTick pumping for only the current Editor process session. enable defaults to true and intervalMs defaults to 16; intervalMs accepts 0..60000, where 0 requests every scheduler update. State is not persisted and resets on domain reload or process restart. Framework dry-run reports the exact state transition without enabling ticks.",
            Group = "editor",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "editor-tick-rate" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[]
            {
                new ToolExample { Description = "Enable approximately 60Hz unfocused ticking.", Input = new EditorAutoTickSetRequest { Enable = true, IntervalMs = 16 } },
                new ToolExample { Description = "Preview disabling auto-tick.", Input = new EditorAutoTickSetRequest { Enable = false }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        /// <summary>所有参数与SignalTick availability在唯一barrier前冻结；scheduler是唯一状态owner。</summary>
        public System.Threading.Tasks.ValueTask<EditorAutoTickSetResult> ExecuteAsync(EditorAutoTickSetRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.autotick.set");
            ct.ThrowIfCancellationRequested();
            request = request ?? new EditorAutoTickSetRequest();
            var enable = request.Enable ?? true;
            var interval = request.IntervalMs ?? 16L;
            if (interval < 0 || interval > 60000)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_AUTOTICK_INTERVAL_INVALID", "intervalMs must be between 0 and 60000.", "Pass a bounded non-negative intervalMs value.");

            var before = UnityCliWaitScheduler.ReadAutoTickState();
            UnityCliWaitScheduler.PrepareAutoTick(enable);
            var changed = before.Enabled != enable || (enable && before.IntervalMs != interval);
            var effects = changed
                ? new List<EditorAutoTickPlannedEffect> { new EditorAutoTickPlannedEffect { Type = "editor.autotick.set", Enable = enable, IntervalMs = interval } }
                : new List<EditorAutoTickPlannedEffect>();
            if (ctx.DryRun || !changed)
            {
                var preview = new EditorAutoTickState
                {
                    EditorSessionId = before.EditorSessionId,
                    Enabled = enable,
                    IntervalMs = enable ? interval : before.IntervalMs,
                    ForcedTickCount = before.ForcedTickCount,
                    SignalTickAvailable = before.SignalTickAvailable,
                    LastError = before.LastError
                };
                return new System.Threading.Tasks.ValueTask<EditorAutoTickSetResult>(new EditorAutoTickSetResult { Changed = false, WouldChange = changed, DryRun = ctx.DryRun, Before = before, After = preview, PlannedEffects = effects });
            }

            ctx.CommitEffect();
            UnityCliWaitScheduler.ConfigureAutoTick(enable, interval);
            var after = UnityCliWaitScheduler.ReadAutoTickState();
            if (after.Enabled != enable || (enable && after.IntervalMs != interval) || !string.Equals(after.EditorSessionId, before.EditorSessionId, System.StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_AUTOTICK_VERIFY_FAILED", "The central scheduler did not retain the requested session auto-tick state.", "Inspect editor.state before deciding whether to retry.");
            return new System.Threading.Tasks.ValueTask<EditorAutoTickSetResult>(new EditorAutoTickSetResult { Changed = true, WouldChange = true, DryRun = false, Before = before, After = after, PlannedEffects = effects });
        }
    }

    public sealed class EditorAutoTickSetRequest
    {
        public bool? Enable { get; set; }
        public long? IntervalMs { get; set; }
    }

    public sealed class EditorAutoTickState
    {
        public string EditorSessionId { get; set; }
        public bool Enabled { get; set; }
        public long IntervalMs { get; set; }
        public long ForcedTickCount { get; set; }
        public bool SignalTickAvailable { get; set; }
        public string LastError { get; set; }
    }

    public sealed class EditorAutoTickPlannedEffect
    {
        public string Type { get; set; }
        public bool Enable { get; set; }
        public long IntervalMs { get; set; }
    }

    public sealed class EditorAutoTickSetResult
    {
        public bool Changed { get; set; }
        public bool WouldChange { get; set; }
        public bool DryRun { get; set; }
        public EditorAutoTickState Before { get; set; }
        public EditorAutoTickState After { get; set; }
        public List<EditorAutoTickPlannedEffect> PlannedEffects { get; set; }
    }
}
#endif
