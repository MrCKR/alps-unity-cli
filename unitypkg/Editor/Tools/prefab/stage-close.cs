#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// prefab.stage-close - 关闭没有未保存修改的 Prefab Stage。
    /// </summary>
    public class PrefabStageCloseTool : IUnityCliTool<PrefabStageCloseTool.Request, PrefabStageCloseTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
        }

        public sealed class Result
        {
            public PrefabStageInfo Stage { get; set; }
            public List<PrefabPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.stage-close",
            Summary = "Close the currently open Prefab Stage after verifying it has no unsaved changes.",
            Description = "Close the currently open Prefab Stage without saving. If the stage has unsaved changes, the call fails with E_PREFAB_DIRTY_CONFLICT; run prefab.stage-save first, then call this tool again.",
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
                new ToolExample { Description = "Close the open Prefab Stage", Input = new Request { } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.stage-close");
            var stage = PrefabToolSupport.RequireCurrentPrefabStage(request.Path);
            var path = PrefabToolSupport.StageAssetPath(stage);
            if (PrefabToolSupport.StageIsDirty(stage))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_DIRTY_CONFLICT", "Prefab Stage has unsaved changes: " + path, "Run tools run prefab.stage-save, then retry prefab.stage-close.");
            }
            if (!PrefabToolSupport.ClosePrefabStage())
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_CLOSE_FAILED", "Failed to close Prefab Stage: " + path, "Close the Prefab Stage in Unity or check UnityCliBridge.log.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Stage = new PrefabStageInfo { Action = "close", Path = path, Dirty = false },
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.stage-close", Path = path } }
            });
        }
    }
}
#endif
