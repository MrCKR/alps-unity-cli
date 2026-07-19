#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// prefab.stage-open — 在 Prefab Stage 中打开 Assets/*.prefab。editor-only。
    /// </summary>
    public class PrefabStageOpenTool : IUnityCliTool<PrefabStageOpenTool.Request, PrefabStageOpenTool.Result>
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
            Name = "prefab.stage-open",
            Summary = "Open an Assets/*.prefab in the Unity Prefab Stage for isolated editing.",
            Description = "Open an Assets/*.prefab in the Unity Prefab Stage. Editor-only: this drives the editor's Prefab Isolation UI. Refuses if another dirty Prefab Stage is open; run prefab.stage-save or prefab.stage-close first.",
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
                new ToolExample { Description = "Open a prefab in Prefab Stage", Input = new Request { Path = "Assets/Prefabs/Player.prefab" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.stage-open");
            var path = PrefabToolSupport.RequirePrefabPath(request.Path);
            PrefabToolSupport.LoadPrefabAsset(path);
            var stage = PrefabToolSupport.CurrentPrefabStage();
            if (stage != null && PrefabToolSupport.StageIsDirty(stage)
                && !string.Equals(PrefabToolSupport.StageAssetPath(stage), path, System.StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_DIRTY_CONFLICT", "Current Prefab Stage has unsaved changes.", "Run tools run prefab.stage-save or close/save the current Prefab Stage in Unity before opening another Prefab.");
            }
            ctx.CommitEffect();
            if (!PrefabToolSupport.OpenPrefabStage(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_OPEN_FAILED", "Failed to open Prefab Stage: " + path, "Check Unity Console and UnityCliBridge.log.");
            }
            var observedStage = PrefabToolSupport.CurrentPrefabStage();
            if (observedStage == null || !string.Equals(PrefabToolSupport.StageAssetPath(observedStage), path, System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_OPEN_MISMATCH", "Prefab Stage did not open the requested asset: " + path, "Inspect the current Prefab Stage and retry.");
            var observed = PrefabToolSupport.BuildStageInfo("open", observedStage);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Stage = observed,
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.stage-open", Path = observed.Path } }
            });
        }
    }
}
#endif
