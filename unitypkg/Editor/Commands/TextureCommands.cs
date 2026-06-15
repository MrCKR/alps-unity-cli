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
        private static void RegisterTextureCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "texture.info", "texture", false, TextureInfo);
            RegisterCommand(registry, "texture.create", "texture", true, TextureCreate);
            RegisterCommand(registry, "texture.sprite", "texture", true, TextureSprite);
            RegisterCommand(registry, "texture.import-settings", "texture", true, TextureImportSettings);
        }

        private static string TextureInfo(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            var texture = LoadTextureAsset(path);
            return SuccessJson("{\"texture\":" + TextureAssetJson(path, texture) + ",\"importSettings\":" + TextureImporterJson(RequireTextureImporter(path)) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string TextureCreate(BridgeRequest request)
        {
            var path = RequireTexturePath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (AssetExists(path) && !overwrite)
            {
                return ErrorJson("E_TEXTURE_OUTPUT_EXISTS", "Texture output already exists: " + path, "Pass --overwrite or choose a different texture asset path.");
            }

            CreateAssetDirectory(path);
            var source = request.StringArg("source");
            if (!string.IsNullOrEmpty(source))
            {
                CopyTextureSource(source, path, overwrite);
            }
            else
            {
                WriteGeneratedTexture(path, request);
            }

            try
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                ApplyTextureSettings(path, request, true);
                AssetDatabase.SaveAssets();
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ErrorJson("E_IMPORT_FAILED", "Failed to import texture: " + path + " (" + exception.Message + ")", "Check texture file contents, importer settings and Unity Console diagnostics.");
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                return ErrorJson("E_IMPORT_FAILED", "Texture import did not produce a Texture2D asset: " + path, "Check texture file contents and Unity Console diagnostics.");
            }
            WriteLog("texture_created path=" + path);
            return SuccessJson(TextureMutationJson("texture.create", path, texture));
        }

        private static string TextureSprite(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            LoadTextureAsset(path);
            var importer = RequireTextureImporter(path);
            importer.textureType = TextureImporterType.Sprite;
            var spriteMode = request.StringArg("spriteMode");
            if (!string.IsNullOrEmpty(spriteMode))
            {
                importer.spriteImportMode = string.Equals(spriteMode, "multiple", StringComparison.OrdinalIgnoreCase) ? SpriteImportMode.Multiple : SpriteImportMode.Single;
            }
            var pixelsPerUnit = request.IntArg("pixelsPerUnit", -1);
            if (pixelsPerUnit > 0)
            {
                importer.spritePixelsPerUnit = pixelsPerUnit;
            }
            importer.spritePivot = SpritePivot(request, importer.spritePivot);
            try
            {
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                return ErrorJson("E_IMPORT_FAILED", "Failed to reimport texture as Sprite: " + path + " (" + exception.Message + ")", "Check TextureImporter settings and Unity Console diagnostics.");
            }
            WriteLog("texture_sprite path=" + path);
            return SuccessJson(TextureMutationJson("texture.sprite", path, LoadTextureAsset(path)));
        }

        private static string TextureImportSettings(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            LoadTextureAsset(path);
            bool changed;
            try
            {
                changed = ApplyTextureSettings(path, request, true);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_IMPORT_FAILED", "Failed to apply texture import settings: " + path + " (" + exception.Message + ")", "Check TextureImporter settings and Unity Console diagnostics.");
            }
            var texture = LoadTextureAsset(path);
            return SuccessJson("{\"texture\":" + TextureAssetJson(path, texture) + ",\"importSettings\":" + TextureImporterJson(RequireTextureImporter(path)) + ",\"changed\":" + Bool(changed) + ",\"plannedEffects\":" + (changed ? "[{\"type\":\"texture.import-settings\",\"path\":" + Quote(path) + "}]" : "[]") + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string RequireTexturePath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_TEXTURE_PATH_REQUIRED", "Pass a texture path under Assets/.", "Use unity_cli texture info <path> or pass --path Assets/Textures/Example.png.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !IsTextureExtension(path))
            {
                throw new BridgeCommandException("E_TEXTURE_PATH_INVALID", "Texture path must be under Assets/ and use a supported texture extension: " + path, "Use .png, .jpg, .jpeg, .tga, .psd or .exr under Assets/.");
            }
            return path;
        }

        private static string ResolveTexturePath(string rawPath)
        {
            var path = RequireTexturePath(rawPath);
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found: " + path, "Check the path or run unity_cli asset search --type texture.");
            }
            return path;
        }

        private static Texture2D LoadTextureAsset(string path)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                throw new BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found or failed to import: " + path, "Check importer diagnostics in Unity Console and UnityCliBridge.log.");
            }
            return texture;
        }

        private static TextureImporter RequireTextureImporter(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new BridgeCommandException("E_TEXTURE_IMPORTER_NOT_FOUND", "TextureImporter not found for: " + path, "Reimport the asset or verify it is a texture file.");
            }
            return importer;
        }

        private static bool IsTextureExtension(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".tga" || extension == ".psd" || extension == ".exr";
        }

        private static void CopyTextureSource(string source, string destinationPath, bool overwrite)
        {
            var sourcePath = NormalizeAssetPath(source);
            var fullSource = sourcePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? AssetFullPath(sourcePath) : Path.GetFullPath(source.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            var fullDestination = AssetFullPath(destinationPath);
            if (!File.Exists(fullSource))
            {
                throw new BridgeCommandException("E_TEXTURE_SOURCE_NOT_FOUND", "Texture source file not found: " + source, "Pass --source with an existing file path or Assets/ texture path.");
            }
            if (File.Exists(fullDestination) && overwrite)
            {
                File.Delete(fullDestination);
            }
            File.Copy(fullSource, fullDestination, overwrite);
            AssetDatabase.Refresh();
        }

        private static void WriteGeneratedTexture(string path, BridgeRequest request)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            {
                throw new BridgeCommandException("E_TEXTURE_FORMAT_UNSUPPORTED", "Generated textures can only be written as .png or .jpg: " + path, "Pass --source for other texture formats.");
            }
            var width = Math.Max(1, request.IntArg("width", 1));
            var height = Math.Max(1, request.IntArg("height", 1));
            var color = ParseTextureColor(request.StringArg("color"));
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[width * height];
                for (var index = 0; index < pixels.Length; index++)
                {
                    pixels[index] = color;
                }
                texture.SetPixels(pixels);
                texture.Apply();
                var bytes = extension == ".png" ? texture.EncodeToPNG() : texture.EncodeToJPG(95);
                File.WriteAllBytes(AssetFullPath(path), bytes);
                AssetDatabase.Refresh();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static Color ParseTextureColor(string rawColor)
        {
            if (string.IsNullOrEmpty(rawColor))
            {
                return Color.white;
            }
            Color color;
            if (ColorUtility.TryParseHtmlString(rawColor.StartsWith("#", StringComparison.Ordinal) ? rawColor : "#" + rawColor, out color))
            {
                return color;
            }
            throw new BridgeCommandException("E_TEXTURE_COLOR_INVALID", "Texture color must be a hex RGB/RGBA value: " + rawColor, "Use values like --color ffffff or --color #ff00ffff.");
        }

        private static bool ApplyTextureSettings(string path, BridgeRequest request, bool saveAndReimport)
        {
            var importer = RequireTextureImporter(path);
            var changed = false;
            changed = ApplyTextureType(importer, request.StringArg("textureType")) || changed;
            changed = ApplyBool(request, "readable", importer.isReadable, value => importer.isReadable = value) || changed;
            changed = ApplyBool(request, "mipmap", importer.mipmapEnabled, value => importer.mipmapEnabled = value) || changed;
            changed = ApplyBool(request, "sRGB", importer.sRGBTexture, value => importer.sRGBTexture = value) || changed;
            changed = ApplyBool(request, "alphaIsTransparency", importer.alphaIsTransparency, value => importer.alphaIsTransparency = value) || changed;
            changed = ApplyEnum(request.StringArg("wrapMode"), importer.wrapMode.ToString(), value => importer.wrapMode = (TextureWrapMode)Enum.Parse(typeof(TextureWrapMode), value, true)) || changed;
            changed = ApplyEnum(request.StringArg("filterMode"), importer.filterMode.ToString(), value => importer.filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), value, true)) || changed;
            changed = ApplyEnum(request.StringArg("compression"), importer.textureCompression.ToString(), value => importer.textureCompression = (TextureImporterCompression)Enum.Parse(typeof(TextureImporterCompression), value, true)) || changed;
            var maxTextureSize = request.IntArg("maxTextureSize", -1);
            if (maxTextureSize > 0 && importer.maxTextureSize != maxTextureSize)
            {
                importer.maxTextureSize = maxTextureSize;
                changed = true;
            }
            if (saveAndReimport && changed)
            {
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
                WriteLog("texture_import_settings path=" + path);
            }
            return changed;
        }

        private static bool ApplyTextureType(TextureImporter importer, string rawType)
        {
            if (string.IsNullOrEmpty(rawType))
            {
                return false;
            }
            var normalized = string.Equals(rawType, "sprite", StringComparison.OrdinalIgnoreCase) ? "Sprite" : rawType;
            var next = (TextureImporterType)Enum.Parse(typeof(TextureImporterType), normalized, true);
            if (importer.textureType == next)
            {
                return false;
            }
            importer.textureType = next;
            return true;
        }

        private delegate void BoolSetter(bool value);
        private delegate void StringSetter(string value);

        private static bool ApplyBool(BridgeRequest request, string name, bool current, BoolSetter setter)
        {
            if (!request.HasArg(name))
            {
                return false;
            }
            var next = request.BoolArg(name, current);
            if (current == next)
            {
                return false;
            }
            setter(next);
            return true;
        }

        private static bool ApplyEnum(string rawValue, string current, StringSetter setter)
        {
            if (string.IsNullOrEmpty(rawValue) || string.Equals(rawValue, current, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            setter(rawValue);
            return true;
        }

        private static Vector2 SpritePivot(BridgeRequest request, Vector2 fallback)
        {
            var x = ParseFloat(request.StringArg("pivotX"), fallback.x);
            var y = ParseFloat(request.StringArg("pivotY"), fallback.y);
            return new Vector2(Mathf.Clamp01(x), Mathf.Clamp01(y));
        }

        private static float ParseFloat(string rawValue, float fallback)
        {
            float parsed;
            return !string.IsNullOrEmpty(rawValue) && float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static string TextureMutationJson(string type, string path, Texture2D texture)
        {
            return "{\"texture\":" + TextureAssetJson(path, texture) + ",\"importSettings\":" + TextureImporterJson(RequireTextureImporter(path)) + ",\"plannedEffects\":[{\"type\":" + Quote(type) + ",\"path\":" + Quote(path) + ",\"refresh\":true}],\"logPath\":" + Quote(LogPath()) + "}";
        }
    }
}
#endif
