#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator.get-parameter — 读取单个 Animator 参数的当前值。</summary>
    public class AnimatorGetParameterTool : IUnityCliTool<AnimatorGetParameterTool.Request, AnimatorGetParameterTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string ParameterName { get; set; } }
        public sealed class Result { public AnimatorParameterWithValue Parameter { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.get-parameter", Summary = "Read a single Animator parameter's current or default value.", Description = "Resolve a target with an Animator and read a named parameter. Returns runtime value if available, otherwise the controller default.", Group = "animator",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read Speed parameter", Input = new Request { Target = "Player", ParameterName = "Speed" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator get-parameter requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            if (string.IsNullOrEmpty(r.ParameterName)) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_PARAMETER_NAME_REQUIRED", "Animator get-parameter requires --parameter-name <name>.", "Pass the name of an Animator parameter.");
            var param = AnimationToolSupport.FindAnimatorParameter(animator, r.ParameterName);
            if (param == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + r.ParameterName + "' not found on Animator: " + GameObjectToolSupport.HierarchyPath(go), "Run animator.get-info to list available parameters.");
            object value = null;
            var hasRuntime = animator.parameterCount > 0;
            switch (param.type)
            {
                case AnimatorControllerParameterType.Float: value = hasRuntime ? animator.GetFloat(r.ParameterName) : (object)param.defaultFloat; break;
                case AnimatorControllerParameterType.Int: value = hasRuntime ? (long)animator.GetInteger(r.ParameterName) : (long)param.defaultInt; break;
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger: value = hasRuntime ? animator.GetBool(r.ParameterName) : param.defaultBool; break;
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Parameter = new AnimatorParameterWithValue { Name = param.name, Type = param.type.ToString(), Value = value } });
        }
    }
}
#endif
