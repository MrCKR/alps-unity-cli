#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// texture 域 IUnityCliTool 共享的基础设施 helper:纹理路径校验、纹理资产/importer 加载、
    /// 导入设置应用、颜色解析、sprite 切片构造。仅供 texture 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class TextureToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求,避免脏状态写入。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        // 校验并返回 Assets/ 纹理路径;空或扩展名不支持抛 BridgeCommandException。
        public static string RequireTexturePath(string rawPath)
        {
            var path = AssetToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_PATH_REQUIRED", "Pass a texture path under Assets/.", "Use tools run asset.search --input '{\"filter\":\"t:Texture2D\"}' to discover the path, or pass an Assets/*.png path.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !IsTextureExtension(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_PATH_INVALID", "Texture path must be under Assets/ and use a supported texture extension: " + path, "Use .png, .jpg, .jpeg, .tga, .psd or .exr under Assets/.");
            }
            return path;
        }

        /// <summary>校验纹理路径并要求资产已存在。</summary>
        public static string ResolveTexturePath(string rawPath)
        {
            var path = RequireTexturePath(rawPath);
            if (!AssetToolSupport.AssetExists(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Texture2D\"}' to discover the path.");
            }
            return path;
        }

        /// <summary>加载 Texture2D 资产;不存在或导入失败抛 BridgeCommandException。</summary>
        public static Texture2D LoadTextureAsset(string path)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found or failed to import: " + path, "Check importer diagnostics in Unity Console and UnityCliBridge.log.");
            }
            return texture;
        }

        /// <summary>获取 TextureImporter;不存在抛 BridgeCommandException。</summary>
        public static TextureImporter RequireTextureImporter(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_IMPORTER_NOT_FOUND", "TextureImporter not found for: " + path, "Reimport the asset or verify it is a texture file.");
            }
            return importer;
        }

        /// <summary>判断路径是否为支持的纹理扩展名。</summary>
        public static bool IsTextureExtension(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".tga" || extension == ".psd" || extension == ".exr";
        }

        /// <summary>构造纹理资产摘要(path/guid/name/width/height/format/sizeBytes)。</summary>
        public static TextureInfo BuildTextureInfo(string path, Texture2D texture)
        {
            var fileInfo = new FileInfo(AssetToolSupport.AssetFullPath(path));
            return new TextureInfo
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Name = texture == null ? Path.GetFileNameWithoutExtension(path) : texture.name,
                Width = texture == null ? 0 : texture.width,
                Height = texture == null ? 0 : texture.height,
                Format = texture == null ? null : texture.format.ToString(),
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0L
            };
        }

        /// <summary>构造 TextureImporter 设置摘要。</summary>
        public static TextureImportSettings BuildTextureImportSettings(TextureImporter importer)
        {
            return new TextureImportSettings
            {
                TextureType = importer.textureType.ToString(),
                SpriteImportMode = importer.spriteImportMode.ToString(),
                PixelsPerUnit = importer.spritePixelsPerUnit,
                Pivot = new TexturePivot { X = importer.spritePivot.x, Y = importer.spritePivot.y },
                Readable = importer.isReadable,
                Mipmap = importer.mipmapEnabled,
                SRGB = importer.sRGBTexture,
                AlphaIsTransparency = importer.alphaIsTransparency,
                WrapMode = importer.wrapMode.ToString(),
                FilterMode = importer.filterMode.ToString(),
                MaxTextureSize = importer.maxTextureSize,
                Compression = importer.textureCompression.ToString()
            };
        }

        /// <summary>应用纹理导入设置(可选字段为 null 时跳过);返回是否有变更。saveAndReimport=true 时保存并刷新。</summary>
        public static bool ApplyTextureSettings(TextureImporter importer, TextureSettings f, bool saveAndReimport)
        {
            var changed = false;
            if (!string.IsNullOrEmpty(f.TextureType))
            {
                var normalized = string.Equals(f.TextureType, "sprite", StringComparison.OrdinalIgnoreCase) ? "Sprite" : f.TextureType;
                var next = (TextureImporterType)Enum.Parse(typeof(TextureImporterType), normalized, true);
                if (importer.textureType != next)
                {
                    importer.textureType = next;
                    changed = true;
                }
            }
            changed = ApplyBool(f.Readable, importer.isReadable, v => importer.isReadable = v) || changed;
            changed = ApplyBool(f.Mipmap, importer.mipmapEnabled, v => importer.mipmapEnabled = v) || changed;
            changed = ApplyBool(f.SRGB, importer.sRGBTexture, v => importer.sRGBTexture = v) || changed;
            changed = ApplyBool(f.AlphaIsTransparency, importer.alphaIsTransparency, v => importer.alphaIsTransparency = v) || changed;
            changed = ApplyEnum(f.WrapMode, importer.wrapMode.ToString(), v => importer.wrapMode = (TextureWrapMode)Enum.Parse(typeof(TextureWrapMode), v, true)) || changed;
            changed = ApplyEnum(f.FilterMode, importer.filterMode.ToString(), v => importer.filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), v, true)) || changed;
            changed = ApplyEnum(f.Compression, importer.textureCompression.ToString(), v => importer.textureCompression = (TextureImporterCompression)Enum.Parse(typeof(TextureImporterCompression), v, true)) || changed;
            if (f.MaxTextureSize.HasValue && f.MaxTextureSize.Value > 0 && importer.maxTextureSize != f.MaxTextureSize.Value)
            {
                importer.maxTextureSize = f.MaxTextureSize.Value;
                changed = true;
            }
            if (saveAndReimport && changed)
            {
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
            }
            return changed;
        }

        private static bool ApplyBool(bool? next, bool current, Action<bool> setter)
        {
            if (!next.HasValue || current == next.Value) return false;
            setter(next.Value);
            return true;
        }

        private static bool ApplyEnum(string next, string current, Action<string> setter)
        {
            if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) return false;
            setter(next);
            return true;
        }

        /// <summary>解析纹理颜色(十六进制 RGB/RGBA,可带 #);空返回白色。</summary>
        public static Color ParseTextureColor(string rawColor)
        {
            if (string.IsNullOrEmpty(rawColor)) return Color.white;
            Color color;
            if (ColorUtility.TryParseHtmlString(rawColor.StartsWith("#", StringComparison.Ordinal) ? rawColor : "#" + rawColor, out color))
            {
                return color;
            }
            throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_COLOR_INVALID", "Texture color must be a hex RGB/RGBA value: " + rawColor, "Use values like --color ffffff or --color #ff00ffff.");
        }

        /// <summary>把结构化 sprite 切片输入转换为 SpriteMetaData 数组。</summary>
        public static SpriteMetaData[] BuildSpriteSlices(List<TextureSliceInput> slices)
        {
            if (slices == null || slices.Count == 0) return new SpriteMetaData[0];
            var result = new List<SpriteMetaData>();
            for (var i = 0; i < slices.Count; i++)
            {
                var item = slices[i];
                if (item == null || item.Width <= 0 || item.Height <= 0)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_SPRITE_SLICE_INVALID", "Sprite slice width and height must be positive.", "Pass slices with name,x,y,width,height and optional pivotX/pivotY.");
                }
                result.Add(new SpriteMetaData
                {
                    name = string.IsNullOrEmpty(item.Name) ? "slice_" + i.ToString(CultureInfo.InvariantCulture) : item.Name,
                    rect = new Rect(item.X, item.Y, item.Width, item.Height),
                    pivot = new Vector2(Mathf.Clamp01(item.PivotX), Mathf.Clamp01(item.PivotY)),
                    alignment = (int)SpriteAlignment.Custom
                });
            }
            return result.ToArray();
        }
    }

    // ─── texture 域共享 DTO ───────────────────────────────────────────────

    public sealed class TextureInfo
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
        public string Format { get; set; }
        public long SizeBytes { get; set; }
    }

    public sealed class TextureImportSettings
    {
        public string TextureType { get; set; }
        public string SpriteImportMode { get; set; }
        public double PixelsPerUnit { get; set; }
        public TexturePivot Pivot { get; set; }
        public bool Readable { get; set; }
        public bool Mipmap { get; set; }
        public bool SRGB { get; set; }
        public bool AlphaIsTransparency { get; set; }
        public string WrapMode { get; set; }
        public string FilterMode { get; set; }
        public long MaxTextureSize { get; set; }
        public string Compression { get; set; }
    }

    public sealed class TexturePivot
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>导入设置可选字段集合(均为 nullable,未提供则跳过)。</summary>
    public sealed class TextureSettings
    {
        public string TextureType { get; set; }
        public bool? Readable { get; set; }
        public bool? Mipmap { get; set; }
        public bool? SRGB { get; set; }
        public bool? AlphaIsTransparency { get; set; }
        public string WrapMode { get; set; }
        public string FilterMode { get; set; }
        public string Compression { get; set; }
        public int? MaxTextureSize { get; set; }
    }

    /// <summary>sprite 切片输入(由 Request 反序列化)。</summary>
    public sealed class TextureSliceInput
    {
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float PivotX { get; set; } = 0.5f;
        public float PivotY { get; set; } = 0.5f;
    }

    /// <summary>sprite 切片输出摘要。</summary>
    public sealed class TextureSliceOutput
    {
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    /// <summary>计划副作用项,texture 域 mutation 工具 Result 共享。</summary>
    public sealed class TexturePlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public bool Refresh { get; set; }
    }
}
#endif
