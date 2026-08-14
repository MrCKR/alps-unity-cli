#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animation.clip.create — 创建持久化 AnimationClip 资产。</summary>
    public class AnimationClipCreateTool : IUnityCliTool<AnimationClipCreateTool.Request, AnimationClipCreateTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public bool? Overwrite { get; set; } public float? FrameRate { get; set; } public bool? Legacy { get; set; } public string WrapMode { get; set; } public bool? LoopTime { get; set; } public float? StartTime { get; set; } public float? StopTime { get; set; } public float? Length { get; set; } public int? MaxCurveKeys { get; set; } }
        public sealed class Result { public bool DryRun { get; set; } public bool WouldOverwrite { get; set; } public AnimationClipInfo Clip { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.clip.create", Summary = "Create or overwrite a persistent AnimationClip asset at an Assets/*.anim path.", Description = "Create a new .anim asset with clip-level settings (frameRate, legacy, wrapMode, loopTime, startTime/stopTime/length). overwrite=true to replace existing.", Group = "animation",DestructiveHint = true, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create clip", Input = new Request { Path = "Assets/Animations/Created.anim", FrameRate = 30f, LoopTime = true } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireAnimationClipPath(r.Path);
            var overwrite = r.Overwrite ?? false;
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            var wm = AnimationToolSupport.ValidateClipSettings(r.FrameRate, r.WrapMode, r.StartTime, r.StopTime, r.Length);
            if (existing != null && !overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_EXISTS", "AnimationClip already exists: " + path, "Pass overwrite:true after inspecting the existing clip, or choose another Assets/*.anim path.");
            var dryRun = ctx.DryRun;
            if (dryRun)
                return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = true, WouldOverwrite = existing != null, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.create", Path = path } } });
            ctx.CommitEffect();
            SceneToolSupport.CreateAssetDirectory(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path) };
            if (r.FrameRate.HasValue) clip.frameRate = r.FrameRate.Value;
            if (r.Legacy.HasValue) clip.legacy = r.Legacy.Value;
            if (!string.IsNullOrEmpty(r.WrapMode)) clip.wrapMode = wm;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (r.StartTime.HasValue) settings.startTime = r.StartTime.Value;
            if (r.StopTime.HasValue) settings.stopTime = r.StopTime.Value;
            else if (r.Length.HasValue) settings.stopTime = settings.startTime + r.Length.Value;
            if (r.LoopTime.HasValue) settings.loopTime = r.LoopTime.Value;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.CreateAsset(clip, path);
            var observedClip = AnimationToolSupport.ReloadAnimationClipAsset(path);
            var observedSettings = observedClip == null ? default(AnimationClipSettings) : AnimationUtility.GetAnimationClipSettings(observedClip);
            if (observedClip == null || observedClip.name != clip.name || (r.FrameRate.HasValue && !Mathf.Approximately(observedClip.frameRate, r.FrameRate.Value)) || (r.Legacy.HasValue && observedClip.legacy != r.Legacy.Value) || (!string.IsNullOrEmpty(r.WrapMode) && observedClip.wrapMode != wm) || (r.LoopTime.HasValue && observedSettings.loopTime != r.LoopTime.Value) || (r.StartTime.HasValue && !Mathf.Approximately(observedSettings.startTime, r.StartTime.Value)) || (r.StopTime.HasValue && !Mathf.Approximately(observedSettings.stopTime, r.StopTime.Value)) || (r.Length.HasValue && !r.StopTime.HasValue && !Mathf.Approximately(observedSettings.stopTime - observedSettings.startTime, r.Length.Value)))
                throw AnimationToolSupport.StateMismatch("E_ANIMATION_CLIP_CREATE_STATE_MISMATCH", "AnimationClip creation");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = false, Clip = AnimationToolSupport.BuildAnimationClipInfo(path, observedClip, r.MaxCurveKeys ?? 5), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.create", Path = path } } });
        }
    }
}
#endif
