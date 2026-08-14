#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>controller.add-transition — 在状态间添加转换。</summary>
    public class ControllerAddTransitionTool : IUnityCliTool<ControllerAddTransitionTool.Request, ControllerAddTransitionTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string FromState { get; set; } public string ToState { get; set; } public int? LayerIndex { get; set; } public bool? HasExitTime { get; set; } public float? Duration { get; set; } public float? ExitTime { get; set; } public string Conditions { get; set; } }
        public sealed class Result { public string Path { get; set; } public string ToState { get; set; } public bool HasExitTime { get; set; } public double Duration { get; set; } public long ConditionCount { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-transition", Summary = "Add a transition between two states in an AnimatorController layer.", Description = "Add a transition from fromState to toState. Supports AnyState as source. conditions is a JSON array of {parameter, mode, threshold}.", Group = "controller",DestructiveHint = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add transition", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", FromState = "Idle", ToState = "Walk" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.FromState) || string.IsNullOrEmpty(r.ToState)) throw new UnityCliBridge.BridgeCommandException("E_STATE_NAME_REQUIRED", "controller.add-transition requires fromState and toState.", "Pass source and destination state names.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect available layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            var isAnyState = string.Equals(r.FromState, "AnyState", System.StringComparison.OrdinalIgnoreCase) || string.Equals(r.FromState, "Any", System.StringComparison.OrdinalIgnoreCase);
            var toState = AnimationToolSupport.FindStateInLayer(sm, r.ToState);
            if (toState == null) throw new UnityCliBridge.BridgeCommandException("E_STATE_NOT_FOUND", "Destination state '" + r.ToState + "' not found in layer " + layerIndex + ".", "Run controller.get-info to inspect available states.");
            var fromState = isAnyState ? null : AnimationToolSupport.FindStateInLayer(sm, r.FromState);
            if (!isAnyState && fromState == null) throw new UnityCliBridge.BridgeCommandException("E_STATE_NOT_FOUND", "Source state '" + r.FromState + "' not found in layer " + layerIndex + ".", "Run controller.get-info to inspect available states.");
            ctx.CommitEffect();
            Undo.RecordObject(controller, "Add Animator Transition");
            AnimatorStateTransition transition;
            var fromStateName = r.FromState;
            if (isAnyState) { transition = sm.AddAnyStateTransition(toState); fromStateName = "AnyState"; }
            else transition = fromState.AddTransition(toState);
            transition.hasExitTime = r.HasExitTime ?? true;
            transition.duration = r.Duration ?? 0.25f;
            transition.exitTime = r.ExitTime ?? 0.75f;
            var conditionCount = 0;
            if (!string.IsNullOrEmpty(r.Conditions))
            {
                foreach (var condStr in ExtractArrayItems(r.Conditions))
                {
                    var pName = ExtractJsonField(condStr, "parameter");
                    var modeStr = ExtractJsonField(condStr, "mode") ?? "greater";
                    var threshold = ExtractJsonFloat(condStr, "threshold", 0f);
                    if (string.IsNullOrEmpty(pName)) continue;
                    AnimatorConditionMode mode;
                    switch (modeStr.ToLowerInvariant()) { case "less": mode = AnimatorConditionMode.Less; break; case "equals": mode = AnimatorConditionMode.Equals; break; case "notequal": case "not_equal": mode = AnimatorConditionMode.NotEqual; break; case "if": case "true": mode = AnimatorConditionMode.If; break; case "ifnot": case "if_not": case "false": mode = AnimatorConditionMode.IfNot; break; default: mode = AnimatorConditionMode.Greater; break; }
                    transition.AddCondition(mode, threshold, pName); conditionCount++;
                }
            }
            EditorUtility.SetDirty(controller);
            var observedController = AnimationToolSupport.ReloadControllerAsset(path);
            var observedMachine = observedController == null ? null : observedController.layers[layerIndex].stateMachine;
            var observedDestination = observedMachine == null ? null : AnimationToolSupport.FindStateInLayer(observedMachine, r.ToState);
            AnimatorStateTransition observedTransition = null;
            if (observedMachine != null && observedDestination != null)
            {
                var transitions = isAnyState ? observedMachine.anyStateTransitions : AnimationToolSupport.FindStateInLayer(observedMachine, r.FromState).transitions;
                foreach (var candidate in transitions) if (candidate.destinationState == observedDestination && candidate.hasExitTime == (r.HasExitTime ?? true) && Mathf.Approximately(candidate.duration, r.Duration ?? 0.25f) && candidate.conditions.Length == conditionCount) { observedTransition = candidate; break; }
            }
            if (observedTransition == null) throw AnimationToolSupport.StateMismatch("E_CONTROLLER_TRANSITION_STATE_MISMATCH", "AnimatorController transition");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Path = path, ToState = observedDestination.name, HasExitTime = observedTransition.hasExitTime, Duration = observedTransition.duration, ConditionCount = observedTransition.conditions.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-transition", Path = path, State = fromStateName + "->" + observedDestination.name } } });
        }
        internal static string[] ExtractArrayItems(string json) { var items = new List<string>(); var depth = 0; var start = -1; for (var i = 0; i < json.Length; i++) { if (json[i] == '{') { if (depth == 0) start = i; depth++; } else if (json[i] == '}') { depth--; if (depth == 0 && start >= 0) { items.Add(json.Substring(start, i - start + 1)); start = -1; } } } return items.ToArray(); }
        internal static string ExtractJsonField(string json, string field) { var key = "\"" + field + "\""; var idx = json.IndexOf(key, System.StringComparison.OrdinalIgnoreCase); if (idx < 0) return null; idx = json.IndexOf(':', idx + key.Length); if (idx < 0) return null; idx++; while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++; if (idx >= json.Length) return null; if (json[idx] == '"') { var end = json.IndexOf('"', idx + 1); return end > idx ? json.Substring(idx + 1, end - idx - 1) : null; } var endIdx = idx; while (endIdx < json.Length && json[endIdx] != ',' && json[endIdx] != '}' && !char.IsWhiteSpace(json[endIdx])) endIdx++; return json.Substring(idx, endIdx - idx); }
        internal static float ExtractJsonFloat(string json, string field, float fallback) { var val = ExtractJsonField(json, field); float f; return float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fallback; }
    }
}
#endif
