#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// debug 值读取的安全投影 owner。
    ///
    /// 与事件入库投影的区别：本投影绝不执行用户代码，因此不调用任意 ToString、不执行属性 getter、
    /// 不运行用户序列化器，也不遍历用户实现的 IEnumerable。
    /// 规则：
    /// - 只物化已知安全的值：基元、string、enum、decimal、日期时间、Guid、Type 元数据、Unity 对象身份，
    ///   以及框架自有的数组、IList 与 IDictionary；
    /// - 其它引用类型只报告身份（object:&lt;类型全名&gt;），不序列化它的对象图；
    /// - 深度、每层条目与字符串字节复用 DebugLimits 的既有投影上限。
    /// </summary>
    internal static class DebugValueProjection
    {
        /// <summary>投影结果类别：有界投影、仅身份、Unity 对象身份。</summary>
        internal const string Bounded = "bounded-projection";
        internal const string IdentityOnly = "identity-only";
        internal const string UnityObjectIdentity = "unity-object-identity";

        /// <summary>投影说明；公开文本与实现共用同一来源。</summary>
        internal const string Note =
            "The value is a bounded copy materialised without running user code: no property getter, no arbitrary ToString, "
            + "no user serializer and no user enumerator. Reference types outside the safe set report their declared type only.";

        /// <summary>安全投影；返回投影类别与是否触发截断。</summary>
        internal static object Project(object value, out string projection, out bool truncated)
        {
            truncated = false;
            projection = CategoryOf(value);
            var sink = Bounded;
            return ProjectValue(value, 0, ref truncated, ref sink);
        }

        /// <summary>只关心结果值的投影；类别与截断事实由调用方另行读取。</summary>
        internal static object ProjectValueOnly(object value)
        {
            return Project(value, out _, out _);
        }

        /// <summary>投影类别只描述被读取的叶值本身；容器内部的仅身份元素在值里显式可见。</summary>
        private static string CategoryOf(object value)
        {
            if (value == null) return Bounded;
            if (value is UnityEngine.Object) return UnityObjectIdentity;
            if (value is Array) return Bounded;
            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is Enum || value is decimal || value is DateTime || value is TimeSpan || value is Guid || value is Type) return Bounded;
            if ((value is IDictionary || value is IList) && IsFrameworkContainer(type)) return Bounded;
            return IdentityOnly;
        }

        /// <summary>嵌套元素的投影走独立的类别账本，避免覆盖叶值的投影类别。</summary>
        private static object Nested(object value, int depth, ref bool truncated)
        {
            var sink = Bounded;
            return ProjectValue(value, depth, ref truncated, ref sink);
        }

        private static object ProjectValue(object value, int depth, ref bool truncated, ref string projection)
        {
            if (value == null) return null;
            var type = value.GetType();
            if (value is bool || value is int || value is long) return value;
            if (value is byte || value is sbyte || value is short || value is ushort || value is uint || value is ulong)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is char) return ((char)value).ToString();
            if (value is string) return BoundedString((string)value, ref truncated);
            if (value is float) return BoundedNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture), ref truncated);
            if (value is double) return BoundedNumber((double)value, ref truncated);
            if (value is decimal) return BoundedNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture), ref truncated);
            if (value is Enum) return EnumName((Enum)value);
            if (value is DateTime) return ((DateTime)value).ToString("O", CultureInfo.InvariantCulture);
            if (value is TimeSpan) return ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture);
            if (value is Guid) return ((Guid)value).ToString("D");
            if (value is Type) return TypeName((Type)value);

            var unityObject = value as UnityEngine.Object;
            if (unityObject != null)
            {
                projection = UnityObjectIdentity;
                return UnityObjectIdentityOf(unityObject);
            }

            if (depth >= DebugLimits.MaxProjectionDepth)
            {
                truncated = true;
                projection = IdentityOnly;
                return IdentityMarker(type);
            }

            var array = value as Array;
            if (array != null)
            {
                if (array.Rank != 1)
                {
                    projection = IdentityOnly;
                    return IdentityMarker(type);
                }
                var items = new List<object>();
                for (var index = 0; index < array.Length; index++)
                {
                    if (items.Count >= DebugLimits.MaxProjectionItems)
                    {
                        truncated = true;
                        break;
                    }
                    items.Add(Nested(array.GetValue(index), depth + 1, ref truncated));
                }
                return items;
            }

            if (!IsFrameworkContainer(type))
            {
                projection = IdentityOnly;
                return IdentityMarker(type);
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var entries = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entries.Count >= DebugLimits.MaxProjectionItems)
                    {
                        truncated = true;
                        break;
                    }
                    var key = entry.Key as string;
                    if (key == null)
                    {
                        truncated = true;
                        continue;
                    }
                    entries[key] = Nested(entry.Value, depth + 1, ref truncated);
                }
                return entries;
            }

            var list = value as IList;
            if (list != null)
            {
                var items = new List<object>();
                for (var index = 0; index < list.Count; index++)
                {
                    if (items.Count >= DebugLimits.MaxProjectionItems)
                    {
                        truncated = true;
                        break;
                    }
                    items.Add(Nested(list[index], depth + 1, ref truncated));
                }
                return items;
            }

            projection = IdentityOnly;
            return IdentityMarker(type);
        }

        /// <summary>
        /// 只接受框架自有的容器类型，避免通过用户 IEnumerable/IList 实现执行用户代码。
        /// </summary>
        internal static bool IsFrameworkContainer(Type type)
        {
            if (type == null) return false;
            var assembly = type.Assembly;
            if (assembly == typeof(List<>).Assembly || assembly == typeof(Dictionary<,>).Assembly) return true;
            var name = assembly.GetName().Name;
            return string.Equals(name, "mscorlib", StringComparison.Ordinal)
                || string.Equals(name, "System.Private.CoreLib", StringComparison.Ordinal)
                || string.Equals(name, "System.Core", StringComparison.Ordinal)
                || string.Equals(name, "System.Collections", StringComparison.Ordinal)
                || string.Equals(name, "netstandard", StringComparison.Ordinal);
        }

        /// <summary>Unity 对象身份投影：只读 instanceId、类型与（主线程可证明时）对象名。</summary>
        internal static Dictionary<string, object> UnityObjectIdentityOf(UnityEngine.Object value)
        {
            var identity = new Dictionary<string, object>
            {
                { "type", TypeName(value.GetType()) },
                { "instanceId", (long)value.GetInstanceID() }
            };
            // 对象名是 Unity 主线程 API；无法证明主线程身份时不读取它，也不回填旧值。
            identity["name"] = DebugEventClocks.IsCurrentThreadMainThread() ? value.name : string.Empty;
            identity["nameSource"] = DebugEventClocks.IsCurrentThreadMainThread() ? "main-thread-unity-api" : "not-read-off-main-thread";
            return identity;
        }

        /// <summary>仅身份的占位投影；它明确没有序列化对象图。</summary>
        private static Dictionary<string, object> IdentityMarker(Type type)
        {
            return new Dictionary<string, object>
            {
                { "object", TypeName(type) },
                { "suppressed", "no-user-code-projection" }
            };
        }

        /// <summary>枚举只取元数据名称，不走用户重写的 ToString。</summary>
        private static object EnumName(Enum value)
        {
            var name = Enum.GetName(value.GetType(), value);
            return name ?? Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        private static string BoundedString(string value, ref bool truncated)
        {
            var bounded = UnityCliDebugValueBounds.BoundText(value, DebugLimits.MaxProjectionStringBytes);
            if (bounded.Length != value.Length) truncated = true;
            return bounded;
        }

        /// <summary>非有限浮点值不是 JSON 数字，显式转成名称而不是伪造数值。</summary>
        private static object BoundedNumber(double value, ref bool truncated)
        {
            if (double.IsNaN(value))
            {
                truncated = true;
                return "NaN";
            }
            if (double.IsInfinity(value))
            {
                truncated = true;
                return value > 0d ? "Infinity" : "-Infinity";
            }
            return value;
        }

        private static string TypeName(Type type)
        {
            return type == null ? string.Empty : type.FullName ?? type.Name;
        }
    }
}
#endif
