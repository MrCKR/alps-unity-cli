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
        private static void RegisterAssetCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "asset.search", "asset", false, AssetSearch);
            RegisterCommand(registry, "asset.info", "asset", false, AssetInfo);
            RegisterCommand(registry, "asset.create", "asset", false, AssetCreate);
            RegisterCommand(registry, "asset.import", "asset", false, AssetImport);
            RegisterCommand(registry, "asset.modify", "asset", false, AssetModify);
            RegisterCommand(registry, "asset.delete", "asset", false, AssetDelete);
            RegisterCommand(registry, "asset.duplicate", "asset", false, AssetDuplicate);
            RegisterCommand(registry, "asset.move", "asset", false, AssetMove);
            RegisterCommand(registry, "asset.rename", "asset", false, AssetRename);
            RegisterCommand(registry, "asset.search-built-in", "asset", false, AssetSearchBuiltIn);
            RegisterCommand(registry, "asset.get-data", "asset", false, AssetGetData);
            RegisterCommand(registry, "asset.get-components", "asset", false, AssetGetComponents);
            RegisterCommand(registry, "asset.preview", "asset", false, AssetPreview);
            RegisterCommand(registry, "asset.refresh", "asset", false, AssetRefresh);
        }

        private static bool AssetExists(string assetPath)
        {
            return File.Exists(AssetFullPath(assetPath)) || !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath));
        }

        private static string AssetFullPath(string assetPath)
        {
            return Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void WriteTextAsset(string path, string content, bool overwrite)
        {
            CreateAssetDirectory(path);
            var fullPath = AssetFullPath(path);
            if (File.Exists(fullPath) && !overwrite)
            {
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Output already exists: " + path, "Pass --overwrite or choose a different asset path.");
            }
            try
            {
                File.WriteAllText(fullPath, content, new UTF8Encoding(false));
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_IO_ERROR", "Failed to write asset file: " + exception.Message, "Check destination path and permissions: " + path);
            }
        }

        /// <summary>搜索 AssetDatabase：支持 Unity 原生 filter、类型追加、多目录范围与分页。</summary>
        private static string AssetSearch(BridgeRequest request)
        {
            var filter = request.StringArg("filter");
            var query = request.StringArg("query");
            var type = request.StringArg("type");
            var rawPath = request.StringArg("path");
            var searchInFolders = NormalizeAssetSearchFolders(rawPath, request.RawJsonArg("searchInFolders"));
            var filterParts = new List<string>();
            if (!string.IsNullOrEmpty(filter))
            {
                filterParts.Add(filter);
            }
            else if (!string.IsNullOrEmpty(query))
            {
                filterParts.Add(query);
            }
            if (!string.IsNullOrEmpty(type))
            {
                filterParts.Add("t:" + type);
            }
            var resolvedFilter = string.Join(" ", filterParts.ToArray()).Trim();
            var guids = AssetDatabase.FindAssets(resolvedFilter, searchInFolders.Length == 0 ? null : searchInFolders);
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", request.IntArg("maxResults", 100)), 500));
            var items = new List<string>();
            for (var index = cursor; index < guids.Length && items.Count < pageSize; index++)
            {
                var assetPath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guids[index]));
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                items.Add(AssetSummaryJson(assetPath, assetType));
            }
            var nextCursor = cursor + items.Count < guids.Length ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("filter", resolvedFilter)
                .Prop("query", query)
                .Prop("type", type)
                .Prop("path", searchInFolders.Length == 1 ? searchInFolders[0] : null)
                .PropArray("searchInFolders", arr => { foreach (var folder in searchInFolders) arr.Value(folder); })
                .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .PropObject("pageInfo", pageInfo => pageInfo
                    .Prop("pageSize", (long)pageSize)
                    .Prop("cursor", (long)cursor)
                    .Prop("nextCursor", nextCursor)
                    .Prop("total", (long)guids.Length)
                    .Prop("truncated", nextCursor != null))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        /// <summary>查找内置资源：对齐 Ivan assets-find-built-in。</summary>
        private static string AssetSearchBuiltIn(BridgeRequest request)
        {
            var keyword = request.StringArg("keyword") ?? request.StringArg("search");
            var typeFilter = request.StringArg("type");
            var guids = AssetDatabase.FindAssets(keyword ?? string.Empty);
            var results = new List<string>();
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 100), 500));
            foreach (var guid in guids)
            {
                if (results.Count >= pageSize) break;
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                // 仅保留 Resources/ 与内置包路径
                if (!assetPath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)
                    && !assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                    && !assetPath.Contains("/Resources/"))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(typeFilter))
                {
                    var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (mainAsset == null || !mainAsset.GetType().Name.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                results.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("path", NormalizeAssetPath(assetPath))
                    .Prop("guid", guid)
                    .Prop("name", System.IO.Path.GetFileNameWithoutExtension(assetPath))
                    .EndObject().ToString());
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "asset.search-built-in")
                .Prop("count", (long)results.Count)
                .PropArray("assets", arr => { foreach (var r in results) arr.RawValue(r); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>资产深读：底层复用 ObjectSerializer。对齐 Ivan assets-get-data。</summary>
        private static string AssetGetData(BridgeRequest request)
        {
            var path = RequireAssetPath(request.StringArg("assetPath") ?? request.StringArg("path"));
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' to discover the path.");
            }
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                throw new BridgeCommandException("E_ASSET_LOAD_FAILED", "Asset main object could not be loaded: " + path, "Run asset.info to confirm the asset type, or use a more specific tool for folders/importers.");
            }
            var options = BuildSerializerOptions(request);
            var memberJson = ObjectSerializer.Serialize(asset, options);
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "asset.get-data");
            w.Prop("assetPath", path);
            w.Prop("guid", AssetDatabase.AssetPathToGUID(path));
            w.Prop("typeName", asset.GetType().FullName);
            w.PropRaw("data", memberJson);
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        /// <summary>生成资产预览：输出 artifact metadata。对齐 CoplayDev generate_preview。</summary>
        private static string AssetPreview(BridgeRequest request)
        {
            var path = RequireAssetPath(request.StringArg("assetPath") ?? request.StringArg("path"));
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' to discover the path.");
            }
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var preview = UnityEditor.AssetPreview.GetAssetPreview(asset);
            var hasPreview = preview != null;
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "asset.preview");
            w.Prop("assetPath", path);
            w.Prop("guid", AssetDatabase.AssetPathToGUID(path));
            w.Prop("typeName", asset.GetType().FullName);
            w.Prop("hasPreview", hasPreview);
            if (hasPreview)
            {
                w.Prop("width", (long)preview.width);
                w.Prop("height", (long)preview.height);
                w.Prop("isTransient", UnityEditor.AssetPreview.IsLoadingAssetPreview(asset.GetInstanceID()));
            }
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        /// <summary>读取资产主对象、Importer 与 Prefab/GameObject 组件摘要，避免深读整个对象图。</summary>
        private static string AssetGetComponents(BridgeRequest request)
        {
            var path = RequireExistingAssetPath(request.StringArg("assetPath") ?? request.StringArg("path") ?? request.StringArg("target"));
            var includeInactive = request.BoolArg("includeInactive", true);
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 200), 1000));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var importer = AssetImporter.GetAtPath(path);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var rows = new List<string>();
            if (root != null)
            {
                foreach (var transform in root.GetComponentsInChildren<Transform>(includeInactive))
                {
                    var components = transform.GetComponents<Component>();
                    for (var index = 0; index < components.Length; index++)
                    {
                        rows.Add(AssetComponentJson(root, transform.gameObject, components[index], index));
                    }
                }
            }
            var page = rows.Skip(cursor).Take(pageSize).ToList();
            var nextCursor = cursor + page.Count < rows.Count ? (cursor + page.Count).ToString(CultureInfo.InvariantCulture) : null;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "asset.get-components")
                .Prop("assetPath", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("mainObjectType", asset == null ? null : asset.GetType().FullName)
                .PropObject("importer", imp => imp
                    .Prop("type", importer == null ? null : importer.GetType().FullName)
                    .Prop("assetBundleName", importer == null ? null : importer.assetBundleName)
                    .Prop("userData", importer == null ? null : importer.userData))
                .Prop("isGameObjectAsset", root != null)
                .Prop("includeInactive", includeInactive)
                .Prop("componentCount", (long)rows.Count)
                .PropArray("components", arr => { foreach (var item in page) arr.RawValue(item); })
                .PropObject("pageInfo", p => p.Prop("pageSize", (long)pageSize).Prop("cursor", (long)cursor).Prop("nextCursor", nextCursor).Prop("total", (long)rows.Count))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>Live Editor 资产刷新入口，避免 AI 只能通过 batch job 触发 refresh。</summary>
        private static string AssetRefresh(BridgeRequest request)
        {
            var path = request.StringArg("path") ?? request.StringArg("assetPath") ?? request.StringArg("target");
            var options = ImportAssetOptions.Default;
            if (request.BoolArg("forceUpdate", false)) options |= ImportAssetOptions.ForceUpdate;
            if (request.BoolArg("importRecursive", false)) options |= ImportAssetOptions.ImportRecursive;
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "asset.refresh").Prop("dryRun", true).Prop("path", path).Prop("options", options.ToString())
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", string.IsNullOrEmpty(path) ? "asset.refresh" : "asset.import").Prop("path", path).Prop("options", options.ToString()).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            if (string.IsNullOrEmpty(path))
            {
                AssetDatabase.Refresh(options);
            }
            else
            {
                AssetDatabase.ImportAsset(RequireAssetPath(path), options);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "asset.refresh").Prop("path", path).Prop("options", options.ToString()).Prop("refreshed", true).Prop("logPath", LogPath()).EndObject().ToString());
        }

        private static string AssetComponentJson(GameObject root, GameObject gameObject, Component component, int componentIndex)
        {
            var relativePath = PrefabRelativePath(root.transform, gameObject.transform);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("gameObject", gameObject.name)
                .Prop("path", relativePath)
                .Prop("active", gameObject.activeSelf)
                .Prop("componentIndex", (long)componentIndex)
                .Prop("type", component == null ? "MissingScript" : component.GetType().Name)
                .Prop("fullName", component == null ? null : component.GetType().FullName)
                .Prop("enabled", component != null && ComponentEnabled(component))
                .EndObject().ToString();
        }

        private static string PrefabRelativePath(Transform root, Transform transform)
        {
            var stack = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                if (current == root) break;
                current = current.parent;
            }
            return string.Join("/", stack.ToArray());
        }

        private static string AssetInfo(BridgeRequest request)
        {
            var path = RequireAssetPath(request.StringArg("path") ?? request.StringArg("target"));
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' or pass an existing Assets/* path.");
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("asset", AssetDetailJson(path))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        private static string AssetCreate(BridgeRequest request)
        {
            var path = RequireAssetPath(request.StringArg("path"));
            var type = request.StringArg("type") ?? "file";
            var overwrite = request.BoolArg("overwrite", false);
            if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                CreateAssetFolder(path);
                return SuccessJson(AssetMutationJson("asset.create", path, null));
            }
            WriteTextAsset(path, request.StringArg("content") ?? string.Empty, overwrite);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return SuccessJson(AssetMutationJson("asset.create", path, null));
        }

        private static string AssetImport(BridgeRequest request)
        {
            var path = RequireAssetPath(request.StringArg("path") ?? request.StringArg("destination"));
            var source = request.StringArg("source");
            var overwrite = request.BoolArg("overwrite", false);
            if (!string.IsNullOrEmpty(source))
            {
                var fullSource = source.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? AssetFullPath(NormalizeAssetPath(source)) : Path.GetFullPath(source.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullSource))
                {
                    throw new BridgeCommandException("E_ASSET_SOURCE_NOT_FOUND", "Source file not found: " + source, "Pass an existing file path or Assets/* source asset.");
                }
                CreateAssetDirectory(path);
                var fullDestination = AssetFullPath(path);
                if (File.Exists(fullDestination) && !overwrite)
                {
                    throw new BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Asset already exists: " + path, "Pass --overwrite or choose another destination.");
                }
                File.Copy(fullSource, fullDestination, true);
            }
            else
            {
                WriteTextAsset(path, request.StringArg("content") ?? string.Empty, overwrite);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return SuccessJson(AssetMutationJson("asset.import", path, source));
        }

        private static string AssetModify(BridgeRequest request)
        {
            var path = RequireExistingAssetPath(request.StringArg("path"));
            var content = request.StringArg("content");
            if (content == null)
            {
                throw new BridgeCommandException("E_ASSET_CONTENT_REQUIRED", "Asset modify requires --content or --content-file.", "Pass text content for a text-like asset file.");
            }
            WriteTextAsset(path, content, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return SuccessJson(AssetMutationJson("asset.modify", path, null));
        }

        private static string AssetDelete(BridgeRequest request)
        {
            var path = RequireExistingAssetPath(request.StringArg("path"));
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "asset.delete")
                    .Prop("dryRun", true)
                    .PropRaw("asset", AssetDetailJson(path))
                    .PropArray("plannedEffects", arr => arr
                        .RawValue(new UnityCliJsonWriter().BeginObject()
                            .Prop("type", "asset.delete")
                            .Prop("path", path)
                            .Prop("sideEffects", "none")
                            .EndObject().ToString()))
                    .Prop("logPath", LogPath())
                    .EndObject()
                    .ToString());
            }
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new BridgeCommandException("E_ASSET_DELETE_FAILED", "Failed to delete asset: " + path, "Check Unity Console and file permissions.");
            }
            AssetDatabase.Refresh();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropObject("asset", a => a
                    .Prop("path", path)
                    .Prop("exists", false))
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "asset.delete")
                        .Prop("path", path)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        private static string AssetDuplicate(BridgeRequest request)
        {
            var source = RequireExistingAssetPath(request.StringArg("source") ?? request.StringArg("path"));
            var destination = RequireAssetPath(request.StringArg("destination"));
            CreateAssetDirectory(destination);
            if (AssetExists(destination))
            {
                throw new BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Destination asset already exists: " + destination, "Choose another destination path.");
            }
            if (!AssetDatabase.CopyAsset(source, destination))
            {
                throw new BridgeCommandException("E_ASSET_DUPLICATE_FAILED", "Failed to duplicate asset: " + source, "Check destination path and Unity Console diagnostics.");
            }
            AssetDatabase.Refresh();
            return SuccessJson(AssetMutationJson("asset.duplicate", destination, source));
        }

        private static string AssetMove(BridgeRequest request)
        {
            var source = RequireExistingAssetPath(request.StringArg("source") ?? request.StringArg("path"));
            var destination = RequireAssetPath(request.StringArg("destination"));
            CreateAssetDirectory(destination);
            var error = AssetDatabase.MoveAsset(source, destination);
            if (!string.IsNullOrEmpty(error))
            {
                throw new BridgeCommandException("E_ASSET_MOVE_FAILED", "Failed to move asset: " + error, "Check source, destination and Unity Console diagnostics.");
            }
            AssetDatabase.Refresh();
            return SuccessJson(AssetMutationJson("asset.move", destination, source));
        }

        private static string AssetRename(BridgeRequest request)
        {
            var path = RequireExistingAssetPath(request.StringArg("path"));
            var name = request.StringArg("name");
            if (string.IsNullOrEmpty(name))
            {
                throw new BridgeCommandException("E_ASSET_NAME_REQUIRED", "Asset rename requires --name <newName>.", "Pass a filename without path separators.");
            }
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            {
                throw new BridgeCommandException("E_ASSET_NAME_INVALID", "Asset rename name must not include path separators: " + name, "Use asset move for changing directories.");
            }
            var guid = AssetDatabase.AssetPathToGUID(path);
            var error = AssetDatabase.RenameAsset(path, name);
            if (!string.IsNullOrEmpty(error))
            {
                throw new BridgeCommandException("E_ASSET_RENAME_FAILED", "Failed to rename asset: " + error, "Check name validity and Unity Console diagnostics.");
            }
            AssetDatabase.Refresh();
            var renamedPath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guid));
            if (string.IsNullOrEmpty(renamedPath))
            {
                renamedPath = NormalizeAssetPath(Path.Combine(Path.GetDirectoryName(path) ?? "Assets", name));
            }
            return SuccessJson(AssetMutationJson("asset.rename", renamedPath, path));
        }

        /// <summary>规范化搜索目录，显式拒绝会导致全项目误搜的非法范围。</summary>
        private static string[] NormalizeAssetSearchFolders(string path, string searchInFoldersJson)
        {
            var folders = new List<string>();
            if (!string.IsNullOrEmpty(searchInFoldersJson))
            {
                foreach (var folder in JsonStringArray(searchInFoldersJson))
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

        /// <summary>校验单个 AssetDatabase 搜索范围，仅允许 Assets/ 或 Packages/。</summary>
        private static void AddAssetSearchFolder(List<string> folders, string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return;
            var normalized = NormalizeAssetPath(rawPath);
            if (!normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith("Packages", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_ASSET_SEARCH_SCOPE_INVALID", "Asset search scope must be under Assets/ or Packages/: " + rawPath, "Pass filter/query for search text, and use path/searchInFolders only for folder scopes.");
            }
            folders.Add(normalized);
        }

        private static string RequireAssetPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_ASSET_PATH_REQUIRED", "Pass an Assets/* asset path.", "Use tools run asset.info --input '{\"path\":\"Assets/Example.asset\"}' or pass path in the tool input.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_ASSET_PATH_INVALID", "Asset path must be under Assets/: " + path, "Pass a Unity project asset path such as Assets/Textures/Icon.png.");
            }
            return path;
        }

        private static string RequireExistingAssetPath(string rawPath)
        {
            var path = RequireAssetPath(rawPath);
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run tools run asset.search --input '{\"query\":\"<keyword>\"}' or pass an existing Assets/* path.");
            }
            return path;
        }

        private static void CreateAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }
            if (AssetExists(path))
            {
                throw new BridgeCommandException("E_ASSET_OUTPUT_EXISTS", "Asset already exists and is not a folder: " + path, "Choose another Assets/* folder path.");
            }
            var segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = segments.Length > 0 && string.Equals(segments[0], "Assets", StringComparison.OrdinalIgnoreCase) ? "Assets" : null;
            if (current == null)
            {
                throw new BridgeCommandException("E_ASSET_PATH_INVALID", "Asset folder must be under Assets/: " + path, "Pass a path such as Assets/Generated.");
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

        private static string AssetMutationJson(string type, string path, string source)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("asset", AssetDetailJson(path))
                .Prop("source", source)
                .PropArray("plannedEffects", arr =>
                {
                    var effect = new UnityCliJsonWriter().BeginObject()
                        .Prop("type", type)
                        .Prop("path", path);
                    if (!string.IsNullOrEmpty(source)) effect.Prop("source", source);
                    arr.RawValue(effect.EndObject().ToString());
                })
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private static string AssetDetailJson(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            var fullPath = AssetFullPath(path);
            var fileInfo = new FileInfo(fullPath);
            var isFolder = AssetDatabase.IsValidFolder(path) || Directory.Exists(fullPath);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", asset == null ? Path.GetFileNameWithoutExtension(path) : asset.name)
                .Prop("type", type == null ? (isFolder ? "Folder" : null) : type.Name)
                .Prop("extension", Path.GetExtension(path))
                .Prop("isFolder", isFolder)
                .Prop("exists", AssetExists(path))
                .Prop("sizeBytes", fileInfo.Exists ? (long)fileInfo.Length : 0L)
                .EndObject()
                .ToString();
        }

        private static string AssetSummaryJson(string path, Type assetType)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("type", assetType == null ? null : assetType.Name)
                .Prop("name", Path.GetFileNameWithoutExtension(path))
                .EndObject()
                .ToString();
        }

        private static string TextureAssetJson(string path, Texture2D texture)
        {
            var fileInfo = new FileInfo(AssetFullPath(path));
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", texture == null ? Path.GetFileNameWithoutExtension(path) : texture.name)
                .Prop("width", texture == null ? 0L : (long)texture.width)
                .Prop("height", texture == null ? 0L : (long)texture.height)
                .Prop("format", texture == null ? null : texture.format.ToString())
                .Prop("sizeBytes", fileInfo.Exists ? (long)fileInfo.Length : 0L)
                .EndObject()
                .ToString();
        }

        private static string TextureImporterJson(TextureImporter importer)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("textureType", importer.textureType.ToString())
                .Prop("spriteImportMode", importer.spriteImportMode.ToString())
                .Prop("pixelsPerUnit", (long)importer.spritePixelsPerUnit)
                .PropObject("pivot", pivot => pivot
                    .Prop("x", importer.spritePivot.x)
                    .Prop("y", importer.spritePivot.y))
                .Prop("readable", importer.isReadable)
                .Prop("mipmap", importer.mipmapEnabled)
                .Prop("sRGB", importer.sRGBTexture)
                .Prop("alphaIsTransparency", importer.alphaIsTransparency)
                .Prop("wrapMode", importer.wrapMode.ToString())
                .Prop("filterMode", importer.filterMode.ToString())
                .Prop("maxTextureSize", (long)importer.maxTextureSize)
                .Prop("compression", importer.textureCompression.ToString())
                .EndObject()
                .ToString();
        }
    }
}
#endif
