#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator.set-parameter — 设置 Animator 参数默认值(编辑模式修改 Controller 资产)。</summary>
    public class AnimatorSetParameterTool : IUnityCliTool<AnimatorSetParameterTool.Request, AnimatorSetParameterTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string ParameterName { get; set; } public string Value { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorParameterRef Parameter { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.set-parameter", Summary = "Set an Animator parameter default value by editing the AnimatorController asset.", Description = "In edit mode, modifies the AnimatorController asset parameter defaults. Requires an AnimatorController assigned to the Animator.", Group = "animation",DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set Speed default", Input = new Request { Target = "Player", ParameterName = "Speed", Value = "0.5" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator set-parameter requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            if (string.IsNullOrEmpty(r.ParameterName)) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_PARAMETER_NAME_REQUIRED", "Animator set-parameter requires --parameter-name <name>.", "Pass the name of an Animator parameter.");
            var ac = animator.runtimeAnimatorController as AnimatorController;
            if (ac == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NO_CONTROLLER", "Animator has no AnimatorController assigned: " + GameObjectToolSupport.HierarchyPath(go), "Assign an AnimatorController asset.");
            var param = AnimationToolSupport.FindAnimatorParameter(animator, r.ParameterName);
            if (param == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + r.ParameterName + "' not found: " + GameObjectToolSupport.HierarchyPath(go), "Run animator.get-info to list parameters.");
            var expectedFloat = 0f;
            var expectedInt = 0;
            var expectedBool = false;
            switch (param.type)
            {
                case AnimatorControllerParameterType.Float: expectedFloat = GameObjectToolSupport.ParseFloat(r.Value ?? "0", r.ParameterName); break;
                case AnimatorControllerParameterType.Int: expectedInt = int.Parse(r.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture); break;
                case AnimatorControllerParameterType.Bool: expectedBool = bool.Parse(r.Value ?? "false"); break;
            }
            ctx.CommitEffect();
            Undo.RecordObject(ac, "Set Animator Parameter Default");
            var parameters = ac.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == r.ParameterName)
                {
                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float: parameters[i].defaultFloat = expectedFloat; break;
                        case AnimatorControllerParameterType.Int: parameters[i].defaultInt = expectedInt; break;
                        case AnimatorControllerParameterType.Bool: parameters[i].defaultBool = expectedBool; break;
                    }
                    break;
                }
            }
            ac.parameters = parameters;
            EditorUtility.SetDirty(ac);
            var observedController = AnimationToolSupport.ReloadControllerAsset(AssetDatabase.GetAssetPath(ac));
            AnimatorControllerParameter observedParameter = null;
            if (observedController != null) foreach (var candidate in observedController.parameters) if (candidate.name == r.ParameterName) { observedParameter = candidate; break; }
            if (observedParameter == null || !DefaultValueMatches(observedParameter, r.Value)) throw AnimationToolSupport.StateMismatch("E_ANIMATOR_PARAMETER_STATE_MISMATCH", "AnimatorController parameter default");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Parameter = new AnimatorParameterRef { Name = observedParameter.name, Type = observedParameter.type.ToString() }, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.set-parameter", Target = GameObjectToolSupport.HierarchyPath(go), Parameter = observedParameter.name } } });
        }

        private static bool DefaultValueMatches(AnimatorControllerParameter parameter, string value)
        {
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float: return Mathf.Approximately(parameter.defaultFloat, GameObjectToolSupport.ParseFloat(value ?? "0", parameter.name));
                case AnimatorControllerParameterType.Int: return parameter.defaultInt == int.Parse(value ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                case AnimatorControllerParameterType.Bool: return parameter.defaultBool == bool.Parse(value ?? "false");
                default: return true;
            }
        }
    }
}
#endif
