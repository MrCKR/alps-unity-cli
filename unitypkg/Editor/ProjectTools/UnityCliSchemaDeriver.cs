#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// 从 C# Request/Result 类型派生严格 JSON Schema。未知动态类型必须显式标记 AnyJson。
    /// </summary>
    internal static class UnityCliSchemaDeriver
    {
        /// <summary>派生根类型 schema；递归 DTO 通过 $defs/$ref 表达。</summary>
        public static string DeriveSchema(Type type)
        {
            if (type == null) throw new ArgumentNullException("type");
            var definitions = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var schema = BuildSchema(type, "$", definitions, null, true);
            var root = schema as IDictionary<string, object>;
            if (root != null && definitions.Count > 0)
            {
                root["$defs"] = definitions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }
            return UnityCliSerializer.Serialize(schema);
        }

        /// <summary>按 CLR 类型递归构造 schema；不支持类型立即抛错，禁止回退为空 schema。</summary>
        private static object BuildSchema(Type rawType, string path, SortedDictionary<string, object> definitions, MemberInfo member, bool inlineRoot)
        {
            var anyJson = member != null && member.IsDefined(typeof(UnityCliAnyJsonAttribute), true);
            if (rawType == null) throw Unsupported(path, "<null>");
            if (anyJson)
            {
                Type anyDictionaryValue;
                if (TryDictionaryValueType(rawType, out anyDictionaryValue))
                    return new Dictionary<string, object> { { "type", "object" }, { "additionalProperties", AnyJsonSchema() } };
                Type anyElement;
                if (TryElementType(rawType, out anyElement))
                    return new Dictionary<string, object> { { "type", "array" }, { "items", AnyJsonSchema() } };
                return AnyJsonSchema();
            }

            var nullableType = Nullable.GetUnderlyingType(rawType);
            if (nullableType != null)
            {
                if (member != null && member.IsDefined(typeof(UnityCliRequiredAttribute), true))
                    return BuildSchema(nullableType, path, definitions, null, false);
                return new Dictionary<string, object>
                {
                    { "oneOf", new object[] { BuildSchema(nullableType, path, definitions, null, false), new Dictionary<string, object> { { "type", "null" } } } }
                };
            }

            if (rawType == typeof(object)) throw Unsupported(path, rawType.FullName);
            if (rawType == typeof(string) || rawType == typeof(char)) return TypeSchema("string");
            if (rawType == typeof(bool)) return TypeSchema("boolean");
            if (IsInteger(rawType)) return IntegerSchema(rawType);
            if (IsNumber(rawType)) return TypeSchema("number");
            if (rawType.IsEnum)
            {
                return new Dictionary<string, object>
                {
                    { "type", "string" },
                    { "enum", Enum.GetNames(rawType) }
                };
            }

            Type dictionaryValueType;
            if (TryDictionaryValueType(rawType, out dictionaryValueType))
            {
                return new Dictionary<string, object>
                {
                    { "type", "object" },
                    { "additionalProperties", BuildSchema(dictionaryValueType, path + ".*", definitions, member, false) }
                };
            }

            Type elementType;
            if (TryElementType(rawType, out elementType))
            {
                return new Dictionary<string, object>
                {
                    { "type", "array" },
                    { "items", BuildSchema(elementType, path + "[]", definitions, member, false) }
                };
            }

            if (rawType.IsPointer || rawType.IsByRef || rawType.IsGenericTypeDefinition || rawType.IsInterface || rawType.IsAbstract)
                throw Unsupported(path, rawType.FullName);

            if (inlineRoot) return BuildObjectSchema(rawType, path, definitions);

            var definitionName = DefinitionName(rawType);
            if (!definitions.ContainsKey(definitionName))
            {
                definitions[definitionName] = new Dictionary<string, object>();
                definitions[definitionName] = BuildObjectSchema(rawType, path, definitions);
            }
            return new Dictionary<string, object> { { "$ref", "#/$defs/" + EscapeJsonPointer(definitionName) } };
        }

        /// <summary>派生 DTO 的 properties/required，并默认拒绝未知字段。</summary>
        private static Dictionary<string, object> BuildObjectSchema(Type type, string path, SortedDictionary<string, object> definitions)
        {
            if (!type.IsValueType && type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null)
                throw Unsupported(path, type.FullName + " (DTO requires a parameterless constructor)");
            var properties = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var required = new List<string>();
            var members = SerializableMembers(type);
            if (members.Count == 0 && (type.Namespace == "System" || (type.Namespace != null && type.Namespace.StartsWith("System.", StringComparison.Ordinal))))
                throw Unsupported(path, type.FullName + " (framework type has no serializable protocol members)");
            foreach (var member in members)
            {
                var name = ToCamelCase(member.Name);
                properties[name] = BuildSchema(MemberType(member), path + "." + name, definitions, member, false);
                if (member.IsDefined(typeof(UnityCliRequiredAttribute), true)) required.Add(name);
            }

            var schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) },
                { "additionalProperties", false }
            };
            if (required.Count > 0) schema["required"] = required.ToArray();
            return schema;
        }

        /// <summary>返回可参与协议的 public instance 属性和字段。</summary>
        internal static IReadOnlyList<MemberInfo> SerializableMembers(Type type)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                .Cast<MemberInfo>();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(field => !field.IsStatic && !field.IsInitOnly)
                .Cast<MemberInfo>();
            return properties.Concat(fields).OrderBy(member => ToCamelCase(member.Name), StringComparer.Ordinal).ToArray();
        }

        /// <summary>读取协议成员的 CLR 类型。</summary>
        internal static Type MemberType(MemberInfo member)
        {
            var property = member as PropertyInfo;
            if (property != null) return property.PropertyType;
            var field = member as FieldInfo;
            if (field != null) return field.FieldType;
            throw new ArgumentException("Unsupported member: " + member, "member");
        }

        /// <summary>设置 DTO 属性或字段值。</summary>
        internal static void SetMemberValue(MemberInfo member, object target, object value)
        {
            var property = member as PropertyInfo;
            if (property != null)
            {
                if (!property.CanWrite) return;
                property.SetValue(target, value, null);
                return;
            }
            var field = member as FieldInfo;
            if (field != null) field.SetValue(target, value);
        }

        /// <summary>读取 DTO 属性或字段值。</summary>
        internal static object GetMemberValue(MemberInfo member, object target)
        {
            var property = member as PropertyInfo;
            if (property != null) return property.GetValue(target, null);
            return ((FieldInfo)member).GetValue(target);
        }

        internal static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.Length == 1) return name.ToLowerInvariant();
            if (char.IsLower(name[0])) return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        internal static bool TryDictionaryValueType(Type type, out Type valueType)
        {
            valueType = null;
            var candidate = DictionaryInterface(type);
            if (candidate == null) return false;
            var arguments = candidate.GetGenericArguments();
            if (arguments[0] != typeof(string)) throw Unsupported("$", type.FullName + " (dictionary key must be string)");
            valueType = arguments[1];
            var concreteType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
            if (!type.IsAssignableFrom(concreteType))
                throw Unsupported("$", type.FullName + " (dictionary cannot be assigned from Dictionary<string, TValue>)");
            return true;
        }

        internal static bool TryElementType(Type type, out Type elementType)
        {
            elementType = null;
            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return true;
            }
            if (type == typeof(string) || TryDictionaryValueType(type, out _)) return false;
            var enumerable = type.GetInterfaces().Concat(new[] { type })
                .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerable == null) return false;
            elementType = enumerable.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            if (!type.IsAssignableFrom(listType))
                throw Unsupported("$", type.FullName + " (collection cannot be assigned from List<T>)");
            return true;
        }

        internal static bool IsInteger(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
        }

        internal static bool IsNumber(Type type)
        {
            return type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        private static Type DictionaryInterface(Type type)
        {
            return type.GetInterfaces().Concat(new[] { type })
                .FirstOrDefault(candidate => candidate.IsGenericType &&
                    (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        }

        private static Dictionary<string, object> TypeSchema(string type)
        {
            return new Dictionary<string, object> { { "type", type } };
        }

        private static Dictionary<string, object> IntegerSchema(Type type)
        {
            decimal minimum;
            decimal maximum;
            IntegerRange(type, out minimum, out maximum);
            return new Dictionary<string, object>
            {
                { "type", "integer" },
                { "minimum", minimum },
                { "maximum", maximum }
            };
        }

        internal static void IntegerRange(Type type, out decimal minimum, out decimal maximum)
        {
            if (type == typeof(byte)) { minimum = byte.MinValue; maximum = byte.MaxValue; return; }
            if (type == typeof(sbyte)) { minimum = sbyte.MinValue; maximum = sbyte.MaxValue; return; }
            if (type == typeof(short)) { minimum = short.MinValue; maximum = short.MaxValue; return; }
            if (type == typeof(ushort)) { minimum = ushort.MinValue; maximum = ushort.MaxValue; return; }
            if (type == typeof(int)) { minimum = int.MinValue; maximum = int.MaxValue; return; }
            if (type == typeof(uint)) { minimum = uint.MinValue; maximum = uint.MaxValue; return; }
            if (type == typeof(long)) { minimum = long.MinValue; maximum = long.MaxValue; return; }
            minimum = ulong.MinValue;
            maximum = ulong.MaxValue;
        }

        private static Dictionary<string, object> AnyJsonSchema()
        {
            return new Dictionary<string, object> { { "x-unityCliAnyJson", true } };
        }

        private static string DefinitionName(Type type)
        {
            return (type.FullName ?? type.Name).Replace('+', '.');
        }

        private static string EscapeJsonPointer(string value)
        {
            return value.Replace("~", "~0").Replace("/", "~1");
        }

        private static InvalidOperationException Unsupported(string path, string type)
        {
            return new InvalidOperationException("Unsupported schema type at " + path + ": " + type + ". Add [UnityCliAnyJson] only when the field intentionally accepts arbitrary JSON.");
        }
    }
}
#endif
