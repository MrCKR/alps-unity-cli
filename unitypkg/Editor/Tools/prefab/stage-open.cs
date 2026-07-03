#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// prefab.stage-open — 在 Prefab Stage 中打开 Assets/*.prefab。editor-only。
    /// 对齐旧 PrefabStageOpen handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
            if (!PrefabToolSupport.OpenPrefabStage(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_STAGE_OPEN_FAILED", "Failed to open Prefab Stage: " + path, "Check Unity Console and UnityCliBridge.log.");
            }
            return new Result
            {
                Stage = new PrefabStageInfo { Action = "open", Path = path, Dirty = false },
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.stage-open", Path = path } }
            };
        }
    }
}
#endif
