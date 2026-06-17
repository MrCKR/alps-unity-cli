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

        private static string AssetSearch(BridgeRequest request)
        {
            var query = request.StringArg("query");
            var type = request.StringArg("type");
            var path = NormalizeAssetSearchPath(request.StringArg("path"));
            var filterParts = new List<string>();
            if (!string.IsNullOrEmpty(query))
            {
                filterParts.Add(query);
            }
            if (!string.IsNullOrEmpty(type))
            {
                filterParts.Add("t:" + type);
            }
            var guids = AssetDatabase.FindAssets(string.Join(" ", filterParts.ToArray()), string.IsNullOrEmpty(path) ? null : new[] { path });
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 100), 500));
            var items = new List<string>();
            for (var index = cursor; index < guids.Length && items.Count < pageSize; index++)
            {
                var assetPath = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guids[index]));
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                items.Add(AssetSummaryJson(assetPath, assetType));
            }
            var nextCursor = cursor + items.Count < guids.Length ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("query", query)
                .Prop("type", type)
                .Prop("path", path)
                .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .PropObject("pageInfo", pageInfo => pageInfo
                    .Prop("pageSize", (long)pageSize)
                    .Prop("cursor", (long)cursor)
                    .Prop("nextCursor", nextCursor)
                    .Prop("total", (long)guids.Length))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        private static string AssetInfo(BridgeRequest request)
        {
            var path = RequireAssetPath(request.StringArg("path") ?? request.StringArg("target"));
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run unity_cli asset search <keyword> or pass an existing Assets/* path.");
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

        private static string NormalizeAssetSearchPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            var normalized = NormalizeAssetPath(path);
            return normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("Packages", StringComparison.OrdinalIgnoreCase) ? normalized : null;
        }

        private static string RequireAssetPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_ASSET_PATH_REQUIRED", "Pass an Assets/* asset path.", "Use unity_cli asset info <path> or pass --path Assets/Example.asset.");
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
                throw new BridgeCommandException("E_ASSET_NOT_FOUND", "Asset not found: " + path, "Run unity_cli asset search <keyword> or pass an existing Assets/* path.");
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
