#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace UnityCli
{
    /// <summary>
    /// schema 派生器:从 C# Request/Result 类型反射派生 JSON Schema。
    ///
    /// 设计依据:.plan/自定义工具定义规范.md 第四节(schema 派生:类型即真相)
    /// - required 修饰(C# required 属性)→ schema required
    /// - 可空(? / Nullable)→ 可选,描述 proto3 null 省略语义
    /// - string → {"type":"string"}; number(double/float/int/long) → {"type":"number"} 或 integer
    /// - bool → {"type":"boolean"}
    /// - 嵌套 DTO → {"type":"object","properties":{...}}
    /// - 数组/List → {"type":"array","items":{...}}
    /// - Describe 无法染指 schema,单一真相 = 类型
    /// </summary>
    internal static class UnityCliSchemaDeriver
    {
        /// <summary>派生一个类型的 JSON Schema(根对象形式)。</summary>
        public static string DeriveSchema(Type type)
        {
            var builder = new StringBuilder();
            WriteTypeSchema(builder, type, new HashSet<Type>(TypeIdentityComparer.Instance));
            return builder.ToString();
        }

        // 写一个类型的 schema。ancestors 用于检测递归引用(避免无限嵌套)。
        private static void WriteTypeSchema(StringBuilder builder, Type type, HashSet<Type> ancestors)
        {
            if (type == null || type == typeof(object))
            {
                builder.Append("{}");
                return;
            }

            // 可空值类型:取底层
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) type = underlying;

            // 字符串
            if (type == typeof(string)) { builder.Append("{\"type\":\"string\"}"); return; }
            // 布尔
            if (type == typeof(bool)) { builder.Append("{\"type\":\"boolean\"}"); return; }
            // 整数
            if (type == typeof(int) || type == typeof(long)) { builder.Append("{\"type\":\"integer\"}"); return; }
            // 数字
            if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) { builder.Append("{\"type\":\"number\"}"); return; }

            // 数组/List<T>
            Type elementType = null;
            if (type.IsArray) elementType = type.GetElementType();
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) elementType = type.GetGenericArguments()[0];
            else if (typeof(System.Collections.IList).IsAssignableFrom(type) && type.IsGenericType) elementType = type.GetGenericArguments()[0];

            if (elementType != null)
            {
                builder.Append("{\"type\":\"array\",\"items\":");
                WriteTypeSchema(builder, elementType, ancestors);
                builder.Append('}');
                return;
            }

            // 嵌套 DTO:写 object + properties + required
            WriteObjectSchema(builder, type, ancestors);
        }

        // 写对象 schema:反射 public 属性
        private static void WriteObjectSchema(StringBuilder builder, Type type, HashSet<Type> ancestors)
        {
            // 递归引用保护
            if (ancestors.Contains(type))
            {
                builder.Append("{}");
                return;
            }
            ancestors.Add(type);

            builder.Append("{\"type\":\"object\",\"properties\":{");
            var first = true;
            var required = new List<string>();
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in properties)
            {
                if (!prop.CanRead) continue;
                if (!first) builder.Append(',');
                first = false;

                var camelName = ToCamelCase(prop.Name);
                WriteString(builder, camelName);
                builder.Append(':');

                var propType = prop.PropertyType;
                var isRequired = IsRequired(prop);
                var isNullable = IsNullable(propType, prop);

                WriteTypeSchema(builder, propType, ancestors);

                if (isRequired) required.Add(camelName);
            }

            builder.Append('}');

            if (required.Count > 0)
            {
                builder.Append(",\"required\":[");
                for (var i = 0; i < required.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    WriteString(builder, required[i]);
                }
                builder.Append(']');
            }

            builder.Append('}');
            ancestors.Remove(type);
        }

        // C# required 修饰符(required 关键字)→ schema 必填
        private static bool IsRequired(PropertyInfo prop)
        {
            // Unity 2022.3 默认 C# 9.0 不支持 required 关键字,改用 [UnityCliRequired] 特性显式标记。
            // 必填判定:属性带 [UnityCliRequired] 特性 → required。
            return prop.IsDefined(typeof(UnityCliRequiredAttribute), true);
        }

        // 可空:Nullable<T> → 可空。引用类型默认可空(开发者可用 [UnityCliRequired] 标记必填);值类型非 Nullable → 非可空。
        private static bool IsNullable(Type type, PropertyInfo prop)
        {
            if (Nullable.GetUnderlyingType(type) != null) return true;
            if (!type.IsValueType) return true;
            return false;
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.Length == 1) return name.ToLowerInvariant();
            if (char.IsLower(name[0])) return name;
            var chars = name.ToCharArray();
            chars[0] = char.ToLowerInvariant(chars[0]);
            return new string(chars);
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            builder.Append(value);
            builder.Append('"');
        }

        // 类型比较器(按 Type 本身)
        private sealed class TypeIdentityComparer : IEqualityComparer<Type>
        {
            public static readonly TypeIdentityComparer Instance = new TypeIdentityComparer();
            public bool Equals(Type x, Type y) { return ReferenceEquals(x, y); }
            public int GetHashCode(Type obj) { return obj == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
#endif
