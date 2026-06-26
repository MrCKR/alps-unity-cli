#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterScriptableObjectCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "scriptable-object.create", "scriptable-object", true, ScriptableObjectCreate);
            RegisterCommand(registry, "scriptable-object.info", "scriptable-object", false, ScriptableObjectInfo);
            RegisterCommand(registry, "scriptable-object.modify", "scriptable-object", true, ScriptableObjectModify);
        }

        private static string ScriptableObjectCreate(BridgeRequest request)
        {
            EnsureEditorNotBusy("scriptable-object.create");
            var path = RequireScriptableObjectPath(request.StringArg("path"));
            var type = ResolveScriptableObjectType(request.StringArg("type"));
            var overwrite = request.BoolArg("overwrite", false);
            if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(path) != null && !overwrite)
            {
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_EXISTS", "ScriptableObject asset already exists: " + path, "Pass --overwrite or choose a different Assets/*.asset path.");
            }
            CreateAssetDirectory(path);
            if (overwrite && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
            {
                AssetDatabase.DeleteAsset(path);
            }
            var asset = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            return SuccessJson(ScriptableObjectMutationJson("scriptable-object.create", path, asset));
        }

        private static string ScriptableObjectInfo(BridgeRequest request)
        {
            var path = RequireScriptableObjectPath(request.StringArg("path") ?? request.StringArg("target"));
            var asset = LoadScriptableObject(path);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("scriptableObject", ScriptableObjectJson(path, asset))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ScriptableObjectModify(BridgeRequest request)
        {
            EnsureEditorNotBusy("scriptable-object.modify");
            var path = RequireScriptableObjectPath(request.StringArg("path") ?? request.StringArg("target"));
            var asset = LoadScriptableObject(path);
            var property = request.StringArg("property");
            if (string.IsNullOrEmpty(property))
            {
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_PROPERTY_REQUIRED", "scriptable-object.modify requires --property <name>.", "Pass a public writable field or property name.");
            }
            Undo.RecordObject(asset, "unity_cli tools run scriptable-object.modify");
            SetObjectMemberValue(asset, property, request.StringArg("value"));
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return SuccessJson(ScriptableObjectMutationJson("scriptable-object.modify", path, asset));
        }

        private static string RequireScriptableObjectPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_PATH_INVALID", "ScriptableObject path must be under Assets/ and end with .asset: " + path, "Pass a Unity asset path such as Assets/Data/Config.asset.");
            }
            return path;
        }

        private static Type ResolveScriptableObjectType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_TYPE_REQUIRED", "scriptable-object.create requires --type <ScriptableObjectType>.", "Pass a full type name or a unique class name deriving from ScriptableObject.");
            }
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
                        if (candidate != null && string.Equals(candidate.Name, typeName, StringComparison.Ordinal))
                        {
                            type = candidate;
                            break;
                        }
                    }
                    if (type != null) break;
                }
            }
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
            {
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_TYPE_NOT_FOUND", "ScriptableObject type not found: " + typeName, "Use a type deriving from UnityEngine.ScriptableObject.");
            }
            return type;
        }

        private static ScriptableObject LoadScriptableObject(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null)
            {
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_NOT_FOUND", "ScriptableObject asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:ScriptableObject\"}' to discover the path.");
            }
            return asset;
        }

        private static string ScriptableObjectMutationJson(string command, string path, ScriptableObject asset)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("scriptableObject", ScriptableObjectJson(path, asset))
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson(command, "path", Quote(path))))
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }

        private static string ScriptableObjectJson(string path, ScriptableObject asset)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", asset.name)
                .Prop("type", asset.GetType().FullName)
                .PropRaw("properties", ObjectReadablePropertiesJson(asset))
                .EndObject().ToString();
        }

        private static string ObjectReadablePropertiesJson(object target)
        {
            var writer = new UnityCliJsonWriter().BeginObject();
            foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                writer.Prop(field.Name, Convert.ToString(field.GetValue(target), CultureInfo.InvariantCulture));
            }
            foreach (var property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                try { writer.Prop(property.Name, Convert.ToString(property.GetValue(target, null), CultureInfo.InvariantCulture)); }
                catch { }
            }
            return writer.EndObject().ToString();
        }

        private static void SetObjectMemberValue(object target, string propertyName, string value)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            var property = target.GetType().GetProperty(propertyName, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, ConvertScriptableObjectValue(value, property.PropertyType), null);
                return;
            }
            var field = target.GetType().GetField(propertyName, flags);
            if (field != null)
            {
                field.SetValue(target, ConvertScriptableObjectValue(value, field.FieldType));
                return;
            }
            throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_PROPERTY_NOT_FOUND", "Writable ScriptableObject member not found: " + propertyName, "Use a public writable field or property name.");
        }

        // 将 CLI 字符串输入转换为 ScriptableObject 公共字段/属性的基础类型。
        private static object ConvertScriptableObjectValue(string value, Type type)
        {
            if (Nullable.GetUnderlyingType(type) != null)
            {
                if (string.IsNullOrEmpty(value)) return null;
                type = Nullable.GetUnderlyingType(type);
            }
            if (type == typeof(string)) return value;
            if (type == typeof(bool)) return bool.Parse(value);
            if (type == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (type.IsEnum) return Enum.Parse(type, value, true);
            throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_VALUE_TYPE_UNSUPPORTED", "Unsupported ScriptableObject member type: " + type.Name, "Use string, bool, numeric, enum, or a dedicated asset/object tool for complex values.");
        }
    }
}
#endif
