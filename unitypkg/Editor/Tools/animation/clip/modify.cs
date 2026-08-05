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
        public sealed class Result { public bool DryRun { get; set; } public AnimationClipInfo Clip { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.clip.modify", Summary = "Update clip-level settings on an existing AnimationClip asset.", Description = "Modify frameRate, legacy, wrapMode, loopTime, startTime/stopTime/length on an existing .anim asset. Does not touch curves or events.", Group = "animation",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set loop time", Input = new Request { Path = "Assets/Animations/Walk.anim", LoopTime = true } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireAnimationClipPath(r.Path);
            var clip = AnimationToolSupport.LoadAnimationClipAsset(path);
            var wm = AnimationToolSupport.ValidateClipSettings(r.FrameRate, r.WrapMode, r.StartTime, r.StopTime, r.Length);
            var dryRun = ctx.DryRun;
            if (dryRun)
                return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = true, Clip = AnimationToolSupport.BuildAnimationClipInfo(path, clip, r.MaxCurveKeys ?? 5), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.modify", Path = path } } });
            ctx.CommitEffect();
            Undo.RecordObject(clip, "Modify AnimationClip Settings");
            if (r.FrameRate.HasValue) clip.frameRate = r.FrameRate.Value;
            if (r.Legacy.HasValue) clip.legacy = r.Legacy.Value;
            if (!string.IsNullOrEmpty(r.WrapMode)) clip.wrapMode = wm;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (r.StartTime.HasValue) settings.startTime = r.StartTime.Value;
            if (r.StopTime.HasValue) settings.stopTime = r.StopTime.Value;
            else if (r.Length.HasValue) settings.stopTime = settings.startTime + r.Length.Value;
            if (r.LoopTime.HasValue) settings.loopTime = r.LoopTime.Value;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            var observedClip = AnimationToolSupport.ReloadAnimationClipAsset(path);
            var observedSettings = observedClip == null ? default(AnimationClipSettings) : AnimationUtility.GetAnimationClipSettings(observedClip);
            if (observedClip == null || (r.FrameRate.HasValue && !Mathf.Approximately(observedClip.frameRate, r.FrameRate.Value)) || (r.Legacy.HasValue && observedClip.legacy != r.Legacy.Value) || (!string.IsNullOrEmpty(r.WrapMode) && observedClip.wrapMode != wm) || (r.LoopTime.HasValue && observedSettings.loopTime != r.LoopTime.Value) || (r.StartTime.HasValue && !Mathf.Approximately(observedSettings.startTime, r.StartTime.Value)) || (r.StopTime.HasValue && !Mathf.Approximately(observedSettings.stopTime, r.StopTime.Value)) || (r.Length.HasValue && !r.StopTime.HasValue && !Mathf.Approximately(observedSettings.stopTime - observedSettings.startTime, r.Length.Value)))
                throw AnimationToolSupport.StateMismatch("E_ANIMATION_CLIP_MODIFY_STATE_MISMATCH", "AnimationClip settings");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = false, Clip = AnimationToolSupport.BuildAnimationClipInfo(path, observedClip, r.MaxCurveKeys ?? 5), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.modify", Path = path } } });
        }
    }
}
#endif
