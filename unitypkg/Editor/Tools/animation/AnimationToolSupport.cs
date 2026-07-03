#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// animation 域 IUnityCliTool 共享 helper:AnimationClip 资产加载/设置、
    /// Animator 信息构造、AnimatorController 资产操作。仅供 animation 域自包含工具复用。
    /// </summary>
    public static class AnimationToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        /// <summary>规范化并校验 Assets/*.anim 路径。</summary>
        public static string RequireAnimationClipPath(string rawPath)
        {
            var path = SceneToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_PATH_REQUIRED", "Pass an Assets/*.anim path.", "Use tools run animation.clip --input '{\"path\":\"Assets/Animations/Example.anim\"}'.");
            if (!path.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".anim", System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_PATH_INVALID", "AnimationClip path must be under Assets/ and end with .anim: " + path, "Pass a Unity asset path such as Assets/Animations/Player.anim.");
            return path;
        }

        /// <summary>加载 AnimationClip 资产;不存在抛异常。</summary>
        public static AnimationClip LoadAnimationClipAsset(string path)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_NOT_FOUND", "AnimationClip asset not found: " + path, "Check the path or create one with animation.clip.create.");
            return clip;
        }

        /// <summary>规范化并校验 Assets/*.controller 路径。</summary>
        public static string RequireControllerPath(string rawPath)
        {
            var path = SceneToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_PATH_REQUIRED", "Pass --controller-path <Assets/...>.controller>.", "Use controller.create to create one first.");
            if (!path.EndsWith(".controller", System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_PATH_INVALID", "AnimatorController path must end with .controller: " + path, "Pass a Unity asset path such as Assets/Animations/Player.controller.");
            return path;
        }

        /// <summary>加载 AnimatorController 资产。</summary>
        public static AnimatorController LoadControllerAsset(string controllerPath)
        {
            var path = SceneToolSupport.NormalizeAssetPath(controllerPath);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        }

        /// <summary>读取 Animator 可用层数量;运行态缓存未初始化时回退到 AnimatorController 资产层。</summary>
        public static int EffectiveAnimatorLayerCount(Animator animator)
        {
            if (animator == null) return 0;
            if (animator.layerCount > 0) return animator.layerCount;
            var controller = animator.runtimeAnimatorController as AnimatorController;
            return controller == null || controller.layers == null ? 0 : controller.layers.Length;
        }

        /// <summary>读取 Animator 参数定义;运行态缓存为空时回退到 AnimatorController 资产。</summary>
        public static AnimatorControllerParameter[] AnimatorParameterDefinitions(Animator animator)
        {
            if (animator == null) return new AnimatorControllerParameter[0];
            if (animator.parameterCount > 0)
            {
                var arr = new AnimatorControllerParameter[animator.parameterCount];
                for (var i = 0; i < animator.parameterCount; i++) arr[i] = animator.GetParameter(i);
                return arr;
            }
            var controller = animator.runtimeAnimatorController as AnimatorController;
            return controller == null || controller.parameters == null ? new AnimatorControllerParameter[0] : controller.parameters;
        }

        /// <summary>按名称查找 Animator 参数定义。</summary>
        public static AnimatorControllerParameter FindAnimatorParameter(Animator animator, string parameterName)
        {
            foreach (var p in AnimatorParameterDefinitions(animator))
                if (p != null && p.name == parameterName) return p;
            return null;
        }

        /// <summary>按短名或 Base.Layer.State 路径查找 Controller 状态。</summary>
        public static AnimatorState FindStateInLayer(AnimatorStateMachine sm, string stateName)
        {
            if (sm == null || sm.states == null) return null;
            foreach (var cs in sm.states)
            {
                if (cs.state == null) continue;
                if (cs.state.name == stateName) return cs.state;
                if (!string.IsNullOrEmpty(stateName) && stateName.EndsWith("." + cs.state.name, System.StringComparison.Ordinal)) return cs.state;
            }
            return null;
        }

        /// <summary>构造 AnimationClip 信息 DTO。</summary>
        public static AnimationClipInfo BuildAnimationClipInfo(string path, AnimationClip clip, int maxCurveKeys)
        {
            var keyLimit = Mathf.Clamp(maxCurveKeys, 0, 100);
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var events = AnimationUtility.GetAnimationEvents(clip);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var bindingList = new List<AnimationBindingInfo>();
            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                var keys = curve == null ? new Keyframe[0] : curve.keys;
                var keyList = new List<AnimationKeyInfo>();
                for (var i = 0; i < Mathf.Min(keys.Length, keyLimit); i++)
                {
                    keyList.Add(new AnimationKeyInfo { Time = keys[i].time, Value = keys[i].value, InTangent = keys[i].inTangent, OutTangent = keys[i].outTangent });
                }
                bindingList.Add(new AnimationBindingInfo { Path = binding.path, Property = binding.propertyName, Type = binding.type == null ? null : binding.type.FullName, KeyCount = keys.Length, KeysTruncated = keys.Length > keyLimit, Keys = keyList });
            }
            var eventList = new List<AnimationEventInfo>();
            foreach (var evt in events)
            {
                eventList.Add(new AnimationEventInfo { Time = evt.time, FunctionName = evt.functionName, StringParameter = evt.stringParameter, FloatParameter = evt.floatParameter, IntParameter = evt.intParameter });
            }
            return new AnimationClipInfo
            {
                Path = path, Guid = AssetDatabase.AssetPathToGUID(path), Name = clip.name, Length = clip.length,
                FrameRate = clip.frameRate, Legacy = clip.legacy, WrapMode = clip.wrapMode.ToString(),
                Settings = new AnimationClipSettingsInfo { StartTime = settings.startTime, StopTime = settings.stopTime, LoopTime = settings.loopTime, LoopBlend = settings.loopBlend },
                Bindings = new AnimationBindingList { Count = bindings.Length, MaxCurveKeys = keyLimit, Items = bindingList },
                Events = new AnimationEventList { Count = events.Length, Items = eventList }
            };
        }

        /// <summary>构造 Animator 信息 DTO。</summary>
        public static AnimatorInfo BuildAnimatorInfo(Animator animator, int layer)
        {
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var controllerPath = animator.runtimeAnimatorController == null ? null : SceneToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController));
            var controllerLayers = controller == null || controller.layers == null ? new AnimatorControllerLayer[0] : controller.layers;
            var parameterDefinitions = AnimatorParameterDefinitions(animator);
            var runtimeLayerCount = animator.layerCount;
            var layerCount = runtimeLayerCount > 0 ? runtimeLayerCount : controllerLayers.Length;
            var safeLayer = layerCount <= 0 ? 0 : Mathf.Clamp(layer, 0, layerCount - 1);
            var state = runtimeLayerCount > 0 && safeLayer < runtimeLayerCount ? animator.GetCurrentAnimatorStateInfo(safeLayer) : default(AnimatorStateInfo);
            var parameters = new List<AnimatorParameterInfo>();
            foreach (var p in parameterDefinitions)
            {
                if (p == null) continue;
                parameters.Add(new AnimatorParameterInfo { Name = p.name, Type = p.type.ToString(), DefaultFloat = p.defaultFloat, DefaultInt = p.defaultInt, DefaultBool = p.defaultBool });
            }
            var layers = new List<AnimatorLayerInfo>();
            for (var i = 0; i < layerCount; i++)
            {
                var hasRuntime = i < runtimeLayerCount;
                var hasController = i < controllerLayers.Length;
                var stateInfo = hasRuntime ? animator.GetCurrentAnimatorStateInfo(i) : default(AnimatorStateInfo);
                layers.Add(new AnimatorLayerInfo { Index = i, Name = hasController ? controllerLayers[i].name : null, Weight = hasRuntime ? animator.GetLayerWeight(i) : hasController ? controllerLayers[i].defaultWeight : 0f, CurrentStateHash = stateInfo.fullPathHash, CurrentStateNormalizedTime = stateInfo.normalizedTime, CurrentStateLength = stateInfo.length, IsInTransition = hasRuntime && animator.IsInTransition(i) });
            }
            var clips = new List<AnimatorClipInfo>();
            if (animator.runtimeAnimatorController != null)
            {
                var animationClips = animator.runtimeAnimatorController.animationClips;
                if (animationClips != null)
                    foreach (var clip in animationClips)
                        if (clip != null)
                            clips.Add(new AnimatorClipInfo { Name = clip.name, Length = clip.length, FrameRate = clip.frameRate, IsLooping = clip.isLooping });
            }
            return new AnimatorInfo
            {
                Enabled = animator.enabled, Speed = animator.speed, ApplyRootMotion = animator.applyRootMotion,
                UpdateMode = animator.updateMode.ToString(), CullingMode = animator.cullingMode.ToString(),
                LayerCount = layerCount, ParameterCount = parameterDefinitions.Length,
                SelectedLayer = safeLayer,
                RuntimeAnimatorController = new ControllerRef { Name = animator.runtimeAnimatorController == null ? null : animator.runtimeAnimatorController.name, Path = controllerPath, Guid = string.IsNullOrEmpty(controllerPath) ? null : AssetDatabase.AssetPathToGUID(controllerPath) },
                CurrentState = new AnimatorStateRef { FullPathHash = state.fullPathHash, ShortNameHash = state.shortNameHash, NormalizedTime = state.normalizedTime, Length = state.length },
                Parameters = parameters, Layers = layers, Clips = clips
            };
        }
    }

    // ── animation 域 DTO ──

    public sealed class AnimationClipInfo
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public double Length { get; set; }
        public double FrameRate { get; set; }
        public bool Legacy { get; set; }
        public string WrapMode { get; set; }
        public AnimationClipSettingsInfo Settings { get; set; }
        public AnimationBindingList Bindings { get; set; }
        public AnimationEventList Events { get; set; }
    }

    public sealed class AnimationClipSettingsInfo
    {
        public double StartTime { get; set; }
        public double StopTime { get; set; }
        public bool LoopTime { get; set; }
        public bool LoopBlend { get; set; }
    }

    public sealed class AnimationBindingList { public long Count { get; set; } public long MaxCurveKeys { get; set; } public List<AnimationBindingInfo> Items { get; set; } }
    public sealed class AnimationBindingInfo { public string Path { get; set; } public string Property { get; set; } public string Type { get; set; } public long KeyCount { get; set; } public bool KeysTruncated { get; set; } public List<AnimationKeyInfo> Keys { get; set; } }
    public sealed class AnimationKeyInfo { public double Time { get; set; } public double Value { get; set; } public double InTangent { get; set; } public double OutTangent { get; set; } }
    public sealed class AnimationEventList { public long Count { get; set; } public List<AnimationEventInfo> Items { get; set; } }
    public sealed class AnimationEventInfo { public double Time { get; set; } public string FunctionName { get; set; } public string StringParameter { get; set; } public double FloatParameter { get; set; } public long IntParameter { get; set; } }

    public sealed class AnimatorInfo
    {
        public bool Enabled { get; set; }
        public double Speed { get; set; }
        public bool ApplyRootMotion { get; set; }
        public string UpdateMode { get; set; }
        public string CullingMode { get; set; }
        public long LayerCount { get; set; }
        public long ParameterCount { get; set; }
        public long SelectedLayer { get; set; }
        public ControllerRef RuntimeAnimatorController { get; set; }
        public AnimatorStateRef CurrentState { get; set; }
        public List<AnimatorParameterInfo> Parameters { get; set; }
        public List<AnimatorLayerInfo> Layers { get; set; }
        public List<AnimatorClipInfo> Clips { get; set; }
    }

    public sealed class ControllerRef { public string Name { get; set; } public string Path { get; set; } public string Guid { get; set; } }
    public sealed class AnimatorStateRef { public long FullPathHash { get; set; } public long ShortNameHash { get; set; } public double NormalizedTime { get; set; } public double Length { get; set; } }
    public sealed class AnimatorParameterInfo { public string Name { get; set; } public string Type { get; set; } public double DefaultFloat { get; set; } public long DefaultInt { get; set; } public bool DefaultBool { get; set; } }
    public sealed class AnimatorLayerInfo { public long Index { get; set; } public string Name { get; set; } public double Weight { get; set; } public long CurrentStateHash { get; set; } public double CurrentStateNormalizedTime { get; set; } public double CurrentStateLength { get; set; } public bool IsInTransition { get; set; } }
    public sealed class AnimatorClipInfo { public string Name { get; set; } public double Length { get; set; } public double FrameRate { get; set; } public bool IsLooping { get; set; } }

    public sealed class AnimationPlannedEffect { public string Type { get; set; } public string Target { get; set; } public string State { get; set; } public string Path { get; set; } public string Parameter { get; set; } public string Property { get; set; } public string Value { get; set; } }
    public sealed class ControllerInfo { public string Path { get; set; } public string Guid { get; set; } public string Name { get; set; } public long LayerCount { get; set; } public long ParameterCount { get; set; } }
}
#endif
