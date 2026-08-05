#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// prefab.stage-save — 保存当前 Prefab Stage 的改动到磁盘。editor-only。
    /// </summary>
    public class PrefabStageSaveTool : IUnityCliTool<PrefabStageSaveTool.Request, PrefabStageSaveTool.Result>
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
            Name = "prefab.stage-save",
            Summary = "Save the currently open Prefab Stage's edits back to its Assets/*.prefab.",
            Description = "Save the currently open Prefab Stage's edits back to its Assets/*.prefab. Requires a Prefab Stage to be open (run prefab.stage-open first). Optionally pass the expected path to guard against saving the wrong prefab.",
            Group = "prefab",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Save the open Prefab Stage", Input = new Request { } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.stage-save");
            var stage = PrefabToolSupport.RequireCurrentPrefabStage(request.Path);
            var path = PrefabToolSupport.StageAssetPath(stage);
            ctx.CommitEffect();
            if (!PrefabToolSupport.SavePrefabStage(stage))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_SAVE_FAILED", "Failed to save Prefab Stage: " + path, "Check Unity Console and UnityCliBridge.log.");
            }
            AssetDatabase.SaveAssets();
            var observedStage = PrefabToolSupport.CurrentPrefabStage();
            if (observedStage == null || !string.Equals(PrefabToolSupport.StageAssetPath(observedStage), path, System.StringComparison.OrdinalIgnoreCase) || PrefabToolSupport.StageIsDirty(observedStage))
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_SAVE_MISMATCH", "Prefab Stage did not reach a saved terminal state: " + path, "Inspect the current Prefab Stage and retry.");
            var observed = PrefabToolSupport.BuildStageInfo("save", observedStage);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Stage = observed,
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.stage-save", Path = observed.Path } }
            });
        }
    }
}
#endif
