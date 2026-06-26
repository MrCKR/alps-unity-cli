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
        private static void RegisterMaterialCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "material.info", "material", false, MaterialInfo);
            RegisterCommand(registry, "material.create", "material", true, MaterialCreate);
            RegisterCommand(registry, "material.assign", "material", true, MaterialAssign);
            RegisterCommand(registry, "material.set", "material", true, MaterialSet);
        }

        private static string MaterialInfo(BridgeRequest request)
        {
            var path = RequireMaterialPath(request.StringArg("path"));
            var material = LoadMaterialAsset(path);
            // namePattern: 可选逗号分隔属性名，用于 token 安全的轻量读取，避免大 shader 全量序列化
            var nameFilter = ParseNameFilter(request.StringArg("namePattern"));
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("material", MaterialJson(path, material, nameFilter))
                .Prop("nameFilterApplied", nameFilter != null)
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        private static string MaterialCreate(BridgeRequest request)
        {
            var path = RequireMaterialPath(request.StringArg("path"));
            var shaderName = request.StringArg("shader");
            var overwrite = request.BoolArg("overwrite", false);
            if (string.IsNullOrEmpty(shaderName))
            {
                return ErrorJson("E_SHADER_REQUIRED", "Material create requires --shader <name>.", "Pass a Unity shader name such as Standard or Universal Render Pipeline/Lit.");
            }
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                return ErrorJson("E_SHADER_NOT_FOUND", "Shader not found: " + shaderName, "Check the shader name in Unity or import the required render pipeline package.");
            }
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null && !overwrite)
            {
                return ErrorJson("E_MATERIAL_OUTPUT_EXISTS", "Material output already exists: " + path, "Pass --overwrite or choose a different Assets/*.mat path.");
            }

            CreateAssetDirectory(path);
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            var material = new Material(shader);
            material.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            WriteLog("material_created path=" + path + " shader=" + shaderName);
            return SuccessJson(MaterialMutationJson("material.create", path, MaterialJson(path, LoadMaterialAsset(path))));
        }

        private static string MaterialAssign(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var materialPath = RequireMaterialPath(request.StringArg("material"));
            var material = LoadMaterialAsset(materialPath);
            var gameObject = ResolveUniqueGameObject(target);
            if (gameObject == null)
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Material assign target not found: " + target, "Pass a GameObject name, hierarchy path or instance id.");
            }

            var renderer = gameObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                var slot = Math.Max(0, request.IntArg("slot", 0));
                var materials = renderer.sharedMaterials;
                if (slot >= materials.Length)
                {
                    return ErrorJson("E_MATERIAL_SLOT_INVALID", "Renderer material slot is out of range: " + slot.ToString(CultureInfo.InvariantCulture), "Pass --slot between 0 and " + Math.Max(0, materials.Length - 1).ToString(CultureInfo.InvariantCulture) + ".");
                }
                materials[slot] = material;
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
                AssetDatabase.SaveAssets();
                WriteLog("material_assigned target=" + target + " material=" + materialPath + " component=Renderer slot=" + slot.ToString(CultureInfo.InvariantCulture));
                return SuccessJson(MaterialAssignJson(gameObject, materialPath, "Renderer", slot));
            }

            var graphic = FindUiGraphic(gameObject);
            if (graphic != null)
            {
                var property = graphic.GetType().GetProperty("material", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanWrite)
                {
                    return ErrorJson("E_MATERIAL_ASSIGN_UNSUPPORTED", "UI Graphic material property is not writable on target: " + target, "Assign to a Renderer or UI Graphic component with a writable material property.");
                }
                property.SetValue(graphic, material, null);
                EditorUtility.SetDirty(graphic);
                AssetDatabase.SaveAssets();
                WriteLog("material_assigned target=" + target + " material=" + materialPath + " component=Graphic");
                return SuccessJson(MaterialAssignJson(gameObject, materialPath, "Graphic", 0));
            }

            return ErrorJson("E_MATERIAL_TARGET_UNSUPPORTED", "Target has no Renderer or UI Graphic component: " + target, "Add a Renderer/UI Graphic component or choose another target.");
        }

        private static string MaterialSet(BridgeRequest request)
        {
            var path = RequireMaterialPath(request.StringArg("path"));
            var propertyName = request.StringArg("property");
            var value = request.StringArg("value");
            var requestedType = request.StringArg("type");
            var material = LoadMaterialAsset(path);
            if (string.IsNullOrEmpty(propertyName))
            {
                return ErrorJson("E_MATERIAL_PROPERTY_REQUIRED", "Material set requires --property <name>.", "Inspect shader properties with tools run material.info --input '{\"path\":\"<path>\"}'.");
            }
            if (!material.HasProperty(propertyName))
            {
                return ErrorJson("E_MATERIAL_PROPERTY_NOT_FOUND", "Material property not found: " + propertyName, "Inspect shader properties with tools run material.info --input '{\"path\":\"<path>\"}'.");
            }

            var propertyType = MaterialPropertyType(material, propertyName);
            var normalizedType = NormalizeMaterialValueType(string.IsNullOrEmpty(requestedType) ? propertyType : requestedType);
            if (string.IsNullOrEmpty(normalizedType))
            {
                return ErrorJson("E_MATERIAL_PROPERTY_TYPE_UNSUPPORTED", "Unsupported Material property type for " + propertyName + ": " + propertyType, "Pass --type color|float|vector|texture when the shader property cannot be inferred.");
            }
            if (!TypesCompatible(propertyType, normalizedType))
            {
                return ErrorJson("E_MATERIAL_PROPERTY_TYPE_MISMATCH", "Material property " + propertyName + " is " + propertyType + ", not " + requestedType + ".", "Use the property type reported by tools run material.info --input '{\"path\":\"<path>\"}'.");
            }

            var appliedValue = ApplyMaterialValue(material, propertyName, normalizedType, value);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            WriteLog("material_set path=" + path + " property=" + propertyName + " type=" + normalizedType);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("material", MaterialJson(path, material))
                .Prop("property", propertyName)
                .Prop("propertyType", propertyType)
                .PropRaw("appliedValue", appliedValue)
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "material.set")
                        .Prop("path", path)
                        .Prop("property", propertyName)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString());
        }

        private static string RequireMaterialPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_MATERIAL_PATH_REQUIRED", "Pass an Assets/*.mat path.", "Use tools run material.info --input '{\"path\":\"<path>\"}' or pass an Assets/*.mat path.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_MATERIAL_PATH_INVALID", "Material path must be under Assets/ and end with .mat: " + path, "Pass a Unity asset path such as Assets/Materials/Player.mat.");
            }
            return path;
        }

        private static Material LoadMaterialAsset(string path)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                throw new BridgeCommandException("E_MATERIAL_NOT_FOUND", "Material asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:Material\"}' to discover the path.");
            }
            return material;
        }

        private static GameObject ResolveUniqueGameObject(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "Material assign requires --target <go>.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var matches = new List<GameObject>();
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            if (matches.Count > 1)
            {
                throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched: " + target, "Pass a hierarchy path or instance id.");
            }
            return matches.Count == 1 ? matches[0] : null;
        }

        private static string MaterialJson(string path, Material material, HashSet<string> nameFilter = null)
        {
            var shader = material.shader;
            var dependencies = AssetDatabase.GetDependencies(path, false);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", material.name)
                .Prop("shader", shader == null ? null : shader.name)
                .Prop("renderQueue", (long)material.renderQueue)
                .PropArray("keywords", arr => { foreach (var keyword in material.shaderKeywords) { arr.Value(keyword); } })
                .PropRaw("properties", MaterialPropertiesJson(material, nameFilter))
                .PropRaw("textureReferences", MaterialTextureReferencesJson(material, nameFilter))
                .PropRaw("dependencies", DependencySummaryJson(dependencies))
                .EndObject()
                .ToString();
        }

        // 解析逗号分隔的属性名筛选集合；null 表示不过滤（全量返回）
        private static HashSet<string> ParseNameFilter(string namePattern)
        {
            if (string.IsNullOrEmpty(namePattern))
            {
                return null;
            }
            var set = new HashSet<string>();
            foreach (var part in namePattern.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    set.Add(trimmed);
                }
            }
            return set.Count == 0 ? null : set;
        }


        private static string MaterialPropertiesJson(Material material, HashSet<string> nameFilter = null)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return new UnityCliJsonWriter().BeginObject()
                    .Prop("count", 0L)
                    .PropArray("items", arr => {})
                    .EndObject()
                    .ToString();
            }
            var items = new List<string>();
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var index = 0; index < count; index++)
            {
                var name = ShaderUtil.GetPropertyName(shader, index);
                if (nameFilter != null && !nameFilter.Contains(name))
                {
                    continue;
                }
                var type = ShaderUtil.GetPropertyType(shader, index).ToString();
                var description = ShaderUtil.GetPropertyDescription(shader, index);
                var valueJson = material.HasProperty(name) ? MaterialPropertyValueJson(material, name, NormalizeMaterialValueType(type)) : "null";
                items.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("name", name)
                    .Prop("displayName", description)
                    .Prop("type", type)
                    .PropRaw("value", valueJson)
                    .EndObject()
                    .ToString());
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)items.Count)
                .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .EndObject()
                .ToString();
        }

        private static string MaterialTextureReferencesJson(Material material, HashSet<string> nameFilter = null)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return new UnityCliJsonWriter().BeginObject()
                    .Prop("count", 0L)
                    .PropArray("items", arr => {})
                    .EndObject()
                    .ToString();
            }
            var items = new List<string>();
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var index = 0; index < count; index++)
            {
                var name = ShaderUtil.GetPropertyName(shader, index);
                if (nameFilter != null && !nameFilter.Contains(name))
                {
                    continue;
                }
                if (NormalizeMaterialValueType(ShaderUtil.GetPropertyType(shader, index).ToString()) != "texture" || !material.HasProperty(name))
                {
                    continue;
                }
                var texture = material.GetTexture(name);
                var texturePath = texture == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(texture));
                items.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("property", name)
                    .Prop("path", texturePath)
                    .Prop("guid", string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.AssetPathToGUID(texturePath))
                    .EndObject()
                    .ToString());
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)items.Count)
                .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .EndObject()
                .ToString();
        }

        private static string MaterialPropertyType(Material material, string propertyName)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return null;
            }
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

        private static string NormalizeMaterialValueType(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return null;
            }
            var normalized = type.ToLowerInvariant();
            if (normalized == "color")
            {
                return "color";
            }
            if (normalized == "float" || normalized == "range")
            {
                return "float";
            }
            if (normalized == "vector")
            {
                return "vector";
            }
            if (normalized == "texture" || normalized == "texenv" || normalized == "texture2d")
            {
                return "texture";
            }
            return null;
        }

        private static bool TypesCompatible(string shaderType, string requestedType)
        {
            var inferred = NormalizeMaterialValueType(shaderType);
            return string.IsNullOrEmpty(inferred) || inferred == requestedType;
        }

        private static string ApplyMaterialValue(Material material, string propertyName, string type, string value)
        {
            if (type == "color")
            {
                var color = ParseColor(value);
                material.SetColor(propertyName, color);
                return ColorJson(color);
            }
            if (type == "float")
            {
                var number = ParseFloat(value, "E_MATERIAL_VALUE_INVALID", "Pass a numeric value for float/range Material properties.");
                material.SetFloat(propertyName, number);
                return number.ToString(CultureInfo.InvariantCulture);
            }
            if (type == "vector")
            {
                var vector = ParseVector(value);
                material.SetVector(propertyName, vector);
                return VectorJson(vector);
            }
            if (type == "texture")
            {
                var texturePath = NormalizeAssetPath(value);
                Texture texture = null;
                if (!string.IsNullOrEmpty(texturePath) && !string.Equals(texturePath, "null", StringComparison.OrdinalIgnoreCase))
                {
                    texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                    if (texture == null)
                    {
                        throw new BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found: " + texturePath, "Pass an existing Assets texture path or value null to clear the texture.");
                    }
                }
                material.SetTexture(propertyName, texture);
                return TextureValueJson(texturePath, texture);
            }
            throw new BridgeCommandException("E_MATERIAL_PROPERTY_TYPE_UNSUPPORTED", "Unsupported Material property value type: " + type, "Pass --type color|float|vector|texture.");
        }

        private static string MaterialPropertyValueJson(Material material, string propertyName, string type)
        {
            try
            {
                if (type == "color")
                {
                    return ColorJson(material.GetColor(propertyName));
                }
                if (type == "float")
                {
                    return material.GetFloat(propertyName).ToString(CultureInfo.InvariantCulture);
                }
                if (type == "vector")
                {
                    return VectorJson(material.GetVector(propertyName));
                }
                if (type == "texture")
                {
                    var texture = material.GetTexture(propertyName);
                    var texturePath = texture == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(texture));
                    return TextureValueJson(texturePath, texture);
                }
            }
            catch
            {
            }
            return "null";
        }

        private static string TextureValueJson(string texturePath, Texture texture)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", texturePath)
                .Prop("guid", texture == null ? null : AssetDatabase.AssetPathToGUID(texturePath))
                .EndObject()
                .ToString();
        }

        private static Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Pass a color value.", "Use #RRGGBB, #RRGGBBAA or comma-separated r,g,b[,a].");
            }
            Color color;
            if (ColorUtility.TryParseHtmlString(value, out color))
            {
                return color;
            }
            var parts = value.Split(',');
            if (parts.Length == 3 || parts.Length == 4)
            {
                return new Color(ParseFloat(parts[0], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."), ParseFloat(parts[1], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."), ParseFloat(parts[2], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."), parts.Length == 4 ? ParseFloat(parts[3], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric.") : 1f);
            }
            throw new BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Invalid color value: " + value, "Use #RRGGBB, #RRGGBBAA or comma-separated r,g,b[,a].");
        }

        private static Vector4 ParseVector(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Pass a vector value.", "Use comma-separated x,y,z,w numbers.");
            }
            var parts = value.Split(',');
            if (parts.Length != 4)
            {
                throw new BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Vector values require four numbers: " + value, "Use comma-separated x,y,z,w numbers.");
            }
            return new Vector4(ParseFloat(parts[0], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."), ParseFloat(parts[1], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."), ParseFloat(parts[2], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."), ParseFloat(parts[3], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."));
        }

        private static float ParseFloat(string value, string code, string nextAction)
        {
            float parsed;
            if (!float.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new BridgeCommandException(code, "Invalid numeric Material value: " + value, nextAction);
            }
            return parsed;
        }

        private static string ColorJson(Color color)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("r", color.r)
                .Prop("g", color.g)
                .Prop("b", color.b)
                .Prop("a", color.a)
                .EndObject()
                .ToString();
        }

        private static string VectorJson(Vector4 vector)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("x", vector.x)
                .Prop("y", vector.y)
                .Prop("z", vector.z)
                .Prop("w", vector.w)
                .EndObject()
                .ToString();
        }

        private static string MaterialMutationJson(string type, string path, string materialJson)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("material", materialJson)
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", type)
                        .Prop("path", path)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }

        private static string MaterialAssignJson(GameObject gameObject, string materialPath, string componentType, int slot)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropObject("target", target => target
                    .Prop("id", (long)gameObject.GetInstanceID())
                    .Prop("name", gameObject.name)
                    .Prop("path", HierarchyPath(gameObject)))
                .PropObject("material", mat => mat
                    .Prop("path", materialPath)
                    .Prop("guid", AssetDatabase.AssetPathToGUID(materialPath)))
                .Prop("componentType", componentType)
                .Prop("slot", (long)slot)
                .PropArray("plannedEffects", arr => arr
                    .RawValue(new UnityCliJsonWriter().BeginObject()
                        .Prop("type", "material.assign")
                        .Prop("target", HierarchyPath(gameObject))
                        .Prop("material", materialPath)
                        .EndObject().ToString()))
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
        }
    }
}
#endif
