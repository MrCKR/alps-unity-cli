#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
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
            RegisterCommand(registry, "scriptable-object.list-types", "scriptable-object", false, ScriptableObjectListTypes);
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
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "scriptable-object.create")
                    .Prop("dryRun", true)
                    .Prop("path", path)
                    .Prop("type", type.FullName)
                    .Prop("overwrite", overwrite)
                    .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("scriptable-object.create", "path", Quote(path))))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
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
            var patchesJson = request.RawJsonArg("patches");
            if (!string.IsNullOrEmpty(patchesJson))
            {
                return ScriptableObjectModifyPatches(request, path, asset, patchesJson);
            }
            var property = request.StringArg("property");
            if (string.IsNullOrEmpty(property))
            {
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_PROPERTY_REQUIRED", "scriptable-object.modify requires --property <name> or patches[].", "Pass a public writable field/property name, or patches with SerializedObject propertyPath/value entries.");
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("dryRun", true)
                    .PropRaw("scriptableObject", ScriptableObjectJson(path, asset))
                    .PropArray("plannedEffects", arr => arr.RawValue(EffectJson("scriptable-object.modify", property, JsonLiteralOrString(request.StringArg("value")))))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            Undo.RecordObject(asset, "unity_cli tools run scriptable-object.modify");
            SetObjectMemberValue(asset, property, request.StringArg("value"));
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return SuccessJson(ScriptableObjectMutationJson("scriptable-object.modify", path, asset));
        }

        // 列出当前 Editor domain 中可创建的 ScriptableObject 类型，供 AI 在 create 前自发现类型名。
        private static string ScriptableObjectListTypes(BridgeRequest request)
        {
            var namePattern = request.StringArg("namePattern") ?? request.StringArg("name-pattern") ?? request.StringArg("query");
            var assemblyPattern = request.StringArg("assemblyPattern") ?? request.StringArg("assembly-pattern");
            var includeAbstract = request.BoolArg("includeAbstract", false);
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 50), 200));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrEmpty(assemblyPattern) && assembly.GetName().Name.IndexOf(assemblyPattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Type[] candidates;
                try { candidates = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { candidates = exception.Types; }
                catch { continue; }
                foreach (var candidate in candidates)
                {
                    if (candidate == null || candidate == typeof(ScriptableObject) || !typeof(ScriptableObject).IsAssignableFrom(candidate)) continue;
                    if (!includeAbstract && candidate.IsAbstract) continue;
                    if (!string.IsNullOrEmpty(namePattern) && candidate.FullName.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0 && candidate.Name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    types.Add(candidate);
                }
            }
            types.Sort(delegate(Type left, Type right) { return string.Compare(left.FullName, right.FullName, StringComparison.Ordinal); });
            var total = types.Count;
            var end = Math.Min(total, cursor + pageSize);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("count", (long)total)
                .PropArray("items", arr =>
                {
                    for (var i = cursor; i < end; i++) arr.RawValue(ScriptableObjectTypeJson(types[i]));
                })
                .PropObject("pageInfo", page => page.Prop("pageSize", (long)pageSize).Prop("cursor", cursor.ToString(CultureInfo.InvariantCulture)).Prop("nextCursor", end < total ? end.ToString(CultureInfo.InvariantCulture) : null).Prop("total", (long)total))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // 通过 Unity SerializedObject 批量验证并应用 patches；任一 patch 失败时不落盘。
        private static string ScriptableObjectModifyPatches(BridgeRequest request, string path, ScriptableObject asset, string patchesJson)
        {
            var patchItems = JsonArrayElements(patchesJson);
            if (patchItems.Count == 0)
            {
                throw new BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCHES_INVALID", "patches must be a non-empty JSON array.", "Pass patches like [{\"propertyPath\":\"fieldName\",\"value\":123}].");
            }
            var so = new SerializedObject(asset);
            so.Update();
            var results = new List<string>();
            var hasFailure = false;
            var changed = false;
            if (!request.DryRun) Undo.RecordObject(asset, "unity_cli tools run scriptable-object.modify patches");
            for (var i = 0; i < patchItems.Count; i++)
            {
                bool patchOk;
                bool patchChanged;
                var result = ApplyScriptableObjectPatch(so, patchItems[i], i, request.DryRun, out patchOk, out patchChanged);
                results.Add(result);
                if (!patchOk) hasFailure = true;
                if (patchChanged) changed = true;
            }
            if (hasFailure)
            {
                return ErrorJsonWithDetails("E_SCRIPTABLE_OBJECT_PATCH_FAILED", "One or more ScriptableObject patches could not be applied.", new UnityCliJsonWriter().BeginObject().PropArray("patchResults", arr => { foreach (var result in results) arr.RawValue(result); }).EndObject().ToString(), "Fix failed patch propertyPath/value entries and retry; no changes were applied.");
            }
            if (!request.DryRun && changed)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "scriptable-object.modify")
                .Prop("dryRun", request.DryRun)
                .Prop("changed", changed && !request.DryRun)
                .PropRaw("scriptableObject", ScriptableObjectJson(path, asset))
                .PropArray("patchResults", arr => { foreach (var result in results) arr.RawValue(result); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        // 验证或写入单个 SerializedProperty patch，支持基础值、Unity 值类型和资产引用。
        private static string ApplyScriptableObjectPatch(SerializedObject so, string patchJson, int index, bool dryRun, out bool ok, out bool changed)
        {
            ok = false;
            changed = false;
            if (string.IsNullOrEmpty(patchJson) || !patchJson.TrimStart().StartsWith("{"))
            {
                return ScriptableObjectPatchResultJson(index, null, null, false, false, null, "Patch must be a JSON object.");
            }
            var rawPath = JsonStringProperty(patchJson, "propertyPath") ?? JsonStringProperty(patchJson, "property_path") ?? JsonStringProperty(patchJson, "path");
            var op = (JsonStringProperty(patchJson, "op") ?? "set").Trim().ToLowerInvariant();
            var propertyPath = NormalizeSerializedPropertyPath(rawPath);
            if (string.IsNullOrEmpty(propertyPath))
            {
                return ScriptableObjectPatchResultJson(index, rawPath, op, false, false, null, "Missing required field: propertyPath.");
            }
            if (op == "array_resize")
            {
                var arrayProp = so.FindProperty(propertyPath.EndsWith(".Array.size", StringComparison.Ordinal) ? propertyPath.Substring(0, propertyPath.Length - ".Array.size".Length) : propertyPath);
                var sizeRaw = JsonRawProperty(patchJson, "value");
                int size;
                if (arrayProp == null || !arrayProp.isArray)
                {
                    return ScriptableObjectPatchResultJson(index, propertyPath, op, false, false, null, "Property is not an array.");
                }
                if (!TryParseJsonInt(sizeRaw, out size) || size < 0)
                {
                    return ScriptableObjectPatchResultJson(index, propertyPath, op, false, false, arrayProp.propertyType.ToString(), "array_resize requires non-negative integer value.");
                }
                ok = true;
                changed = arrayProp.arraySize != size;
                if (!dryRun) arrayProp.arraySize = size;
                return ScriptableObjectPatchResultJson(index, propertyPath, op, true, changed, arrayProp.propertyType.ToString(), dryRun ? "Validated array resize." : "Array resize applied.");
            }
            if (op != "set")
            {
                return ScriptableObjectPatchResultJson(index, propertyPath, op, false, false, null, "Unsupported patch op: " + op + ".");
            }
            var prop = so.FindProperty(propertyPath);
            if (prop == null)
            {
                return ScriptableObjectPatchResultJson(index, propertyPath, op, false, false, null, "Property not found.");
            }
            var valueRaw = JsonRawProperty(patchJson, "value");
            string message;
            if (!dryRun && !SetSerializedPropertyValue(prop, valueRaw, out message))
            {
                return ScriptableObjectPatchResultJson(index, propertyPath, op, false, false, prop.propertyType.ToString(), message);
            }
            if (dryRun && !CanSetSerializedPropertyValue(prop, valueRaw, out message))
            {
                return ScriptableObjectPatchResultJson(index, propertyPath, op, false, false, prop.propertyType.ToString(), message);
            }
            ok = true;
            changed = true;
            return ScriptableObjectPatchResultJson(index, propertyPath, op, true, true, prop.propertyType.ToString(), dryRun ? "Validated set patch." : "Set patch applied.");
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

        // 输出 ScriptableObject 类型摘要，避免返回完整反射树造成上下文膨胀。
        private static string ScriptableObjectTypeJson(Type type)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", type.Name)
                .Prop("fullName", type.FullName)
                .Prop("assembly", type.Assembly.GetName().Name)
                .Prop("namespace", type.Namespace)
                .Prop("abstract", type.IsAbstract)
                .EndObject().ToString();
        }

        // 解析 patches[]，让 SerializedObject 修改可以接收结构化 JSON 数组。
        private static List<string> JsonArrayElements(string json)
        {
            var items = new List<string>();
            if (string.IsNullOrEmpty(json)) return items;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return items;
            var index = 1;
            while (index < trimmed.Length - 1)
            {
                while (index < trimmed.Length - 1 && (char.IsWhiteSpace(trimmed[index]) || trimmed[index] == ',')) index++;
                if (index >= trimmed.Length - 1) break;
                var end = FindJsonValueEnd(trimmed, index);
                if (end <= index) break;
                items.Add(trimmed.Substring(index, end - index).Trim());
                index = end;
            }
            return items;
        }

        private static string NormalizeSerializedPropertyPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return rawPath;
            var path = rawPath.Trim();
            if (path.StartsWith("$.", StringComparison.Ordinal)) path = path.Substring(2);
            path = path.Replace("/", ".");
            if (path.IndexOf(".Array.data[", StringComparison.Ordinal) >= 0) return path;
            return Regex.Replace(path, "\\[(\\d+)\\]", ".Array.data[$1]");
        }

        private static string ScriptableObjectPatchResultJson(int index, string propertyPath, string op, bool ok, bool changed, string propertyType, string message)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("index", (long)index)
                .Prop("propertyPath", propertyPath)
                .Prop("op", op)
                .Prop("ok", ok)
                .Prop("changed", changed)
                .Prop("propertyType", propertyType)
                .Prop("message", message)
                .EndObject().ToString();
        }

        // dry-run 分支只验证值格式，不修改 SerializedObject 缓存。
        private static bool CanSetSerializedPropertyValue(SerializedProperty prop, string valueRaw, out string message)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    int intValue;
                    return TryValidate(TryParseJsonInt(valueRaw, out intValue), "Expected integer value.", out message);
                case SerializedPropertyType.Boolean:
                    bool boolValue;
                    return TryValidate(TryParseJsonBool(valueRaw, out boolValue), "Expected boolean value.", out message);
                case SerializedPropertyType.Float:
                    float floatValue;
                    return TryValidate(TryParseJsonFloat(valueRaw, out floatValue), "Expected numeric value.", out message);
                case SerializedPropertyType.String:
                    message = null;
                    return true;
                case SerializedPropertyType.Color:
                    Color color;
                    return TryValidate(TryParseColorJson(valueRaw, out color), "Expected color as #RRGGBB/#RRGGBBAA, comma string, or {r,g,b,a}.", out message);
                case SerializedPropertyType.ObjectReference:
                    UnityEngine.Object reference;
                    return TryValidate(TryResolveObjectReference(valueRaw, out reference), "Expected object reference as null, asset path, guid, or {path|guid|ref}.", out message);
                case SerializedPropertyType.Enum:
                    int enumIndex;
                    return TryValidate(TryParseEnumIndex(prop, valueRaw, out enumIndex), "Expected enum name or enum index.", out message);
                case SerializedPropertyType.Vector2:
                    Vector2 vector2;
                    return TryValidate(TryParseVector2Json(valueRaw, out vector2), "Expected Vector2 as comma string or {x,y}.", out message);
                case SerializedPropertyType.Vector3:
                    Vector3 vector3;
                    return TryValidate(TryParseVector3Json(valueRaw, out vector3), "Expected Vector3 as comma string or {x,y,z}.", out message);
                case SerializedPropertyType.Vector4:
                    Vector4 vector4;
                    return TryValidate(TryParseVector4Json(valueRaw, out vector4), "Expected Vector4 as comma string or {x,y,z,w}.", out message);
                case SerializedPropertyType.Quaternion:
                    Vector4 quaternion;
                    return TryValidate(TryParseVector4Json(valueRaw, out quaternion), "Expected Quaternion as comma string or {x,y,z,w}.", out message);
                default:
                    message = "Unsupported SerializedProperty type: " + prop.propertyType + ".";
                    return false;
            }
        }

        // 将 JSON patch 值写入 Unity SerializedProperty，保持类型转换集中在一个入口。
        private static bool SetSerializedPropertyValue(SerializedProperty prop, string valueRaw, out string message)
        {
            message = null;
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    int intValue;
                    if (!TryParseJsonInt(valueRaw, out intValue)) { message = "Expected integer value."; return false; }
                    prop.intValue = intValue;
                    return true;
                case SerializedPropertyType.Boolean:
                    bool boolValue;
                    if (!TryParseJsonBool(valueRaw, out boolValue)) { message = "Expected boolean value."; return false; }
                    prop.boolValue = boolValue;
                    return true;
                case SerializedPropertyType.Float:
                    float floatValue;
                    if (!TryParseJsonFloat(valueRaw, out floatValue)) { message = "Expected numeric value."; return false; }
                    prop.floatValue = floatValue;
                    return true;
                case SerializedPropertyType.String:
                    prop.stringValue = JsonRawToString(valueRaw);
                    return true;
                case SerializedPropertyType.Color:
                    Color color;
                    if (!TryParseColorJson(valueRaw, out color)) { message = "Expected color as #RRGGBB/#RRGGBBAA, comma string, or {r,g,b,a}."; return false; }
                    prop.colorValue = color;
                    return true;
                case SerializedPropertyType.ObjectReference:
                    UnityEngine.Object reference;
                    if (!TryResolveObjectReference(valueRaw, out reference)) { message = "Expected object reference as null, asset path, guid, or {path|guid|ref}."; return false; }
                    prop.objectReferenceValue = reference;
                    return true;
                case SerializedPropertyType.Enum:
                    int enumIndex;
                    if (!TryParseEnumIndex(prop, valueRaw, out enumIndex)) { message = "Expected enum name or enum index."; return false; }
                    prop.enumValueIndex = enumIndex;
                    return true;
                case SerializedPropertyType.Vector2:
                    Vector2 vector2;
                    if (!TryParseVector2Json(valueRaw, out vector2)) { message = "Expected Vector2 as comma string or {x,y}."; return false; }
                    prop.vector2Value = vector2;
                    return true;
                case SerializedPropertyType.Vector3:
                    Vector3 vector3;
                    if (!TryParseVector3Json(valueRaw, out vector3)) { message = "Expected Vector3 as comma string or {x,y,z}."; return false; }
                    prop.vector3Value = vector3;
                    return true;
                case SerializedPropertyType.Vector4:
                    Vector4 vector4;
                    if (!TryParseVector4Json(valueRaw, out vector4)) { message = "Expected Vector4 as comma string or {x,y,z,w}."; return false; }
                    prop.vector4Value = vector4;
                    return true;
                case SerializedPropertyType.Quaternion:
                    Vector4 quaternion;
                    if (!TryParseVector4Json(valueRaw, out quaternion)) { message = "Expected Quaternion as comma string or {x,y,z,w}."; return false; }
                    prop.quaternionValue = new Quaternion(quaternion.x, quaternion.y, quaternion.z, quaternion.w);
                    return true;
                default:
                    message = "Unsupported SerializedProperty type: " + prop.propertyType + ".";
                    return false;
            }
        }

        private static bool TryValidate(bool ok, string failureMessage, out string message)
        {
            message = ok ? null : failureMessage;
            return ok;
        }

        private static string JsonRawToString(string raw)
        {
            if (raw == null || raw.Trim() == "null") return null;
            var trimmed = raw.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == (char)34 && trimmed[trimmed.Length - 1] == (char)34)
            {
                return JsonUnescape(trimmed.Substring(1, trimmed.Length - 2));
            }
            return trimmed;
        }

        private static bool TryParseJsonBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(raw)) return false;
            var text = JsonRawToString(raw);
            return bool.TryParse(text, out value);
        }

        private static bool TryParseJsonInt(string raw, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            return int.TryParse(JsonRawToString(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseJsonFloat(string raw, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(raw)) return false;
            return float.TryParse(JsonRawToString(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseEnumIndex(SerializedProperty prop, string raw, out int value)
        {
            value = 0;
            if (TryParseJsonInt(raw, out value)) return value >= 0 && value < prop.enumNames.Length;
            var text = JsonRawToString(raw);
            if (string.IsNullOrEmpty(text)) return false;
            for (var i = 0; i < prop.enumNames.Length; i++)
            {
                if (string.Equals(prop.enumNames[i], text, StringComparison.OrdinalIgnoreCase) || string.Equals(prop.enumDisplayNames[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    value = i;
                    return true;
                }
            }
            return false;
        }

        private static bool TryParseColorJson(string raw, out Color color)
        {
            color = Color.clear;
            var text = JsonRawToString(raw);
            if (!string.IsNullOrEmpty(text) && ColorUtility.TryParseHtmlString(text.StartsWith("#", StringComparison.Ordinal) ? text : "#" + text, out color)) return true;
            float r;
            float g;
            float b;
            float a;
            if (TryParseFloatMember(raw, "r", out r) && TryParseFloatMember(raw, "g", out g) && TryParseFloatMember(raw, "b", out b))
            {
                if (!TryParseFloatMember(raw, "a", out a)) a = 1f;
                color = new Color(r, g, b, a);
                return true;
            }
            var parts = string.IsNullOrEmpty(text) ? new string[0] : text.Split(',');
            if (parts.Length == 3 || parts.Length == 4)
            {
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r) && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g) && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                {
                    a = 1f;
                    if (parts.Length == 4 && !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a)) return false;
                    color = new Color(r, g, b, a);
                    return true;
                }
            }
            return false;
        }

        private static bool TryParseVector2Json(string raw, out Vector2 value)
        {
            Vector4 vector;
            var ok = TryParseVectorJson(raw, 2, out vector);
            value = new Vector2(vector.x, vector.y);
            return ok;
        }

        private static bool TryParseVector3Json(string raw, out Vector3 value)
        {
            Vector4 vector;
            var ok = TryParseVectorJson(raw, 3, out vector);
            value = new Vector3(vector.x, vector.y, vector.z);
            return ok;
        }

        private static bool TryParseVector4Json(string raw, out Vector4 value)
        {
            return TryParseVectorJson(raw, 4, out value);
        }

        private static bool TryParseVectorJson(string raw, int dimensions, out Vector4 value)
        {
            value = Vector4.zero;
            float x;
            float y;
            float z;
            float w;
            if (TryParseFloatMember(raw, "x", out x) && TryParseFloatMember(raw, "y", out y))
            {
                z = 0f;
                w = dimensions == 4 ? 1f : 0f;
                if (dimensions >= 3 && !TryParseFloatMember(raw, "z", out z)) return false;
                if (dimensions >= 4 && !TryParseFloatMember(raw, "w", out w)) return false;
                value = new Vector4(x, y, z, w);
                return true;
            }
            var text = JsonRawToString(raw);
            var parts = string.IsNullOrEmpty(text) ? new string[0] : text.Split(',');
            if (parts.Length != dimensions) return false;
            var values = new float[dimensions];
            for (var i = 0; i < dimensions; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return false;
            }
            value = new Vector4(values[0], values[1], dimensions >= 3 ? values[2] : 0f, dimensions >= 4 ? values[3] : 0f);
            return true;
        }

        private static bool TryParseFloatMember(string json, string name, out float value)
        {
            value = 0f;
            var raw = JsonRawProperty(json, name);
            return TryParseJsonFloat(raw, out value);
        }

        private static bool TryResolveObjectReference(string raw, out UnityEngine.Object reference)
        {
            reference = null;
            if (raw == null || raw.Trim() == "null") return true;
            var candidate = raw.Trim();
            var nested = JsonRawProperty(candidate, "ref") ?? JsonRawProperty(candidate, "value");
            if (!string.IsNullOrEmpty(nested)) candidate = nested;
            var path = JsonStringProperty(candidate, "path") ?? JsonStringProperty(candidate, "assetPath");
            var guid = JsonStringProperty(candidate, "guid");
            if (!string.IsNullOrEmpty(guid)) path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) path = JsonRawToString(candidate);
            if (string.IsNullOrEmpty(path)) return false;
            path = NormalizeAssetPath(path);
            reference = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            return reference != null;
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
