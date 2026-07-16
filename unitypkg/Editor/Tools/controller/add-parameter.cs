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
            Undo.RecordObject(controller, "Add Animator Parameter");
            controller.AddParameter(r.ParameterName, paramType);
            if (r.DefaultValue != null) { var allParams = controller.parameters; var added = allParams[allParams.Length - 1]; if (paramType == AnimatorControllerParameterType.Float) { float f; if (float.TryParse(r.DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) added.defaultFloat = f; } else if (paramType == AnimatorControllerParameterType.Int) { int i; if (int.TryParse(r.DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) added.defaultInt = i; } else if (paramType == AnimatorControllerParameterType.Bool) { added.defaultBool = string.Equals(r.DefaultValue, "true", System.StringComparison.OrdinalIgnoreCase) || r.DefaultValue == "1"; } controller.parameters = allParams; }
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, ParameterName = r.ParameterName, ParameterType = typeStr, TotalParameters = controller.parameters.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-parameter", Path = path, Parameter = r.ParameterName } } });
        }
    }
}
#endif
