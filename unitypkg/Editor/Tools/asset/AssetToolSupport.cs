#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// asset 域 IUnityCliTool 共享的基础设施 helper:资产路径规范化、资产存在判断、
    /// 资产目录创建、文本资产写入、资产信息构造、忙状态拦截。仅供 asset 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class AssetToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求,避免脏状态写入。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        // 资产路径规范化:反斜杠转正斜杠并去首尾空白;空返回 null。
        public static string NormalizeAssetPath(string rawPath)
        {
            return string.IsNullOrEmpty(rawPath) ? null : rawPath.Replace('\\', '/').Trim();
        }

        /// <summary>Unity 工程根目录(Assets 的父目录)。</summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        }

        /// <summary>Assets/ 相对路径转工程内绝对路径。</summary>
        public static string AssetFullPath(string assetPath)
        {
            return Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>判断 Assets/ 路径是否存在；历史 GUID 不能代表文件或已加载资产仍存在。</summary>
        public static bool AssetExists(string assetPath)
        {
            var fullPath = AssetFullPath(assetPath);
            return File.Exists(fullPath) || Directory.Exists(fullPath) || AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
        }

        /// <summary>确保资产目录存在,缺失则创建并刷新 AssetDatabase。</summary>
        public static void CreateAssetDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory) || AssetDatabase.IsValidFolder(directory.Replace('\\', '/')))
            {
                return;
            }
            Directory.CreateDirectory(Path.Combine(ProjectRoot(), directory.Replace('/', Path.DirectorySeparatorChar)));
            AssetDatabase.Refresh();
        }

        // 校验并返回 Assets/ 资产路径;空或非 Assets/ 前缀抛 BridgeCommandException。
        public static string RequireAssetPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_PATH_REQUIRED", "Pass an Assets/* asset path.", "Use tools run asset.info --input '{\"path\":\"Assets/Example.asset\"}' or pass path in the tool input.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_PATH_INVALID", "Asset path must be under Assets/: " + path, "Pass a Unity project asset path such as Assets/Textures/Icon.png.");
            }
            return path;
        }

        // 校验 Assets/ 资产路径并要求资产已存在。
        public static string RequireExistingAssetPath(string rawPath)
        {
            var path = RequireAssetPath(rawPath);
            if (!AssetExists(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' or pass an existing Assets/* path.");
            }
            return path;
        }

        /// <summary>创建 Assets/ 文件夹(逐级创建);已存在则跳过,冲突则报错。</summary>
        public static void CreateAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }
            if (AssetExists(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Asset already exists and is not a folder: " + path, "Choose another Assets/* folder path.");
            }
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = segments.Length > 0 && string.Equals(segments[0], "Assets", StringComparison.OrdinalIgnoreCase) ? "Assets" : null;
            if (current == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_PATH_INVALID", "Asset folder must be under Assets/: " + path, "Pass a path such as Assets/Generated.");
            }
            for (var index = 1; index < segments.Length; index++)
            {
                var next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[index]);
                }
                current = next;
            }
            AssetDatabase.Refresh();
        }

        /// <summary>写入文本资产文件;已存在且未授权覆盖则报错。写入后刷新 AssetDatabase。</summary>
        public static void WriteTextAsset(string path, string content, bool overwrite)
        {
            CreateAssetDirectory(path);
            var fullPath = AssetFullPath(path);
            if (File.Exists(fullPath) && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Output already exists: " + path, "Pass --overwrite or choose a different asset path.");
            }
            try
            {
                File.WriteAllText(fullPath, content, new UTF8Encoding(false));
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_IO_ERROR", "Failed to write asset file: " + exception.Message, "Check destination path and permissions: " + path);
            }
        }

        /// <summary>规范化搜索目录数组;仅允许 Assets/ 或 Packages/ 前缀。</summary>
        public static string[] NormalizeAssetSearchFolders(string path, List<string> searchInFolders)
        {
            var folders = new List<string>();
            if (searchInFolders != null)
            {
                foreach (var folder in searchInFolders)
                {
                    AddAssetSearchFolder(folders, folder);
                }
            }
            if (!string.IsNullOrEmpty(path))
            {
                AddAssetSearchFolder(folders, path);
            }
            return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // 校验单个 AssetDatabase 搜索范围,仅允许 Assets/ 或 Packages/。
        private static void AddAssetSearchFolder(List<string> folders, string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return;
            var normalized = NormalizeAssetPath(rawPath);
            if (!normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith("Packages", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_SEARCH_SCOPE_INVALID", "Asset search scope must be under Assets/ or Packages/: " + rawPath, "Pass filter/query for search text, and use path/searchInFolders only for folder scopes.");
            }
            folders.Add(normalized);
        }

        /// <summary>构造资产详情 DTO(path/guid/name/type/extension/isFolder/exists/sizeBytes)。</summary>
        public static AssetDetail BuildAssetDetail(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            var fullPath = AssetFullPath(path);
            var fileInfo = new FileInfo(fullPath);
            var isFolder = AssetDatabase.IsValidFolder(path) || Directory.Exists(fullPath);
            return new AssetDetail
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Name = asset == null ? Path.GetFileNameWithoutExtension(path) : asset.name,
                Type = type == null ? (isFolder ? "Folder" : null) : type.Name,
                Extension = Path.GetExtension(path),
                IsFolder = isFolder,
                Exists = AssetExists(path),
                SizeBytes = fileInfo.Exists ? (long)fileInfo.Length : 0L
            };
        }

        /// <summary>构造资产摘要 DTO(path/guid/type/name)。</summary>
        public static AssetSummary BuildAssetSummary(string path, Type assetType)
        {
            return new AssetSummary
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Type = assetType == null ? null : assetType.Name,
                Name = Path.GetFileNameWithoutExtension(path)
            };
        }
    }

    /// <summary>资产详情,asset 域工具 Result 共享。</summary>
    public sealed class AssetDetail
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Extension { get; set; }
        public bool IsFolder { get; set; }
        public bool Exists { get; set; }
        public long SizeBytes { get; set; }
    }

    /// <summary>资产搜索结果摘要项,asset.search 共享。</summary>
    public sealed class AssetSummary
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
    }

    /// <summary>分页信息,asset 域分页工具共享。</summary>
    public sealed class AssetPageInfo
    {
        public long PageSize { get; set; }
        public long Cursor { get; set; }
        public long? NextCursor { get; set; }
        public long Total { get; set; }
        public bool Truncated { get; set; }
    }

    /// <summary>计划副作用项,asset 域 mutation 工具 Result 共享。</summary>
    public sealed class AssetPlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string Source { get; set; }
        public string SideEffects { get; set; }
    }
}
#endif
