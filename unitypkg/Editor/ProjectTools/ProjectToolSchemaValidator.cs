#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>结构化 schema 失败，路径使用 JSONPath 风格。</summary>
        private sealed class SchemaValidationFailure
        {
            public string Path { get; set; }
            public string Expected { get; set; }
            public string Actual { get; set; }
            public string Message { get; set; }
        }

        /// <summary>验证 Tool 输入；调用方将失败转换为统一 E_SCHEMA_INVALID。</summary>
        private static SchemaValidationFailure ValidateProjectToolInput(RegisteredTool definition, string inputJson)
        {
            return definition.OfficialAttributeBinding != null
                ? ValidateOfficialAttributeInput(definition.OfficialAttributeBinding, inputJson)
                : ValidateTypedInput(definition.RequestType, inputJson);
        }

        /// <summary>验证 Tool 输出；官方 attribute 的任意 JSON fallback 不伪造 CLR schema。</summary>
        private static SchemaValidationFailure ValidateProjectToolOutput(RegisteredTool definition, string outputJson)
        {
            if (definition.OfficialAttributeBinding == null) return ValidateTypedInput(definition.ResultType, outputJson);
            if (definition.ResultType == typeof(object)) return null;
            if (definition.ResultType == typeof(void))
            {
                object value;
                try { value = UnityCliSerializer.Parse(outputJson); }
                catch (Exception exception) { return Failure("$", "null", "malformed JSON", exception.Message); }
                return value == null ? null : Failure("$", "null", ActualType(value));
            }
            return ValidateTypedInput(definition.ResultType, outputJson);
        }

        /// <summary>按 Request CLR 类型递归验证 JSON，不依赖手写 schema 文本。</summary>
        private static SchemaValidationFailure ValidateTypedInput(Type requestType, string inputJson)
        {
            object value;
            try
            {
                value = UnityCliSerializer.Parse(inputJson);
            }
            catch (Exception exception) when (exception is FormatException || exception is OverflowException)
            {
                return Failure("$", "valid JSON", "malformed JSON", exception.Message);
            }
            return ValidateValue(value, requestType, "$", null);
        }

        /// <summary>构造带稳定 details.path/expected/actual 的 Bridge schema 错误。</summary>
        private static BridgeCommandException SchemaValidationException(SchemaValidationFailure failure)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                failure.Message,
                new Dictionary<string, object>
                {
                    { "path", failure.Path },
                    { "expected", failure.Expected },
                    { "actual", failure.Actual }
                },
                new[]
                {
                    "Run unity_cli tools describe <tool> or unity_cli tools describe <tool> to inspect inputSchema.",
                    "Retry with JSON matching the reported path, expected type, and required fields."
                },
                false);
        }

        /// <summary>递归检查 primitive、enum、集合、dictionary、DTO 和显式 AnyJson。</summary>
        private static SchemaValidationFailure ValidateValue(object value, Type rawType, string path, MemberInfo member)
        {
            var anyJson = member != null && member.IsDefined(typeof(UnityCliAnyJsonAttribute), true);
            if (anyJson && value != null)
            {
                Type anyDictionaryValue;
                if (UnityCliSchemaDeriver.TryDictionaryValueType(rawType, out anyDictionaryValue))
                    return value is IDictionary<string, object> ? null : Failure(path, "object", ActualType(value));
                Type anyElement;
                if (UnityCliSchemaDeriver.TryElementType(rawType, out anyElement))
                    return value is IList ? null : Failure(path, "array", ActualType(value));
                return null;
            }

            var nullableType = Nullable.GetUnderlyingType(rawType);
            if (value == null)
            {
                if (member != null && member.IsDefined(typeof(UnityCliRequiredAttribute), true))
                    return Failure(path, "non-null required value", "null");
                if (nullableType != null || !rawType.IsValueType) return null;
                return Failure(path, ExpectedType(rawType), "null");
            }
            var type = nullableType ?? rawType;
            if (type == typeof(object))
                return Failure(path, "explicit [UnityCliAnyJson]", ActualType(value), "Arbitrary JSON requires [UnityCliAnyJson].");

            if (type == typeof(string))
            {
                var text = value as string;
                if (text == null) return Failure(path, "string", ActualType(value));
                var stringEnum = member == null ? null : member.GetCustomAttributes(typeof(UnityCliStringEnumAttribute), true).Cast<UnityCliStringEnumAttribute>().SingleOrDefault();
                return stringEnum == null || stringEnum.Values.Contains(text, StringComparer.Ordinal)
                    ? null
                    : Failure(path, "enum(" + string.Join("|", stringEnum.Values) + ")", ActualValue(value));
            }
            if (type == typeof(char))
            {
                var character = value as string;
                return character != null && character.Length == 1
                    ? null
                    : Failure(path, "single-character string", ActualValue(value));
            }
            if (type == typeof(bool)) return value is bool ? null : Failure(path, "boolean", ActualType(value));
            if (UnityCliSchemaDeriver.IsInteger(type)) return ValidateInteger(value, type, path);
            if (UnityCliSchemaDeriver.IsNumber(type)) return ValidateNumber(value, type, path);
            if (type.IsEnum)
            {
                var text = value as string;
                return text != null && Enum.GetNames(type).Contains(text, StringComparer.Ordinal)
                    ? null
                    : Failure(path, "enum(" + string.Join("|", Enum.GetNames(type)) + ")", ActualValue(value));
            }

            Type dictionaryValueType;
            if (UnityCliSchemaDeriver.TryDictionaryValueType(type, out dictionaryValueType))
            {
                var dictionary = value as IDictionary<string, object>;
                if (dictionary == null) return Failure(path, "object", ActualType(value));
                foreach (var pair in dictionary)
                {
                    var failure = ValidateValue(pair.Value, dictionaryValueType, path + "." + pair.Key, null);
                    if (failure != null) return failure;
                }
                return null;
            }

            Type elementType;
            if (UnityCliSchemaDeriver.TryElementType(type, out elementType))
            {
                var items = value as IList;
                if (items == null) return Failure(path, "array", ActualType(value));
                for (var index = 0; index < items.Count; index++)
                {
                    var failure = ValidateValue(items[index], elementType, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", null);
                    if (failure != null) return failure;
                }
                return null;
            }

            var objectValue = value as IDictionary<string, object>;
            if (objectValue == null) return Failure(path, "object", ActualType(value));
            var members = UnityCliSchemaDeriver.SerializableMembers(type);
            var known = members.ToDictionary(item => UnityCliSchemaDeriver.ToCamelCase(item.Name), item => item, StringComparer.Ordinal);
            foreach (var key in objectValue.Keys)
            {
                if (!known.ContainsKey(key)) return Failure(path + "." + key, "known property", "unknown property");
            }
            foreach (var pair in known)
            {
                object child;
                if (!objectValue.TryGetValue(pair.Key, out child))
                {
                    if (pair.Value.IsDefined(typeof(UnityCliRequiredAttribute), true) || pair.Value.IsDefined(typeof(UnityCliPresenceRequiredAttribute), true))
                        return Failure(path + "." + pair.Key, "required property", "missing");
                    continue;
                }
                var failure = ValidateValue(child, UnityCliSchemaDeriver.MemberType(pair.Value), path + "." + pair.Key, pair.Value);
                if (failure != null) return failure;
            }
            return null;
        }

        /// <summary>验证 JSON integer 并检查目标 CLR 整数范围。</summary>
        private static SchemaValidationFailure ValidateInteger(object value, Type type, string path)
        {
            decimal number;
            if (value is long) number = (long)value;
            else if (value is decimal) number = (decimal)value;
            else return Failure(path, ExpectedType(type), ActualType(value));
            if (decimal.Truncate(number) != number) return Failure(path, ExpectedType(type), ActualValue(value));

            decimal minimum;
            decimal maximum;
            UnityCliSchemaDeriver.IntegerRange(type, out minimum, out maximum);
            return number < minimum || number > maximum
                ? Failure(path, ExpectedType(type) + " in range " + minimum.ToString(CultureInfo.InvariantCulture) + ".." + maximum.ToString(CultureInfo.InvariantCulture), ActualValue(value))
                : null;
        }

        /// <summary>验证 JSON number，并拒绝非有限值和目标类型 overflow。</summary>
        private static SchemaValidationFailure ValidateNumber(object value, Type type, string path)
        {
            double number;
            try { number = Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return Failure(path, ExpectedType(type), ActualType(value)); }
            if (double.IsNaN(number) || double.IsInfinity(number)) return Failure(path, "finite " + ExpectedType(type), ActualValue(value));
            if (type == typeof(float) && (number < -float.MaxValue || number > float.MaxValue))
                return Failure(path, "number in float range", ActualValue(value));
            try
            {
                if (type == typeof(decimal)) Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                return Failure(path, "number in decimal range", ActualValue(value));
            }
            return null;
        }

        private static SchemaValidationFailure Failure(string path, string expected, string actual, string message = null)
        {
            return new SchemaValidationFailure
            {
                Path = path,
                Expected = expected,
                Actual = actual,
                Message = message ?? "Input schema validation failed at " + path + ": expected " + expected + ", received " + actual + "."
            };
        }

        private static string ExpectedType(Type type)
        {
            if (UnityCliSchemaDeriver.IsInteger(type)) return type.Name + " integer";
            if (UnityCliSchemaDeriver.IsNumber(type)) return type.Name + " number";
            return type.Name;
        }

        private static string ActualType(object value)
        {
            if (value == null) return "null";
            if (value is string) return "string";
            if (value is bool) return "boolean";
            if (value is long) return "integer";
            if (value is decimal)
                return decimal.Truncate((decimal)value) == (decimal)value ? "integer" : "number";
            if (value is double || value is float) return "number";
            if (value is IList) return "array";
            if (value is IDictionary<string, object>) return "object";
            return value.GetType().Name;
        }

        private static string ActualValue(object value)
        {
            return value == null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
#endif
