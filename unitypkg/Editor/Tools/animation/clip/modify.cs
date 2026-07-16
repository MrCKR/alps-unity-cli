#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animation.clip.modify — 修改已存在 AnimationClip 的 clip 级设置。</summary>
    public class AnimationClipModifyTool : IUnityCliTool<AnimationClipModifyTool.Request, AnimationClipModifyTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public float? FrameRate { get; set; } public bool? Legacy { get; set; } public string WrapMode { get; set; } public bool? LoopTime { get; set; } public float? StartTime { get; set; } public float? StopTime { get; set; } public float? Length { get; set; } public int? MaxCurveKeys { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public AnimationClipInfo Clip { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.clip.modify", Summary = "Update clip-level settings on an existing AnimationClip asset.", Description = "Modify frameRate, legacy, wrapMode, loopTime, startTime/stopTime/length on an existing .anim asset. Does not touch curves or events.", Group = "animation", Version = "1.0.0", DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set loop time", Input = new Request { Path = "Assets/Animations/Walk.anim", LoopTime = true } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireAnimationClipPath(r.Path);
            var clip = AnimationToolSupport.LoadAnimationClipAsset(path);
            var dryRun = ctx.DryRun;
            if (dryRun)
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "animation.clip.modify", DryRun = true, Clip = AnimationToolSupport.BuildAnimationClipInfo(path, clip, r.MaxCurveKeys ?? 5), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.modify", Path = path } } });
            Undo.RecordObject(clip, "Modify AnimationClip Settings");
            if (r.FrameRate.HasValue) clip.frameRate = r.FrameRate.Value;
            if (r.Legacy.HasValue) clip.legacy = r.Legacy.Value;
            if (!string.IsNullOrEmpty(r.WrapMode) && System.Enum.TryParse<WrapMode>(r.WrapMode, true, out var wm)) clip.wrapMode = wm;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (r.StartTime.HasValue) settings.startTime = r.StartTime.Value;
            if (r.StopTime.HasValue) settings.stopTime = r.StopTime.Value;
            else if (r.Length.HasValue) settings.stopTime = settings.startTime + r.Length.Value;
            if (r.LoopTime.HasValue) settings.loopTime = r.LoopTime.Value;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Command = "animation.clip.modify", DryRun = false, Clip = AnimationToolSupport.BuildAnimationClipInfo(path, AnimationToolSupport.LoadAnimationClipAsset(path), r.MaxCurveKeys ?? 5), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.modify", Path = path } } });
        }
    }
}
#endif
