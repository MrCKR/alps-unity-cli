#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// object.modify 与 component.modify 共用的 verified SerializedProperty patch 引擎。
        /// 公开契约只允许非空 direct replace，不保留 reflection merge/add/remove 旁路。
        /// </summary>
        internal static class ObjectPatch
        {
            public sealed class PatchResult
            {
                public readonly List<string> ChangedPaths = new List<string>();
            }

            /// <summary>在 effect barrier 前验证每个 path、类型和值均可由 fresh SerializedProperty 回读。</summary>
            public static void ValidateOnly(UnityEngine.Object target, IList pathPatches, object mergePatch)
            {
                if (mergePatch != null || pathPatches == null || pathPatches.Count == 0)
                    throw new BridgeCommandException("E_OBJECT_PATCH_UNSUPPORTED", "Only non-empty pathPatches on directly writable SerializedProperties are supported.", "Use a direct serialized property path and replace operation.");
                var serialized = new SerializedObject(target);
                foreach (var item in pathPatches)
                {
                    var patch = item as Dictionary<string, object>;
                    var path = GetField(patch, "path") as string;
                    var op = (GetField(patch, "op") as string) ?? "replace";
                    if (patch == null || string.IsNullOrEmpty(path) || !string.Equals(op, "replace", StringComparison.OrdinalIgnoreCase) || !HasField(patch, "value"))
                        throw new BridgeCommandException("E_OBJECT_PATCH_UNSUPPORTED", "Each patch must be a direct replace with a value.", "Use { path, op: replace, value }.");
                    var property = serialized.FindProperty(path);
                    if (property == null || !IsVerifiableProperty(property.propertyType))
                        throw new BridgeCommandException("E_OBJECT_PATCH_UNSUPPORTED", "Patch path is not a verifiable SerializedProperty: " + path, "Use a supported direct serialized integer, boolean, float, string, enum, or object-reference property.");
                    ValidateFiniteAndReference(GetField(patch, "value"), property, path);
                }
            }

            /// <summary>barrier 后只执行已验证的 replace；调用者负责 Undo/dirty 与 fresh proof。</summary>
            public static PatchResult ApplyVerifiedSerializedPatches(UnityEngine.Object target, IList pathPatches)
            {
                var result = new PatchResult();
                var serialized = new SerializedObject(target);
                foreach (var item in pathPatches)
                {
                    var patch = item as Dictionary<string, object>;
                    var path = GetField(patch, "path") as string;
                    var value = GetField(patch, "value");
                    var property = serialized.FindProperty(path);
                    switch (property.propertyType)
                    {
                        case SerializedPropertyType.Integer: property.longValue = Convert.ToInt64(value, CultureInfo.InvariantCulture); break;
                        case SerializedPropertyType.Boolean: property.boolValue = ToBool(value); break;
                        case SerializedPropertyType.Float: property.floatValue = ToFloat(value); break;
                        case SerializedPropertyType.String: property.stringValue = Convert.ToString(value, CultureInfo.InvariantCulture); break;
                        case SerializedPropertyType.ObjectReference: property.objectReferenceValue = ResolveUnityObjectValue(value); break;
                        case SerializedPropertyType.Enum: property.enumValueIndex = GetEnumValueIndex(property, value); break;
                        default: throw new BridgeCommandException("E_OBJECT_PATCH_UNSUPPORTED", "Patch type is not writable by the verified patcher: " + property.propertyType, "Use a supported direct property.");
                    }
                    result.ChangedPaths.Add(path);
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return result;
            }

            /// <summary>从 fresh-resolved Unity Object 逐 path 证明最终值，不接受 handler return 作为完成证据。</summary>
            public static void VerifyFreshSerializedProperties(UnityEngine.Object target, IList pathPatches)
            {
                var serialized = new SerializedObject(target);
                serialized.Update();
                foreach (var item in pathPatches)
                {
                    var patch = item as Dictionary<string, object>;
                    var path = GetField(patch, "path") as string;
                    var property = serialized.FindProperty(path);
                    if (property == null || !SerializedValueMatches(property, GetField(patch, "value")))
                        throw new BridgeCommandException("E_OBJECT_MODIFY_STATE_MISMATCH", "Fresh serialized property did not match request: " + path, "Inspect the target and retry.");
                }
            }

            private static bool IsVerifiableProperty(SerializedPropertyType type)
            {
                return type == SerializedPropertyType.Integer || type == SerializedPropertyType.Boolean
                    || type == SerializedPropertyType.Float || type == SerializedPropertyType.String
                    || type == SerializedPropertyType.ObjectReference || type == SerializedPropertyType.Enum;
            }

            private static void ValidateFiniteAndReference(object value, SerializedProperty property, string path)
            {
                var type = property.propertyType;
                if (type == SerializedPropertyType.Float || type == SerializedPropertyType.Integer)
                {
                    double number;
                    if (value == null || !double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number))
                        throw new BridgeCommandException("E_OBJECT_PATCH_VALUE_INVALID", "Patch value must be finite: " + path, "Pass a finite numeric value.");
                }
                if (type == SerializedPropertyType.Enum && GetEnumValueIndex(property, value) < 0)
                    throw new BridgeCommandException("E_OBJECT_PATCH_VALUE_INVALID", "Patch enum value is invalid: " + path, "Pass a declared enum name or index.");
                if (type == SerializedPropertyType.ObjectReference && value != null && ResolveUnityObjectValue(value) == null)
                    throw new BridgeCommandException("E_OBJECT_PATCH_VALUE_INVALID", "Object reference could not be resolved: " + path, "Pass a valid instanceId, assetPath, or guid.");
            }

            private static bool SerializedValueMatches(SerializedProperty property, object value)
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer: return property.longValue == Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Boolean: return property.boolValue == ToBool(value);
                    case SerializedPropertyType.Float: return Mathf.Approximately(property.floatValue, ToFloat(value));
                    case SerializedPropertyType.String: return property.stringValue == Convert.ToString(value, CultureInfo.InvariantCulture);
                    case SerializedPropertyType.ObjectReference: return property.objectReferenceValue == ResolveUnityObjectValue(value);
                    case SerializedPropertyType.Enum: return property.enumValueIndex == GetEnumValueIndex(property, value);
                    default: return false;
                }
            }

            private static int GetEnumValueIndex(SerializedProperty property, object value)
            {
                var enumNames = property.enumNames;
                var enumValue = Convert.ToString(value, CultureInfo.InvariantCulture);
                int index;
                if (int.TryParse(enumValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                    return index >= 0 && index < enumNames.Length ? index : -1;
                for (var i = 0; i < enumNames.Length; i++)
                    if (string.Equals(enumNames[i], enumValue, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }

            /// <summary>对象引用只接受现有对象、instanceId、assetPath、guid 或 asset path 字符串。</summary>
            private static UnityEngine.Object ResolveUnityObjectValue(object value)
            {
                if (value is UnityEngine.Object unityObject) return unityObject;
                var dictionary = value as Dictionary<string, object>;
                if (dictionary != null)
                {
                    object rawInstanceId;
                    if (dictionary.TryGetValue("instanceId", out rawInstanceId))
                    {
                        var instanceId = ToInt(rawInstanceId);
                        if (instanceId != 0) return EditorUtility.InstanceIDToObject(instanceId);
                    }
                    object rawAssetPath;
                    if (dictionary.TryGetValue("assetPath", out rawAssetPath) && rawAssetPath is string assetPath)
                        return AssetDatabase.LoadMainAssetAtPath(assetPath);
                    object rawGuid;
                    if (dictionary.TryGetValue("guid", out rawGuid) && rawGuid is string guid)
                        return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                }
                return value is string path ? AssetDatabase.LoadMainAssetAtPath(path) : null;
            }

            private static bool ToBool(object value)
            {
                if (value is bool boolean) return boolean;
                if (value is string text) return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }

            private static int ToInt(object value)
            {
                if (value is int integer) return integer;
                if (value is long longValue) return checked((int)longValue);
                if (value is double doubleValue) return checked((int)doubleValue);
                return int.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }

            private static float ToFloat(object value)
            {
                if (value is float floatValue) return floatValue;
                if (value is double doubleValue) return (float)doubleValue;
                if (value is int integer) return integer;
                return float.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }

            private static object GetField(Dictionary<string, object> dictionary, string name)
            {
                object value;
                return dictionary != null && dictionary.TryGetValue(name, out value) ? value : null;
            }

            private static bool HasField(Dictionary<string, object> dictionary, string name)
            {
                return dictionary != null && dictionary.ContainsKey(name);
            }
        }
    }
}
#endif
