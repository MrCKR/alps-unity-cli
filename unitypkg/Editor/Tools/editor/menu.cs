#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>editor.menu — 执行具有确定 terminal predicate 的窄菜单项。</summary>
    public class EditorMenuTool : IUnityCliTool<EditorMenuTool.Request, EditorMenuTool.Response>
    {
        public sealed class Request { public string Path { get; set; } }
        public sealed class Response { public MenuResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }

        public ToolDescriptor Describe() => new() { Name = "editor.menu", Summary = "Execute the terminally verifiable File/Save Unity Editor menu command.", Description = "Execute only File/Save, which succeeds after the active saved scene is no longer dirty. Assets/Refresh and other menu paths are rejected because they have no attributable business completion predicate.", Group = "editor",DestructiveHint = true, SideEffects = new[] { "scene-save" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Save the active scene", Input = new Request { Path = "File/Save" } } } };

        /// <summary>首次调用执行 allowlisted menu；reload redelivery 只验证对应终态。</summary>
        public async System.Threading.Tasks.ValueTask<Response> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", "Editor menu path is required.", "Pass File/Save.");
            var saveScene = string.Equals(request.Path, "File/Save", StringComparison.Ordinal);
            if (!saveScene)
                throw new UnityCliBridge.BridgeCommandException("E_MENU_COMPLETION_UNSUPPORTED", "The menu path has no registered terminal completion predicate: " + request.Path, "Use a dedicated Tool, or use File/Save.");

            var scene = SceneManager.GetActiveScene();
            if (saveScene && string.IsNullOrEmpty(scene.path))
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_PATH_REQUIRED", "File/Save cannot prove completion for an untitled scene.", "Use scene.save with an explicit Assets/*.unity path.");
            var startedAt = DateTime.UtcNow;
            EditorToolSupport.EnsureEditorNotBusy("editor.menu");
            ctx.CommitEffect();
            if (!EditorApplication.ExecuteMenuItem(request.Path))
                throw new UnityCliBridge.BridgeCommandException("E_MENU_NOT_FOUND", "Unity menu item was not found or could not be executed.", "Use File/Save in a compatible Editor state.");
            var stable = await ctx.Wait.UntilStable(
                () =>
                {
                    var activeScene = SceneManager.GetActiveScene();
                    return !string.IsNullOrEmpty(activeScene.path) && !activeScene.isDirty;
                }, TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct: ct);
            scene = SceneManager.GetActiveScene();
            if (!stable || string.IsNullOrEmpty(scene.path) || scene.isDirty)
                throw new UnityCliBridge.BridgeCommandException("E_MENU_TERMINAL_NOT_OBSERVED", "The allowlisted menu command did not reach its terminal predicate.", "Inspect editor.state and scene state before submitting a new request.");
            var elapsedMs = (long)Math.Round((DateTime.UtcNow - startedAt).TotalMilliseconds);
            return new Response
            {
                Result = new MenuResult { Path = request.Path, Executed = true, ElapsedMs = elapsedMs },
                State = EditorToolSupport.BuildEditorState(),
                PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.menu", Path = request.Path } }
            };
        }
    }
}
#endif
