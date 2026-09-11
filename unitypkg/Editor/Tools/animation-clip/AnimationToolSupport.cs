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
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_PATH_REQUIRED", "Pass an Assets/*.anim path.", "Use tools run animation-clip.read --input '{\"path\":\"Assets/Animations/Example.anim\"}'.");
            if (!path.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".anim", System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_PATH_INVALID", "AnimationClip path must be under Assets/ and end with .anim: " + path, "Pass a Unity asset path such as Assets/Animations/Player.anim.");
            return path;
        }

        public static WrapMode ValidateClipSettings(float? frameRate, string wrapMode, float? startTime, float? stopTime, float? length)
        {
            if (frameRate.HasValue && (float.IsNaN(frameRate.Value) || float.IsInfinity(frameRate.Value) || frameRate.Value <= 0f))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_VALUE_INVALID", "frameRate must be finite and greater than zero.", "Pass a positive finite frameRate.");
            if (startTime.HasValue && (float.IsNaN(startTime.Value) || float.IsInfinity(startTime.Value)) || stopTime.HasValue && (float.IsNaN(stopTime.Value) || float.IsInfinity(stopTime.Value)) || length.HasValue && (float.IsNaN(length.Value) || float.IsInfinity(length.Value) || length.Value <= 0f))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_VALUE_INVALID", "Clip time values must be finite and length must be greater than zero.", "Pass finite startTime/stopTime and a positive finite length.");
            if (stopTime.HasValue && length.HasValue)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_TIME_CONFLICT", "stopTime and length are mutually exclusive.", "Pass either stopTime or length.");
            if (startTime.HasValue && stopTime.HasValue && stopTime.Value <= startTime.Value)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_TIME_INVALID", "stopTime must be greater than startTime.", "Pass startTime < stopTime.");
            if (startTime.HasValue && length.HasValue && float.IsInfinity(startTime.Value + length.Value))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_TIME_INVALID", "startTime plus length must be representable as a finite float.", "Use smaller finite time values.");
            if (string.IsNullOrEmpty(wrapMode)) return default(WrapMode);
            WrapMode parsed;
            if (!System.Enum.TryParse<WrapMode>(wrapMode, true, out parsed) || !System.Enum.IsDefined(typeof(WrapMode), parsed))
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_WRAP_MODE_INVALID", "Unsupported WrapMode: " + wrapMode, "Pass a defined Unity WrapMode value.");
            return parsed;
        }

        /// <summary>加载 AnimationClip 资产;不存在抛异常。</summary>
        public static AnimationClip LoadAnimationClipAsset(string path)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
                throw new UnityCliBridge.BridgeCommandException("E_ANIMATION_CLIP_NOT_FOUND", "AnimationClip asset not found: " + path, "Check the path or create one with animation-clip.create.");
            return clip;
        }

        /// <summary>规范化并校验 Assets/*.controller 路径。</summary>
        public static string RequireControllerPath(string rawPath)
        {
            var path = SceneToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
                throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_PATH_REQUIRED", "Pass --controller-path <Assets/...>.controller>.", "Use animator-controller.create to create one first.");
            if (!path.StartsWith("Assets/", System.StringComparison.Ordinal) || !path.EndsWith(".controller", System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_CONTROLLER_PATH_INVALID", "AnimatorController path must be a canonical Assets/*.controller path: " + path, "Pass a Unity asset path such as Assets/Animations/Player.controller.");
            // 在任何 AssetDatabase 查找或创建目录前证明物理路径仍受 Assets/ 包含。
            SceneToolSupport.AssetFullPath(path);
            return path;
        }

        /// <summary>加载 AnimatorController 资产。</summary>
        public static AnimatorController LoadControllerAsset(string controllerPath)
        {
            var path = SceneToolSupport.NormalizeAssetPath(controllerPath);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        }

        /// <summary>强制从 AssetDatabase 重新加载控制器，避免用 effect 前的缓存对象作为完成证明。</summary>
        public static AnimatorController ReloadControllerAsset(string path)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        }

        /// <summary>按 effect 前 GUID 重新定位控制器，拒绝 path 重绑定后的缓存读回。</summary>
        public static AnimatorController ReloadControllerAsset(string path, string expectedGuid)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var observedPath = string.IsNullOrEmpty(expectedGuid) ? null : AssetDatabase.GUIDToAssetPath(expectedGuid);
            if (string.IsNullOrEmpty(observedPath) || !string.Equals(observedPath, path, System.StringComparison.Ordinal)) return null;
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(observedPath);
        }

        /// <summary>捕获层的可持久化完整身份，用于数组编辑后的顺序与删除证明。</summary>
        public static ControllerLayerIdentity CaptureLayerIdentity(AnimatorControllerLayer layer)
        {
            return new ControllerLayerIdentity
            {
                Name = layer.name,
                DefaultWeight = layer.defaultWeight,
                BlendingMode = layer.blendingMode,
                IKPass = layer.iKPass,
                SyncedLayerIndex = layer.syncedLayerIndex,
                SyncedLayerAffectsTiming = layer.syncedLayerAffectsTiming,
                AvatarMaskIdentity = AssetObjectIdentity(layer.avatarMask),
                StateMachineIdentity = AssetObjectIdentity(layer.stateMachine)
            };
        }

        /// <summary>比较层全部可公开持久化字段及其子资产 identity，不能只依赖数组索引或数量。</summary>
        public static bool SameLayerIdentity(ControllerLayerIdentity actual, ControllerLayerIdentity expected)
        {
            return actual != null && expected != null
                && actual.Name == expected.Name
                && Mathf.Approximately(actual.DefaultWeight, expected.DefaultWeight)
                && actual.BlendingMode == expected.BlendingMode
                && actual.IKPass == expected.IKPass
                && actual.SyncedLayerIndex == expected.SyncedLayerIndex
                && actual.SyncedLayerAffectsTiming == expected.SyncedLayerAffectsTiming
                && actual.AvatarMaskIdentity == expected.AvatarMaskIdentity
                && actual.StateMachineIdentity == expected.StateMachineIdentity;
        }

        private static string AssetObjectIdentity(Object value)
        {
            if (value == null) return string.Empty;
            string guid;
            long localId;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out guid, out localId))
                return guid + ":" + localId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return AssetDatabase.GetAssetPath(value) + ":" + value.name;
        }

        /// <summary>强制重新加载动画片段，用于持久化写入后的独立 readback。</summary>
        public static AnimationClip ReloadAnimationClipAsset(string path)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        }

        /// <summary>构造稳定的业务 readback mismatch，供 Coordinator 将 effect 后失败归类为 unknown。</summary>
        public static UnityCliBridge.BridgeCommandException StateMismatch(string code, string subject)
        {
            return new UnityCliBridge.BridgeCommandException(code, subject + " did not match the requested value after independent readback.", "Inspect the target state and retry after the Editor is idle.");
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
    public sealed class ControllerLayerIdentity { public string Name { get; set; } public float DefaultWeight { get; set; } public AnimatorLayerBlendingMode BlendingMode { get; set; } public bool IKPass { get; set; } public int SyncedLayerIndex { get; set; } public bool SyncedLayerAffectsTiming { get; set; } public string AvatarMaskIdentity { get; set; } public string StateMachineIdentity { get; set; } }
    public sealed class AnimatorStateRef { public long FullPathHash { get; set; } public long ShortNameHash { get; set; } public double NormalizedTime { get; set; } public double Length { get; set; } }
    public sealed class AnimatorParameterInfo { public string Name { get; set; } public string Type { get; set; } public double DefaultFloat { get; set; } public long DefaultInt { get; set; } public bool DefaultBool { get; set; } }
    public sealed class AnimatorLayerInfo { public long Index { get; set; } public string Name { get; set; } public double Weight { get; set; } public long CurrentStateHash { get; set; } public double CurrentStateNormalizedTime { get; set; } public double CurrentStateLength { get; set; } public bool IsInTransition { get; set; } }
    public sealed class AnimatorClipInfo { public string Name { get; set; } public double Length { get; set; } public double FrameRate { get; set; } public bool IsLooping { get; set; } }

    public sealed class AnimationPlannedEffect { public string Type { get; set; } public string Target { get; set; } public string State { get; set; } public string Path { get; set; } public string Parameter { get; set; } public string Property { get; set; } public string Value { get; set; } }
    public sealed class ControllerInfo { public string Path { get; set; } public string Guid { get; set; } public string Name { get; set; } public long LayerCount { get; set; } public long ParameterCount { get; set; } }
}
#endif
