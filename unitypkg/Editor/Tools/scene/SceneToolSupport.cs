#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene 域 IUnityCliTool 共享的基础设施 helper:路径规范化、场景信息构造、
    /// 资产目录创建、忙状态拦截、场景路径解析。仅供 scene 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class SceneToolSupport
    {
        /// <summary>Wrapper for ObjectRefResolver.FindGameObjectByPath,供 scene 工具使用(ObjectRefResolver 是 internal nested)。</summary>
        public static GameObject ResolveGameObjectByPath(string path)
        {
            return UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(path);
        }

        // 场景资产路径规范化:反斜杠转正斜杠并去首尾空白;空返回 null。
        internal static string NormalizeAssetPath(string rawPath)
        {
            return string.IsNullOrEmpty(rawPath) ? null : rawPath.Replace('\\', '/').Trim();
        }

        // Unity 工程根目录(Assets 的父目录)。
        internal static string ProjectRoot()
        {
            return Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        }

        // Assets/ 相对路径转工程内绝对路径。
        internal static string AssetFullPath(string assetPath)
        {
            return Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        // 判断 Assets/ 路径是否存在(文件或已导入资产)。
        internal static bool AssetExists(string assetPath)
        {
            return File.Exists(AssetFullPath(assetPath)) || !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath));
        }

        // 确保资产目录存在,缺失则创建并刷新 AssetDatabase。
        internal static void CreateAssetDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory) || AssetDatabase.IsValidFolder(directory.Replace('\\', '/')))
            {
                return;
            }
            Directory.CreateDirectory(Path.Combine(ProjectRoot(), directory.Replace('/', Path.DirectorySeparatorChar)));
            AssetDatabase.Refresh();
        }

        // 拦截编译/导入/构建/PlayMode 过渡期的写入请求,避免脏状态写入。
        // 不含 domainReloading 字段检查(该字段为 UnityCliBridge 私有,自包含工具不可见);
        // isCompiling/isUpdating 已覆盖绝大多数不可写窗口。
        internal static void EnsureEditorNotBusy(string command)
        {
            if (EditorApplication.isCompiling)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", transient: true, retryAfterMs: 5000);
            }
            if (EditorApplication.isUpdating)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", transient: true, retryAfterMs: 5000);
            }
            if (UnityCliBridge.IsPlayModeChanging())
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", transient: true, retryAfterMs: 3000);
            }
            if (BuildPipeline.isBuildingPlayer)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
            }
        }

        /// <summary>优先校验显式 path；仅给出 name 时按场景资产文件名精确解析，拒绝 0 个或多个匹配。</summary>
        internal static string ResolveExistingScenePath(string path, string name)
        {
            if (!string.IsNullOrWhiteSpace(path)) return ResolveScenePath(path, true);

            var normalizedName = NormalizeAssetPath(name);
            if (string.IsNullOrEmpty(normalizedName)) return ResolveScenePath(null, true);
            if (normalizedName.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return ResolveScenePath(normalizedName, true);

            var sceneName = Path.GetFileNameWithoutExtension(normalizedName);
            var matches = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Scene"))
            {
                var candidate = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guid));
                if (!string.IsNullOrEmpty(candidate)
                    && candidate.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileNameWithoutExtension(candidate), sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(candidate);
                }
            }
            matches.Sort(StringComparer.OrdinalIgnoreCase);
            if (matches.Count == 0)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_NOT_FOUND", "Scene asset name not found: " + normalizedName, "Run tools run asset.search --input '{\"filter\":\"t:Scene\"}' or pass an existing Assets/*.unity path.");
            }
            if (matches.Count > 1)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_AMBIGUOUS", "Multiple scene assets matched name " + normalizedName + ": " + string.Join(", ", matches.ToArray()), "Pass the exact Assets/*.unity path.");
            }
            return matches[0];
        }

        // 解析并校验 Assets/*.unity 场景路径;requireExisting=true 时还要求资产已存在。
        internal static string ResolveScenePath(string pathOrName, bool requireExisting)
        {
            var path = NormalizeAssetPath(pathOrName);
            if (string.IsNullOrEmpty(path))
            {
                if (requireExisting)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_SCENE_PATH_REQUIRED", "Pass an Assets/*.unity scene path.", "Use tools run scene.load --input '{\"path\":\"Assets/Scenes/Main.unity\"}'.");
                }
                return null;
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_PATH_INVALID", "Scene path must be under Assets/ and end with .unity: " + path, "Pass a Unity scene asset path such as Assets/Scenes/Main.unity.");
            }
            if (requireExisting && !AssetExists(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_NOT_FOUND", "Scene asset not found: " + path, "Run tools run asset.search --input '{\"filter\":\"t:Scene\"}' or pass an existing Assets/*.unity path.");
            }
            return path;
        }

        // 构造轻量场景信息(name/path/buildIndex/isLoaded/isDirty)。
        internal static SceneInfo BuildSceneInfo(Scene scene)
        {
            return new SceneInfo
            {
                Name = scene.name,
                Path = NormalizeAssetPath(scene.path),
                BuildIndex = scene.buildIndex,
                IsLoaded = scene.isLoaded,
                IsDirty = scene.isDirty
            };
        }
    }

    /// <summary>场景摘要信息,scene 域工具 Result 共享。</summary>
    public sealed class SceneInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public long BuildIndex { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsDirty { get; set; }
    }

    /// <summary>计划副作用项,scene 域 mutation 工具 dryRun/成功结果共享。</summary>
    public sealed class ScenePlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string Target { get; set; }
        public string SideEffects { get; set; }
    }
}
#endif
