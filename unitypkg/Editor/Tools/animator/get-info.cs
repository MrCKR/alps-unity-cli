#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator.get-info — 读取 Animator 详细信息(层/参数/剪辑)。</summary>
    public class AnimatorGetInfoTool : IUnityCliTool<AnimatorGetInfoTool.Request, AnimatorGetInfoTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public AnimatorInfo Animator { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.get-info", Summary = "Read Animator details: layers, parameters, clips, and runtime controller.", Description = "Resolve a target with an Animator and return enabled, speed, layerCount, parameters, layers, and clips.", Group = "animator",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read animator info", Input = new Request { Target = "Player" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator info requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Animator = AnimationToolSupport.BuildAnimatorInfo(animator, 0) });
        }
    }
}
#endif
