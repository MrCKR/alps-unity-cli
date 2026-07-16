#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        internal static ScriptableObjectPatchResult ApplyScriptableObjectPatch(SerializedObject so, Dictionary<string, object> patch, int index, bool dryRun, out bool ok, out bool changed)
        {
            ok = false;
            changed = false;
            if (patch == null) return BuildScriptableObjectPatchResult(index, null, null, false, false, null, "Patch must be an object.");
            object field;
            patch.TryGetValue("propertyPath", out field);
            var rawPath = field as string;
            if (rawPath == null && patch.TryGetValue("property_path", out field)) rawPath = field as string;
            if (rawPath == null && patch.TryGetValue("path", out field)) rawPath = field as string;
            var op = patch.TryGetValue("op", out field) && field is string ? ((string)field).Trim().ToLowerInvariant() : "set";
            var propertyPath = NormalizeSerializedPropertyPath(rawPath);
            if (string.IsNullOrEmpty(propertyPath)) return BuildScriptableObjectPatchResult(index, rawPath, op, false, false, null, "Missing required field: propertyPath.");
            patch.TryGetValue("value", out field);
            if (op == "array_resize")
            {
                var arrayProp = so.FindProperty(propertyPath.EndsWith(".Array.size", StringComparison.Ordinal) ? propertyPath.Substring(0, propertyPath.Length - ".Array.size".Length) : propertyPath);
                int size;
                try { size = Convert.ToInt32(field, CultureInfo.InvariantCulture); }
                catch { size = -1; }
                if (arrayProp == null || !arrayProp.isArray) return BuildScriptableObjectPatchResult(index, propertyPath, op, false, false, null, "Property is not an array.");
                if (size < 0) return BuildScriptableObjectPatchResult(index, propertyPath, op, false, false, arrayProp.propertyType.ToString(), "array_resize requires non-negative integer value.");
                ok = true;
                changed = arrayProp.arraySize != size;
                if (!dryRun) arrayProp.arraySize = size;
                return BuildScriptableObjectPatchResult(index, propertyPath, op, true, changed, arrayProp.propertyType.ToString(), dryRun ? "Validated array resize." : "Array resize applied.");
            }
            if (op != "set") return BuildScriptableObjectPatchResult(index, propertyPath, op, false, false, null, "Unsupported patch op: " + op + ".");
            var prop = so.FindProperty(propertyPath);
            if (prop == null) return BuildScriptableObjectPatchResult(index, propertyPath, op, false, false, null, "Property not found.");
            string message;
            if (!TrySetTypedSerializedPropertyValue(prop, field, dryRun, out message)) return BuildScriptableObjectPatchResult(index, propertyPath, op, false, false, prop.propertyType.ToString(), message);
            ok = true;
            changed = true;
            return BuildScriptableObjectPatchResult(index, propertyPath, op, true, true, prop.propertyType.ToString(), dryRun ? "Validated set patch." : "Set patch applied.");
        }

        private static bool TrySetTypedSerializedPropertyValue(SerializedProperty prop, object value, bool validateOnly, out string message)
        {
            message = null;
            try
            {
                object converted;
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer: converted = Convert.ToInt32(value, CultureInfo.InvariantCulture); if (!validateOnly) prop.intValue = (int)converted; return true;
                    case SerializedPropertyType.Boolean: converted = Convert.ToBoolean(value, CultureInfo.InvariantCulture); if (!validateOnly) prop.boolValue = (bool)converted; return true;
                    case SerializedPropertyType.Float: converted = Convert.ToSingle(value, CultureInfo.InvariantCulture); if (!validateOnly) prop.floatValue = (float)converted; return true;
                    case SerializedPropertyType.String: if (!validateOnly) prop.stringValue = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture); return true;
                    case SerializedPropertyType.Color: converted = ObjectPatch.ConvertValue(value, typeof(Color)); if (!validateOnly) prop.colorValue = (Color)converted; return true;
                    case SerializedPropertyType.ObjectReference: converted = ObjectPatch.ConvertValue(value, typeof(UnityEngine.Object)); if (!validateOnly) prop.objectReferenceValue = converted as UnityEngine.Object; return value == null || converted != null;
                    case SerializedPropertyType.Vector2: converted = ObjectPatch.ConvertValue(value, typeof(Vector2)); if (!validateOnly) prop.vector2Value = (Vector2)converted; return true;
                    case SerializedPropertyType.Vector3: converted = ObjectPatch.ConvertValue(value, typeof(Vector3)); if (!validateOnly) prop.vector3Value = (Vector3)converted; return true;
                    case SerializedPropertyType.Vector4: converted = ObjectPatch.ConvertValue(value, typeof(Vector4)); if (!validateOnly) prop.vector4Value = (Vector4)converted; return true;
                    case SerializedPropertyType.Quaternion: converted = ObjectPatch.ConvertValue(value, typeof(Quaternion)); if (!validateOnly) prop.quaternionValue = (Quaternion)converted; return true;
                    case SerializedPropertyType.Enum:
                        var enumText = Convert.ToString(value, CultureInfo.InvariantCulture);
                        int enumIndex;
                        if (!int.TryParse(enumText, NumberStyles.Integer, CultureInfo.InvariantCulture, out enumIndex)) enumIndex = Array.FindIndex(prop.enumNames, name => string.Equals(name, enumText, StringComparison.OrdinalIgnoreCase));
                        if (enumIndex < 0 || enumIndex >= prop.enumNames.Length) throw new FormatException("Enum value is invalid.");
                        if (!validateOnly) prop.enumValueIndex = enumIndex;
                        return true;
                    default: message = "Unsupported SerializedProperty type: " + prop.propertyType + "."; return false;
                }
            }
            catch (Exception exception) { message = exception.Message; return false; }
        }

        private static string RequireScriptableObjectPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_PATH_INVALID", "ScriptableObject path must be under Assets/ and end with .asset: " + path, "Pass a Unity asset path such as Assets/Data/Config.asset.");
            return path;
        }

        private static Type ResolveScriptableObjectType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_TYPE_REQUIRED", "scriptable-object.create requires --type <ScriptableObjectType>.", "Pass a full type name or a unique class name deriving from ScriptableObject.");
            var type = FindType(typeName);
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException exception) { types = exception.Types; }
                    catch { continue; }
                    foreach (var candidate in types)
                    {
                        if (candidate != null && string.Equals(candidate.Name, typeName, StringComparison.Ordinal)) { type = candidate; break; }
                    }
                    if (type != null) break;
                }
            }
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_TYPE_NOT_FOUND", "ScriptableObject type not found: " + typeName, "Use a type deriving from UnityEngine.ScriptableObject.");
            return type;
        }

        private static ScriptableObject LoadScriptableObject(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null) throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_NOT_FOUND", "ScriptableObject asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:ScriptableObject\"}' to discover the path.");
            return asset;
        }

        // ScriptableObject 结果在内存中保留 typed 结构，外层 response 负责唯一一次序列化。
        private static Dictionary<string, object> ScriptableObjectMutationData(string command, string path, ScriptableObject asset)
        {
            return new Dictionary<string, object>
            {
                { "scriptableObject", ScriptableObjectData(path, asset) },
                { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", command }, { "property", "path" }, { "value", path } } } },
                { "logPath", LogPath() }
            };
        }

        private static Dictionary<string, object> ScriptableObjectData(string path, ScriptableObject asset)
        {
            return new Dictionary<string, object>
            {
                { "path", path }, { "guid", AssetDatabase.AssetPathToGUID(path) }, { "name", asset.name }, { "type", asset.GetType().FullName }, { "properties", ObjectReadablePropertiesData(asset) }
            };
        }

        private static Dictionary<string, object> ObjectReadablePropertiesData(object target)
        {
            var properties = new Dictionary<string, object>();
            foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)) properties[field.Name] = Convert.ToString(field.GetValue(target), CultureInfo.InvariantCulture);
            foreach (var property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                try { properties[property.Name] = Convert.ToString(property.GetValue(target, null), CultureInfo.InvariantCulture); }
                catch { }
            }
            return properties;
        }

        private static Dictionary<string, object> ScriptableObjectTypeData(Type type)
        {
            return new Dictionary<string, object>
            {
                { "name", type.Name }, { "fullName", type.FullName }, { "assembly", type.Assembly.GetName().Name }, { "namespace", type.Namespace }, { "abstract", type.IsAbstract }
            };
        }

        internal static string NormalizeSerializedPropertyPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return rawPath;
            var path = rawPath.Trim();
            if (path.StartsWith("$.", StringComparison.Ordinal)) path = path.Substring(2);
            path = path.Replace("/", ".");
            if (path.IndexOf(".Array.data[", StringComparison.Ordinal) >= 0) return path;
            return Regex.Replace(path, "\\[(\\d+)\\]", ".Array.data[$1]");
        }

        internal sealed class ScriptableObjectPatchResult
        {
            public long Index { get; set; }
            public string PropertyPath { get; set; }
            public string Op { get; set; }
            public bool Ok { get; set; }
            public bool Changed { get; set; }
            public string PropertyType { get; set; }
            public string Message { get; set; }
        }

        private static ScriptableObjectPatchResult BuildScriptableObjectPatchResult(int index, string propertyPath, string op, bool ok, bool changed, string propertyType, string message)
        {
            return new ScriptableObjectPatchResult { Index = index, PropertyPath = propertyPath, Op = op, Ok = ok, Changed = changed, PropertyType = propertyType, Message = message };
        }

        internal static void SetObjectMemberValue(object target, string propertyName, object value)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            var property = target.GetType().GetProperty(propertyName, flags);
            if (property != null && property.CanWrite) { property.SetValue(target, ConvertScriptableObjectValue(value, property.PropertyType), null); return; }
            var field = target.GetType().GetField(propertyName, flags);
            if (field != null) { field.SetValue(target, ConvertScriptableObjectValue(value, field.FieldType)); return; }
            throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_PROPERTY_NOT_FOUND", "Writable ScriptableObject member not found: " + propertyName, "Use a public writable field or property name.");
        }

        // 将 typed 输入转换为 ScriptableObject 公共字段/属性的基础类型。
        private static object ConvertScriptableObjectValue(object value, Type type)
        {
            if (Nullable.GetUnderlyingType(type) != null) { if (value == null) return null; type = Nullable.GetUnderlyingType(type); }
            if (value == null) return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (type == typeof(bool)) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            if (type == typeof(int)) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (type.IsEnum) return Enum.Parse(type, Convert.ToString(value, CultureInfo.InvariantCulture), true);
            throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_VALUE_TYPE_UNSUPPORTED", "Unsupported ScriptableObject member type: " + type.Name, "Use string, bool, numeric, enum, or a dedicated asset/object tool for complex values.");
        }
    }
}
#endif
