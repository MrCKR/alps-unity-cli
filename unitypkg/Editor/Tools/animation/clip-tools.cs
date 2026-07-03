#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animation.clip — 读取 AnimationClip 资产的设置、绑定曲线和事件。</summary>
    public class AnimationClipTool : IUnityCliTool<AnimationClipTool.Request, AnimationClipTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public int? MaxCurveKeys { get; set; } }
        public sealed class Result { public AnimationClipInfo Clip { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.clip", Summary = "Read an AnimationClip asset's settings, curve bindings, and events.", Description = "Read Assets/*.anim AnimationClip settings, events, and bounded curve summaries. maxCurveKeys limits keys per binding (default 5).", Group = "animation", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read clip", Input = new Request { Path = "Assets/Animations/Idle.anim" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireAnimationClipPath(r.Path);
            var clip = AnimationToolSupport.LoadAnimationClipAsset(path);
            return new Result { Clip = AnimationToolSupport.BuildAnimationClipInfo(path, clip, r.MaxCurveKeys ?? 5) };
        }
    }

    /// <summary>animation.clip.create — 创建持久化 AnimationClip 资产。</summary>
    public class AnimationClipCreateTool : IUnityCliTool<AnimationClipCreateTool.Request, AnimationClipCreateTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public bool? Overwrite { get; set; } public float? FrameRate { get; set; } public bool? Legacy { get; set; } public string WrapMode { get; set; } public bool? LoopTime { get; set; } public float? StartTime { get; set; } public float? StopTime { get; set; } public float? Length { get; set; } public int? MaxCurveKeys { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public string Path { get; set; } public bool WouldOverwrite { get; set; } public AnimationClipInfo Clip { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.clip.create", Summary = "Create or overwrite a persistent AnimationClip asset at an Assets/*.anim path.", Description = "Create a new .anim asset with clip-level settings (frameRate, legacy, wrapMode, loopTime, startTime/stopTime/length). overwrite=true to replace existing.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create clip", Input = new Request { Path = "Assets/Animations/Walk.anim", FrameRate = 30f, LoopTime = true } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireAnimationClipPath(r.Path);
            var overwrite = r.Overwrite ?? false;
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null && !overwrite)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_EXISTS", "AnimationClip already exists: " + path, "Pass overwrite:true after inspecting the existing clip, or choose another Assets/*.anim path.");
            var dryRun = r.DryRun ?? false;
            if (dryRun)
                return new Result { Command = "animation.clip.create", DryRun = true, Path = path, WouldOverwrite = existing != null, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.create", Path = path } } };
            SceneToolSupport.CreateAssetDirectory(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path) };
            if (r.FrameRate.HasValue) clip.frameRate = r.FrameRate.Value;
            if (r.Legacy.HasValue) clip.legacy = r.Legacy.Value;
            if (!string.IsNullOrEmpty(r.WrapMode) && System.Enum.TryParse<WrapMode>(r.WrapMode, true, out var wm)) clip.wrapMode = wm;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (r.StartTime.HasValue) settings.startTime = r.StartTime.Value;
            if (r.StopTime.HasValue) settings.stopTime = r.StopTime.Value;
            else if (r.Length.HasValue) settings.stopTime = settings.startTime + r.Length.Value;
            if (r.LoopTime.HasValue) settings.loopTime = r.LoopTime.Value;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return new Result { Command = "animation.clip.create", DryRun = false, Path = path, Clip = AnimationToolSupport.BuildAnimationClipInfo(path, AnimationToolSupport.LoadAnimationClipAsset(path), r.MaxCurveKeys ?? 5), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.create", Path = path } } };
        }
    }

    /// <summary>animation.clip.modify — 修改已存在 AnimationClip 的 clip 级设置。</summary>
    public class AnimationClipModifyTool : IUnityCliTool<AnimationClipModifyTool.Request, AnimationClipModifyTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public float? FrameRate { get; set; } public bool? Legacy { get; set; } public string WrapMode { get; set; } public bool? LoopTime { get; set; } public float? StartTime { get; set; } public float? StopTime { get; set; } public float? Length { get; set; } public int? MaxCurveKeys { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public AnimationClipInfo Clip { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.clip.modify", Summary = "Update clip-level settings on an existing AnimationClip asset.", Description = "Modify frameRate, legacy, wrapMode, loopTime, startTime/stopTime/length on an existing .anim asset. Does not touch curves or events.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set loop time", Input = new Request { Path = "Assets/Animations/Walk.anim", LoopTime = true } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireAnimationClipPath(r.Path);
            var clip = AnimationToolSupport.LoadAnimationClipAsset(path);
            var dryRun = r.DryRun ?? false;
            if (dryRun)
                return new Result { Command = "animation.clip.modify", DryRun = true, Clip = AnimationToolSupport.BuildAnimationClipInfo(path, clip, r.MaxCurveKeys ?? 5), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.modify", Path = path } } };
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
            return new Result { Command = "animation.clip.modify", DryRun = false, Clip = AnimationToolSupport.BuildAnimationClipInfo(path, AnimationToolSupport.LoadAnimationClipAsset(path), r.MaxCurveKeys ?? 5), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.clip.modify", Path = path } } };
        }
    }
}
#endif
