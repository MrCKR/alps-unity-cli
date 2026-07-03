#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    // ── controller.* AnimatorController editing tools ──

    /// <summary>controller.create — 创建 AnimatorController 资产。</summary>
    public class ControllerCreateTool : IUnityCliTool<ControllerCreateTool.Request, ControllerCreateTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } }
        public sealed class Result { public ControllerInfo Controller { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.create", Summary = "Create a new AnimatorController asset at an Assets/*.controller path.", Description = "Create a new .controller asset with a default Base layer. Refuses to overwrite an existing controller.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create controller", Input = new Request { ControllerPath = "Assets/Animations/Player.controller" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
                throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_EXISTS", "AnimatorController already exists at: " + path, "Delete it first or use a different path.");
            SceneToolSupport.CreateAssetDirectory(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            return new Result { Controller = new ControllerInfo { Path = path, Guid = AssetDatabase.AssetPathToGUID(path), Name = controller.name, LayerCount = controller.layers.Length, ParameterCount = controller.parameters.Length }, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.create", Path = path } } };
        }
    }

    /// <summary>controller.add-state — 在指定层添加状态。</summary>
    public class ControllerAddStateTool : IUnityCliTool<ControllerAddStateTool.Request, ControllerAddStateTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string StateName { get; set; } public string ClipPath { get; set; } public int? LayerIndex { get; set; } public float? Speed { get; set; } public bool? IsDefault { get; set; } }
        public sealed class Result { public string Path { get; set; } public string StateName { get; set; } public long LayerIndex { get; set; } public bool HasMotion { get; set; } public string MotionName { get; set; } public double Speed { get; set; } public bool IsDefault { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-state", Summary = "Add a new state to an AnimatorController layer.", Description = "Add a state to the specified layer. Optionally assign a motion clip and set as default state.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add Walk state", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "Walk" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.StateName)) throw new UnityCliBridge.BridgeCommandException("E_STATE_NAME_REQUIRED", "controller.add-state requires stateName.", "Pass a name for the new state.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect available layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            if (AnimationToolSupport.FindStateInLayer(sm, r.StateName) != null) throw new UnityCliBridge.BridgeCommandException("E_STATE_EXISTS", "State '" + r.StateName + "' already exists in layer " + layerIndex + ".", "Choose a different state name.");
            AnimationClip clip = null;
            if (!string.IsNullOrEmpty(r.ClipPath)) { clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(SceneToolSupport.NormalizeAssetPath(r.ClipPath)); if (clip == null) throw new UnityCliBridge.BridgeCommandException("E_CLIP_NOT_FOUND", "AnimationClip not found at: " + r.ClipPath, "Create the clip first or omit clipPath."); }
            Undo.RecordObject(controller, "Add Animator State");
            var state = sm.AddState(r.StateName);
            if (clip != null) state.motion = clip;
            state.speed = r.Speed ?? 1f;
            var isDefault = r.IsDefault ?? false;
            if (isDefault) sm.defaultState = state;
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new Result { Path = path, StateName = r.StateName, LayerIndex = layerIndex, HasMotion = state.motion != null, MotionName = state.motion?.name, Speed = state.speed, IsDefault = isDefault, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-state", Path = path, State = r.StateName } } };
        }
    }

    /// <summary>controller.add-transition — 在状态间添加转换。</summary>
    public class ControllerAddTransitionTool : IUnityCliTool<ControllerAddTransitionTool.Request, ControllerAddTransitionTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string FromState { get; set; } public string ToState { get; set; } public int? LayerIndex { get; set; } public bool? HasExitTime { get; set; } public float? Duration { get; set; } public float? ExitTime { get; set; } public string Conditions { get; set; } }
        public sealed class Result { public string Path { get; set; } public string FromState { get; set; } public string ToState { get; set; } public bool HasExitTime { get; set; } public double Duration { get; set; } public long ConditionCount { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-transition", Summary = "Add a transition between two states in an AnimatorController layer.", Description = "Add a transition from fromState to toState. Supports AnyState as source. conditions is a JSON array of {parameter, mode, threshold}.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add transition", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", FromState = "Idle", ToState = "Walk" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
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
            Undo.RecordObject(controller, "Add Animator Transition");
            AnimatorStateTransition transition;
            var fromStateName = r.FromState;
            if (isAnyState) { transition = sm.AddAnyStateTransition(toState); fromStateName = "AnyState"; }
            else { var fromState = AnimationToolSupport.FindStateInLayer(sm, r.FromState); if (fromState == null) throw new UnityCliBridge.BridgeCommandException("E_STATE_NOT_FOUND", "Source state '" + r.FromState + "' not found in layer " + layerIndex + ".", "Run controller.get-info to inspect available states."); transition = fromState.AddTransition(toState); }
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
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new Result { Path = path, FromState = fromStateName, ToState = r.ToState, HasExitTime = transition.hasExitTime, Duration = transition.duration, ConditionCount = conditionCount, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-transition", Path = path, State = fromStateName + "->" + r.ToState } } };
        }
        internal static string[] ExtractArrayItems(string json) { var items = new List<string>(); var depth = 0; var start = -1; for (var i = 0; i < json.Length; i++) { if (json[i] == '{') { if (depth == 0) start = i; depth++; } else if (json[i] == '}') { depth--; if (depth == 0 && start >= 0) { items.Add(json.Substring(start, i - start + 1)); start = -1; } } } return items.ToArray(); }
        internal static string ExtractJsonField(string json, string field) { var key = "\"" + field + "\""; var idx = json.IndexOf(key, System.StringComparison.OrdinalIgnoreCase); if (idx < 0) return null; idx = json.IndexOf(':', idx + key.Length); if (idx < 0) return null; idx++; while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++; if (idx >= json.Length) return null; if (json[idx] == '"') { var end = json.IndexOf('"', idx + 1); return end > idx ? json.Substring(idx + 1, end - idx - 1) : null; } var endIdx = idx; while (endIdx < json.Length && json[endIdx] != ',' && json[endIdx] != '}' && !char.IsWhiteSpace(json[endIdx])) endIdx++; return json.Substring(idx, endIdx - idx); }
        internal static float ExtractJsonFloat(string json, string field, float fallback) { var val = ExtractJsonField(json, field); float f; return float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fallback; }
    }

    /// <summary>controller.add-parameter — 添加参数到 Controller。</summary>
    public class ControllerAddParameterTool : IUnityCliTool<ControllerAddParameterTool.Request, ControllerAddParameterTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string ParameterName { get; set; } public string ParameterType { get; set; } public string DefaultValue { get; set; } }
        public sealed class Result { public string Path { get; set; } public string ParameterName { get; set; } public string ParameterType { get; set; } public long TotalParameters { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-parameter", Summary = "Add a parameter to an AnimatorController asset.", Description = "Add a float/int/bool/trigger parameter. Optionally set a default value.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add Speed parameter", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", ParameterName = "Speed", ParameterType = "float" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
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
            return new Result { Path = path, ParameterName = r.ParameterName, ParameterType = typeStr, TotalParameters = controller.parameters.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-parameter", Path = path, Parameter = r.ParameterName } } };
        }
    }

    /// <summary>controller.get-info — 读取 Controller 层/状态/参数信息。</summary>
    public class ControllerGetInfoTool : IUnityCliTool<ControllerGetInfoTool.Request, ControllerGetInfoTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } }
        public sealed class Result { public ControllerInfo Controller { get; set; } public List<AnimatorParameterInfo> Parameters { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.get-info", Summary = "Read AnimatorController layers, states, and parameters.", Description = "Read-only: returns controller path, guid, layer count, parameter count, and parameter definitions.", Group = "controller", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read controller info", Input = new Request { ControllerPath = "Assets/Animations/Player.controller" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            var parameters = new List<AnimatorParameterInfo>();
            foreach (var p in controller.parameters) parameters.Add(new AnimatorParameterInfo { Name = p.name, Type = p.type.ToString(), DefaultFloat = p.defaultFloat, DefaultInt = p.defaultInt, DefaultBool = p.defaultBool });
            return new Result { Controller = new ControllerInfo { Path = path, Guid = AssetDatabase.AssetPathToGUID(path), Name = controller.name, LayerCount = controller.layers.Length, ParameterCount = controller.parameters.Length }, Parameters = parameters };
        }
    }

    /// <summary>controller.assign — 将 Controller 分配到目标 Animator。</summary>
    public class ControllerAssignTool : IUnityCliTool<ControllerAssignTool.Request, ControllerAssignTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string Target { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public string ControllerName { get; set; } public string ControllerPath { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.assign", Summary = "Assign an AnimatorController asset to a target GameObject's Animator.", Description = "Resolve a target GameObject, add an Animator if missing, and assign the controller asset.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Assign controller", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", Target = "Player" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "controller.assign requires target.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) animator = Undo.AddComponent<Animator>(go);
            Undo.RecordObject(animator, "Assign AnimatorController");
            animator.runtimeAnimatorController = controller;
            EditorUtility.SetDirty(animator); AssetDatabase.SaveAssets();
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, ControllerName = controller.name, ControllerPath = path, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.assign", Path = path, Target = GameObjectToolSupport.HierarchyPath(go) } } };
        }
    }

    /// <summary>controller.add-layer — 添加层到 Controller。</summary>
    public class ControllerAddLayerTool : IUnityCliTool<ControllerAddLayerTool.Request, ControllerAddLayerTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string LayerName { get; set; } public float? Weight { get; set; } public string BlendingMode { get; set; } }
        public sealed class Result { public string Path { get; set; } public string LayerName { get; set; } public long LayerIndex { get; set; } public double Weight { get; set; } public string BlendingMode { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-layer", Summary = "Add a new layer to an AnimatorController.", Description = "Add a layer with a name, weight (default 1), and blending mode (override or additive).", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add layer", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", LayerName = "UpperBody" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.LayerName)) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NAME_REQUIRED", "controller.add-layer requires layerName.", "Pass a name for the new layer.");
            var weight = r.Weight ?? 1f;
            var blendingMode = (r.BlendingMode ?? "override").ToLowerInvariant() == "additive" ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            Undo.RecordObject(controller, "Add Layer");
            controller.AddLayer(r.LayerName);
            var layers = controller.layers;
            var newLayer = layers[layers.Length - 1];
            newLayer.defaultWeight = weight; newLayer.blendingMode = blendingMode;
            layers[layers.Length - 1] = newLayer; controller.layers = layers;
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new Result { Path = path, LayerName = r.LayerName, LayerIndex = layers.Length - 1, Weight = weight, BlendingMode = blendingMode.ToString(), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-layer", Path = path } } };
        }
    }

    /// <summary>controller.remove-layer — 移除 Controller 层。</summary>
    public class ControllerRemoveLayerTool : IUnityCliTool<ControllerRemoveLayerTool.Request, ControllerRemoveLayerTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public int? LayerIndex { get; set; } public string LayerName { get; set; } }
        public sealed class Result { public string Path { get; set; } public long RemovedLayerIndex { get; set; } public string RemovedLayerName { get; set; } public long RemainingLayerCount { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.remove-layer", Summary = "Remove a layer from an AnimatorController by index or name.", Description = "Remove a layer. The base layer (index 0) cannot be removed. Pass layerIndex or layerName.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Remove layer", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", LayerIndex = 1 } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (!r.LayerIndex.HasValue && string.IsNullOrEmpty(r.LayerName)) throw new UnityCliBridge.BridgeCommandException("E_LAYER_IDENTIFIER_REQUIRED", "controller.remove-layer requires layerIndex or layerName.", "Pass either a layer index or a layer name.");
            var removeIndex = -1; var removeName = "";
            if (r.LayerIndex.HasValue) { removeIndex = r.LayerIndex.Value; if (removeIndex < 0 || removeIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer index out of range: " + removeIndex, "Run controller.get-info to inspect layers."); removeName = controller.layers[removeIndex].name; }
            else { for (var i = 0; i < controller.layers.Length; i++) if (controller.layers[i].name == r.LayerName) { removeIndex = i; removeName = r.LayerName; break; } if (removeIndex < 0) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer not found: " + r.LayerName, "Run controller.get-info to inspect layers."); }
            if (removeIndex == 0) throw new UnityCliBridge.BridgeCommandException("E_LAYER_BASE_IMMUTABLE", "The base layer (index 0) cannot be removed.", "Remove a non-base layer instead.");
            Undo.RecordObject(controller, "Remove Layer");
            var layers = new List<AnimatorControllerLayer>(controller.layers); layers.RemoveAt(removeIndex); controller.layers = layers.ToArray();
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new Result { Path = path, RemovedLayerIndex = removeIndex, RemovedLayerName = removeName, RemainingLayerCount = controller.layers.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.remove-layer", Path = path } } };
        }
    }

    /// <summary>controller.set-layer-weight — 设置层权重。</summary>
    public class ControllerSetLayerWeightTool : IUnityCliTool<ControllerSetLayerWeightTool.Request, ControllerSetLayerWeightTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public int? LayerIndex { get; set; } public string LayerName { get; set; } public float? Weight { get; set; } }
        public sealed class Result { public string Path { get; set; } public long LayerIndex { get; set; } public string LayerName { get; set; } public double Weight { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.set-layer-weight", Summary = "Set the default weight of an AnimatorController layer.", Description = "Set the default weight of a layer by index or name. Weight 0 disables the layer.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set layer weight", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", LayerIndex = 1, Weight = 0.5f } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            var idx = -1;
            if (r.LayerIndex.HasValue) idx = r.LayerIndex.Value;
            else if (!string.IsNullOrEmpty(r.LayerName)) { for (var i = 0; i < controller.layers.Length; i++) if (controller.layers[i].name == r.LayerName) { idx = i; break; } }
            if (idx < 0 || idx >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_NOT_FOUND", "Layer not found.", "Run controller.get-info to inspect layers.");
            var weight = r.Weight ?? 1f;
            Undo.RecordObject(controller, "Set Layer Weight");
            var layers = controller.layers; layers[idx].defaultWeight = weight; controller.layers = layers;
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new Result { Path = path, LayerIndex = idx, LayerName = controller.layers[idx].name, Weight = weight, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.set-layer-weight", Path = path } } };
        }
    }

    /// <summary>controller.create-blend-tree-1d — 创建 1D BlendTree 状态。</summary>
    public class ControllerCreateBlendTree1DTool : IUnityCliTool<ControllerCreateBlendTree1DTool.Request, ControllerCreateBlendTree1DTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string StateName { get; set; } public string BlendParameter { get; set; } public int? LayerIndex { get; set; } }
        public sealed class Result { public string Path { get; set; } public string StateName { get; set; } public string BlendParameter { get; set; } public long LayerIndex { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.create-blend-tree-1d", Summary = "Create a 1D BlendTree state in an AnimatorController layer.", Description = "Add a new state with a 1D BlendTree motion using the specified blend parameter.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create 1D blend tree", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "Move", BlendParameter = "Speed" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.StateName)) throw new UnityCliBridge.BridgeCommandException("E_STATE_NAME_REQUIRED", "controller.create-blend-tree-1d requires stateName.", "Pass a name for the blend tree state.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            if (AnimationToolSupport.FindStateInLayer(sm, r.StateName) != null) throw new UnityCliBridge.BridgeCommandException("E_STATE_EXISTS", "State '" + r.StateName + "' already exists.", "Choose a different state name.");
            Undo.RecordObject(controller, "Create BlendTree 1D");
            var blendTree = new BlendTree { name = r.StateName, blendType = BlendTreeType.Simple1D, blendParameter = r.BlendParameter ?? "" };
            AssetDatabase.AddObjectToAsset(blendTree, controller);
            var state = sm.AddState(r.StateName); state.motion = blendTree;
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new Result { Path = path, StateName = r.StateName, BlendParameter = r.BlendParameter, LayerIndex = layerIndex, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.create-blend-tree-1d", Path = path, State = r.StateName } } };
        }
    }

    /// <summary>controller.create-blend-tree-2d — 创建 2D BlendTree 状态。</summary>
    public class ControllerCreateBlendTree2DTool : IUnityCliTool<ControllerCreateBlendTree2DTool.Request, ControllerCreateBlendTree2DTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string StateName { get; set; } public string BlendParameterX { get; set; } public string BlendParameterY { get; set; } public int? LayerIndex { get; set; } public string BlendType { get; set; } }
        public sealed class Result { public string Path { get; set; } public string StateName { get; set; } public string BlendType { get; set; } public long LayerIndex { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.create-blend-tree-2d", Summary = "Create a 2D BlendTree state in an AnimatorController layer.", Description = "Add a new state with a 2D BlendTree motion. blendType: simpledirectional2d, simpledirectional, freesystemlessdirectional.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create 2D blend tree", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "Locomotion", BlendParameterX = "SpeedX", BlendParameterY = "SpeedY" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            if (string.IsNullOrEmpty(r.StateName)) throw new UnityCliBridge.BridgeCommandException("E_STATE_NAME_REQUIRED", "controller.create-blend-tree-2d requires stateName.", "Pass a name for the blend tree state.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            if (AnimationToolSupport.FindStateInLayer(sm, r.StateName) != null) throw new UnityCliBridge.BridgeCommandException("E_STATE_EXISTS", "State '" + r.StateName + "' already exists.", "Choose a different state name.");
            var bt = (r.BlendType ?? "simpledirectional2d").ToLowerInvariant();
            var blendType = bt == "freedirectional" || bt == "freesystemlessdirectional" ? BlendTreeType.FreeformDirectional2D : bt == "freecartesian" || bt == "freesystemlesscartesian" ? BlendTreeType.FreeformCartesian2D : BlendTreeType.SimpleDirectional2D;
            Undo.RecordObject(controller, "Create BlendTree 2D");
            var blendTree = new BlendTree { name = r.StateName, blendType = blendType, blendParameter = r.BlendParameterX ?? "", blendParameterY = r.BlendParameterY ?? "" };
            AssetDatabase.AddObjectToAsset(blendTree, controller);
            var state = sm.AddState(r.StateName); state.motion = blendTree;
            EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new Result { Path = path, StateName = r.StateName, BlendType = blendType.ToString(), LayerIndex = layerIndex, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.create-blend-tree-2d", Path = path, State = r.StateName } } };
        }
    }

    /// <summary>controller.add-blend-tree-child — 向 BlendTree 添加子动画。</summary>
    public class ControllerAddBlendTreeChildTool : IUnityCliTool<ControllerAddBlendTreeChildTool.Request, ControllerAddBlendTreeChildTool.Result>
    {
        public sealed class Request { public string ControllerPath { get; set; } public string StateName { get; set; } public string ClipPath { get; set; } public int? LayerIndex { get; set; } public float? Threshold { get; set; } public float? PositionX { get; set; } public float? PositionY { get; set; } }
        public sealed class Result { public string Path { get; set; } public string StateName { get; set; } public long ChildCount { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "controller.add-blend-tree-child", Summary = "Add a child motion to an existing BlendTree state.", Description = "Add an AnimationClip as a child to a BlendTree state. threshold for 1D, positionX/positionY for 2D.", Group = "controller", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add blend tree child", Input = new Request { ControllerPath = "Assets/Animations/Player.controller", StateName = "Move", ClipPath = "Assets/Animations/Walk.anim", Threshold = 0.5f } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = AnimationToolSupport.RequireControllerPath(r.ControllerPath);
            var controller = AnimationToolSupport.LoadControllerAsset(path);
            if (controller == null) throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + path, "Check the path or create one with controller.create.");
            var layerIndex = r.LayerIndex ?? 0;
            if (layerIndex < 0 || layerIndex >= controller.layers.Length) throw new UnityCliBridge.BridgeCommandException("E_LAYER_INDEX_INVALID", "Layer index out of range.", "Run controller.get-info to inspect layers.");
            var sm = controller.layers[layerIndex].stateMachine;
            var state = AnimationToolSupport.FindStateInLayer(sm, r.StateName);
            if (state == null) throw new UnityCliBridge.BridgeCommandException("E_STATE_NOT_FOUND", "State '" + r.StateName + "' not found in layer " + layerIndex + ".", "Run controller.get-info to inspect states.");
            var blendTree = state.motion as BlendTree;
            if (blendTree == null) throw new UnityCliBridge.BridgeCommandException("E_NOT_A_BLEND_TREE", "State '" + r.StateName + "' does not have a BlendTree motion.", "Use controller.create-blend-tree-1d or create-blend-tree-2d first.");
            if (string.IsNullOrEmpty(r.ClipPath)) throw new UnityCliBridge.BridgeCommandException("E_CLIP_PATH_REQUIRED", "controller.add-blend-tree-child requires clipPath.", "Pass an Assets/*.anim AnimationClip path.");
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(SceneToolSupport.NormalizeAssetPath(r.ClipPath));
            if (clip == null) throw new UnityCliBridge.BridgeCommandException("E_CLIP_NOT_FOUND", "AnimationClip not found at: " + r.ClipPath, "Create the clip first.");
            Undo.RecordObject(blendTree, "Add BlendTree Child");
            blendTree.AddChild(clip, r.Threshold ?? 0f);
            if (r.PositionX.HasValue || r.PositionY.HasValue)
            {
                var children = blendTree.children;
                var last = children[children.Length - 1];
                last.position = new Vector2(r.PositionX ?? 0f, r.PositionY ?? 0f);
                children[children.Length - 1] = last;
                blendTree.children = children;
            }
            EditorUtility.SetDirty(blendTree); EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
            return new Result { Path = path, StateName = r.StateName, ChildCount = blendTree.children.Length, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "controller.add-blend-tree-child", Path = path, State = r.StateName } } };
        }
    }
}
#endif
