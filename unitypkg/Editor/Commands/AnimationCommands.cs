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
                return SuccessJson("{\"clip\":" + AnimationClipJson(path, LoadAnimationClipAsset(path)) + ",\"plannedEffects\":[{\"type\":\"animation.clip.create\",\"path\":" + Quote(path) + "}],\"logPath\":" + Quote(LogPath()) + "}");
            }
            return SuccessJson("{\"clip\":" + AnimationClipJson(path, LoadAnimationClipAsset(path)) + ",\"logPath\":" + Quote(LogPath()) + "}");
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
            return SuccessJson("{\"target\":" + GameObjectTargetJson(gameObject) + ",\"animator\":" + AnimatorJson(animator, layer) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string AnimationPlay(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animation play requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
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
            return SuccessJson("{\"target\":" + GameObjectTargetJson(gameObject) + ",\"animator\":" + AnimatorJson(animator, layer) + ",\"plannedEffects\":[{\"type\":\"animation.play\",\"target\":" + Quote(HierarchyPath(gameObject)) + ",\"state\":" + Quote(state) + "}],\"logPath\":" + Quote(LogPath()) + "}");
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
            return SuccessJson("{\"target\":" + GameObjectTargetJson(gameObject) + ",\"animator\":" + AnimatorJson(animator, 0) + ",\"plannedEffects\":[{\"type\":\"animation.stop\",\"target\":" + Quote(HierarchyPath(gameObject)) + "}],\"logPath\":" + Quote(LogPath()) + "}");
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
            return "{"
                + "\"path\":" + Quote(path) + ","
                + "\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(path)) + ","
                + "\"name\":" + Quote(clip.name) + ","
                + "\"length\":" + Number(clip.length) + ","
                + "\"frameRate\":" + Number(clip.frameRate) + ","
                + "\"legacy\":" + Bool(clip.legacy) + ","
                + "\"bindings\":" + AnimationBindingsJson(clip, bindings)
                + "}";
        }

        private static string AnimationBindingsJson(AnimationClip clip, EditorCurveBinding[] bindings)
        {
            var items = new List<string>();
            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                items.Add("{\"path\":" + Quote(binding.path) + ",\"property\":" + Quote(binding.propertyName) + ",\"type\":" + Quote(binding.type == null ? null : binding.type.Name) + ",\"keyCount\":" + (curve == null ? "0" : curve.keys.Length.ToString(CultureInfo.InvariantCulture)) + "}");
            }
            return "{\"count\":" + bindings.Length.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        private static string AnimatorJson(Animator animator, int layer)
        {
            var controller = animator.runtimeAnimatorController;
            var controllerPath = controller == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(controller));
            var layerCount = animator.layerCount;
            var safeLayer = layerCount <= 0 ? 0 : Mathf.Clamp(layer, 0, layerCount - 1);
            var state = layerCount > 0 ? animator.GetCurrentAnimatorStateInfo(safeLayer) : default(AnimatorStateInfo);
            return "{"
                + "\"enabled\":" + Bool(animator.enabled) + ","
                + "\"layerCount\":" + layerCount.ToString(CultureInfo.InvariantCulture) + ","
                + "\"selectedLayer\":" + safeLayer.ToString(CultureInfo.InvariantCulture) + ","
                + "\"runtimeAnimatorController\":{\"name\":" + Quote(controller == null ? null : controller.name) + ",\"path\":" + Quote(controllerPath) + ",\"guid\":" + Quote(string.IsNullOrEmpty(controllerPath) ? null : AssetDatabase.AssetPathToGUID(controllerPath)) + "},"
                + "\"currentState\":{\"fullPathHash\":" + state.fullPathHash.ToString(CultureInfo.InvariantCulture) + ",\"shortNameHash\":" + state.shortNameHash.ToString(CultureInfo.InvariantCulture) + ",\"normalizedTime\":" + Number(state.normalizedTime) + ",\"length\":" + Number(state.length) + "}"
                + "}";
        }
    }
}
#endif
