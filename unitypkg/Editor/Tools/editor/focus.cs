#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>聚焦选定Unity实例的native main window并证明exact foreground HWND。</summary>
    public sealed class EditorFocusTool : IUnityCliTool<EditorFocusRequest, EditorFocusResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "editor.focus",
            Summary = "Focus the selected Unity Editor main window and prove the exact native foreground target.",
            Description = "Freezes one visible top-level HWND owned by the selected Unity Editor process, restores it when minimized, focuses it, and returns only after the exact HWND is the stable Win32 foreground window. Framework dry-run returns the frozen PID/HWND/title without changing desktop focus. Windows only.",
            Group = "editor",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "desktop-window-focus" },
            Permissions = new[] { "desktop-window-control" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 120000,
            Examples = new[]
            {
                new ToolExample { Description = "Focus the selected Unity Editor window.", Input = new EditorFocusRequest() },
                new ToolExample { Description = "Preview the selected native window target.", Input = new EditorFocusRequest(), Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        /// <summary>effect后通过中央wait连续两帧验证frozen HWND，而非只相信SetForegroundWindow返回值。</summary>
        public async System.Threading.Tasks.ValueTask<EditorFocusResult> ExecuteAsync(EditorFocusRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.focus");
            ct.ThrowIfCancellationRequested();
            var plan = EditorWindowFocusToolSupport.Prepare();
            var before = EditorWindowFocusToolSupport.Snapshot(plan);
            var effects = before.Foreground && !before.Minimized
                ? new List<EditorFocusPlannedEffect>()
                : new List<EditorFocusPlannedEffect> { new EditorFocusPlannedEffect { Type = "editor.focus", ProcessId = before.ProcessId, WindowHandle = before.WindowHandle } };
            if (ctx.DryRun)
                return new EditorFocusResult { Changed = false, WouldChange = effects.Count > 0, DryRun = true, Before = before, After = before, PlannedEffects = effects };
            if (effects.Count == 0)
                return new EditorFocusResult { Changed = false, WouldChange = false, DryRun = false, Before = before, After = before, PlannedEffects = effects };

            ctx.CommitEffect();
            EditorWindowFocusToolSupport.Apply(plan);
            var proven = await ctx.Wait.UntilStable(
                () => EditorWindowFocusToolSupport.IsForeground(plan),
                TimeSpan.FromSeconds(3),
                minimumFrames: 2,
                minimumDuration: TimeSpan.FromMilliseconds(100),
                ct: ct);
            var after = EditorWindowFocusToolSupport.Snapshot(plan);
            if (!proven || !after.Foreground || after.ProcessId != before.ProcessId || !string.Equals(after.WindowHandle, before.WindowHandle, StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_FOCUS_UNPROVEN", "The frozen Unity Editor HWND did not become the stable native foreground window.", "Inspect desktop focus restrictions and the selected editor.state before retrying.");
            return new EditorFocusResult { Changed = true, WouldChange = true, DryRun = false, Before = before, After = after, PlannedEffects = effects };
        }
    }

    public sealed class EditorFocusRequest { }

    public sealed class EditorFocusPlannedEffect
    {
        public string Type { get; set; }
        public long ProcessId { get; set; }
        public string WindowHandle { get; set; }
    }

    public sealed class EditorFocusResult
    {
        public bool Changed { get; set; }
        public bool WouldChange { get; set; }
        public bool DryRun { get; set; }
        public EditorFocusWindowState Before { get; set; }
        public EditorFocusWindowState After { get; set; }
        public List<EditorFocusPlannedEffect> PlannedEffects { get; set; }
    }
}
#endif
