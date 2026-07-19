#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// material 域 IUnityCliTool 共享的基础设施 helper:Material 路径校验、Material 资产加载、
    /// 属性枚举/取值/赋值、颜色/向量解析、UI Graphic 查找。仅供 material 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// </summary>
    public static class MaterialToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求,避免脏状态写入。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        // 校验并返回 Assets/*.mat 路径;空或格式不符抛 BridgeCommandException。
        public static string RequireMaterialPath(string rawPath)
        {
            var path = AssetToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PATH_REQUIRED", "Pass an Assets/*.mat path.", "Use tools run material.info --input '{\"path\":\"<path>\"}' or pass an Assets/*.mat path.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PATH_INVALID", "Material path must be under Assets/ and end with .mat: " + path, "Pass a Unity asset path such as Assets/Materials/Player.mat.");
            }
            return path;
        }

        /// <summary>加载 Assets/*.mat 资产;不存在抛 BridgeCommandException。</summary>
        public static Material LoadMaterialAsset(string path)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_NOT_FOUND", "Material asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Material\"}' to discover the path.");
            }
            return material;
        }

        // 解析逗号分隔的属性名筛选集合;null 表示不过滤(全量返回)
        public static HashSet<string> ParseNameFilter(string namePattern)
        {
            if (string.IsNullOrEmpty(namePattern)) return null;
            var set = new HashSet<string>();
            foreach (var part in namePattern.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }
            return set.Count == 0 ? null : set;
        }

        /// <summary>构造 Material 摘要(path/guid/name/shader/renderQueue/keywords/properties/textureReferences/dependencies)。</summary>
        public static MaterialInfo BuildMaterialInfo(string path, Material material, HashSet<string> nameFilter)
        {
            var shader = material.shader;
            var dependencies = AssetDatabase.GetDependencies(path, false);
            return new MaterialInfo
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Name = material.name,
                Shader = shader == null ? null : shader.name,
                RenderQueue = material.renderQueue,
                Keywords = new List<string>(material.shaderKeywords),
                Properties = BuildProperties(material, nameFilter),
                TextureReferences = BuildTextureReferences(material, nameFilter),
                Dependencies = PrefabToolSupport.BuildDependencySummary(dependencies, true)
            };
        }

        private static MaterialPropertyCollection BuildProperties(Material material, HashSet<string> nameFilter)
        {
            var shader = material.shader;
            var items = new List<MaterialProperty>();
            if (shader == null)
            {
                return new MaterialPropertyCollection { Count = 0, Items = items };
            }
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var index = 0; index < count; index++)
            {
                var name = ShaderUtil.GetPropertyName(shader, index);
                if (nameFilter != null && !nameFilter.Contains(name)) continue;
                var type = ShaderUtil.GetPropertyType(shader, index).ToString();
                var description = ShaderUtil.GetPropertyDescription(shader, index);
                object value = material.HasProperty(name) ? PropertyValue(material, name, NormalizeMaterialValueType(type)) : null;
                items.Add(new MaterialProperty { Name = name, DisplayName = description, Type = type, Value = value });
            }
            return new MaterialPropertyCollection { Count = items.Count, Items = items };
        }

        private static MaterialTextureReferenceCollection BuildTextureReferences(Material material, HashSet<string> nameFilter)
        {
            var shader = material.shader;
            var items = new List<MaterialTextureReference>();
            if (shader == null)
            {
                return new MaterialTextureReferenceCollection { Count = 0, Items = items };
            }
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var index = 0; index < count; index++)
            {
                var name = ShaderUtil.GetPropertyName(shader, index);
                if (nameFilter != null && !nameFilter.Contains(name)) continue;
                if (NormalizeMaterialValueType(ShaderUtil.GetPropertyType(shader, index).ToString()) != "texture" || !material.HasProperty(name)) continue;
                var texture = material.GetTexture(name);
                var texturePath = texture == null ? null : AssetToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(texture));
                items.Add(new MaterialTextureReference
                {
                    Property = name,
                    Path = texturePath,
                    Guid = string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.AssetPathToGUID(texturePath)
                });
            }
            return new MaterialTextureReferenceCollection { Count = items.Count, Items = items };
        }

        /// <summary>读取 Material 属性的 ShaderUtil 类型字符串;无则 null。</summary>
        public static string MaterialPropertyType(Material material, string propertyName)
        {
            var shader = material.shader;
            if (shader == null) return null;
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var index = 0; index < count; index++)
            {
                if (ShaderUtil.GetPropertyName(shader, index) == propertyName)
                {
                    return ShaderUtil.GetPropertyType(shader, index).ToString();
                }
            }
            return null;
        }

        /// <summary>规范化 Material 属性值类型(color/float/vector/texture);不支持返回 null。</summary>
        public static string NormalizeMaterialValueType(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            var normalized = type.ToLowerInvariant();
            if (normalized == "color") return "color";
            if (normalized == "float" || normalized == "range") return "float";
            if (normalized == "vector") return "vector";
            if (normalized == "texture" || normalized == "texenv" || normalized == "texture2d") return "texture";
            return null;
        }

        /// <summary>判断 shader 属性类型与请求类型是否兼容。</summary>
        public static bool TypesCompatible(string shaderType, string requestedType)
        {
            var inferred = NormalizeMaterialValueType(shaderType);
            return string.IsNullOrEmpty(inferred) || inferred == requestedType;
        }

        /// <summary>对 Material 属性赋值并返回赋值后的结构化值。</summary>
        public static object ApplyMaterialValue(Material material, string propertyName, string type, string value)
        {
            return ApplyMaterialValue(material, propertyName, BuildMaterialValuePlan(type, value));
        }

        /// <summary>在 effect 前将请求值解析为类型化、可复用的赋值计划。</summary>
        public static MaterialValuePlan BuildMaterialValuePlan(string type, string value)
        {
            if (type == "color") return new MaterialValuePlan { Type = type, Color = ParseColor(value) };
            if (type == "float") return new MaterialValuePlan { Type = type, Float = ParseFloat(value, "E_MATERIAL_VALUE_INVALID", "Pass a finite numeric value for float/range Material properties.") };
            if (type == "vector") return new MaterialValuePlan { Type = type, Vector = ParseVector(value) };
            var texturePath = AssetToolSupport.NormalizeAssetPath(value);
            if (string.IsNullOrEmpty(texturePath) || string.Equals(texturePath, "null", StringComparison.OrdinalIgnoreCase)) return new MaterialValuePlan { Type = "texture" };
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null) throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found: " + texturePath, "Pass an existing Assets texture path or value null to clear the texture.");
            return new MaterialValuePlan { Type = "texture", Texture = texture, TexturePath = texturePath };
        }

        public static object ApplyMaterialValue(Material material, string propertyName, MaterialValuePlan plan)
        {
            var type = plan.Type;
            if (type == "color")
            {
                var color = plan.Color;
                material.SetColor(propertyName, color);
                return new MaterialColor { R = color.r, G = color.g, B = color.b, A = color.a };
            }
            if (type == "float")
            {
                var number = plan.Float;
                material.SetFloat(propertyName, number);
                return number;
            }
            if (type == "vector")
            {
                var vector = plan.Vector;
                material.SetVector(propertyName, vector);
                return new MaterialVector4 { X = vector.x, Y = vector.y, Z = vector.z, W = vector.w };
            }
            if (type == "texture")
            {
                material.SetTexture(propertyName, plan.Texture);
                return new MaterialTextureValue { Path = plan.TexturePath, Guid = plan.Texture == null ? null : AssetDatabase.AssetPathToGUID(plan.TexturePath) };
            }
            throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_PROPERTY_TYPE_UNSUPPORTED", "Unsupported Material property value type: " + type, "Pass --type color|float|vector|texture.");
        }

        private static object PropertyValue(Material material, string propertyName, string type)
        {
            try
            {
                if (type == "color")
                {
                    var c = material.GetColor(propertyName);
                    return new MaterialColor { R = c.r, G = c.g, B = c.b, A = c.a };
                }
                if (type == "float") return material.GetFloat(propertyName);
                if (type == "vector")
                {
                    var v = material.GetVector(propertyName);
                    return new MaterialVector4 { X = v.x, Y = v.y, Z = v.z, W = v.w };
                }
                if (type == "texture")
                {
                    var texture = material.GetTexture(propertyName);
                    var texturePath = texture == null ? null : AssetToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(texture));
                    return new MaterialTextureValue { Path = texturePath, Guid = texture == null ? null : AssetDatabase.AssetPathToGUID(texturePath) };
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>解析颜色值(#RRGGBB / #RRGGBBAA / r,g,b[,a])。</summary>
        public static Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Pass a color value.", "Use #RRGGBB, #RRGGBBAA or comma-separated r,g,b[,a].");
            }
            Color color;
            if (ColorUtility.TryParseHtmlString(value, out color)) return color;
            var parts = value.Split(',');
            if (parts.Length == 3 || parts.Length == 4)
            {
                return new Color(
                    ParseFloat(parts[0], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."),
                    ParseFloat(parts[1], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."),
                    ParseFloat(parts[2], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."),
                    parts.Length == 4 ? ParseFloat(parts[3], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric.") : 1f);
            }
            throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Invalid color value: " + value, "Use #RRGGBB, #RRGGBBAA or comma-separated r,g,b[,a].");
        }

        /// <summary>解析四分量向量(x,y,z,w)。</summary>
        public static Vector4 ParseVector(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Pass a vector value.", "Use comma-separated x,y,z,w numbers.");
            }
            var parts = value.Split(',');
            if (parts.Length != 4)
            {
                throw new UnityCliBridge.BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Vector values require four numbers: " + value, "Use comma-separated x,y,z,w numbers.");
            }
            return new Vector4(
                ParseFloat(parts[0], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."),
                ParseFloat(parts[1], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."),
                ParseFloat(parts[2], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."),
                ParseFloat(parts[3], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."));
        }

        /// <summary>解析浮点数;失败抛 BridgeCommandException。</summary>
        public static float ParseFloat(string value, string code, string nextAction)
        {
            float parsed;
            if (!float.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new UnityCliBridge.BridgeCommandException(code, "Invalid numeric Material value: " + value, nextAction);
            }
            if (float.IsNaN(parsed) || float.IsInfinity(parsed))
                throw new UnityCliBridge.BridgeCommandException(code, "Numeric Material value must be finite: " + value, nextAction);
            return parsed;
        }

        /// <summary>查找 GameObject 上的 UnityEngine.UI.Graphic 派生组件(无则 null)。</summary>
        public static Component FindUiGraphic(GameObject gameObject)
        {
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null) continue;
                var type = component.GetType();
                while (type != null)
                {
                    if (type.FullName == "UnityEngine.UI.Graphic") return component;
                    type = type.BaseType;
                }
            }
            return null;
        }
    }

    // ─── material 域共享 DTO ──────────────────────────────────────────────

    public sealed class MaterialValuePlan
    {
        public string Type { get; set; }
        public Color Color { get; set; }
        public float Float { get; set; }
        public Vector4 Vector { get; set; }
        public Texture Texture { get; set; }
        public string TexturePath { get; set; }
    }

    public sealed class MaterialInfo
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Shader { get; set; }
        public long RenderQueue { get; set; }
        public List<string> Keywords { get; set; }
        public MaterialPropertyCollection Properties { get; set; }
        public MaterialTextureReferenceCollection TextureReferences { get; set; }
        public DependencySummary Dependencies { get; set; }
    }

    public sealed class MaterialPropertyCollection
    {
        public long Count { get; set; }
        public List<MaterialProperty> Items { get; set; }
    }

    public sealed class MaterialProperty
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Type { get; set; }
        [UnityCliAnyJson] public object Value { get; set; }
    }

    public sealed class MaterialTextureReferenceCollection
    {
        public long Count { get; set; }
        public List<MaterialTextureReference> Items { get; set; }
    }

    public sealed class MaterialTextureReference
    {
        public string Property { get; set; }
        public string Path { get; set; }
        public string Guid { get; set; }
    }

    public sealed class MaterialColor
    {
        public double R { get; set; }
        public double G { get; set; }
        public double B { get; set; }
        public double A { get; set; }
    }

    public sealed class MaterialVector4
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double W { get; set; }
    }

    public sealed class MaterialTextureValue
    {
        public string Path { get; set; }
        public string Guid { get; set; }
    }

    /// <summary>计划副作用项,material 域 mutation 工具 Result 共享。</summary>
    public sealed class MaterialPlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string Property { get; set; }
        public string Target { get; set; }
    }

    /// <summary>Material assign 结果目标引用。</summary>
    public sealed class MaterialAssignTarget
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
    }
}
#endif
