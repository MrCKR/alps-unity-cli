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
            RegisterCommand(registry, "animation.clip", "animation", true, AnimationClipCommand);
            RegisterCommand(registry, "animation.state", "animation", false, AnimationState);
            RegisterCommand(registry, "animation.play", "animation", true, AnimationPlay);
            RegisterCommand(registry, "animation.stop", "animation", true, AnimationStop);
            RegisterCommand(registry, "animator.get-info", "animation", false, AnimatorGetInfo);
            RegisterCommand(registry, "animator.get-parameter", "animation", false, AnimatorGetParameter);
            RegisterCommand(registry, "animator.set-parameter", "animation", true, AnimatorSetParameter);
            RegisterCommand(registry, "animator.crossfade", "animation", true, AnimatorCrossfade);
            RegisterCommand(registry, "animator.set-speed", "animation", true, AnimatorSetSpeed);
            RegisterCommand(registry, "animator.set-enabled", "animation", true, AnimatorSetEnabled);
            // ── controller.* AnimatorController editing tools ───
            RegisterCommand(registry, "controller.create", "animation", true, ControllerCreate);
            RegisterCommand(registry, "controller.add-state", "animation", true, ControllerAddState);
            RegisterCommand(registry, "controller.add-transition", "animation", true, ControllerAddTransition);
            RegisterCommand(registry, "controller.add-parameter", "animation", true, ControllerAddParameter);
            RegisterCommand(registry, "controller.get-info", "animation", false, ControllerGetInfo);
            RegisterCommand(registry, "controller.assign", "animation", true, ControllerAssign);
            RegisterCommand(registry, "controller.add-layer", "animation", true, ControllerAddLayer);
            RegisterCommand(registry, "controller.remove-layer", "animation", true, ControllerRemoveLayer);
            RegisterCommand(registry, "controller.set-layer-weight", "animation", true, ControllerSetLayerWeight);
            RegisterCommand(registry, "controller.create-blend-tree-1d", "animation", true, ControllerCreateBlendTree1D);
            RegisterCommand(registry, "controller.create-blend-tree-2d", "animation", true, ControllerCreateBlendTree2D);
            RegisterCommand(registry, "controller.add-blend-tree-child", "animation", true, ControllerAddBlendTreeChild);
        }

        private static string AnimationClipCommand(BridgeRequest request)
        {
            var action = string.Equals(request.StringArg("action"), "create", StringComparison.OrdinalIgnoreCase) ? "create" : "info";
            var path = RequireAnimationClipPath(request.StringArg("path"));
            if (action == "create")
            {
                var overwrite = request.BoolArg("overwrite", false);
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null && !overwrite)
                {
                    return ErrorJson("E_ANIMATION_CLIP_EXISTS", "AnimationClip already exists: " + path, "Pass --overwrite or choose a different Assets/*.anim path.");
                }
                CreateAssetDirectory(path);
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                var clip = new AnimationClip();
                clip.name = Path.GetFileNameWithoutExtension(path);
                clip.frameRate = Math.Max(1f, request.FloatArg("frameRate", 60f));
                clip.legacy = request.BoolArg("legacy", false);
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                WriteLog("animation_clip_created path=" + path);
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("clip", AnimationClipJson(path, LoadAnimationClipAsset(path)))
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "animation.clip.create").Prop("path", path).EndObject())
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("clip", AnimationClipJson(path, LoadAnimationClipAsset(path)))
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
            if (animator.runtimeAnimatorController == null || animator.layerCount <= 0)
            {
                return ErrorJson("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController assigned (layerCount=0): " + HierarchyPath(gameObject), "Assign a RuntimeAnimatorController with at least one layer, then retry.");
            }
            var state = request.StringArg("state");
            if (string.IsNullOrEmpty(state))
            {
                return ErrorJson("E_ANIMATION_STATE_REQUIRED", "Animation play requires --state <name>.", "Pass an Animator state name or hash-compatible state path.");
            }
            var layer = request.IntArg("layer", 0);
            if (layer < 0 || layer >= animator.layerCount)
            {
                return ErrorJson("E_ANIMATION_LAYER_INVALID", "Animator layer is out of range: " + layer.ToString(CultureInfo.InvariantCulture), "Run unity_cli animation state --target <go> to inspect layer count.");
            }
            animator.Play(state, layer, request.FloatArg("normalizedTime", 0f));
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
                throw new BridgeCommandException("E_ANIMATION_CLIP_PATH_REQUIRED", "Pass an Assets/*.anim path.", "Use unity_cli animation clip <path> or pass --path Assets/Animations/Example.anim.");
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
                throw new BridgeCommandException("E_ANIMATION_CLIP_NOT_FOUND", "AnimationClip asset not found: " + path, "Check the path or create one with unity_cli animation clip <path> --create.");
            }
            return clip;
        }

        private static string AnimationClipJson(string path, AnimationClip clip)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", clip.name)
                .Prop("length", clip.length)
                .Prop("frameRate", clip.frameRate)
                .Prop("legacy", clip.legacy)
                .PropRaw("bindings", AnimationBindingsJson(clip, bindings))
                .EndObject().ToString();
        }

        private static string AnimationBindingsJson(AnimationClip clip, EditorCurveBinding[] bindings)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)bindings.Length)
                .PropArray("items", arr =>
                {
                    foreach (var binding in bindings)
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        arr.BeginObject()
                            .Prop("path", binding.path)
                            .Prop("property", binding.propertyName)
                            .Prop("type", binding.type == null ? null : binding.type.Name)
                            .Prop("keyCount", curve == null ? (long)0 : (long)curve.keys.Length)
                            .EndObject();
                    }
                })
                .EndObject().ToString();
        }

        private static string AnimatorJson(Animator animator, int layer)
        {
            var controller = animator.runtimeAnimatorController;
            var controllerPath = controller == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(controller));
            var layerCount = animator.layerCount;
            var safeLayer = layerCount <= 0 ? 0 : Mathf.Clamp(layer, 0, layerCount - 1);
            var state = layerCount > 0 ? animator.GetCurrentAnimatorStateInfo(safeLayer) : default(AnimatorStateInfo);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("enabled", animator.enabled)
                .Prop("layerCount", (long)layerCount)
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
            var layerCount = animator.layerCount;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("target", GameObjectTargetJson(gameObject))
                .PropObject("animator", obj => obj
                    .Prop("enabled", animator.enabled)
                    .Prop("speed", animator.speed)
                    .Prop("applyRootMotion", animator.applyRootMotion)
                    .Prop("updateMode", animator.updateMode.ToString())
                    .Prop("cullingMode", animator.cullingMode.ToString())
                    .Prop("layerCount", (long)layerCount)
                    .Prop("parameterCount", (long)animator.parameterCount)
                    .PropObject("runtimeAnimatorController", ctrl => ctrl
                        .Prop("name", animator.runtimeAnimatorController == null ? null : animator.runtimeAnimatorController.name)
                        .Prop("path", controllerPath)
                        .Prop("guid", string.IsNullOrEmpty(controllerPath) ? null : AssetDatabase.AssetPathToGUID(controllerPath)))
                    .PropArray("parameters", arr =>
                    {
                        for (var i = 0; i < animator.parameterCount; i++)
                        {
                            var p = animator.GetParameter(i);
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
                            var stateInfo = animator.GetCurrentAnimatorStateInfo(i);
                            var inTransition = animator.IsInTransition(i);
                            arr.BeginObject()
                                .Prop("index", (long)i)
                                .Prop("weight", animator.GetLayerWeight(i))
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
            // Find the parameter definition to determine its type
            AnimatorControllerParameter foundParam = null;
            for (var i = 0; i < animator.parameterCount; i++)
            {
                var p = animator.GetParameter(i);
                if (p.name == parameterName)
                {
                    foundParam = p;
                    break;
                }
            }
            if (foundParam == null)
            {
                return ErrorJson("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + parameterName + "' not found on Animator: " + HierarchyPath(gameObject), "Run animator.get-info --target <go> to list available parameters.");
            }
            var param = foundParam;
            object value;
            switch (param.type)
            {
                case AnimatorControllerParameterType.Float:
                    value = animator.GetFloat(parameterName);
                    break;
                case AnimatorControllerParameterType.Int:
                    value = (long)animator.GetInteger(parameterName);
                    break;
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    value = animator.GetBool(parameterName);
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
            // Find parameter definition to determine type
            AnimatorControllerParameter foundParam = null;
            for (var i = 0; i < animator.parameterCount; i++)
            {
                var p = animator.GetParameter(i);
                if (p.name == parameterName)
                {
                    foundParam = p;
                    break;
                }
            }
            if (foundParam == null)
            {
                return ErrorJson("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + parameterName + "' not found on Animator: " + HierarchyPath(gameObject), "Run animator.get-info --target <go> to list available parameters.");
            }
            var param = foundParam;
            var paramType = request.StringArg("parameterType");
            // In edit mode, modify the AnimatorController asset defaults.
            // unity-mcp also has a play-mode path (runtime SetFloat/SetInteger/SetBool),
            // but alps-unity-cli's execute guard blocks bridge write commands in Play Mode,
            // so only the edit-mode (controller-asset) path is reachable.
            var ac = animator.runtimeAnimatorController as AnimatorController;
            if (ac == null)
            {
                return ErrorJson("E_ANIMATOR_NO_CONTROLLER", "Animator has no AnimatorController assigned (cannot set parameter defaults in edit mode): " + HierarchyPath(gameObject), "Assign an AnimatorController asset with at least one parameter, then retry.");
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
                return ErrorJson("E_ANIMATOR_PARAMETER_NOT_FOUND", "Parameter '" + parameterName + "' not found on AnimatorController: " + ac.name, "Run animator.get-info --target <go> to list available parameters.");
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
            if (animator.runtimeAnimatorController == null || animator.layerCount <= 0)
            {
                return ErrorJson("E_ANIMATION_NO_CONTROLLER", "Animator has no RuntimeAnimatorController assigned (layerCount=0): " + HierarchyPath(gameObject), "Assign a RuntimeAnimatorController with at least one layer, then retry.");
            }
            var stateName = request.StringArg("state");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_ANIMATION_STATE_REQUIRED", "Animator crossfade requires --state <name>.", "Pass an Animator state name or hash-compatible state path.");
            }
            var duration = request.FloatArg("duration", 0.25f);
            var layer = request.IntArg("layer", -1);
            if (layer >= 0 && layer >= animator.layerCount)
            {
                return ErrorJson("E_ANIMATION_LAYER_INVALID", "Animator layer is out of range: " + layer.ToString(CultureInfo.InvariantCulture), "Run animator.get-info --target <go> to inspect layer count.");
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
                throw new BridgeCommandException("E_CONTROLLER_PATH_REQUIRED", "Pass --controller-path <Assets/...>.controller>.", "Use unity_cli controller create <path> to create one first.");
            }
            if (!path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_CONTROLLER_PATH_INVALID", "AnimatorController path must end with .controller: " + path, "Pass a Unity asset path such as Assets/Animations/Player.controller.");
            }
            return path;
        }

        private static AnimatorState FindStateInLayer(AnimatorStateMachine sm, string stateName)
        {
            foreach (var cs in sm.states)
            {
                if (cs.state != null && cs.state.name == stateName) return cs.state;
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var stateName = request.StringArg("stateName");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller add-state requires --state-name <name>.", "Pass a name for the new state.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range (controller has " + controller.layers.Length.ToString(CultureInfo.InvariantCulture) + " layers).", "Use controller get-info <path> to inspect available layers.");
            }
            var rootStateMachine = controller.layers[layerIndex].stateMachine;
            if (FindStateInLayer(rootStateMachine, stateName) != null)
            {
                return ErrorJson("E_STATE_EXISTS", "State '" + stateName + "' already exists in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Choose a different state name or use a different layer.");
            }
            Undo.RecordObject(controller, "Add Animator State");
            var state = rootStateMachine.AddState(stateName);
            // Optionally assign a clip
            var clipPath = request.StringArg("clipPath");
            if (!string.IsNullOrEmpty(clipPath))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(NormalizeAssetPath(clipPath));
                if (clip != null) state.motion = clip;
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var fromStateName = request.StringArg("fromState");
            var toStateName = request.StringArg("toState");
            if (string.IsNullOrEmpty(fromStateName) || string.IsNullOrEmpty(toStateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller add-transition requires --from-state and --to-state.", "Pass source and destination state names.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Use controller get-info <path> to inspect available layers.");
            }
            var rootStateMachine = controller.layers[layerIndex].stateMachine;
            var isAnyState = string.Equals(fromStateName, "AnyState", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fromStateName, "Any", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fromStateName, "Any State", StringComparison.OrdinalIgnoreCase);
            var toState = FindStateInLayer(rootStateMachine, toStateName);
            if (toState == null)
            {
                return ErrorJson("E_STATE_NOT_FOUND", "Destination state '" + toStateName + "' not found in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Use controller get-info <path> to inspect available states.");
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
                    return ErrorJson("E_STATE_NOT_FOUND", "Source state '" + fromStateName + "' not found in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Use controller get-info <path> to inspect available states.");
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var paramName = request.StringArg("parameterName");
            if (string.IsNullOrEmpty(paramName))
            {
                return ErrorJson("E_PARAMETER_NAME_REQUIRED", "controller add-parameter requires --parameter-name <name>.", "Pass a name for the new parameter.");
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("path", controllerPath)
                .Prop("guid", AssetDatabase.AssetPathToGUID(controllerPath))
                .Prop("name", controller.name)
                .Prop("layerCount", (long)controller.layers.Length)
                .Prop("parameterCount", (long)controller.parameters.Length)
                .PropArray("layers", arr =>
                {
                    for (int i = 0; i < controller.layers.Length; i++)
                    {
                        var layer = controller.layers[i];
                        arr.BeginObject()
                            .Prop("index", (long)i)
                            .Prop("name", layer.name)
                            .Prop("defaultWeight", layer.defaultWeight)
                            .Prop("blendingMode", layer.blendingMode.ToString())
                            .Prop("stateCount", (long)(layer.stateMachine != null ? layer.stateMachine.states.Length : 0))
                            .PropArray("states", stateArr =>
                            {
                                if (layer.stateMachine != null)
                                {
                                    foreach (var cs in layer.stateMachine.states)
                                    {
                                        stateArr.BeginObject()
                                            .Prop("name", cs.state.name)
                                            .Prop("speed", cs.state.speed)
                                            .Prop("hasMotion", cs.state.motion != null)
                                            .Prop("motionName", cs.state.motion != null ? cs.state.motion.name : null)
                                            .Prop("isDefault", layer.stateMachine.defaultState == cs.state)
                                            .Prop("transitionCount", (long)cs.state.transitions.Length)
                                            .EndObject();
                                    }
                                }
                            })
                            .EndObject();
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
                .Prop("logPath", LogPath())
                .EndObject();
            return SuccessJson(writer.ToString());
        }

        private static string ControllerAssign(BridgeRequest request)
        {
            var controllerPath = RequireControllerPath(request.StringArg("controllerPath"));
            var controller = LoadControllerAsset(controllerPath);
            if (controller == null)
            {
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var targetStr = request.StringArg("target");
            if (string.IsNullOrEmpty(targetStr))
            {
                return ErrorJson("E_TARGET_REQUIRED", "controller assign requires --target <GameObject>.", "Pass the name or path of a target GameObject.");
            }
            var gameObject = ResolveUniqueTarget(targetStr, "controller assign requires --target <GameObject>.");
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var layerName = request.StringArg("layerName");
            if (string.IsNullOrEmpty(layerName))
            {
                return ErrorJson("E_LAYER_NAME_REQUIRED", "controller add-layer requires --layer-name <name>.", "Pass a name for the new layer.");
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var layerIndexVal = request.OptionalIntArg("layerIndex");
            var layerName = request.StringArg("layerName");
            if (!layerIndexVal.HasValue && string.IsNullOrEmpty(layerName))
            {
                return ErrorJson("E_LAYER_IDENTIFIER_REQUIRED", "controller remove-layer requires --layer-index or --layer-name.", "Pass either a layer index or a layer name.");
            }
            var layers = controller.layers;
            int targetIndex;
            if (layerIndexVal.HasValue)
            {
                targetIndex = layerIndexVal.Value;
                if (targetIndex < 0 || targetIndex >= layers.Length)
                {
                    return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + targetIndex.ToString(CultureInfo.InvariantCulture) + " out of range (0-" + (layers.Length - 1).ToString(CultureInfo.InvariantCulture) + ").", "Use controller get-info <path> to inspect available layers.");
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
                    return ErrorJson("E_LAYER_NOT_FOUND", "Layer '" + layerName + "' not found.", "Use controller get-info <path> to inspect available layers.");
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var layerIndexVal = request.OptionalIntArg("layerIndex");
            var layerName = request.StringArg("layerName");
            if (!layerIndexVal.HasValue && string.IsNullOrEmpty(layerName))
            {
                return ErrorJson("E_LAYER_IDENTIFIER_REQUIRED", "controller set-layer-weight requires --layer-index or --layer-name.", "Pass either a layer index or a layer name.");
            }
            var weight = request.FloatArg("weight", 1f);
            var layers = controller.layers;
            int targetIndex;
            if (layerIndexVal.HasValue)
            {
                targetIndex = layerIndexVal.Value;
                if (targetIndex < 0 || targetIndex >= layers.Length)
                {
                    return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + targetIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Use controller get-info <path> to inspect available layers.");
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
                    return ErrorJson("E_LAYER_NOT_FOUND", "Layer '" + layerName + "' not found.", "Use controller get-info <path> to inspect available layers.");
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var stateName = request.StringArg("stateName");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller create-blend-tree-1d requires --state-name <name>.", "Pass a name for the blend tree state.");
            }
            var blendParameter = request.StringArg("blendParameter");
            if (string.IsNullOrEmpty(blendParameter))
            {
                return ErrorJson("E_BLEND_PARAMETER_REQUIRED", "controller create-blend-tree-1d requires --blend-parameter <name>.", "Pass the name of the blend parameter.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Use controller get-info <path> to inspect available layers.");
            }
            var stateMachine = controller.layers[layerIndex].stateMachine;
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var stateName = request.StringArg("stateName");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller create-blend-tree-2d requires --state-name <name>.", "Pass a name for the blend tree state.");
            }
            var blendParameterX = request.StringArg("blendParameterX");
            var blendParameterY = request.StringArg("blendParameterY");
            if (string.IsNullOrEmpty(blendParameterX) || string.IsNullOrEmpty(blendParameterY))
            {
                return ErrorJson("E_BLEND_PARAMETER_REQUIRED", "controller create-blend-tree-2d requires --blend-parameter-x and --blend-parameter-y.", "Pass the names of both blend parameters.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Use controller get-info <path> to inspect available layers.");
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
                return ErrorJson("E_CONTROLLER_NOT_FOUND", "AnimatorController not found at: " + controllerPath, "Check the path or create one with unity_cli controller create <path>.");
            }
            var stateName = request.StringArg("stateName");
            if (string.IsNullOrEmpty(stateName))
            {
                return ErrorJson("E_STATE_NAME_REQUIRED", "controller add-blend-tree-child requires --state-name <name>.", "Pass the name of the blend tree state.");
            }
            var clipPath = request.StringArg("clipPath");
            if (string.IsNullOrEmpty(clipPath))
            {
                return ErrorJson("E_CLIP_PATH_REQUIRED", "controller add-blend-tree-child requires --clip-path <path>.", "Pass the path of the AnimationClip to add.");
            }
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(NormalizeAssetPath(clipPath));
            if (clip == null)
            {
                return ErrorJson("E_CLIP_NOT_FOUND", "AnimationClip not found at: " + clipPath, "Check the clip path.");
            }
            var layerIndex = request.IntArg("layerIndex", 0);
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                return ErrorJson("E_LAYER_INDEX_INVALID", "Layer index " + layerIndex.ToString(CultureInfo.InvariantCulture) + " out of range.", "Use controller get-info <path> to inspect available layers.");
            }
            var stateMachine = controller.layers[layerIndex].stateMachine;
            var state = FindStateInLayer(stateMachine, stateName);
            if (state == null)
            {
                return ErrorJson("E_STATE_NOT_FOUND", "State '" + stateName + "' not found in layer " + layerIndex.ToString(CultureInfo.InvariantCulture) + ".", "Use controller get-info <path> to inspect available states.");
            }
            var blendTree = state.motion as BlendTree;
            if (blendTree == null)
            {
                return ErrorJson("E_NOT_A_BLEND_TREE", "State '" + stateName + "' does not have a BlendTree motion.", "Create a blend tree state with controller create-blend-tree-1d or create-blend-tree-2d first.");
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
