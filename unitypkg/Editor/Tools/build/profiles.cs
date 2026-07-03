#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// build.profiles — 通过反射读取/创建/切换/修改 Unity Build Profile,避免版本编译期依赖。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class BuildProfilesTool : IUnityCliTool<BuildProfilesTool.Request, BuildProfilesTool.Result>
    {
        public sealed class Request
        {
            public string Action { get; set; } = "list";
            public string Path { get; set; }
            public string Name { get; set; }
            public bool DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool Available { get; set; }
            public string Action { get; set; }
            public string ActivePath { get; set; }
            public List<BuildProfileEntry> Profiles { get; set; }
            public string Path { get; set; }
            public string Name { get; set; }
            public bool DryRun { get; set; }
        }

        public sealed class BuildProfileEntry
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public bool Active { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "build.profiles",
            Summary = "List, create, switch or modify Unity Build Profiles via reflection (version-compatible).",
            Description = "Manage Unity Build Profiles via reflection to avoid version-specific compile-time dependencies. action=list/get returns available profiles with active state. action=create/switch/modify mutates profile assets. Supports dry-run. Falls back to available=false when the Build Profile API is not present in this Editor version.",
            Group = "build",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List build profiles", Input = new Request { Action = "list" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var action = string.IsNullOrEmpty(request.Action) ? "list" : request.Action;
            var profileType = BuildToolSupport.FindUnityType("UnityEditor.Build.Profile.BuildProfile") ?? BuildToolSupport.FindUnityType("UnityEditor.BuildProfile");
            if (profileType == null)
            {
                if (action == "list" || action == "get")
                {
                    return new Result { Command = "build.profiles", Available = false, Action = action, Profiles = new List<BuildProfileEntry>() };
                }
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_API_UNAVAILABLE", "Unity Build Profile API is not available in this Editor version.", "Use build.settings/build.scenes/build.platform, or upgrade Unity.");
            }

            if (action == "list" || action == "get")
            {
                var active = BuildToolSupport.ActiveBuildProfile(profileType);
                var guids = AssetDatabase.FindAssets("t:" + profileType.Name);
                var profiles = new List<BuildProfileEntry>();
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var profile = AssetDatabase.LoadAssetAtPath(path, profileType);
                    profiles.Add(new BuildProfileEntry
                    {
                        Path = path,
                        Name = profile != null ? profile.name : System.IO.Path.GetFileNameWithoutExtension(path),
                        Active = active != null && profile == active
                    });
                }
                return new Result
                {
                    Command = "build.profiles",
                    Available = true,
                    Action = action,
                    ActivePath = active != null ? AssetDatabase.GetAssetPath(active) : null,
                    Profiles = profiles
                };
            }

            if (action == "create")
            {
                var path = request.Path;
                if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_PATH_REQUIRED", "build.profiles create requires path.", "Pass an Assets/*.asset path for the new Build Profile.");
                if (request.DryRun)
                {
                    return new Result { Command = "build.profiles", Action = "create", Path = path, DryRun = true };
                }
                var profile = ScriptableObject.CreateInstance(profileType);
                if (!string.IsNullOrEmpty(request.Name)) profile.name = request.Name;
                AssetDatabase.CreateAsset(profile, path);
                AssetDatabase.SaveAssets();
                return new Result { Command = "build.profiles", Action = "create", Path = path, Name = profile.name };
            }

            if (action == "switch")
            {
                var path = request.Path;
                if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_PATH_REQUIRED", "build.profiles switch requires path.", "Pass an existing Build Profile asset path.");
                var profile = AssetDatabase.LoadAssetAtPath(path, profileType);
                if (profile == null) throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_NOT_FOUND", "Build Profile was not found: " + path, "Run build.profiles action=list and pass an existing path.");
                if (request.DryRun)
                {
                    return new Result { Command = "build.profiles", Action = "switch", Path = path, DryRun = true };
                }
                BuildToolSupport.SetActiveBuildProfile(profileType, profile);
                return new Result { Command = "build.profiles", Action = "switch", Path = path, ActivePath = path };
            }

            if (action == "modify")
            {
                var path = request.Path;
                if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_PATH_REQUIRED", "build.profiles modify requires path.", "Pass an existing Build Profile asset path.");
                var profile = AssetDatabase.LoadAssetAtPath(path, profileType);
                if (profile == null) throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_NOT_FOUND", "Build Profile was not found: " + path, "Run build.profiles action=list and pass an existing path.");
                if (request.DryRun)
                {
                    return new Result { Command = "build.profiles", Action = "modify", Path = path, Name = request.Name, DryRun = true };
                }
                if (!string.IsNullOrEmpty(request.Name)) { profile.name = request.Name; EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets(); }
                return new Result { Command = "build.profiles", Action = "modify", Path = path, Name = profile.name };
            }

            throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_ACTION", "Unknown action: " + action, "Use list, get, create, switch, or modify.");
        }
    }
}
#endif
