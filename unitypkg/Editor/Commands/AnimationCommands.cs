#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterAnimationCommands(BridgeCommandRegistry registry)
        {
        }

        private static string AnimationClipCommand(BridgeRequest request)
        {
            var path = RequireAnimationClipPath(request.StringArg("path"));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("clip", AnimationClipJson(path, LoadAnimationClipAsset(path), request.IntArg("maxCurveKeys", 5)))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>创建持久化 AnimationClip 资产，并只写入 clip 级基础设置。</summary>
        private static string AnimationClipCreate(BridgeRequest request)
        {
            var path = RequireAnimationClipPath(request.StringArg("path"));
            ValidateAnimationClipCreateRequest(request);
            var overwrite = request.BoolArg("overwrite", false);
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null && !overwrite)
            {
                return ErrorJson("E_ANIMATION_CLIP_EXISTS", "AnimationClip already exists: " + path, "Pass overwrite:true after inspecting the existing clip, or choose another Assets/*.anim path.");
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "animation.clip.create")
                    .Prop("dryRun", true)
                    .Prop("path", path)
                    .Prop("wouldOverwrite", existing != null)
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animation.clip.create").Prop("path", path).Prop("overwrite", existing != null).EndObject())
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            CreateAssetDirectory(path);
            if (existing != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            var clip = new AnimationClip();
            clip.name = Path.GetFileNameWithoutExtension(path);
            var requestedWrapMode = ApplyAnimationClipSettings(clip, request, true);
            AssetDatabase.CreateAsset(clip, path);
            if (requestedWrapMode.HasValue)
            {
                SetSerializedAnimationClipWrapMode(clip, requestedWrapMode.Value);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            WriteLog("animation_clip_created path=" + path);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("clip", AnimationClipJson(path, LoadAnimationClipAsset(path), request.IntArg("maxCurveKeys", 5)))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animation.clip.create").Prop("path", path).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>修改已存在 AnimationClip 的 clip 级基础设置，不触碰曲线和事件。</summary>
        private static string AnimationClipModify(BridgeRequest request)
        {
            var path = RequireAnimationClipPath(request.StringArg("path"));
            ValidateAnimationClipCreateRequest(request);
            var clip = LoadAnimationClipAsset(path);
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "animation.clip.modify")
                    .Prop("dryRun", true)
                    .PropRaw("clip", AnimationClipJson(path, clip, request.IntArg("maxCurveKeys", 5)))
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animation.clip.modify").Prop("path", path).EndObject())
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            Undo.RecordObject(clip, "Modify AnimationClip Settings");
            var requestedWrapMode = ApplyAnimationClipSettings(clip, request, false);
            if (requestedWrapMode.HasValue)
            {
                SetSerializedAnimationClipWrapMode(clip, requestedWrapMode.Value);
            }
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            WriteLog("animation_clip_modified path=" + path);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("clip", AnimationClipJson(path, LoadAnimationClipAsset(path), request.IntArg("maxCurveKeys", 5)))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animation.clip.modify").Prop("path", path).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AnimationState(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animation state requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var layer = Mathf.Clamp(request.IntArg("layer", 0), 0, Math.Max(0, animator.layerCount - 1));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("animator", AnimatorJson(animator, layer))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AnimationPlay(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animation play requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var runtimeLayerCount = animator.layerCount;
            var effectiveLayerCount = EffectiveAnimatorLayerCount(animator);
            if (animator.runtimeAnimatorController == null || effectiveLayerCount <= 0)
            {
                return ErrorJson("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController assigned or no effective layers are available: " + HierarchyPath(gameObject), "Assign a RuntimeAnimatorController with at least one layer, then retry.");
            }
            var state = request.StringArg("state");
            if (string.IsNullOrEmpty(state))
            {
                return ErrorJson("E_ANIMATION_STATE_REQUIRED", "Animation play requires --state <name>.", "Pass an Animator state name or hash-compatible state path.");
            }
            if (runtimeLayerCount <= 0 && effectiveLayerCount > 0)
            {
                animator.Rebind();
                animator.Update(0f);
                runtimeLayerCount = animator.layerCount;
                effectiveLayerCount = EffectiveAnimatorLayerCount(animator);
            }
            var layer = request.IntArg("layer", 0);
            if (layer < 0 || layer >= effectiveLayerCount)
            {
                return ErrorJson("E_ANIMATION_LAYER_INVALID", "Animator layer is out of range: " + layer.ToString(CultureInfo.InvariantCulture), "Run tools run animation.state --input '{\"target\":\"<go>\"}' to inspect layer count.");
            }
            animator.Play(state, runtimeLayerCount > 0 ? layer : 0, request.FloatArg("normalizedTime", 0f));
            animator.Update(0f);
            EditorUtility.SetDirty(animator);
            WriteLog("animation_play target=" + HierarchyPath(gameObject) + " state=" + state + " layer=" + layer.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("animator", AnimatorJson(animator, layer))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animation.play").Prop("target", HierarchyPath(gameObject)).Prop("state", state).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AnimationStop(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animation stop requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            animator.Rebind();
            animator.Update(0f);
            EditorUtility.SetDirty(animator);
            WriteLog("animation_stop target=" + HierarchyPath(gameObject));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropRaw("animator", AnimatorJson(animator, 0))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animation.stop").Prop("target", HierarchyPath(gameObject)).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string RequireAnimationClipPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_PATH_REQUIRED", "Pass an Assets/*.anim path.", "Use tools run animation.clip --input '{\"path\":\"Assets/Animations/Example.anim\"}', or pass path in the tool input.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_PATH_INVALID", "AnimationClip path must be under Assets/ and end with .anim: " + path, "Pass a Unity asset path such as Assets/Animations/Player.anim.");
            }
            return path;
        }

        private static AnimationClip LoadAnimationClipAsset(string path)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_NOT_FOUND", "AnimationClip asset not found: " + path, "Check the path or create one with tools run animation.clip.create --input '{\"path\":\"<Assets/...>.anim\"}' --yes.");
            }
            return clip;
        }

        /// <summary>校验 AnimationClip 创建参数，确保 dry-run 与真实执行一致失败。</summary>
        private static void ValidateAnimationClipCreateRequest(BridgeRequest request)
        {
            var frameRate = request.OptionalFloatArg("frameRate");
            if (frameRate.HasValue && frameRate.Value <= 0f)
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_SETTING_INVALID", "frameRate must be greater than 0.", "Pass a positive frameRate, such as 30 or 60.");
            }
            var length = request.OptionalFloatArg("length");
            if (length.HasValue && length.Value <= 0f)
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_SETTING_INVALID", "length must be greater than 0 when provided.", "Pass a positive clip length in seconds, or omit length.");
            }
            var startTime = request.OptionalFloatArg("startTime") ?? 0f;
            var stopTime = request.OptionalFloatArg("stopTime") ?? (length.HasValue ? startTime + length.Value : (float?)null);
            if (stopTime.HasValue && stopTime.Value < startTime)
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_SETTING_INVALID", "stopTime must be greater than or equal to startTime.", "Adjust startTime/stopTime, or pass length instead of stopTime.");
            }
            var wrapMode = request.StringArg("wrapMode");
            WrapMode parsed;
            if (!string.IsNullOrEmpty(wrapMode) && !Enum.TryParse<WrapMode>(wrapMode, true, out parsed))
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_WRAP_MODE_INVALID", "Invalid AnimationClip wrapMode: " + wrapMode, "Use one of Default, Once/Clamp, Loop, PingPong, or ClampForever.");
            }
        }

        /// <summary>把请求中的 clip 级属性写入 AnimationClip；修改模式只写显式传入字段。</summary>
        private static WrapMode? ApplyAnimationClipSettings(AnimationClip clip, BridgeRequest request, bool applyDefaults)
        {
            var frameRate = request.OptionalFloatArg("frameRate");
            if (frameRate.HasValue || applyDefaults)
            {
                clip.frameRate = frameRate ?? 60f;
            }
            if (request.HasArg("legacy") || applyDefaults)
            {
                clip.legacy = request.BoolArg("legacy", false);
            }
            WrapMode parsedWrapMode;
            WrapMode? requestedWrapMode = null;
            if (!string.IsNullOrEmpty(request.StringArg("wrapMode")) && Enum.TryParse<WrapMode>(request.StringArg("wrapMode"), true, out parsedWrapMode))
            {
                clip.wrapMode = parsedWrapMode;
                requestedWrapMode = parsedWrapMode;
            }
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var startTime = request.OptionalFloatArg("startTime");
            var stopTime = request.OptionalFloatArg("stopTime");
            var length = request.OptionalFloatArg("length");
            if (startTime.HasValue) settings.startTime = startTime.Value;
            if (stopTime.HasValue) settings.stopTime = stopTime.Value;
            else if (length.HasValue) settings.stopTime = settings.startTime + length.Value;
            var loopTime = request.OptionalBoolArg("loopTime");
            if (loopTime.HasValue) settings.loopTime = loopTime.Value;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return requestedWrapMode;
        }

        /// <summary>同步 legacy wrapMode 的序列化字段，避免只改运行时属性。</summary>
        private static void SetSerializedAnimationClipWrapMode(AnimationClip clip, WrapMode wrapMode)
        {
            var serialized = new SerializedObject(clip);
            var wrapProperty = serialized.FindProperty("m_WrapMode");
            if (wrapProperty != null)
            {
                wrapProperty.intValue = (int)wrapMode;
                serialized.ApplyModifiedProperties();
            }
        }

        private static string AnimationClipJson(string path, AnimationClip clip, int maxCurveKeys)
        {
            var keyLimit = Mathf.Clamp(maxCurveKeys, 0, 100);
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var objectReferenceBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            var events = AnimationUtility.GetAnimationEvents(clip);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", clip.name)
                .Prop("length", clip.length)
                .Prop("frameRate", clip.frameRate)
                .Prop("legacy", clip.legacy)
                .Prop("wrapMode", clip.wrapMode.ToString())
                .PropRaw("localBounds", BoundsJson(clip.localBounds))
                .PropRaw("settings", AnimationClipSettingsJson(clip))
                .PropRaw("bindings", AnimationBindingsJson(clip, bindings, keyLimit))
                .PropRaw("objectReferenceBindings", AnimationObjectReferenceBindingsJson(clip, objectReferenceBindings, keyLimit))
                .PropRaw("events", AnimationEventsJson(events))
                .EndObject().ToString();
        }

        private static string AnimationClipSettingsJson(AnimationClip clip)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("startTime", settings.startTime)
                .Prop("stopTime", settings.stopTime)
                .Prop("loopTime", settings.loopTime)
                .Prop("loopBlend", settings.loopBlend)
                .Prop("loopBlendOrientation", settings.loopBlendOrientation)
                .Prop("loopBlendPositionY", settings.loopBlendPositionY)
                .Prop("loopBlendPositionXZ", settings.loopBlendPositionXZ)
                .Prop("keepOriginalOrientation", settings.keepOriginalOrientation)
                .Prop("keepOriginalPositionY", settings.keepOriginalPositionY)
                .Prop("keepOriginalPositionXZ", settings.keepOriginalPositionXZ)
                .Prop("cycleOffset", settings.cycleOffset)
                .Prop("orientationOffsetY", settings.orientationOffsetY)
                .Prop("level", settings.level)
                .Prop("mirror", settings.mirror)
                .EndObject().ToString();
        }

        private static string AnimationBindingsJson(AnimationClip clip, EditorCurveBinding[] bindings, int keyLimit)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)bindings.Length)
                .Prop("maxCurveKeys", (long)keyLimit)
                .PropArray("items", arr =>
                {
                    foreach (var binding in bindings)
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        var keys = curve == null ? new Keyframe[0] : curve.keys;
                        arr.BeginObject()
                            .Prop("path", binding.path)
                            .Prop("property", binding.propertyName)
                            .Prop("type", binding.type == null ? null : binding.type.FullName)
                            .Prop("keyCount", (long)keys.Length)
                            .Prop("keysTruncated", keys.Length > keyLimit)
                            .PropArray("keys", keyArr =>
                            {
                                for (var i = 0; i < Math.Min(keys.Length, keyLimit); i++)
                                {
                                    var key = keys[i];
                                    keyArr.BeginObject()
                                        .Prop("time", key.time)
                                        .Prop("value", key.value)
                                        .Prop("inTangent", key.inTangent)
                                        .Prop("outTangent", key.outTangent)
                                        .Prop("inWeight", key.inWeight)
                                        .Prop("outWeight", key.outWeight)
                                        .Prop("weightedMode", key.weightedMode.ToString())
                                        .EndObject();
                                }
                            })
                            .EndObject();
                    }
                })
                .EndObject().ToString();
        }

        private static string AnimationObjectReferenceBindingsJson(AnimationClip clip, EditorCurveBinding[] bindings, int keyLimit)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)bindings.Length)
                .Prop("maxCurveKeys", (long)keyLimit)
                .PropArray("items", arr =>
                {
                    foreach (var binding in bindings)
                    {
                        var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding) ?? new ObjectReferenceKeyframe[0];
                        arr.BeginObject()
                            .Prop("path", binding.path)
                            .Prop("property", binding.propertyName)
                            .Prop("type", binding.type == null ? null : binding.type.FullName)
                            .Prop("keyCount", (long)keys.Length)
                            .Prop("keysTruncated", keys.Length > keyLimit)
                            .PropArray("keys", keyArr =>
                            {
                                for (var i = 0; i < Math.Min(keys.Length, keyLimit); i++)
                                {
                                    keyArr.BeginObject()
                                        .Prop("time", keys[i].time)
                                        .PropRaw("value", AnimationObjectReferenceJson(keys[i].value))
                                        .EndObject();
                                }
                            })
                            .EndObject();
                    }
                })
                .EndObject().ToString();
        }

        private static string AnimationEventsJson(AnimationEvent[] events)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)events.Length)
                .PropArray("items", arr =>
                {
                    foreach (var evt in events)
                    {
                        arr.BeginObject()
                            .Prop("time", evt.time)
                            .Prop("functionName", evt.functionName)
                            .Prop("stringParameter", evt.stringParameter)
                            .Prop("floatParameter", evt.floatParameter)
                            .Prop("intParameter", (long)evt.intParameter)
                            .Prop("messageOptions", evt.messageOptions.ToString())
                            .PropRaw("objectReferenceParameter", AnimationObjectReferenceJson(evt.objectReferenceParameter))
                            .EndObject();
                    }
                })
                .EndObject().ToString();
        }

        private static string AnimationObjectReferenceJson(UnityEngine.Object value)
        {
            if (value == null) return "null";
            var assetPath = AssetDatabase.GetAssetPath(value);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", value.name)
                .Prop("type", value.GetType().FullName)
                .Prop("path", string.IsNullOrEmpty(assetPath) ? null : NormalizeAssetPath(assetPath))
                .Prop("guid", string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath))
                .Prop("instanceId", (long)value.GetInstanceID())
                .EndObject().ToString();
        }

        private static string BoundsJson(Bounds bounds)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("center", Vector3Json(bounds.center))
                .PropRaw("size", Vector3Json(bounds.size))
                .PropRaw("extents", Vector3Json(bounds.extents))
                .PropRaw("min", Vector3Json(bounds.min))
                .PropRaw("max", Vector3Json(bounds.max))
                .EndObject().ToString();
        }

        private static string AnimatorJson(Animator animator, int layer)
        {
            var controller = animator.runtimeAnimatorController;
            var controllerPath = controller == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(controller));
            var runtimeLayerCount = animator.layerCount;
            var layerCount = EffectiveAnimatorLayerCount(animator);
            var safeLayer = layerCount <= 0 ? 0 : Mathf.Clamp(layer, 0, layerCount - 1);
            var state = runtimeLayerCount > 0 && safeLayer < runtimeLayerCount ? animator.GetCurrentAnimatorStateInfo(safeLayer) : default(AnimatorStateInfo);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("enabled", animator.enabled)
                .Prop("layerCount", (long)layerCount)
                .Prop("runtimeLayerCount", (long)runtimeLayerCount)
                .Prop("selectedLayer", (long)safeLayer)
                .PropObject("runtimeAnimatorController", obj => obj
                    .Prop("name", controller == null ? null : controller.name)
                    .Prop("path", controllerPath)
                    .Prop("guid", string.IsNullOrEmpty(controllerPath) ? null : AssetDatabase.AssetPathToGUID(controllerPath)))
                .PropObject("currentState", obj => obj
                    .Prop("fullPathHash", (long)state.fullPathHash)
                    .Prop("shortNameHash", (long)state.shortNameHash)
                    .Prop("normalizedTime", state.normalizedTime)
                    .Prop("length", state.length))
                .EndObject().ToString();
        }

        /// <summary>读取 Animator 可用层数量；运行态缓存未初始化时回退到 AnimatorController 资产层。</summary>
        private static int EffectiveAnimatorLayerCount(Animator animator)
        {
            if (animator == null)
            {
                return 0;
            }
            if (animator.layerCount > 0)
            {
                return animator.layerCount;
            }
            var controller = animator.runtimeAnimatorController as AnimatorController;
            return controller == null || controller.layers == null ? 0 : controller.layers.Length;
        }

        // ── animator.* runtime-state tools ──────────────────────────
        // Reference: unity-mcp AnimatorRead.cs / AnimatorControl.cs
        // Key differences from unity-mcp:
        //   - Uses alps-unity-cli envelope (SuccessJson / ErrorJson + nextActions)
        //     instead of unity-mcp's { success, message } anonymous objects.
        //   - Edit-mode only (execute guard blocks bridge write commands in Play Mode),
        //     so set-parameter modifies AnimatorController asset defaults.
        //   - unity-mcp has Play/Edit mode split for set-parameter; we only need
        //     the edit-mode (controller-asset) path because Play Mode write commands
        //     are blocked by the bridge execute guard.
        //   - Uses RegisterCommand + target-scoped ResolveUniqueTarget instead of
        //     unity-mcp's ObjectResolver + action-prefix dispatch.
        //   - Includes plannedEffects and logPath in all mutation responses.

        /// <summary>读取 Animator 参数定义；运行态缓存为空时回退到 AnimatorController 资产。</summary>
        private static AnimatorControllerParameter[] AnimatorParameterDefinitions(Animator animator)
        {
            if (animator == null)
            {
                return new AnimatorControllerParameter[0];
            }
            if (animator.parameterCount > 0)
            {
                var runtimeParameters = new AnimatorControllerParameter[animator.parameterCount];
                for (var i = 0; i < animator.parameterCount; i++)
                {
                    runtimeParameters[i] = animator.GetParameter(i);
                }
                return runtimeParameters;
            }
            var controller = animator.runtimeAnimatorController as AnimatorController;
            return controller == null || controller.parameters == null ? new AnimatorControllerParameter[0] : controller.parameters;
        }

        /// <summary>按名称查找 Animator 参数定义，兼容运行态缓存和 Controller 资产。</summary>
        private static AnimatorControllerParameter FindAnimatorParameter(Animator animator, string parameterName)
        {
            foreach (var parameter in AnimatorParameterDefinitions(animator))
            {
                if (parameter != null && parameter.name == parameterName)
                {
                    return parameter;
                }
            }
            return null;
        }

        /// <summary>判断 Animator 当前运行态缓存是否包含参数，用于区分当前值和 Controller 默认值。</summary>
        private static bool AnimatorHasRuntimeParameter(Animator animator, string parameterName)
        {
            if (animator == null)
            {
                return false;
            }
            for (var i = 0; i < animator.parameterCount; i++)
            {
                var parameter = animator.GetParameter(i);
                if (parameter != null && parameter.name == parameterName)
                {
                    return true;
                }
            }
            return false;
        }

        private static string AnimatorGetInfo(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animator info requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var controllerPath = animator.runtimeAnimatorController == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController));
            var controllerLayers = controller == null || controller.layers == null ? new AnimatorControllerLayer[0] : controller.layers;
            var parameterDefinitions = AnimatorParameterDefinitions(animator);
            var runtimeLayerCount = animator.layerCount;
            var layerCount = runtimeLayerCount > 0 ? runtimeLayerCount : controllerLayers.Length;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropObject("animator", obj => obj
                    .Prop("enabled", animator.enabled)
                    .Prop("speed", animator.speed)
                    .Prop("applyRootMotion", animator.applyRootMotion)
                    .Prop("updateMode", animator.updateMode.ToString())
                    .Prop("cullingMode", animator.cullingMode.ToString())
                    .Prop("layerCount", (long)layerCount)
                    .Prop("parameterCount", (long)parameterDefinitions.Length)
                    .PropObject("runtimeAnimatorController", ctrl => ctrl
                        .Prop("name", animator.runtimeAnimatorController == null ? null : animator.runtimeAnimatorController.name)
                        .Prop("path", controllerPath)
                        .Prop("guid", string.IsNullOrEmpty(controllerPath) ? null : AssetDatabase.AssetPathToGUID(controllerPath)))
                    .PropArray("parameters", arr =>
                    {
                        foreach (var p in parameterDefinitions)
                        {
                            if (p == null) continue;
                            arr.BeginObject()
                                .Prop("name", p.name)
                                .Prop("type", p.type.ToString())
                                .Prop("defaultFloat", p.defaultFloat)
                                .Prop("defaultInt", (long)p.defaultInt)
                                .Prop("defaultBool", p.defaultBool)
                                .EndObject();
                        }
                    })
                    .PropArray("layers", arr =>
                    {
                        for (var i = 0; i < layerCount; i++)
                        {
                            var hasRuntimeLayer = i < runtimeLayerCount;
                            var hasControllerLayer = i < controllerLayers.Length;
                            var stateInfo = hasRuntimeLayer ? animator.GetCurrentAnimatorStateInfo(i) : default(AnimatorStateInfo);
                            var inTransition = hasRuntimeLayer && animator.IsInTransition(i);
                            arr.BeginObject()
                                .Prop("index", (long)i)
                                .Prop("name", hasControllerLayer ? controllerLayers[i].name : null)
                                .Prop("weight", hasRuntimeLayer ? animator.GetLayerWeight(i) : hasControllerLayer ? controllerLayers[i].defaultWeight : 0f)
                                .Prop("currentStateHash", (long)stateInfo.fullPathHash)
                                .Prop("currentStateNormalizedTime", stateInfo.normalizedTime)
                                .Prop("currentStateLength", stateInfo.length)
                                .Prop("isInTransition", inTransition)
                                .EndObject();
                        }
                    })
                    .PropArray("clips", arr =>
                    {
                        if (animator.runtimeAnimatorController != null)
                        {
                            var clips = animator.runtimeAnimatorController.animationClips;
                            if (clips != null)
                            {
                                foreach (var clip in clips)
                                {
                                    if (clip != null)
                                    {
                                        arr.BeginObject()
                                            .Prop("name", clip.name)
                                            .Prop("length", clip.length)
                                            .Prop("frameRate", clip.frameRate)
                                            .Prop("isLooping", clip.isLooping)
                                            .EndObject();
                                    }
                                }
                            }
                        }
                    }))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AnimatorGetParameter(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animator get-parameter requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var parameterName = request.StringArg("parameterName");
            if (string.IsNullOrEmpty(parameterName))
            {
                return ErrorJson("E_ANIMATOR_PARAMETER_NAME_REQUIRED", "Animator get-parameter requires --parameter-name <name>.", "Pass the name of an Animator parameter.");
            }
            var param = FindAnimatorParameter(animator, parameterName);
            if (param == null)
            {
                return ErrorJson("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + parameterName + "' not found on Animator: " + HierarchyPath(gameObject), "Run tools run animator.get-info --input '{\"target\":\"<go>\"}' to list available parameters.");
            }
            var hasRuntimeValue = AnimatorHasRuntimeParameter(animator, parameterName);
            object value;
            switch (param.type)
            {
                case AnimatorControllerParameterType.Float:
                    value = hasRuntimeValue ? animator.GetFloat(parameterName) : param.defaultFloat;
                    break;
                case AnimatorControllerParameterType.Int:
                    value = hasRuntimeValue ? (long)animator.GetInteger(parameterName) : (long)param.defaultInt;
                    break;
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    value = hasRuntimeValue ? animator.GetBool(parameterName) : param.defaultBool;
                    break;
                default:
                    value = null;
                    break;
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropObject("parameter", obj => obj
                    .Prop("name", param.name)
                    .Prop("type", param.type.ToString())
                    .PropRaw("value", value is bool b ? (b ? "true" : "false") : value is long l ? l.ToString(CultureInfo.InvariantCulture) : value is float f ? f.ToString(CultureInfo.InvariantCulture) : value is int i ? i.ToString(CultureInfo.InvariantCulture) : value != null ? value.ToString() : "null"))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AnimatorSetParameter(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animator set-parameter requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var parameterName = request.StringArg("parameterName");
            if (string.IsNullOrEmpty(parameterName))
            {
                return ErrorJson("E_ANIMATOR_PARAMETER_NAME_REQUIRED", "Animator set-parameter requires --parameter-name <name>.", "Pass the name of an Animator parameter.");
            }
            // In edit mode, modify the AnimatorController asset defaults.
            // unity-mcp also has a play-mode path (runtime SetFloat/SetInteger/SetBool),
            // but alps-unity-cli's execute guard blocks bridge write commands in Play Mode,
            // so only the edit-mode (controller-asset) path is reachable.
            var ac = animator.runtimeAnimatorController as AnimatorController;
            if (ac == null)
            {
                return ErrorJson("E_ANIMATOR_NO_CONTROLLER", "Animator has no AnimatorController assigned (cannot set parameter defaults in edit mode): " + HierarchyPath(gameObject), "Assign an AnimatorController asset with at least one parameter, then retry.");
            }
            var param = FindAnimatorParameter(animator, parameterName);
            if (param == null)
            {
                return ErrorJson("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + parameterName + "' not found on Animator: " + HierarchyPath(gameObject), "Run tools run animator.get-info --input '{\"target\":\"<go>\"}' to list available parameters.");
            }
            Undo.RecordObject(ac, "Set Animator Parameter Default");
            var parameters = ac.parameters;
            var found = false;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == parameterName)
                {
                    switch (param.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            parameters[i].defaultFloat = request.FloatArg("value", parameters[i].defaultFloat);
                            break;
                        case AnimatorControllerParameterType.Int:
                            parameters[i].defaultInt = request.IntArg("value", parameters[i].defaultInt);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            parameters[i].defaultBool = request.BoolArg("value", parameters[i].defaultBool);
                            break;
                        case AnimatorControllerParameterType.Trigger:
                            // Triggers are runtime-only; no default value to set in edit mode.
                            // Match unity-mcp: acknowledge but no-op.
                            break;
                    }
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return ErrorJson("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + parameterName + "' not found on AnimatorController: " + ac.name, "Run tools run animator.get-info --input '{\"target\":\"<go>\"}' to list available parameters.");
            }
            ac.parameters = parameters;
            EditorUtility.SetDirty(ac);
            AssetDatabase.SaveAssets();
            WriteLog("animator_set_parameter target=" + HierarchyPath(gameObject) + " parameter=" + parameterName);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropObject("parameter", obj => obj
                    .Prop("name", parameterName)
                    .Prop("type", param.type.ToString()))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animator.set-parameter").Prop("target", HierarchyPath(gameObject)).Prop("parameter", parameterName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AnimatorCrossfade(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animator crossfade requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var controller = animator.runtimeAnimatorController as AnimatorController;
            var controllerLayers = controller == null || controller.layers == null ? new AnimatorControllerLayer[0] : controller.layers;
            var runtimeLayerCount = animator.layerCount;
            var effectiveLayerCount = runtimeLayerCount > 0 ? runtimeLayerCount : controllerLayers.Length;
            if (animator.runtimeAnimatorController == null || effectiveLayerCount <= 0)
            {
                return ErrorJson("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController assigned or no layers are available: " + HierarchyPath(gameObject), "Assign a RuntimeAnimatorController with at least one layer, then retry.");
            }
            var stateName = request.StringArg("state");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_ANIMATION_STATE_REQUIRED", "Animator crossfade requires --state <name>.", "Pass an Animator state name or hash-compatible state path.");
            }
            var duration = request.FloatArg("duration", 0.25f);
            var layer = request.IntArg("layer", -1);
            if (layer >= 0 && layer >= effectiveLayerCount)
            {
                return ErrorJson("E_ANIMATION_LAYER_INVALID", "Animator layer is out of range: " + layer.ToString(CultureInfo.InvariantCulture), "Run tools run animator.get-info --input '{\"target\":\"<go>\"}' to inspect layer count.");
            }
            var stateLookupLayer = layer >= 0 ? layer : 0;
            if (controller != null && stateLookupLayer >= 0 && stateLookupLayer < controllerLayers.Length)
            {
                var stateMachine = controllerLayers[stateLookupLayer].stateMachine;
                if (stateMachine != null && FindStateInLayer(stateMachine, stateName) == null)
                {
                    return ErrorJson("E_STATE_NOT_FOUND", "Animator state '" + stateName + "' not found in layer " + stateLookupLayer.ToString(CultureInfo.InvariantCulture) + ".", "Run tools run controller.get-info with the assigned controllerPath to inspect available states.");
                }
            }
            if (runtimeLayerCount <= 0)
            {
                animator.Rebind();
                animator.Update(0f);
            }
            animator.CrossFade(stateName, duration, layer);
            animator.Update(0f);
            EditorUtility.SetDirty(animator);
            WriteLog("animator_crossfade target=" + HierarchyPath(gameObject) + " state=" + stateName + " duration=" + duration.ToString(CultureInfo.InvariantCulture) + " layer=" + layer.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropObject("transition", obj => obj
                    .Prop("state", stateName)
                    .Prop("duration", duration)
                    .Prop("layer", (long)layer))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animator.crossfade").Prop("target", HierarchyPath(gameObject)).Prop("state", stateName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AnimatorSetSpeed(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animator set-speed requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var speed = request.FloatArg("speed", 1f);
            Undo.RecordObject(animator, "Set Animator Speed");
            animator.speed = speed;
            EditorUtility.SetDirty(animator);
            WriteLog("animator_set_speed target=" + HierarchyPath(gameObject) + " speed=" + speed.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .Prop("speed", speed)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animator.set-speed").Prop("target", HierarchyPath(gameObject)).Prop("speed", speed).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string AnimatorSetEnabled(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animator set-enabled requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var enabled = request.BoolArg("value", true);
            Undo.RecordObject(animator, "Set Animator Enabled");
            animator.enabled = enabled;
            EditorUtility.SetDirty(animator);
            WriteLog("animator_set_enabled target=" + HierarchyPath(gameObject) + " enabled=" + enabled);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .Prop("enabled", enabled)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animator.set-enabled").Prop("target", HierarchyPath(gameObject)).Prop("enabled", enabled).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── controller.* AnimatorController editing tools ────────
        // Reference: unity-mcp ControllerCreate.cs / ControllerLayers.cs / ControllerBlendTrees.cs
        // Key differences from unity-mcp:
        //   - Uses alps-unity-cli envelope (SuccessJson / ErrorJson + nextActions)
        //     instead of unity-mcp's { success, message } anonymous objects.
        //   - Edit-mode only (execute guard blocks bridge write commands in Play Mode).
        //   - Uses RegisterCommand + ResolveUniqueTarget instead of unity-mcp's
        //     ObjectResolver + action-prefix dispatch.
        //   - Includes plannedEffects and logPath in all mutation responses.
        //   - controller.* commands address the controller asset by path (controllerPath)
        //     rather than by target-scoped GameObject resolution.
        //   - Conditions for add-transition are passed as a JSON array string instead
        //     of inline Newtonsoft JArray; parsed via MiniJson-style extraction.
        //   - BlendTree 2D child position passed as positionX/positionY instead of
        //     unity-mcp's [x, y] array.

        private static AnimatorController LoadControllerAsset(string controllerPath)
        {
            var path = NormalizeAssetPath(controllerPath);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        }

        private static string RequireControllerPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_CONTROLLER_PATH_REQUIRED", "Pass --controller-path <Assets/...>.controller>.", "Use tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}' to create one first.");
            }
            if (!path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_CONTROLLER_PATH_INVALID", "AnimatorController path must end with .controller: " + path, "Pass a Unity asset path such as Assets/Animations/Player.controller.");
            }
            return path;
        }

        /// <summary>按短名或 Base Layer.State 这类完整路径查找 Controller 状态。</summary>
        private static AnimatorState FindStateInLayer(AnimatorStateMachine sm, string stateName)
        {
            foreach (var cs in sm.states)
            {
                if (cs.state == null) continue;
                if (cs.state.name == stateName) return cs.state;
                if (!string.IsNullOrEmpty(stateName) && stateName.EndsWith("." + cs.state.name, StringComparison.Ordinal)) return cs.state;
            }
            return null;
        }

        private static string ControllerCreate(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (existing != null)
            {
                return ErrorJson("E_CONTROLLER_EXISTS", "AnimatorController already exists at: " + controllerPath, "Delete it first or use a different path.");
            }
            CreateAssetDirectory(controllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            WriteLog("controller_created path=" + controllerPath);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("guid", AssetDatabase.AssetPathToGUID(controllerPath))
                .Prop("name", controller.name)
                .Prop("layerCount", (long)controller.layers.Length)
                .Prop("parameterCount", (long)controller.parameters.Length)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.create").Prop("path", controllerPath).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerAddState(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var stateName = request.StringArg("stateName");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller.add-state requires stateName.", "Pass a name for the new state.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range (controller has " + controller.layers.Length.ToString(CultureInfo.InvariantCulture) + " layers).", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
            }
            var rootStateMachine = controller.layers[layerIndex].stateMachine;
            if (FindStateInLayer(rootStateMachine, stateName) != null)
            {
                return ErrorJson("E_STATE_EXISTS", "State '" + stateName + "' already exists in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Choose a different state name or use a different layer.");
            }
            // 先校验可选 clip，避免错误路径先创建 state 后返回失败。
            var clipPath = request.StringArg("clipPath");
            AnimationClip clip = null;
            if (!string.IsNullOrEmpty(clipPath))
            {
                var normalizedClipPath = NormalizeAssetPath(clipPath);
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(normalizedClipPath);
                if (clip == null)
                {
                    return ErrorJson("E_CLIP_NOT_FOUND", "AnimationClip not found at: " + normalizedClipPath, "Create the clip first with tools run animation.clip.create --input '{\"path\":\"<Assets/...>.anim\"}' --yes, or omit clipPath to add an empty state.");
                }
            }
            Undo.RecordObject(controller, "Add Animator State");
            var state = rootStateMachine.AddState(stateName);
            if (clip != null)
            {
                state.motion = clip;
            }
            var speed = request.FloatArg("speed", 1f);
            state.speed = speed;
            var isDefault = request.BoolArg("isDefault", false);
            if (isDefault) rootStateMachine.defaultState = state;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_add_state path=" + controllerPath + " state=" + stateName + " layer=" + layerIndex.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("stateName", stateName)
                .Prop("layerIndex", (long)layerIndex)
                .Prop("hasMotion", state.motion != null)
                .Prop("motionName", state.motion != null ? state.motion.name : null)
                .Prop("speed", speed)
                .Prop("isDefault", isDefault)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.add-state").Prop("path", controllerPath).Prop("stateName", stateName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerAddTransition(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var fromStateName = request.StringArg("fromState");
            var toStateName = request.StringArg("toState");
            if (string.IsNullOrEmpty(fromStateName) || string.IsNullOrEmpty(toStateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller.add-transition requires fromState and toState.", "Pass source and destination state names.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
            }
            var rootStateMachine = controller.layers[layerIndex].stateMachine;
            var isAnyState = string.Equals(fromStateName, "AnyState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fromStateName, "Any", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fromStateName, "Any State", StringComparison.OrdinalIgnoreCase);
            var toState = FindStateInLayer(rootStateMachine, toStateName);
            if (toState == null)
            {
                return ErrorJson("E_STATE_NOT_FOUND", "Destination state '" + toStateName + "' not found in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Run tools run controller.get-info with the same controllerPath to inspect available states.");
            }
            Undo.RecordObject(controller, "Add Animator Transition");
            AnimatorStateTransition transition;
            if (isAnyState)
            {
                transition = rootStateMachine.AddAnyStateTransition(toState);
                fromStateName = "AnyState";
            }
            else
            {
                var fromState = FindStateInLayer(rootStateMachine, fromStateName);
                if (fromState == null)
                {
                    return ErrorJson("E_STATE_NOT_FOUND", "Source state '" + fromStateName + "' not found in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Run tools run controller.get-info with the same controllerPath to inspect available states.");
                }
                transition = fromState.AddTransition(toState);
            }
            transition.hasExitTime = request.BoolArg("hasExitTime", true);
            transition.duration = request.FloatArg("duration", 0.25f);
            transition.exitTime = request.FloatArg("exitTime", 0.75f);
            // Parse conditions from JSON array string
            var conditionsJson = request.RawJsonArg("conditions");
            var conditionCount = 0;
            if (!string.IsNullOrEmpty(conditionsJson))
            {
                // Simple array-of-objects parsing: extract each {"parameter":"...","mode":"...","threshold":N}
                foreach (var condStr in ExtractArrayItems(conditionsJson))
                {
                    var pName = ExtractFromJson(condStr, "parameter");
                    var modeStr = ExtractFromJson(condStr, "mode");
                    var thresholdVal = ExtractFloatFromJson(condStr, "threshold", 0f);
                    if (string.IsNullOrEmpty(pName)) continue;
                    AnimatorConditionMode mode;
                    switch ((modeStr ?? "greater").ToLowerInvariant())
                    {
                        case "less": mode = AnimatorConditionMode.Less; break;
                        case "equals": mode = AnimatorConditionMode.Equals; break;
                        case "notequal":
                        case "not_equal": mode = AnimatorConditionMode.NotEqual; break;
                        case "if":
                        case "true": mode = AnimatorConditionMode.If; break;
                        case "ifnot":
                        case "if_not":
                        case "false": mode = AnimatorConditionMode.IfNot; break;
                        default: mode = AnimatorConditionMode.Greater; break;
                    }
                    transition.AddCondition(mode, thresholdVal, pName);
                    conditionCount++;
                }
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_add_transition path=" + controllerPath + " from=" + fromStateName + " to=" + toStateName + " conditions=" + conditionCount.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("fromState", fromStateName)
                .Prop("toState", toStateName)
                .Prop("hasExitTime", transition.hasExitTime)
                .Prop("duration", transition.duration)
                .Prop("conditionCount", (long)conditionCount)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.add-transition").Prop("path", controllerPath).Prop("fromState", fromStateName).Prop("toState", toStateName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerAddParameter(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var paramName = request.StringArg("parameterName");
            if (string.IsNullOrEmpty(paramName))
            {
                return ErrorJson("E_PARAMETER_NAME_REQUIRED", "controller.add-parameter requires parameterName.", "Pass a name for the new parameter.");
            }
            var typeStr = (request.StringArg("parameterType") ?? "float").ToLowerInvariant();
            AnimatorControllerParameterType paramType;
            switch (typeStr)
            {
                case "int":
                case "integer": paramType = AnimatorControllerParameterType.Int; break;
                case "bool":
                case "boolean": paramType = AnimatorControllerParameterType.Bool; break;
                case "trigger": paramType = AnimatorControllerParameterType.Trigger; break;
                default: paramType = AnimatorControllerParameterType.Float; break;
            }
            // Check for duplicate
            foreach (var existing in controller.parameters)
            {
                if (existing.name == paramName)
                {
                    return ErrorJson("E_PARAMETER_EXISTS", "Parameter '" + paramName + "' already exists on controller " + controller.name + ".", "Use animator.set-parameter to change its default value, or choose a different name.");
                }
            }
            Undo.RecordObject(controller, "Add Animator Parameter");
            controller.AddParameter(paramName, paramType);
            // Set default value if provided
            var defaultStr = request.StringArg("defaultValue");
            if (defaultStr != null)
            {
                var allParams = controller.parameters;
                var addedParam = allParams[allParams.Length - 1];
                switch (paramType)
                {
                    case AnimatorControllerParameterType.Float:
                        float fval;
                        if (float.TryParse(defaultStr, NumberStyles.Float, CultureInfo.InvariantCulture, out fval))
                            addedParam.defaultFloat = fval;
                        break;
                    case AnimatorControllerParameterType.Int:
                        int ival;
                        if (int.TryParse(defaultStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ival))
                            addedParam.defaultInt = ival;
                        break;
                    case AnimatorControllerParameterType.Bool:
                        addedParam.defaultBool = string.Equals(defaultStr, "true", StringComparison.OrdinalIgnoreCase) || defaultStr == "1";
                        break;
                }
                controller.parameters = allParams;
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_add_parameter path=" + controllerPath + " parameter=" + paramName + " type=" + typeStr);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("parameterName", paramName)
                .Prop("parameterType", typeStr)
                .Prop("totalParameters", (long)controller.parameters.Length)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.add-parameter").Prop("path", controllerPath).Prop("parameterName", paramName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerGetInfo(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var layerName = request.StringArg("layerName");
            var stateName = request.StringArg("stateName");
            var includeTransitions = request.BoolArg("includeTransitions", false);
            var includeBlendTrees = request.BoolArg("includeBlendTrees", false);
            var maxStatesPerLayer = Math.Max(1, Math.Min(request.IntArg("maxStatesPerLayer", 50), 500));
            var maxTransitionsPerState = Math.Max(1, Math.Min(request.IntArg("maxTransitionsPerState", 20), 500));
            var pageSize = LargeOutputPageSize(request, 50, 500);
            var cursor = LargeOutputCursor(request);
            var layerRows = new List<AnimatorControllerLayer>();
            foreach (var layer in controller.layers)
            {
                if (string.IsNullOrEmpty(layerName) || string.Equals(layer.name, layerName, StringComparison.OrdinalIgnoreCase))
                {
                    layerRows.Add(layer);
                }
            }
            var page = layerRows.Skip(cursor).Take(pageSize).ToList();
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("guid", AssetDatabase.AssetPathToGUID(controllerPath))
                .Prop("name", controller.name)
                .Prop("layerCount", (long)controller.layers.Length)
                .Prop("parameterCount", (long)controller.parameters.Length)
                .Prop("layerName", layerName)
                .Prop("stateName", stateName)
                .Prop("includeTransitions", includeTransitions)
                .Prop("includeBlendTrees", includeBlendTrees)
                .PropArray("layers", arr =>
                {
                    for (int i = 0; i < page.Count; i++)
                    {
                        WriteControllerLayerSummary(arr, page[i], cursor + i, stateName, includeTransitions, includeBlendTrees, maxStatesPerLayer, maxTransitionsPerState);
                    }
                })
                .PropArray("parameters", arr =>
                {
                    foreach (var p in controller.parameters)
                    {
                        arr.BeginObject()
                            .Prop("name", p.name)
                            .Prop("type", p.type.ToString())
                            .Prop("defaultFloat", p.defaultFloat)
                            .Prop("defaultInt", (long)p.defaultInt)
                            .Prop("defaultBool", p.defaultBool)
                            .EndObject();
                    }
                })
                .PropRaw("pageInfo", PageInfoJson(pageSize, cursor, layerRows.Count, page.Count))
                .Prop("logPath", LogPath())
                .EndObject();
            return SuccessJson(writer.ToString());
        }

        private static void WriteControllerLayerSummary(UnityCliJsonWriter arr, AnimatorControllerLayer layer, int index, string stateName, bool includeTransitions, bool includeBlendTrees, int maxStatesPerLayer, int maxTransitionsPerState)
        {
            var stateMachine = layer.stateMachine;
            var anyStateTransitions = stateMachine != null && stateMachine.anyStateTransitions != null
                ? stateMachine.anyStateTransitions
                : new AnimatorStateTransition[0];
            var states = new List<ChildAnimatorState>();
            if (stateMachine != null)
            {
                foreach (var cs in stateMachine.states)
                {
                    if (cs.state == null) continue;
                    if (string.IsNullOrEmpty(stateName) || string.Equals(cs.state.name, stateName, StringComparison.OrdinalIgnoreCase))
                    {
                        states.Add(cs);
                    }
                }
            }
            var emittedStates = states.Take(maxStatesPerLayer).ToList();
            arr.BeginObject()
                .Prop("index", (long)index)
                .Prop("name", layer.name)
                .Prop("defaultWeight", layer.defaultWeight)
                .Prop("blendingMode", layer.blendingMode.ToString())
                .Prop("stateCount", (long)(stateMachine != null ? stateMachine.states.Length : 0))
                .Prop("returnedStateCount", (long)emittedStates.Count)
                .Prop("statesTruncated", states.Count > emittedStates.Count)
                .Prop("anyStateTransitionCount", (long)anyStateTransitions.Length)
                .PropArray("states", stateArr =>
                {
                    foreach (var cs in emittedStates)
                    {
                        WriteControllerStateSummary(stateArr, stateMachine, cs, includeTransitions, includeBlendTrees, maxTransitionsPerState);
                    }
                });
            if (includeTransitions)
            {
                arr.PropArray("anyStateTransitions", transitionArr => WriteControllerTransitions(transitionArr, anyStateTransitions.Take(maxTransitionsPerState).ToArray()));
                arr.Prop("anyStateTransitionsTruncated", anyStateTransitions.Length > maxTransitionsPerState);
            }
            arr.EndObject();
        }

        private static void WriteControllerStateSummary(UnityCliJsonWriter stateArr, AnimatorStateMachine stateMachine, ChildAnimatorState cs, bool includeTransitions, bool includeBlendTrees, int maxTransitionsPerState)
        {
            var transitions = cs.state.transitions ?? new AnimatorStateTransition[0];
            stateArr.BeginObject()
                .Prop("name", cs.state.name)
                .Prop("speed", cs.state.speed)
                .Prop("hasMotion", cs.state.motion != null)
                .Prop("motionName", cs.state.motion != null ? cs.state.motion.name : null)
                .Prop("motionType", cs.state.motion != null ? cs.state.motion.GetType().Name : null)
                .Prop("blendTreeChildCount", cs.state.motion is BlendTree bt ? (long)bt.children.Length : 0L)
                .Prop("isDefault", stateMachine != null && stateMachine.defaultState == cs.state)
                .Prop("transitionCount", (long)transitions.Length)
                .Prop("transitionsTruncated", includeTransitions && transitions.Length > maxTransitionsPerState);
            if (includeBlendTrees)
            {
                stateArr.PropRaw("blendTree", BlendTreeJson(cs.state.motion as BlendTree));
            }
            if (includeTransitions)
            {
                stateArr.PropArray("transitions", transitionArr => WriteControllerTransitions(transitionArr, transitions.Take(maxTransitionsPerState).ToArray()));
            }
            stateArr.EndObject();
        }

        /// <summary>写出 BlendTree 参数与子 motion 明细，供 AI 审计 blend tree 子节点是否落盘。</summary>
        private static string BlendTreeJson(BlendTree blendTree)
        {
            if (blendTree == null) return "null";
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", blendTree.name)
                .Prop("blendType", blendTree.blendType.ToString())
                .Prop("blendParameter", blendTree.blendParameter)
                .Prop("blendParameterY", blendTree.blendParameterY)
                .Prop("childCount", (long)blendTree.children.Length)
                .PropArray("children", arr =>
                {
                    foreach (var child in blendTree.children)
                    {
                        var motionPath = child.motion != null ? NormalizeAssetPath(AssetDatabase.GetAssetPath(child.motion)) : null;
                        arr.BeginObject()
                            .Prop("motionName", child.motion != null ? child.motion.name : null)
                            .Prop("motionType", child.motion != null ? child.motion.GetType().Name : null)
                            .Prop("motionPath", motionPath)
                            .Prop("threshold", child.threshold)
                            .PropObject("position", pos => pos.Prop("x", child.position.x).Prop("y", child.position.y))
                            .EndObject();
                    }
                })
                .EndObject()
                .ToString();
        }

        /// <summary>写出 AnimatorController transition 的目标、时间参数与条件，供 AI 审计状态机修改结果。</summary>
        private static void WriteControllerTransitions(UnityCliJsonWriter arr, AnimatorStateTransition[] transitions)
        {
            foreach (var transition in transitions ?? new AnimatorStateTransition[0])
            {
                if (transition == null) continue;
                var conditions = transition.conditions ?? new AnimatorCondition[0];
                arr.BeginObject()
                    .Prop("destinationStateName", transition.destinationState != null ? transition.destinationState.name : null)
                    .Prop("hasExitTime", transition.hasExitTime)
                    .Prop("duration", transition.duration)
                    .Prop("exitTime", transition.exitTime)
                    .Prop("conditionCount", (long)conditions.Length)
                    .PropArray("conditions", conditionArr =>
                    {
                        foreach (var condition in conditions)
                        {
                            conditionArr.BeginObject()
                                .Prop("parameter", condition.parameter)
                                .Prop("mode", condition.mode.ToString())
                                .Prop("threshold", condition.threshold)
                                .EndObject();
                        }
                    })
                    .EndObject();
            }
        }

        private static string ControllerAssign(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var targetStr = request.StringArg("target");
            if (string.IsNullOrEmpty(targetStr))
            {
                return ErrorJson("E_TARGET_REQUIRED", "controller.assign requires target.", "Pass the name or path of a target GameObject.");
            }
            var gameObject = ResolveUniqueTarget(targetStr, "controller.assign requires target.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                Undo.RecordObject(gameObject, "Add Animator Component");
                animator = Undo.AddComponent<Animator>(gameObject);
            }
            Undo.RecordObject(animator, "Assign AnimatorController");
            animator.runtimeAnimatorController = controller;
            EditorUtility.SetDirty(animator);
            EditorUtility.SetDirty(gameObject);
            AssetDatabase.SaveAssets();
            WriteLog("controller_assign path=" + controllerPath + " target=" + HierarchyPath(gameObject));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .Prop("controllerName", controller.name)
                .Prop("controllerPath", controllerPath)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.assign").Prop("path", controllerPath).Prop("target", HierarchyPath(gameObject)).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerAddLayer(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var layerName = request.StringArg("layerName");
            if (string.IsNullOrEmpty(layerName))
            {
                return ErrorJson("E_LAYER_NAME_REQUIRED", "controller.add-layer requires layerName.", "Pass a name for the new layer.");
            }
            var weight = request.FloatArg("weight", 1f);
            var blendingModeStr = (request.StringArg("blendingMode") ?? "override").ToLowerInvariant();
            var blendingMode = blendingModeStr == "additive"
                ? AnimatorLayerBlendingMode.Additive
                : AnimatorLayerBlendingMode.Override;
            Undo.RecordObject(controller, "Add Layer");
            controller.AddLayer(layerName);
            var layers = controller.layers;
            var newLayer = layers[layers.Length - 1];
            newLayer.defaultWeight = weight;
            newLayer.blendingMode = blendingMode;
            layers[layers.Length - 1] = newLayer;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_add_layer path=" + controllerPath + " layer=" + layerName + " weight=" + weight.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("layerName", layerName)
                .Prop("layerIndex", (long)(layers.Length - 1))
                .Prop("weight", weight)
                .Prop("blendingMode", blendingMode.ToString())
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.add-layer").Prop("path", controllerPath).Prop("layerName", layerName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerRemoveLayer(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var layerIndexVal = request.OptionalIntArg("layerIndex");
            var layerName = request.StringArg("layerName");
            if (!layerIndexVal.HasValue && string.IsNullOrEmpty(layerName))
            {
                return ErrorJson("E_LAYER_IDENTIFIER_REQUIRED", "controller.remove-layer requires layerIndex or layerName.", "Pass either a layer index or a layer name.");
            }
            var layers = controller.layers;
            int targetIndex;
            if (layerIndexVal.HasValue)
            {
                targetIndex = layerIndexVal.Value;
                if (targetIndex < 0 || targetIndex >= layers.Length)
                {
                    return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + targetIndex.ToString(CultureInfo.InvariantCulture) + " out of range (0-" + (layers.Length - 1).ToString(CultureInfo.InvariantCulture) + ").", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
                }
                if (targetIndex == 0)
                {
                    return ErrorJson("E_LAYER_BASE_IMMUTABLE", "Cannot remove base layer (index 0).", "Remove layers other than the base layer.");
                }
                layerName = layers[targetIndex].name;
            }
            else
            {
                targetIndex = -1;
                for (var i = 0; i < layers.Length; i++)
                {
                    if (layers[i].name == layerName) { targetIndex = i; break; }
                }
                if (targetIndex < 0)
                {
                    return ErrorJson("E_LAYER_NOT_FOUND", "Layer '" + layerName + "' not found.", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
                }
                if (targetIndex == 0)
                {
                    return ErrorJson("E_LAYER_BASE_IMMUTABLE", "Cannot remove base layer '" + layerName + "'.", "Remove layers other than the base layer.");
                }
            }
            Undo.RecordObject(controller, "Remove Layer");
            controller.RemoveLayer(targetIndex);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_remove_layer path=" + controllerPath + " layer=" + layerName + " index=" + targetIndex.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("layerName", layerName)
                .Prop("layerIndex", (long)targetIndex)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.remove-layer").Prop("path", controllerPath).Prop("layerName", layerName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerSetLayerWeight(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var layerIndexVal = request.OptionalIntArg("layerIndex");
            var layerName = request.StringArg("layerName");
            if (!layerIndexVal.HasValue && string.IsNullOrEmpty(layerName))
            {
                return ErrorJson("E_LAYER_IDENTIFIER_REQUIRED", "controller.set-layer-weight requires layerIndex or layerName.", "Pass either a layer index or a layer name.");
            }
            var weight = request.FloatArg("weight", 1f);
            var layers = controller.layers;
            int targetIndex;
            if (layerIndexVal.HasValue)
            {
                targetIndex = layerIndexVal.Value;
                if (targetIndex < 0 || targetIndex >= layers.Length)
                {
                    return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + targetIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
                }
                layerName = layers[targetIndex].name;
            }
            else
            {
                targetIndex = -1;
                for (var i = 0; i < layers.Length; i++)
                {
                    if (layers[i].name == layerName) { targetIndex = i; break; }
                }
                if (targetIndex < 0)
                {
                    return ErrorJson("E_LAYER_NOT_FOUND", "Layer '" + layerName + "' not found.", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
                }
            }
            Undo.RecordObject(controller, "Set Layer Weight");
            var layer = layers[targetIndex];
            layer.defaultWeight = weight;
            layers[targetIndex] = layer;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_set_layer_weight path=" + controllerPath + " layer=" + layerName + " weight=" + weight.ToString(CultureInfo.InvariantCulture));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("layerName", layerName)
                .Prop("layerIndex", (long)targetIndex)
                .Prop("weight", weight)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.set-layer-weight").Prop("path", controllerPath).Prop("layerName", layerName).Prop("weight", weight).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerCreateBlendTree1D(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var stateName = request.StringArg("stateName");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller.create-blend-tree-1d requires stateName.", "Pass a name for the blend tree state.");
            }
            var blendParameter = request.StringArg("blendParameter");
            if (string.IsNullOrEmpty(blendParameter))
            {
                return ErrorJson("E_BLEND_PARAMETER_REQUIRED", "controller.create-blend-tree-1d requires blendParameter.", "Pass the name of the blend parameter.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
            }
            var stateMachine = controller.layers[layerIndex].stateMachine;
            if (FindStateInLayer(stateMachine, stateName) != null)
            {
                return ErrorJson("E_STATE_EXISTS", "State '" + stateName + "' already exists in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Choose a different state name or use controller.get-info to inspect existing states.");
            }
            Undo.RecordObject(controller, "Create Blend Tree 1D");
            var state = stateMachine.AddState(stateName);
            var blendTree = new BlendTree
            {
                name = stateName,
                blendType = BlendTreeType.Simple1D,
                blendParameter = blendParameter,
                hideFlags = HideFlags.HideInHierarchy
            };
            AssetDatabase.AddObjectToAsset(blendTree, controller);
            state.motion = blendTree;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_create_blend_tree_1d path=" + controllerPath + " state=" + stateName + " parameter=" + blendParameter);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("stateName", stateName)
                .Prop("layerIndex", (long)layerIndex)
                .Prop("blendParameter", blendParameter)
                .Prop("blendType", "Simple1D")
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.create-blend-tree-1d").Prop("path", controllerPath).Prop("stateName", stateName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerCreateBlendTree2D(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var stateName = request.StringArg("stateName");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller.create-blend-tree-2d requires stateName.", "Pass a name for the blend tree state.");
            }
            var blendParameterX = request.StringArg("blendParameterX");
            var blendParameterY = request.StringArg("blendParameterY");
            if (string.IsNullOrEmpty(blendParameterX) || string.IsNullOrEmpty(blendParameterY))
            {
                return ErrorJson("E_BLEND_PARAMETER_REQUIRED", "controller.create-blend-tree-2d requires blendParameterX and blendParameterY.", "Pass the names of both blend parameters.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
            }
            var blendTypeStr = (request.StringArg("blendType") ?? "simpledirectional2d").ToLowerInvariant();
            BlendTreeType blendType;
            switch (blendTypeStr)
            {
                case "freeformdirectional2d": blendType = BlendTreeType.FreeformDirectional2D; break;
                case "freeformcartesian2d": blendType = BlendTreeType.FreeformCartesian2D; break;
                default: blendType = BlendTreeType.SimpleDirectional2D; break;
            }
            var stateMachine = controller.layers[layerIndex].stateMachine;
            if (FindStateInLayer(stateMachine, stateName) != null)
            {
                return ErrorJson("E_STATE_EXISTS", "State '" + stateName + "' already exists in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Choose a different state name or use controller.get-info to inspect existing states.");
            }
            Undo.RecordObject(controller, "Create Blend Tree 2D");
            var state = stateMachine.AddState(stateName);
            var blendTree = new BlendTree
            {
                name = stateName,
                blendType = blendType,
                blendParameter = blendParameterX,
                blendParameterY = blendParameterY,
                hideFlags = HideFlags.HideInHierarchy
            };
            AssetDatabase.AddObjectToAsset(blendTree, controller);
            state.motion = blendTree;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_create_blend_tree_2d path=" + controllerPath + " state=" + stateName + " parameterX=" + blendParameterX + " parameterY=" + blendParameterY);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("stateName", stateName)
                .Prop("layerIndex", (long)layerIndex)
                .Prop("blendParameterX", blendParameterX)
                .Prop("blendParameterY", blendParameterY)
                .Prop("blendType", blendType.ToString())
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.create-blend-tree-2d").Prop("path", controllerPath).Prop("stateName", stateName).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ControllerAddBlendTreeChild(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with tools run controller.create --input '{\"controllerPath\":\"<Assets/...>.controller\"}'.");
            }
            var stateName = request.StringArg("stateName");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller.add-blend-tree-child requires stateName.", "Pass the name of the blend tree state.");
            }
            var clipPath = request.StringArg("clipPath");
            if (string.IsNullOrEmpty(clipPath))
            {
                return ErrorJson("E_CLIP_PATH_REQUIRED", "controller.add-blend-tree-child requires clipPath.", "Pass the path of the AnimationClip to add.");
            }
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(NormalizeAssetPath(clipPath));
            if (clip == null)
            {
                return ErrorJson("E_CLIP_NOT_FOUND", "AnimationClip not found at: " + clipPath, "Check the clip path.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Run tools run controller.get-info with the same controllerPath to inspect available layers.");
            }
            var stateMachine = controller.layers[layerIndex].stateMachine;
            var state = FindStateInLayer(stateMachine, stateName);
            if (state == null)
            {
                return ErrorJson("E_STATE_NOT_FOUND", "State '" + stateName + "' not found in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Run tools run controller.get-info with the same controllerPath to inspect available states.");
            }
            var blendTree = state.motion as BlendTree;
            if (blendTree == null)
            {
                return ErrorJson("E_NOT_A_BLEND_TREE", "State '" + stateName + "' does not have a BlendTree motion.", "Create a blend tree state with tools run controller.create-blend-tree-1d or tools run controller.create-blend-tree-2d first.");
            }
            Undo.RecordObject(blendTree, "Add Blend Tree Child");
            if (blendTree.blendType == BlendTreeType.Simple1D)
            {
                var threshold = request.OptionalFloatArg("threshold");
                if (!threshold.HasValue)
                {
                    return ErrorJson("E_THRESHOLD_REQUIRED", "Threshold is required for 1D blend trees.", "Pass --threshold <value>.");
                }
                blendTree.AddChild(clip, threshold.Value);
            }
            else
            {
                var posX = request.OptionalFloatArg("positionX");
                var posY = request.OptionalFloatArg("positionY");
                if (!posX.HasValue || !posY.HasValue)
                {
                    return ErrorJson("E_POSITION_REQUIRED", "position-x and position-y are required for 2D blend trees.", "Pass --position-x <x> --position-y <y>.");
                }
                blendTree.AddChild(clip, new Vector2(posX.Value, posY.Value));
            }
            EditorUtility.SetDirty(blendTree);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            WriteLog("controller_add_blend_tree_child path=" + controllerPath + " state=" + stateName + " clip=" + clipPath);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("stateName", stateName)
                .Prop("clipPath", clipPath)
                .Prop("childCount", (long)blendTree.children.Length)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "controller.add-blend-tree-child").Prop("path", controllerPath).Prop("stateName", stateName).Prop("clipPath", clipPath).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // ── JSON helpers for controller condition parsing ─────────────

        private static IEnumerable<string> ExtractArrayItems(string jsonArray)
        {
            if (string.IsNullOrEmpty(jsonArray) || jsonArray.Length < 2 || jsonArray[0] != '[') yield break;
            var depth = 0;
            var start = -1;
            for (var i = 0; i < jsonArray.Length; i++)
            {
                var ch = jsonArray[i];
                if (ch == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        yield return jsonArray.Substring(start, i - start + 1);
                        start = -1;
                    }
                }
            }
        }

        private static string ExtractFromJson(string json, string key)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static float ExtractFloatFromJson(string json, string key, float fallback)
        {
            var match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            if (match.Success)
            {
                float parsed;
                if (float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            return fallback;
        }
    }
}
#endif



