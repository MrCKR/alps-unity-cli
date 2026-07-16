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
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireAnimationClipPath(r.Path);
            var clip = AnimationToolSupport.LoadAnimationClipAsset(path);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Clip = AnimationToolSupport.BuildAnimationClipInfo(path, clip, r.MaxCurveKeys ?? 5) });
        }
    }
}
#endif
