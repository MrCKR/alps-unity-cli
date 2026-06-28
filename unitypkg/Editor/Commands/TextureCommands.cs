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
            RegisterCommand(registry, "texture.modify", "texture", true, TextureModify);
            RegisterCommand(registry, "texture.delete", "texture", true, TextureDelete);
            RegisterCommand(registry, "texture.import-settings", "texture", true, TextureImportSettings);
            RegisterCommand(registry, "texture.generate", "texture", true, TextureGenerate);
        }

        /// <summary>生成程序化纹理：pattern/gradient/noise。对齐 CoplayDev texture generate。</summary>
        private static string TextureGenerate(BridgeRequest request)
        {
            EnsureEditorNotBusy("texture.generate");
            var path = request.StringArg("path");
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_TEXTURE_PATH_REQUIRED", "texture.generate requires an Assets/*.png path.", "Pass Assets/Textures/Generated.png.");
            }
            var width = Math.Max(1, Math.Min(request.IntArg("width", 256), 4096));
            var height = Math.Max(1, Math.Min(request.IntArg("height", 256), 4096));
            var pattern = request.StringArg("pattern") ?? "solid";
            var color1 = ParseColorArg(request.StringArg("color"), Color.white);
            var color2 = ParseColorArg(request.StringArg("color2"), Color.black);
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "texture.generate").Prop("dryRun", true)
                    .Prop("path", path).Prop("width", (long)width).Prop("height", (long)height).Prop("pattern", pattern)
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "texture.generate").Prop("path", path).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    Color c;
                    if (string.Equals(pattern, "gradient", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pattern, "radial", StringComparison.OrdinalIgnoreCase))
                    {
                        // gradient=linear(沿 x), radial=从中心向外
                        var t = string.Equals(pattern, "radial", StringComparison.OrdinalIgnoreCase)
                            ? Vector2.Distance(new Vector2(x, y), new Vector2(width / 2f, height / 2f)) / Mathf.Max(width, height) * 2f
                            : (float)x / Mathf.Max(1, width - 1);
                        c = Color.Lerp(color1, color2, Mathf.Clamp01(t));
                    }
                    else if (string.Equals(pattern, "noise", StringComparison.OrdinalIgnoreCase))
                    {
                        var n = Mathf.PerlinNoise((float)x / width * 8f, (float)y / height * 8f);
                        c = Color.Lerp(color1, color2, n);
                    }
                    else if (string.Equals(pattern, "checkerboard", StringComparison.OrdinalIgnoreCase))
                    {
                        var size = Mathf.Max(1, request.IntArg("patternSize", 8));
                        c = ((x / size + y / size) % 2 == 0) ? color1 : color2;
                    }
                    else if (string.Equals(pattern, "stripes", StringComparison.OrdinalIgnoreCase))
                    {
                        var size = Mathf.Max(1, request.IntArg("patternSize", 8));
                        c = ((y / size) % 2 == 0) ? color1 : color2;
                    }
                    else if (string.Equals(pattern, "dots", StringComparison.OrdinalIgnoreCase))
                    {
                        var size = Mathf.Max(1, request.IntArg("patternSize", 8));
                        var cx = x % size;
                        var cy = y % size;
                        var r = size / 2f;
                        c = (cx - r) * (cx - r) + (cy - r) * (cy - r) <= r * r ? color2 : color1;
                    }
                    else if (string.Equals(pattern, "grid", StringComparison.OrdinalIgnoreCase))
                    {
                        var size = Mathf.Max(1, request.IntArg("patternSize", 8));
                        c = (x % size == 0 || y % size == 0) ? color2 : color1;
                    }
                    else if (string.Equals(pattern, "brick", StringComparison.OrdinalIgnoreCase))
                    {
                        var size = Mathf.Max(1, request.IntArg("patternSize", 8));
                        var row = y / size;
                        var offset = (row % 2 == 0) ? 0 : size / 2;
                        c = (x + offset) % size == 0 || y % size == 0 ? color2 : color1;
                    }
                    else
                    {
                        c = color1;
                    }
                    pixels[y * width + x] = c;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            CreateAssetDirectory(path);
            var bytes = texture.EncodeToPNG();
            File.WriteAllBytes(AssetFullPath(path), bytes);
            AssetDatabase.ImportAsset(path);
            UnityEngine.Object.DestroyImmediate(texture);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "texture.generate")
                .Prop("path", path).Prop("width", (long)width).Prop("height", (long)height).Prop("pattern", pattern)
                .Prop("sizeBytes", (long)bytes.Length)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "texture.generate").Prop("path", path).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        private static Color ParseColorArg(string value, Color fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            if (ColorUtility.TryParseHtmlString(value, out var c)) return c;
            return fallback;
        }

        private static string TextureInfo(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            var texture = LoadTextureAsset(path);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("texture", TextureAssetJson(path, texture))
                .PropRaw("importSettings", TextureImporterJson(RequireTextureImporter(path)))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
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
            var slices = ParseSpriteSlices(request.RawJsonArg("slices"));
            if (slices.Length > 0)
            {
                importer.spriteImportMode = SpriteImportMode.Multiple;
#pragma warning disable 618
                importer.spritesheet = slices;
#pragma warning restore 618
            }
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
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("texture", TextureAssetJson(path, LoadTextureAsset(path)))
                .PropRaw("importSettings", TextureImporterJson(RequireTextureImporter(path)))
                .Prop("sliceCount", (long)slices.Length)
                .PropArray("slices", arr => { foreach (var slice in slices) arr.BeginObject().Prop("name", slice.name).Prop("x", slice.rect.x).Prop("y", slice.rect.y).Prop("width", slice.rect.width).Prop("height", slice.rect.height).EndObject(); })
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "texture.sprite").Prop("path", path).Prop("refresh", true).EndObject())
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>修改已有 PNG/JPG texture 的像素区域，并保持 Unity import 流程可追踪。</summary>
        private static string TextureModify(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            {
                throw new BridgeCommandException("E_TEXTURE_FORMAT_UNSUPPORTED", "texture.modify can write PNG/JPG assets only: " + path, "Use texture.create with --source for other texture formats, or modify the source file directly.");
            }
            var color = ParseTextureColor(request.StringArg("color") ?? request.StringArg("fillColor"));
            var texture = LoadTextureAsset(path);
            var x = Math.Max(0, request.IntArg("x", 0));
            var y = Math.Max(0, request.IntArg("y", 0));
            var width = request.IntArg("width", texture.width - x);
            var height = request.IntArg("height", texture.height - y);
            if (width <= 0 || height <= 0 || x >= texture.width || y >= texture.height)
            {
                throw new BridgeCommandException("E_TEXTURE_REGION_INVALID", "Texture modify region is outside texture bounds.", "Pass x/y/width/height within the existing texture dimensions.");
            }
            width = Math.Min(width, texture.width - x);
            height = Math.Min(height, texture.height - y);
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "texture.modify").Prop("dryRun", true).Prop("path", path).Prop("x", (long)x).Prop("y", (long)y).Prop("width", (long)width).Prop("height", (long)height).PropRaw("color", TextureColorJson(color))
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "texture.modify").Prop("path", path).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            var working = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(working, File.ReadAllBytes(AssetFullPath(path))))
                {
                    throw new BridgeCommandException("E_TEXTURE_DECODE_FAILED", "Failed to decode texture bytes: " + path, "Reimport the asset or verify the image file is valid.");
                }
                for (var row = y; row < y + height; row++)
                {
                    for (var col = x; col < x + width; col++)
                    {
                        working.SetPixel(col, row, color);
                    }
                }
                working.Apply();
                var bytes = extension == ".png" ? working.EncodeToPNG() : working.EncodeToJPG(95);
                File.WriteAllBytes(AssetFullPath(path), bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("texture", TextureAssetJson(path, LoadTextureAsset(path)))
                    .PropRaw("importSettings", TextureImporterJson(RequireTextureImporter(path)))
                    .Prop("changedPixels", (long)(width * height))
                    .PropRaw("region", new UnityCliJsonWriter().BeginObject().Prop("x", (long)x).Prop("y", (long)y).Prop("width", (long)width).Prop("height", (long)height).EndObject().ToString())
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "texture.modify").Prop("path", path).Prop("refresh", true).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(working);
            }
        }

        /// <summary>删除 texture asset；这是 asset.delete 的 texture 专用入口，便于 AI 从 texture 组发现。</summary>
        private static string TextureDelete(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "texture.delete").Prop("dryRun", true).PropRaw("texture", TextureAssetJson(path, LoadTextureAsset(path)))
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "texture.delete").Prop("path", path).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new BridgeCommandException("E_TEXTURE_DELETE_FAILED", "Failed to delete texture asset: " + path, "Check Unity Console and file permissions.");
            }
            AssetDatabase.Refresh();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropObject("texture", t => t.Prop("path", path).Prop("exists", false))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "texture.delete").Prop("path", path).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
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
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("texture", TextureAssetJson(path, texture))
                .PropRaw("importSettings", TextureImporterJson(RequireTextureImporter(path)))
                .Prop("changed", changed)
                .PropArray("plannedEffects", arr => {
                    if (changed) arr.RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "texture.import-settings")
                        .Prop("path", path)
                        .EndObject().ToString());
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string RequireTexturePath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_TEXTURE_PATH_REQUIRED", "Pass a texture path under Assets/.", "Use tools run asset.search --input '{\"filter\":\"t:Texture2D\"}' to discover the path, or pass an Assets/*.png path.");
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
                throw new BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Texture2D\"}' to discover the path.");
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

        [Serializable]
        private sealed class TextureSpriteSliceList
        {
            public TextureSpriteSliceDefinition[] items;
        }

        [Serializable]
        private sealed class TextureSpriteSliceDefinition
        {
            public string name;
            public float x;
            public float y;
            public float width;
            public float height;
            public float pivotX = 0.5f;
            public float pivotY = 0.5f;
        }

        /// <summary>解析 sprite sheet 切片定义；数组由 TS bridge 从 slices 结构化输入透传。</summary>
        private static SpriteMetaData[] ParseSpriteSlices(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson)) return new SpriteMetaData[0];
            var list = JsonUtility.FromJson<TextureSpriteSliceList>("{\"items\":" + rawJson + "}");
            if (list == null || list.items == null || list.items.Length == 0) return new SpriteMetaData[0];
            var slices = new List<SpriteMetaData>();
            for (var i = 0; i < list.items.Length; i++)
            {
                var item = list.items[i];
                if (item == null || item.width <= 0 || item.height <= 0)
                {
                    throw new BridgeCommandException("E_TEXTURE_SPRITE_SLICE_INVALID", "Sprite slice width and height must be positive.", "Pass slices with name,x,y,width,height and optional pivotX/pivotY.");
                }
                slices.Add(new SpriteMetaData
                {
                    name = string.IsNullOrEmpty(item.name) ? "slice_" + i.ToString(CultureInfo.InvariantCulture) : item.name,
                    rect = new Rect(item.x, item.y, item.width, item.height),
                    pivot = new Vector2(Mathf.Clamp01(item.pivotX), Mathf.Clamp01(item.pivotY)),
                    alignment = (int)SpriteAlignment.Custom
                });
            }
            return slices.ToArray();
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

        private static string TextureColorJson(Color color)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("r", color.r).Prop("g", color.g).Prop("b", color.b).Prop("a", color.a)
                .EndObject().ToString();
        }

        private static string TextureMutationJson(string type, string path, Texture2D texture)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("texture", TextureAssetJson(path, texture))
                .PropRaw("importSettings", TextureImporterJson(RequireTextureImporter(path)))
                .PropArray("plannedEffects", arr => arr.RawValue(new UnityCliJsonWriter().BeginObject()
                    .Prop("type", type)
                    .Prop("path", path)
                    .Prop("refresh", true)
                    .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }
    }
}
#endif
