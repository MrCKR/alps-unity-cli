#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>animation.state — 读取目标 Animator 的当前状态信息。</summary>
    public class AnimationStateTool : IUnityCliTool<AnimationStateTool.Request, AnimationStateTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public int? Layer { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorInfo Animator { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.state", Summary = "Read the current Animator state for a target GameObject.", Description = "Resolve a target with an Animator and return current state info (layer, state hash, normalized time).", Group = "animation", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read animation state", Input = new Request { Target = "Player" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animation state requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var layer = Mathf.Clamp(r.Layer ?? 0, 0, Mathf.Max(0, AnimationToolSupport.EffectiveAnimatorLayerCount(animator) - 1));
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Animator = AnimationToolSupport.BuildAnimatorInfo(animator, layer) };
        }
    }

    /// <summary>animation.play — 播放指定 Animator 状态。</summary>
    public class AnimationPlayTool : IUnityCliTool<AnimationPlayTool.Request, AnimationPlayTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string State { get; set; } public int? Layer { get; set; } public float? NormalizedTime { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorInfo Animator { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.play", Summary = "Play a named Animator state on a target GameObject.", Description = "Resolve a target with an Animator and play the named state on the given layer.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Play Walk state", Input = new Request { Target = "Player", State = "Walk" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animation play requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var effectiveLayerCount = AnimationToolSupport.EffectiveAnimatorLayerCount(animator);
            if (animator.runtimeAnimatorController == null || effectiveLayerCount <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController assigned or no effective layers: " + GameObjectToolSupport.HierarchyPath(go), "Assign a RuntimeAnimatorController with at least one layer.");
            if (string.IsNullOrEmpty(r.State)) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_REQUIRED", "Animation play requires --state <name>.", "Pass an Animator state name.");
            var layer = r.Layer ?? 0;
            if (layer < 0 || layer >= effectiveLayerCount) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_LAYER_INVALID", "Animator layer is out of range: " + layer, "Run animation.state to inspect layer count.");
            animator.Play(r.State, animator.layerCount > 0 ? layer : 0, r.NormalizedTime ?? 0f);
            animator.Update(0f);
            EditorUtility.SetDirty(animator);
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Animator = AnimationToolSupport.BuildAnimatorInfo(animator, layer), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.play", Target = GameObjectToolSupport.HierarchyPath(go), State = r.State } } };
        }
    }

    /// <summary>animation.stop — 停止/重置目标 Animator。</summary>
    public class AnimationStopTool : IUnityCliTool<AnimationStopTool.Request, AnimationStopTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorInfo Animator { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animation.stop", Summary = "Stop and rebind an Animator on a target GameObject.", Description = "Call Animator.Rebind() and Update(0) to reset the Animator to its initial state.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Stop animation", Input = new Request { Target = "Player" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animation stop requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            animator.Rebind(); animator.Update(0f); EditorUtility.SetDirty(animator);
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Animator = AnimationToolSupport.BuildAnimatorInfo(animator, 0), PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animation.stop", Target = GameObjectToolSupport.HierarchyPath(go) } } };
        }
    }

    /// <summary>animator.get-info — 读取 Animator 详细信息(层/参数/剪辑)。</summary>
    public class AnimatorGetInfoTool : IUnityCliTool<AnimatorGetInfoTool.Request, AnimatorGetInfoTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorInfo Animator { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.get-info", Summary = "Read Animator details: layers, parameters, clips, and runtime controller.", Description = "Resolve a target with an Animator and return enabled, speed, layerCount, parameters, layers, and clips.", Group = "animation", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read animator info", Input = new Request { Target = "Player" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator info requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Animator = AnimationToolSupport.BuildAnimatorInfo(animator, 0) };
        }
    }

    /// <summary>animator.get-parameter — 读取单个 Animator 参数的当前值。</summary>
    public class AnimatorGetParameterTool : IUnityCliTool<AnimatorGetParameterTool.Request, AnimatorGetParameterTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string ParameterName { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorParameterWithValue Parameter { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.get-parameter", Summary = "Read a single Animator parameter's current or default value.", Description = "Resolve a target with an Animator and read a named parameter. Returns runtime value if available, otherwise the controller default.", Group = "animation", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read Speed parameter", Input = new Request { Target = "Player", ParameterName = "Speed" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
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
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Parameter = new AnimatorParameterWithValue { Name = param.name, Type = param.type.ToString(), Value = value } };
        }
    }

    /// <summary>animator.set-parameter — 设置 Animator 参数默认值(编辑模式修改 Controller 资产)。</summary>
    public class AnimatorSetParameterTool : IUnityCliTool<AnimatorSetParameterTool.Request, AnimatorSetParameterTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string ParameterName { get; set; } public string Value { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public AnimatorParameterRef Parameter { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.set-parameter", Summary = "Set an Animator parameter default value by editing the AnimatorController asset.", Description = "In edit mode, modifies the AnimatorController asset parameter defaults. Requires an AnimatorController assigned to the Animator.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set Speed default", Input = new Request { Target = "Player", ParameterName = "Speed", Value = "0.5" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator set-parameter requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            if (string.IsNullOrEmpty(r.ParameterName)) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_PARAMETER_NAME_REQUIRED", "Animator set-parameter requires --parameter-name <name>.", "Pass the name of an Animator parameter.");
            var ac = animator.runtimeAnimatorController as AnimatorController;
            if (ac == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NO_CONTROLLER", "Animator has no AnimatorController assigned: " + GameObjectToolSupport.HierarchyPath(go), "Assign an AnimatorController asset.");
            var param = AnimationToolSupport.FindAnimatorParameter(animator, r.ParameterName);
            if (param == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + r.ParameterName + "' not found: " + GameObjectToolSupport.HierarchyPath(go), "Run animator.get-info to list parameters.");
            Undo.RecordObject(ac, "Set Animator Parameter Default");
            var parameters = ac.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == r.ParameterName)
                {
                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float: parameters[i].defaultFloat = GameObjectToolSupport.ParseFloat(r.Value ?? "0", r.ParameterName); break;
                        case AnimatorControllerParameterType.Int: parameters[i].defaultInt = int.Parse(r.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture); break;
                        case AnimatorControllerParameterType.Bool: parameters[i].defaultBool = bool.Parse(r.Value ?? "false"); break;
                    }
                    break;
                }
            }
            ac.parameters = parameters;
            EditorUtility.SetDirty(ac); AssetDatabase.SaveAssets();
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Parameter = new AnimatorParameterRef { Name = r.ParameterName, Type = param.type.ToString() }, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.set-parameter", Target = GameObjectToolSupport.HierarchyPath(go), Parameter = r.ParameterName } } };
        }
    }

    /// <summary>animator.crossfade — 交叉渐变到指定状态。</summary>
    public class AnimatorCrossfadeTool : IUnityCliTool<AnimatorCrossfadeTool.Request, AnimatorCrossfadeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string State { get; set; } public float? Duration { get; set; } public int? Layer { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public CrossfadeInfo Transition { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.crossfade", Summary = "Cross-fade an Animator to a named state over a duration.", Description = "Resolve a target with an Animator and cross-fade to the named state. duration defaults to 0.25s. layer=-1 uses all layers.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Crossfade to Run", Input = new Request { Target = "Player", State = "Run", Duration = 0.3f } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator crossfade requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var controllerLayers = controller == null || controller.layers == null ? new AnimatorControllerLayer[0] : controller.layers;
            var effectiveLayerCount = animator.layerCount > 0 ? animator.layerCount : controllerLayers.Length;
            if (animator.runtimeAnimatorController == null || effectiveLayerCount <= 0)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController: " + GameObjectToolSupport.HierarchyPath(go), "Assign a RuntimeAnimatorController with at least one layer.");
            if (string.IsNullOrEmpty(r.State)) throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_STATE_REQUIRED", "Animator crossfade requires --state <name>.", "Pass an Animator state name.");
            var duration = r.Duration ?? 0.25f;
            var layer = r.Layer ?? -1;
            if (animator.layerCount <= 0) { animator.Rebind(); animator.Update(0f); }
            animator.CrossFade(r.State, duration, layer);
            animator.Update(0f);
            EditorUtility.SetDirty(animator);
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Transition = new CrossfadeInfo { State = r.State, Duration = duration, Layer = layer }, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.crossfade", Target = GameObjectToolSupport.HierarchyPath(go), State = r.State } } };
        }
    }

    /// <summary>animator.set-speed — 设置 Animator.speed。</summary>
    public class AnimatorSetSpeedTool : IUnityCliTool<AnimatorSetSpeedTool.Request, AnimatorSetSpeedTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public float? Speed { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public double Speed { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.set-speed", Summary = "Set the playback speed of an Animator component.", Description = "Resolve a target with an Animator and set animator.speed. Defaults to 1.0.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set speed to 2x", Input = new Request { Target = "Player", Speed = 2f } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator set-speed requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var speed = r.Speed ?? 1f;
            Undo.RecordObject(animator, "Set Animator Speed");
            animator.speed = speed;
            EditorUtility.SetDirty(animator);
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Speed = speed, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.set-speed", Target = GameObjectToolSupport.HierarchyPath(go), Value = speed.ToString(System.Globalization.CultureInfo.InvariantCulture) } } };
        }
    }

    /// <summary>animator.set-enabled — 启用/禁用 Animator 组件。</summary>
    public class AnimatorSetEnabledTool : IUnityCliTool<AnimatorSetEnabledTool.Request, AnimatorSetEnabledTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public bool? Value { get; set; } }
        public sealed class Result { public GameObjectTargetRef Target { get; set; } public bool Enabled { get; set; } public List<AnimationPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "animator.set-enabled", Summary = "Enable or disable an Animator component on a target GameObject.", Description = "Resolve a target with an Animator and set animator.enabled. Defaults to true.", Group = "animation", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Disable animator", Input = new Request { Target = "Player", Value = false } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = GameObjectToolSupport.ResolveUniqueTarget(r.Target, "Animator set-enabled requires --target <go>.");
            var animator = go.GetComponent<Animator>();
            if (animator == null) throw new UnityCliBridge.BridgeCommandException("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + GameObjectToolSupport.HierarchyPath(go), "Add an Animator component or choose another target.");
            var enabled = r.Value ?? true;
            Undo.RecordObject(animator, "Set Animator Enabled");
            animator.enabled = enabled;
            EditorUtility.SetDirty(animator);
            return new Result { Target = new GameObjectTargetRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go) }, Enabled = enabled, PlannedEffects = new List<AnimationPlannedEffect> { new AnimationPlannedEffect { Type = "animator.set-enabled", Target = GameObjectToolSupport.HierarchyPath(go), Value = enabled.ToString().ToLowerInvariant() } } };
        }
    }

    // ── animator 域 DTO 补充 ──
    public sealed class AnimatorParameterWithValue { public string Name { get; set; } public string Type { get; set; } public object Value { get; set; } }
    public sealed class AnimatorParameterRef { public string Name { get; set; } public string Type { get; set; } }
    public sealed class CrossfadeInfo { public string State { get; set; } public double Duration { get; set; } public long Layer { get; set; } }
}
#endif
