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
        // build.* 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/build/*.cs),
        // 不再用 RegisterCommand。RegisterBuildCommands 已移除。

        private static string BuildScenes_Removed(BridgeRequest request)
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

        private static string BuildPlatform_Removed(BridgeRequest request)
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

        private static string BuildProfiles_Removed(BridgeRequest request)
        {
            var action = request.StringArg("action") ?? "list";
            var profileType = FindUnityType("UnityEditor.Build.Profile.BuildProfile") ?? FindUnityType("UnityEditor.BuildProfile");
            if (profileType == null)
            {
                if (action == "list" || action == "get")
                {
                    return SuccessJson(new UnityCliJsonWriter().BeginObject()
                        .Prop("command", "build.profiles")
                        .Prop("available", false)
                        .Prop("action", action)
                        .PropArray("profiles", arr => { })
                        .PropArray("diagnostics", arr => arr.BeginObject().Prop("code", "BUILD_PROFILE_API_UNAVAILABLE").Prop("message", "Unity Build Profile API is not available in this Editor version.").EndObject())
                        .PropArray("next_actions", arr => arr.Value("Use build.settings/build.scenes/build.platform, or upgrade to a Unity version that exposes Build Profiles."))
                        .Prop("logPath", LogPath()).EndObject().ToString());
                }
                throw new BridgeCommandException("E_BUILD_PROFILE_API_UNAVAILABLE", "Unity Build Profile API is not available in this Editor version.", "Use build.settings/build.scenes/build.platform, or upgrade Unity.");
            }

            if (action == "list" || action == "get")
            {
                var active = ActiveBuildProfile(profileType);
                var guids = AssetDatabase.FindAssets("t:" + profileType.Name);
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("command", "build.profiles").Prop("available", true).Prop("action", action);
                w.Prop("activePath", active != null ? AssetDatabase.GetAssetPath(active) : null);
                w.PropArray("profiles", arr =>
                {
                    foreach (var guid in guids)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        var profile = AssetDatabase.LoadAssetAtPath(path, profileType);
                        arr.BeginObject()
                            .Prop("path", path)
                            .Prop("name", profile != null ? profile.name : Path.GetFileNameWithoutExtension(path))
                            .Prop("active", active != null && profile == active)
                            .EndObject();
                    }
                });
                w.Prop("logPath", LogPath());
                return SuccessJson(w.EndObject().ToString());
            }

            if (action == "create")
            {
                var path = request.StringArg("path");
                if (string.IsNullOrEmpty(path)) throw new BridgeCommandException("E_BUILD_PROFILE_PATH_REQUIRED", "build.profiles create requires path.", "Pass an Assets/*.asset path for the new Build Profile.");
                if (request.DryRun)
                {
                    return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.profiles").Prop("dryRun", true).Prop("action", "create").Prop("path", path)
                        .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "build.profiles.create").Prop("path", path).EndObject())
                        .Prop("logPath", LogPath()).EndObject().ToString());
                }
                var profile = ScriptableObject.CreateInstance(profileType);
                var name = request.StringArg("name");
                if (!string.IsNullOrEmpty(name)) profile.name = name;
                AssetDatabase.CreateAsset(profile, path);
                AssetDatabase.SaveAssets();
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.profiles").Prop("action", "create").Prop("path", path).Prop("name", profile.name).Prop("logPath", LogPath()).EndObject().ToString());
            }

            if (action == "switch")
            {
                var path = request.StringArg("path");
                if (string.IsNullOrEmpty(path)) throw new BridgeCommandException("E_BUILD_PROFILE_PATH_REQUIRED", "build.profiles switch requires path.", "Pass an existing Build Profile asset path.");
                var profile = AssetDatabase.LoadAssetAtPath(path, profileType);
                if (profile == null) throw new BridgeCommandException("E_BUILD_PROFILE_NOT_FOUND", "Build Profile was not found: " + path, "Run build.profiles action=list and pass an existing path.");
                if (request.DryRun)
                {
                    return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.profiles").Prop("dryRun", true).Prop("action", "switch").Prop("path", path)
                        .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "build.profiles.switch").Prop("path", path).EndObject())
                        .Prop("logPath", LogPath()).EndObject().ToString());
                }
                SetActiveBuildProfile(profileType, profile);
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.profiles").Prop("action", "switch").Prop("path", path).Prop("activePath", path).Prop("logPath", LogPath()).EndObject().ToString());
            }

            if (action == "modify")
            {
                var path = request.StringArg("path");
                if (string.IsNullOrEmpty(path)) throw new BridgeCommandException("E_BUILD_PROFILE_PATH_REQUIRED", "build.profiles modify requires path.", "Pass an existing Build Profile asset path.");
                var profile = AssetDatabase.LoadAssetAtPath(path, profileType);
                if (profile == null) throw new BridgeCommandException("E_BUILD_PROFILE_NOT_FOUND", "Build Profile was not found: " + path, "Run build.profiles action=list and pass an existing path.");
                var newName = request.StringArg("name");
                if (request.DryRun)
                {
                    return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.profiles").Prop("dryRun", true).Prop("action", "modify").Prop("path", path)
                        .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "build.profiles.modify").Prop("path", path).Prop("name", newName).EndObject())
                        .Prop("logPath", LogPath()).EndObject().ToString());
                }
                if (!string.IsNullOrEmpty(newName)) { profile.name = newName; EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets(); }
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "build.profiles").Prop("action", "modify").Prop("path", path).Prop("name", profile.name).Prop("logPath", LogPath()).EndObject().ToString());
            }

            return ErrorJson("E_BUILD_PROFILE_ACTION", "Unknown action: " + action, "Use list, get, create, switch, or modify.");
        }

        /// <summary>在已加载程序集中查找 Unity 类型，兼容不同 Editor 版本的命名空间变化。</summary>
        private static Type FindUnityType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName, false); } catch { }
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>通过反射读取当前激活 Build Profile；旧 Unity 没有该属性时返回 null。</summary>
        private static UnityEngine.Object ActiveBuildProfile(Type profileType)
        {
            var prop = typeof(EditorUserBuildSettings).GetProperty("activeBuildProfile", BindingFlags.Public | BindingFlags.Static);
            return prop != null && profileType.IsAssignableFrom(prop.PropertyType) ? prop.GetValue(null, null) as UnityEngine.Object : null;
        }

        /// <summary>通过反射设置当前 Build Profile；API 不存在时返回稳定错误。</summary>
        private static void SetActiveBuildProfile(Type profileType, UnityEngine.Object profile)
        {
            var prop = typeof(EditorUserBuildSettings).GetProperty("activeBuildProfile", BindingFlags.Public | BindingFlags.Static);
            if (prop == null || !prop.CanWrite || !profileType.IsAssignableFrom(prop.PropertyType))
            {
                throw new BridgeCommandException("E_BUILD_PROFILE_SWITCH_UNSUPPORTED", "This Unity version does not expose EditorUserBuildSettings.activeBuildProfile.", "Use build.platform/build.settings or upgrade Unity.");
            }
            prop.SetValue(null, profile, null);
        }

        private static string BuildSettings_Removed(BridgeRequest request)
        {
            var action = request.StringArg("action") ?? "get";
            if (action == "get")
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
            if (action == "set")
            {
                if (request.DryRun)
                {
                    return SuccessJson(new UnityCliJsonWriter().BeginObject()
                        .Prop("command", "build.settings").Prop("dryRun", true).Prop("action", "set")
                        .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "build.settings.set").Prop("sideEffects", "player-settings-write").EndObject())
                        .Prop("logPath", LogPath()).EndObject().ToString());
                }
                var changes = new List<string>();
                var productName = request.StringArg("productName");
                if (!string.IsNullOrEmpty(productName)) { PlayerSettings.productName = productName; changes.Add("productName"); }
                var bundleVersion = request.StringArg("bundleVersion");
                if (!string.IsNullOrEmpty(bundleVersion)) { PlayerSettings.bundleVersion = bundleVersion; changes.Add("bundleVersion"); }
                var companyId = request.StringArg("companyId");
                if (!string.IsNullOrEmpty(companyId)) { PlayerSettings.companyName = companyId; changes.Add("companyId"); }
                var applicationIdentifier = request.StringArg("applicationIdentifier");
                if (!string.IsNullOrEmpty(applicationIdentifier)) { PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Unknown, applicationIdentifier); changes.Add("applicationIdentifier"); }
                var scriptingBackend = request.StringArg("scriptingBackend");
                if (!string.IsNullOrEmpty(scriptingBackend) && Enum.TryParse(scriptingBackend, true, out UnityEditor.ScriptingImplementation sb)) { PlayerSettings.SetScriptingBackend(BuildTargetGroup.Unknown, sb); changes.Add("scriptingBackend"); }
                var apiCompatibilityLevel = request.StringArg("apiCompatibilityLevel");
                if (!string.IsNullOrEmpty(apiCompatibilityLevel) && Enum.TryParse(apiCompatibilityLevel, true, out ApiCompatibilityLevel api)) { PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Unknown, api); changes.Add("apiCompatibilityLevel"); }
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("command", "build.settings").Prop("action", "set");
                w.PropArray("changed", arr => { foreach (var c in changes) arr.Value(c); });
                w.Prop("productName", PlayerSettings.productName);
                w.Prop("bundleVersion", PlayerSettings.bundleVersion);
                w.Prop("companyId", PlayerSettings.companyName);
                w.Prop("applicationIdentifier", PlayerSettings.applicationIdentifier);
                w.Prop("scriptingBackend", PlayerSettings.GetScriptingBackend(BuildTargetGroup.Unknown).ToString());
                w.Prop("apiCompatibilityLevel", PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Unknown).ToString());
                w.PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "build.settings.set").Prop("changed", string.Join(",", changes.ToArray())).EndObject());
                w.Prop("logPath", LogPath());
                return SuccessJson(w.EndObject().ToString());
            }
            return ErrorJson("E_BUILD_SETTINGS_ACTION", "Unknown action: " + action, "Use get or set.");
        }
    }
}
#endif
