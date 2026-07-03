#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// prefab.stage-close — 关闭当前 Prefab Stage,可选先保存。editor-only。
    /// 对齐旧 PrefabStageClose handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class PrefabStageCloseTool : IUnityCliTool<PrefabStageCloseTool.Request, PrefabStageCloseTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public bool? Save { get; set; }
        }

        public sealed class Result
        {
            public PrefabStageInfo Stage { get; set; }
            public List<PrefabPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.stage-close",
            Summary = "Close the currently open Prefab Stage, optionally saving first.",
            Description = "Close the currently open Prefab Stage. If the stage has unsaved changes, pass save=true to save before closing, or run prefab.stage-save first; otherwise the close is refused to avoid losing edits.",
            Group = "prefab",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "editor-session" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Close the open Prefab Stage", Input = new Request { } },
                new ToolExample { Description = "Save and close", Input = new Request { Save = true } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.stage-close");
            var stage = PrefabToolSupport.RequireCurrentPrefabStage(request.Path);
            var path = PrefabToolSupport.StageAssetPath(stage);
            var save = request.Save ?? false;
            if (PrefabToolSupport.StageIsDirty(stage) && !save)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_DIRTY_CONFLICT", "Prefab Stage has unsaved changes: " + path, "Run tools run prefab.stage-close --input '{\"path\":\"<path>\",\"save\":true}' or tools run prefab.stage-save before closing.");
            }
            if (save && !PrefabToolSupport.SavePrefabStage(stage))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_SAVE_FAILED", "Failed to save Prefab Stage before close: " + path, "Check Unity Console and UnityCliBridge.log.");
            }
            if (!PrefabToolSupport.ClosePrefabStage())
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_CLOSE_FAILED", "Failed to close Prefab Stage: " + path, "Close the Prefab Stage in Unity or check UnityCliBridge.log.");
            }
            return new Result
            {
                Stage = new PrefabStageInfo { Action = "close", Path = path, Dirty = false },
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.stage-close", Path = path } }
            };
        }
    }
}
#endif
