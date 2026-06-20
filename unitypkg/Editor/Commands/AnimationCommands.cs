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

        // ── animator.* runtime-state tools (ALP-601) ──────────────────────────
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
            AnimatorControllerParameter? foundParam = null;
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
            var param = foundParam.Value;
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
                    .Prop("value", value))
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
            AnimatorControllerParameter? foundParam = null;
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
            var param = foundParam.Value;
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
    }
}
#endif
