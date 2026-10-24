#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>editor.menu-execute — 按精确路径派发任意 Unity Editor 菜单项，并返回派发证据与同回合 Editor 状态读回。</summary>
    public class EditorMenuExecuteTool : IUnityCliTool<EditorMenuExecuteTool.Request, EditorMenuExecuteTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Path { get; set; }
        }

        public sealed class Result
        {
            public sealed class ReceiptInfo
            {
                public string Type { get; set; }
                public bool Complete { get; set; }
                public bool VerificationRequired { get; set; }
            }

            public string Path { get; set; }
            public bool Executed { get; set; }
            public long ElapsedMs { get; set; }
            public EditorStateInfo State { get; set; }
            public ReceiptInfo Receipt { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "editor.menu-execute",
            Summary = "Execute any Unity Editor menu item by path and return the dispatch result with a fresh Editor state readback.",
            Description = "Execute one Editor menu item by exact path. The result receipt has type=dispatched and complete=false: Unity accepted the dispatch, but the menu's business effect is not proven. Confirm the outcome with the relevant read Tool. Before any effect is dispatched the path is preflighted: a path that does not exist fails with E_MENU_NOT_FOUND and a path whose own validate function reports disabled fails with E_MENU_DISABLED. After the effect barrier the outcome is only reported as E_EXECUTION_TIMEOUT with an unknown outcome when Unity declines the dispatch or the handler throws. Refused with E_UNITY_COMPILING, E_UNITY_IMPORTING or E_UNITY_BUSY while a compile, import, build or Play Mode transition is in progress. Works in Edit and Play Mode.", Notes = DebugDomainNotes.MenuExecuteLimits,
            Group = "editor",
            DestructiveHint = true,
            SideEffects = new[] { "arbitrary-menu-execution" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Open the Console window", Input = new Request { Path = "Window/General/Console" } } }
        };

        /// <summary>首次调用派发菜单；effect barrier 位于 ExecuteMenuItem 之前，失败不重试也不重放。</summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = request?.Path;
            if (string.IsNullOrWhiteSpace(path))
                throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", "editor.menu-execute requires a menu path.", "Pass the exact menu path and use editor.menu-list to find it.");
            EditorToolSupport.EnsureEditorNotBusy("editor.menu-execute");

            bool exists;
            if (EditorMenuSupport.TryGetItemExists(path, out exists) && !exists)
                throw new UnityCliBridge.BridgeCommandException(
                    "E_MENU_NOT_FOUND",
                    "No Editor menu item exists at path '" + UnityCliBridge.BoundedText(path, 512) + "'.",
                    "Run editor.menu-list with a filter to find the exact path.");
            // enabled 判定必须在 effect barrier 之前：barrier 之后扣下只能报 outcome unknown。
            bool enabled;
            if (EditorMenuSupport.TryGetItemEnabled(path, out enabled) && !enabled)
                throw new UnityCliBridge.BridgeCommandException(
                    "E_MENU_DISABLED",
                    "The Editor menu item at path '" + UnityCliBridge.BoundedText(path, 512) + "' is currently disabled.",
                    "Satisfy the menu item's own prerequisite, or use the dedicated Tool for this capability.");

            var startedAt = DateTime.UtcNow;
            ctx.CommitEffect();
            bool executed;
            try
            {
                executed = EditorApplication.ExecuteMenuItem(path);
            }
            catch (Exception exception)
            {
                // barrier 之后不能声称任何确定结果，异常也归档为 outcome unknown。
                throw new UnityCliBridge.BridgeCommandException(
                    "E_EXECUTION_TIMEOUT",
                    "The menu handler threw after the effect barrier, so the outcome is unknown: " + UnityCliBridge.BoundedText(exception.Message, 4096),
                    new Dictionary<string, object> { { "stage", "dispatch" }, { "effectStarted", true }, { "path", UnityCliBridge.BoundedText(path, 512) } },
                    new[] { "Read the Editor Console, inspect the affected business state, then submit a new request." });
            }

            if (!executed)
            {
                // 派发之后 Unity 拒绝执行，框架不能证明未发生任何 effect，只能如实报 unknown。
                throw new UnityCliBridge.BridgeCommandException(
                    "E_EXECUTION_TIMEOUT",
                    "Unity declined to execute the menu item after the effect barrier, so the outcome cannot be proven.",
                    new Dictionary<string, object> { { "stage", "dispatch" }, { "effectStarted", true }, { "path", UnityCliBridge.BoundedText(path, 512) } },
                    new[] { "Inspect editor.state and the affected business state, then submit a new request." });
            }

            var elapsedMs = (long)Math.Round((DateTime.UtcNow - startedAt).TotalMilliseconds);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Path = path,
                Executed = true,
                ElapsedMs = elapsedMs,
                State = EditorToolSupport.BuildEditorState(),
                Receipt = new Result.ReceiptInfo { Type = "dispatched", Complete = false, VerificationRequired = true }
            });
        }
    }
}
#endif
