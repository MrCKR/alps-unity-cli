#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>集中把协议 JSON 值转换为反射调用和 SerializedProperty helper 所需的 CLR/Unity 值。</summary>
    internal static class UnityCliValueConverter
    {
        /// <summary>按目标类型转换一个已反序列化值；不负责对象 patch 导航或写入。</summary>
        internal static object ConvertValue(object value, Type targetType)
        {
            if (targetType == null) throw new ArgumentNullException(nameof(targetType));
            var nullable = Nullable.GetUnderlyingType(targetType);
            if (nullable != null) targetType = nullable;
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;
            if (targetType == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return ToBool(value);
            if (targetType == typeof(byte)) return checked((byte)ToInt64(value));
            if (targetType == typeof(short)) return checked((short)ToInt64(value));
            if (targetType == typeof(int)) return checked((int)ToInt64(value));
            if (targetType == typeof(long)) return ToInt64(value);
            if (targetType == typeof(float)) return ToSingle(value);
            if (targetType == typeof(double)) return ToDouble(value);
            if (targetType.IsEnum) return ToEnum(value, targetType);
            if (targetType == typeof(Vector2)) return ToVector2(value);
            if (targetType == typeof(Vector3)) return ToVector3(value);
            if (targetType == typeof(Vector4)) return ToVector4(value);
            if (targetType == typeof(Color)) return ToColor(value);
            if (targetType == typeof(Quaternion)) return ToQuaternion(value);
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType)) return ResolveUnityObject(value, targetType);
            return value;
        }

        private static object ToEnum(object value, Type enumType)
        {
            if (value is string text) return Enum.Parse(enumType, text, true);
            return Enum.ToObject(enumType, ToInt64(value));
        }

        private static UnityEngine.Object ResolveUnityObject(object value, Type targetType)
        {
            UnityEngine.Object resolved = value as UnityEngine.Object;
            var dictionary = value as Dictionary<string, object>;
            if (resolved == null && dictionary != null)
            {
                if (dictionary.TryGetValue("instanceId", out var rawId)) resolved = EditorUtility.InstanceIDToObject(checked((int)ToInt64(rawId)));
                else if (dictionary.TryGetValue("assetPath", out var rawPath) && rawPath is string assetPath) resolved = AssetDatabase.LoadMainAssetAtPath(assetPath);
                else if (dictionary.TryGetValue("guid", out var rawGuid) && rawGuid is string guid) resolved = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
            }
            if (resolved == null && value is string path) resolved = AssetDatabase.LoadMainAssetAtPath(path);
            return resolved != null && targetType.IsInstanceOfType(resolved) ? resolved : null;
        }

        private static bool ToBool(object value)
        {
            if (value is bool boolean) return boolean;
            if (value is string text) return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static long ToInt64(object value)
        {
            if (value is long longValue) return longValue;
            if (value is int integer) return integer;
            if (value is double doubleValue) return checked((long)doubleValue);
            return long.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static float ToSingle(object value)
        {
            var result = value is float single ? single : Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (float.IsNaN(result) || float.IsInfinity(result)) throw new FormatException("Expected a finite single-precision value.");
            return result;
        }

        private static double ToDouble(object value)
        {
            var result = value is double number ? number : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(result) || double.IsInfinity(result)) throw new FormatException("Expected a finite double-precision value.");
            return result;
        }

        private static Vector2 ToVector2(object value)
        {
            var values = Components(value, new[] { "x", "y" }, new[] { 0f, 0f });
            return new Vector2(values[0], values[1]);
        }

        private static Vector3 ToVector3(object value)
        {
            var values = Components(value, new[] { "x", "y", "z" }, new[] { 0f, 0f, 0f });
            return new Vector3(values[0], values[1], values[2]);
        }

        private static Vector4 ToVector4(object value)
        {
            var values = Components(value, new[] { "x", "y", "z", "w" }, new[] { 0f, 0f, 0f, 0f });
            return new Vector4(values[0], values[1], values[2], values[3]);
        }

        private static Color ToColor(object value)
        {
            var values = Components(value, new[] { "r", "g", "b", "a" }, new[] { 1f, 1f, 1f, 1f });
            return new Color(values[0], values[1], values[2], values[3]);
        }

        private static Quaternion ToQuaternion(object value)
        {
            var values = Components(value, new[] { "x", "y", "z", "w" }, new[] { 0f, 0f, 0f, 1f });
            return new Quaternion(values[0], values[1], values[2], values[3]);
        }

        /// <summary>向量值接受命名对象、数组或逗号分隔字符串，并严格要求完整维度。</summary>
        private static float[] Components(object value, string[] names, float[] defaults)
        {
            var dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
            {
                var result = new float[names.Length];
                for (var index = 0; index < names.Length; index++)
                    result[index] = dictionary.TryGetValue(names[index], out var item) ? ToSingle(item) : defaults[index];
                return result;
            }
            var list = value as IList;
            if (list != null)
            {
                if (list.Count != names.Length) throw new FormatException("Vector value has an unexpected component count.");
                var result = new float[names.Length];
                for (var index = 0; index < names.Length; index++) result[index] = ToSingle(list[index]);
                return result;
            }
            var parts = Convert.ToString(value, CultureInfo.InvariantCulture).Trim().Trim('(', ')', '[', ']').Split(',');
            if (parts.Length != names.Length) throw new FormatException("Vector value has an unexpected component count.");
            var parsed = new float[names.Length];
            for (var index = 0; index < names.Length; index++) parsed[index] = ToSingle(parts[index]);
            return parsed;
        }
    }
}
#endif
