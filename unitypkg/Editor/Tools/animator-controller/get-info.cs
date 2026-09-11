#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animator-controller.get-info — 读取 Controller 层/状态/参数信息。</summary>
    public class AnimatorControllerGetInfoTool : IUnityCliTool<AnimatorControllerGetInfoTool.Request, AnimatorControllerGetInfoTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } }
        public sealed class Result { public ControllerInfo Controller { get; set; } public List<AnimatorParameterInfo> Parameters { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator-controller.get-info", Summary = "Read AnimatorController layers, states, and parameters.", Description = "Read-only: returns controller path, guid, layer count, parameter count, and parameter definitions.", Group = "animator-controller",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read controller info", Input = new Request { ControllerPath = "Assets/Animations/Player.controller" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with animator-controller.create.");
            var parameters = new List<AnimatorParameterInfo>();
            foreach (var p in controller.parameters) parameters.Add(new AnimatorParameterInfo { Name = p.name, Type = p.type.ToString(), DefaultFloat = p.defaultFloat, DefaultInt = p.defaultInt, DefaultBool = p.defaultBool });
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Controller = new ControllerInfo { Path = path, Guid = AssetDatabase.AssetPathToGUID(path), Name = controller.name, LayerCount = controller.layers.Length, ParameterCount = controller.parameters.Length }, Parameters = parameters });
        }
    }
}
#endif
