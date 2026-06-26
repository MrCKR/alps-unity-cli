#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterBuildCommands(BridgeCommandRegistry registry)
        {
            // 对齐 CoplayDev manage_build：BuildTarget/PlayerSettings/Build Settings scenes
            RegisterCommand(registry, "build.scenes", "build", false, BuildScenes);
            RegisterCommand(registry, "build.platform", "build", false, BuildPlatform);
            RegisterCommand(registry, "build.settings", "build", false, BuildSettings);
        }

        /// <summary>读取/修改 Build Settings scenes 列表。</summary>
        private static string BuildScenes(BridgeRequest request)
        {
            var action = request.StringArg("action") ?? "get";
            if (action == "get")
            {
                var scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("command", "build.scenes").Prop("count", (long)scenes.Length);
                w.PropArray("scenes", arr =>
                {
                    for (var i = 0; i < scenes.Length; i++)
                    {
                        arr.BeginObject().Prop("path", NormalizeAssetPath(scenes[i].path)).Prop("enabled", scenes[i].enabled).Prop("buildIndex", (long)i).EndObject();
                    }
                });
                w.Prop("logPath", LogPath());
                return SuccessJson(w.EndObject().ToString());
            }
            if (action == "add" || action == "remove" || action == "enable" || action == "disable")
            {
                if (request.DryRun)
                {
                    return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.scenes").Prop("dryRun", true).Prop("action", action)
                        .Prop("logPath", LogPath()).EndObject().ToString());
                }
                var path = request.StringArg("path");
                var list = (EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0]).ToList();
                if (action == "add" && !string.IsNullOrEmpty(path))
                {
                    if (!list.Any(s => s.path == path)) list.Add(new EditorBuildSettingsScene(path, true));
                }
                else if (action == "remove" && !string.IsNullOrEmpty(path))
                {
                    list.RemoveAll(s => s.path == path);
                }
                else if ((action == "enable" || action == "disable") && !string.IsNullOrEmpty(path))
                {
                    var idx = list.FindIndex(s => s.path == path);
                    if (idx >= 0) list[idx] = new EditorBuildSettingsScene(path, action == "enable");
                }
                EditorBuildSettings.scenes = list.ToArray();
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.scenes").Prop("action", action).Prop("path", path).Prop("count", (long)list.Count)
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            return ErrorJson("E_BUILD_SCENES_ACTION", "Unknown action: " + action, "Use get, add, remove, enable, disable.");
        }

        /// <summary>读取/设置构建平台 (BuildTarget)。</summary>
        private static string BuildPlatform(BridgeRequest request)
        {
            var action = request.StringArg("action") ?? "get";
            if (action == "get")
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "build.platform")
                    .Prop("activeTarget", EditorUserBuildSettings.activeBuildTarget.ToString())
                    .Prop("activeTargetGroup", EditorUserBuildSettings.selectedBuildTargetGroup.ToString())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            if (action == "set")
            {
                var target = request.StringArg("target");
                if (string.IsNullOrEmpty(target))
                {
                    throw new BridgeCommandException("E_BUILD_TARGET_REQUIRED", "build.platform set requires --target.", "Pass a BuildTarget like StandaloneWindows64, Android, iOS.");
                }
                BuildTarget bt;
                if (!Enum.TryParse(target, true, out bt))
                {
                    throw new BridgeCommandException("E_BUILD_TARGET_UNKNOWN", "Unknown BuildTarget: " + target, "Pass a valid BuildTarget enum name.");
                }
                if (request.DryRun)
                {
                    return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.platform").Prop("dryRun", true).Prop("target", target)
                        .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "build.platform.set").Prop("sideEffects", "build-target-switch").EndObject())
                        .Prop("logPath", LogPath()).EndObject().ToString());
                }
                var message = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Unknown, bt);
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.platform").Prop("target", target).Prop("switched", message)
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            return ErrorJson("E_BUILD_PLATFORM_ACTION", "Unknown action: " + action, "Use get or set.");
        }

        /// <summary>读取 PlayerSettings 关键构建配置。</summary>
        private static string BuildSettings(BridgeRequest request)
        {
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "build.settings");
            w.Prop("productName", PlayerSettings.productName);
            w.Prop("bundleVersion", PlayerSettings.bundleVersion);
            w.Prop("companyId", PlayerSettings.companyName);
            w.Prop("applicationIdentifier", PlayerSettings.applicationIdentifier);
            w.Prop("scriptingBackend", PlayerSettings.GetScriptingBackend(BuildTargetGroup.Unknown).ToString());
            w.Prop("apiCompatibilityLevel", PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Unknown).ToString());
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }
    }
}
#endif
