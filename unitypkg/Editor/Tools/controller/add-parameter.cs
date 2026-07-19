#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.add-parameter — 添加参数到 Controller。</summary>
    public class ControllerAddParameterTool : IUnityCliTool<ControllerAddParameterTool.Request, ControllerAddParameterTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string ParameterName { get; set; } public string ParameterType { get; set; } public string DefaultValue { get; set; } }
        public sealed class Result { public string Path { get; set; } public string ParameterName { get; set; } public string ParameterType { get; set; } public long TotalParameters { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-parameter", Summary = "Add a parameter to an AnimatorController asset.", Description = "Add a float/int/bool/trigger parameter. Optionally set a default value.", Group = "controller", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add MatrixSpeed parameter", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", ParameterName = "MatrixSpeed", ParameterType = "float" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.ParameterName)) throw new UnityCliBridge.BridgeCommandException("E_PARAMETER_NAME_REQUIRED", "controller.add-parameter requires parameterName.", "Pass a name for the new parameter.");
            foreach (var existing in controller.parameters) if (existing.name == r.ParameterName) throw new UnityCliBridge.BridgeCommandException("E_PARAMETER_EXISTS", "Parameter '" + r.ParameterName + "' already exists.", "Use animator.set-parameter to change its default value.");
            var typeStr = (r.ParameterType ?? "float").ToLowerInvariant();
            var paramType = typeStr == "int" || typeStr == "integer" ? AnimatorControllerParameterType.Int : typeStr == "bool" || typeStr == "boolean" ? AnimatorControllerParameterType.Bool : typeStr == "trigger" ? AnimatorControllerParameterType.Trigger : AnimatorControllerParameterType.Float;
            var expectedFloat = 0f;
            var expectedInt = 0;
            var expectedBool = false;
            if (r.DefaultValue != null && paramType == AnimatorControllerParameterType.Float)
            {
                if (!float.TryParse(r.DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out expectedFloat) || float.IsNaN(expectedFloat) || float.IsInfinity(expectedFloat))
                    throw new UnityCliBridge.BridgeCommandException("E_PARAMETER_DEFAULT_INVALID", "Float parameter defaultValue must be finite.", "Pass a finite floating-point defaultValue.");
            }
            if (r.DefaultValue != null && paramType == AnimatorControllerParameterType.Int && !int.TryParse(r.DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out expectedInt))
                throw new UnityCliBridge.BridgeCommandException("E_PARAMETER_DEFAULT_INVALID", "Int parameter defaultValue must be an integer.", "Pass an integer defaultValue.");
            if (r.DefaultValue != null && paramType == AnimatorControllerParameterType.Bool)
                expectedBool = string.Equals(r.DefaultValue, "true", System.StringComparison.OrdinalIgnoreCase) || r.DefaultValue == "1";
            ctx.CommitEffect();
            Undo.RecordObject(controller, "Add Animator Parameter");
            controller.AddParameter(r.ParameterName, paramType);
            if (r.DefaultValue != null) { var allParams = controller.parameters; var added = allParams[allParams.Length - 1]; if (paramType == AnimatorControllerParameterType.Float) added.defaultFloat = expectedFloat; else if (paramType == AnimatorControllerParameterType.Int) added.defaultInt = expectedInt; else if (paramType == AnimatorControllerParameterType.Bool) added.defaultBool = expectedBool; controller.parameters = allParams; }
            EditorUtility.SetDirty(controller);
            var observedController = AnimationToolSupport.ReloadControllerAsset(path);
            AnimatorControllerParameter observedParameter = null;
            if (observedController != null) foreach (var candidate in observedController.parameters) if (candidate.name == r.ParameterName) { observedParameter = candidate; break; }
            if (observedParameter == null || observedParameter.type != paramType || (paramType == AnimatorControllerParameterType.Float && r.DefaultValue != null && !Mathf.Approximately(observedParameter.defaultFloat, expectedFloat)) || (paramType == AnimatorControllerParameterType.Int && r.DefaultValue != null && observedParameter.defaultInt != expectedInt) || (paramType == AnimatorControllerParameterType.Bool && r.DefaultValue != null && observedParameter.defaultBool != expectedBool))
                throw AnimationToolSupport.StateMismatch("E_CONTROLLER_ADD_PARAMETER_STATE_MISMATCH", "AnimatorController parameter");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, ParameterName = observedParameter.name, ParameterType = observedParameter.type.ToString(), TotalParameters = observedController.parameters.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-parameter", Path = path, Parameter = observedParameter.name } } });
        }
    }
}
#endif
