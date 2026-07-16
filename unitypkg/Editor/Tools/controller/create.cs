#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.create — 创建 AnimatorController 资产。</summary>
    public class ControllerCreateTool : IUnityCliTool<ControllerCreateTool.Request, ControllerCreateTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } }
        public sealed class Result { public ControllerInfo Controller { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.create", Summary = "Create a new AnimatorController asset at an Assets/*.controller path.", Description = "Create a new .controller asset with a default Base layer. Refuses to overwrite an existing controller.", Group = "controller", Version = "1.0.0", DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create controller", Input = new Request { ControllerPath = "Assets/Animations/Created.controller" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
                throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_EXISTS", "AnimatorController already exists at: " + path, "Delete it first or use a different path.");
            SceneToolSupport.CreateAssetDirectory(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Controller = new ControllerInfo { Path = path, Guid = AssetDatabase.AssetPathToGUID(path), Name = controller.name, LayerCount = controller.layers.Length, ParameterCount = controller.parameters.Length }, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.create", Path = path } } });
        }
    }
}
#endif
