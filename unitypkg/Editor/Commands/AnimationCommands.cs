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
    }
}
#endif
