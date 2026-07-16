#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace UnityCli
{
    /// <summary>
    /// 反射驱动的协议序列化器(CLI 投递的隐藏桥接 helper)。
    ///
    /// - Serialize(object):反射 public 属性 → camelCase JSON;null/可空默认值省略(proto3 语义);必填值类型始终输出。
    /// - Deserialize&lt;T&gt;(string):JSON → token tree → 反射填充 T。
    /// - 序列化、反序列化与 canonical hash 共用同一套严格 JSON 语义。
    /// - CLR public 仅供独立 Extension assembly 复用，不进入 describe/help/docs/公开 CLI 契约。
    /// </summary>
    public static class UnityCliSerializer
    {
        // ─── 序列化 ────────────────────────────────────────────────────────

        /// <summary>
        /// 将对象序列化为 camelCase JSON。null/可空默认值字段省略(proto3),
        /// 必填值类型(bool/double 等)即使为默认值也输出(保证 ok:false 等在线上)。
        /// </summary>
        public static string Serialize(object value)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            var type = value.GetType();

            // 基本类型和 enum 直接输出。
            if (type == typeof(string) || type == typeof(char)) { WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (type == typeof(bool)) { builder.Append(((bool)value) ? "true" : "false"); return; }
            if (type.IsEnum) { WriteString(builder, Enum.GetName(type, value)); return; }
            if (UnityCliSchemaDeriver.IsInteger(type)) { builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            if (type == typeof(decimal)) { builder.Append(((decimal)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (type == typeof(double) || type == typeof(float)) { WriteNumber(builder, Convert.ToDouble(value, CultureInfo.InvariantCulture)); return; }

            // 字典必须先于 IEnumerable 处理。
            if (value is IDictionary dict)
            {
                builder.Append('{');
                var entries = new List<DictionaryEntry>();
                foreach (DictionaryEntry entry in dict) entries.Add(entry);
                entries.Sort((left, right) => string.CompareOrdinal(Convert.ToString(left.Key, CultureInfo.InvariantCulture), Convert.ToString(right.Key, CultureInfo.InvariantCulture)));
                for (var index = 0; index < entries.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    WriteString(builder, Convert.ToString(entries[index].Key, CultureInfo.InvariantCulture));
                    builder.Append(':');
                    WriteValue(builder, entries[index].Value);
                }
                builder.Append('}');
                return;
            }

            if (value is IEnumerable enumerable)
            {
                builder.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteValue(builder, item);
                }
                builder.Append(']');
                return;
            }

            WriteObject(builder, value, type);
        }

        // 写对象：属性和字段共用 schema deriver 的协议成员集合，null 按 proto3 语义省略。
        private static void WriteObject(StringBuilder builder, object value, Type type)
        {
            builder.Append('{');
            var first = true;
            foreach (var member in UnityCliSchemaDeriver.SerializableMembers(type))
            {
                var memberValue = UnityCliSchemaDeriver.GetMemberValue(member, value);
                if (memberValue == null) continue;
                if (!first) builder.Append(',');
                first = false;
                WriteString(builder, ToCamelCase(member.Name));
                builder.Append(':');
                WriteValue(builder, memberValue);
            }
            builder.Append('}');
        }

        // PascalCase → camelCase:仅首字母小写(对生成 DTO 的标准 PascalCase 足够)。
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
            if (value != null)
            {
                foreach (var ch in value)
                {
                    switch (ch)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (ch < 0x20) { builder.Append("\\u"); builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture)); }
                            else { builder.Append(ch); }
                            break;
                    }
                }
            }
            builder.Append('"');
        }

        private static void WriteNumber(StringBuilder builder, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException("JSON does not support non-finite numbers.");
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        // ─── 反序列化 ──────────────────────────────────────────────────────

        /// <summary>
        /// 将 JSON 严格解析为 token tree，再反射填充 T 的 public 协议成员。
        /// </summary>
        public static T Deserialize<T>(string json)
        {
            return (T)Deserialize(json, typeof(T));
        }

        /// <summary>非泛型重载:用运行时 Type 反序列化(供 registry 在泛型类型参数为 Type 变量时使用)。</summary>
        public static object Deserialize(string json, Type targetType)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("JSON payload is empty.");
            var index = 0;
            var token = ParseToken(json, ref index);
            if (token == null) throw new FormatException("JSON payload is malformed.");
            SkipWhitespace(json, ref index);
            if (index != json.Length) throw new FormatException("JSON payload contains trailing tokens.");
            return ConvertToken(token, targetType);
        }

        public static object Parse(string json)
        {
            return Deserialize(json, typeof(object));
        }

        public static string Canonicalize(object value)
        {
            var builder = new StringBuilder();
            WriteCanonicalValue(builder, value);
            return builder.ToString();
        }

        public static string Sha256Canonical(object value)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(Canonicalize(value)));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var valueByte in bytes) builder.Append(valueByte.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        // ─── token tree ───────────────────────────────────────────────────

        private enum TokenType { Null, True, False, Number, String, Object, Array }

        private sealed class Token
        {
            public TokenType Type;
            public string StringValue;          // String/Number 原始字面
            public Dictionary<string, Token> ObjectValue;
            public List<Token> ArrayValue;
        }

        private static Token ParseToken(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length) return null;
            var ch = json[index];
            if (ch == '"') return ParseStringToken(json, ref index);
            if (ch == '{') return ParseObjectToken(json, ref index);
            if (ch == '[') return ParseArrayToken(json, ref index);
            if (ch == '-' || char.IsDigit(ch)) return ParseNumberToken(json, ref index);
            if (MatchLiteral(json, ref index, "true")) return new Token { Type = TokenType.True };
            if (MatchLiteral(json, ref index, "false")) return new Token { Type = TokenType.False };
            if (MatchLiteral(json, ref index, "null")) return new Token { Type = TokenType.Null };
            return null;
        }

        private static Token ParseStringToken(string json, ref int index)
        {
            index++; // 跳过开头 "
            var sb = new StringBuilder();
            while (index < json.Length)
            {
                var ch = json[index++];
                if (ch == '"') return new Token { Type = TokenType.String, StringValue = sb.ToString() };
                if (ch < 0x20) throw new FormatException("JSON string contains an unescaped control character.");
                if (ch == '\\')
                {
                    if (index >= json.Length) throw new FormatException("JSON string ends with an incomplete escape.");
                    var esc = json[index++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (index + 4 > json.Length) throw new FormatException("JSON unicode escape is incomplete.");
                            var hex = json.Substring(index, 4);
                            int codePoint;
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codePoint)) throw new FormatException("JSON unicode escape is invalid.");
                            sb.Append((char)codePoint);
                            index += 4;
                            break;
                        default: throw new FormatException("JSON escape is invalid: \\" + esc + ".");
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            throw new FormatException("JSON string is not terminated.");
        }

        private static Token ParseNumberToken(string json, ref int index)
        {
            var start = index;
            if (json[index] == '-') index++;
            if (index >= json.Length) throw new FormatException("JSON number is incomplete.");
            if (json[index] == '0')
            {
                index++;
                if (index < json.Length && json[index] >= '0' && json[index] <= '9')
                    throw new FormatException("JSON number cannot contain a leading zero.");
            }
            else
            {
                if (json[index] < '1' || json[index] > '9') throw new FormatException("JSON number is malformed.");
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
            }
            if (index < json.Length && json[index] == '.')
            {
                index++;
                var fractionStart = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index == fractionStart) throw new FormatException("JSON number fraction requires a digit.");
            }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                var exponentStart = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index == exponentStart) throw new FormatException("JSON number exponent requires a digit.");
            }
            return new Token { Type = TokenType.Number, StringValue = json.Substring(start, index - start) };
        }

        private static Token ParseObjectToken(string json, ref int index)
        {
            index++; // {
            var obj = new Dictionary<string, Token>(StringComparer.Ordinal);
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == '}') { index++; return new Token { Type = TokenType.Object, ObjectValue = obj }; }
            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"') throw new FormatException("JSON object key must be a string.");
                var keyToken = ParseStringToken(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') throw new FormatException("JSON object key must be followed by ':'.");
                index++;
                var valueToken = ParseToken(json, ref index);
                if (valueToken == null) throw new FormatException("JSON object value is malformed.");
                if (obj.ContainsKey(keyToken.StringValue)) throw new FormatException("JSON object contains duplicate field: " + keyToken.StringValue + ".");
                obj.Add(keyToken.StringValue, valueToken);
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index < json.Length && json[index] == '}') { index++; return new Token { Type = TokenType.Object, ObjectValue = obj }; }
                throw new FormatException("JSON object is missing ',' or '}'.");
            }
            throw new FormatException("JSON object is not terminated.");
        }

        private static Token ParseArrayToken(string json, ref int index)
        {
            index++; // [
            var arr = new List<Token>();
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == ']') { index++; return new Token { Type = TokenType.Array, ArrayValue = arr }; }
            while (index < json.Length)
            {
                var valueToken = ParseToken(json, ref index);
                if (valueToken == null) throw new FormatException("JSON array value is malformed.");
                arr.Add(valueToken);
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index < json.Length && json[index] == ']') { index++; return new Token { Type = TokenType.Array, ArrayValue = arr }; }
                throw new FormatException("JSON array is missing ',' or ']'.");
            }
            throw new FormatException("JSON array is not terminated.");
        }

        private static bool MatchLiteral(string json, ref int index, string literal)
        {
            if (index + literal.Length > json.Length) return false;
            if (string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0) return false;
            index += literal.Length;
            return true;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length)
            {
                var ch = json[index];
                if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n') break;
                index++;
            }
        }

        // ─── token → 目标类型 ─────────────────────────────────────────────

        private static object ConvertToken(Token token, Type targetType)
        {
            if (token == null) throw new FormatException("JSON value is malformed.");
            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (token.Type == TokenType.Null)
            {
                if (nullableType != null || !targetType.IsValueType) return null;
                throw new FormatException("JSON null is not valid for " + targetType.Name + ".");
            }
            if (targetType == typeof(object)) return TokenToObject(token);

            var effectiveType = nullableType ?? targetType;
            if (effectiveType == typeof(string) || effectiveType == typeof(char))
            {
                if (token.Type != TokenType.String) throw new FormatException("Expected JSON string.");
                if (effectiveType == typeof(char) && token.StringValue.Length != 1) throw new FormatException("Expected a single-character JSON string.");
                return effectiveType == typeof(char) ? (object)token.StringValue[0] : token.StringValue;
            }
            if (effectiveType == typeof(bool))
            {
                if (token.Type != TokenType.True && token.Type != TokenType.False) throw new FormatException("Expected JSON boolean.");
                return token.Type == TokenType.True;
            }
            if (effectiveType.IsEnum)
            {
                if (token.Type != TokenType.String || !Enum.IsDefined(effectiveType, token.StringValue)) throw new FormatException("Expected enum value for " + effectiveType.Name + ".");
                return Enum.Parse(effectiveType, token.StringValue, false);
            }
            if (UnityCliSchemaDeriver.IsInteger(effectiveType) || UnityCliSchemaDeriver.IsNumber(effectiveType))
                return ConvertNumberToken(token, effectiveType);

            Type dictionaryValueType;
            if (UnityCliSchemaDeriver.TryDictionaryValueType(effectiveType, out dictionaryValueType))
            {
                if (token.Type != TokenType.Object) throw new FormatException("Expected JSON object for " + effectiveType.Name + ".");
                return FillDictionary(token, effectiveType, dictionaryValueType);
            }

            Type elementType;
            if (UnityCliSchemaDeriver.TryElementType(effectiveType, out elementType))
            {
                if (token.Type != TokenType.Array) throw new FormatException("Expected JSON array for " + effectiveType.Name + ".");
                return FillList(token, effectiveType, elementType);
            }

            if (token.Type != TokenType.Object) throw new FormatException("Expected JSON object for " + effectiveType.Name + ".");
            return FillObject(token, effectiveType);
        }

        /// <summary>将 number token 严格转换到目标 CLR 数字类型并检测 overflow。</summary>
        private static object ConvertNumberToken(Token token, Type type)
        {
            if (token.Type != TokenType.Number) throw new FormatException("Expected JSON number.");
            try
            {
                if (type == typeof(byte)) return byte.Parse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (type == typeof(sbyte)) return sbyte.Parse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (type == typeof(short)) return short.Parse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (type == typeof(ushort)) return ushort.Parse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (type == typeof(int)) return int.Parse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (type == typeof(uint)) return uint.Parse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (type == typeof(long)) return long.Parse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (type == typeof(ulong)) return ulong.Parse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (type == typeof(decimal)) return decimal.Parse(token.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (type == typeof(float))
                {
                    var single = float.Parse(token.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (float.IsNaN(single) || float.IsInfinity(single)) throw new OverflowException();
                    return single;
                }
                var number = double.Parse(token.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) throw new OverflowException();
                return number;
            }
            catch (Exception exception) when (exception is FormatException || exception is OverflowException)
            {
                throw new FormatException("JSON number is outside the target type range.", exception);
            }
        }

        /// <summary>填充严格 DTO；未知字段和缺失 required 字段直接失败。</summary>
        private static object FillObject(Token token, Type type)
        {
            var instance = Activator.CreateInstance(type, true);
            var members = UnityCliSchemaDeriver.SerializableMembers(type);
            var known = members.ToDictionary(member => ToCamelCase(member.Name), member => member, StringComparer.Ordinal);
            foreach (var field in token.ObjectValue.Keys)
            {
                if (!known.ContainsKey(field)) throw new FormatException("Unknown JSON field for " + type.Name + ": " + field + ".");
            }
            foreach (var pair in known)
            {
                Token child;
                if (!token.ObjectValue.TryGetValue(pair.Key, out child))
                {
                    if (pair.Value.IsDefined(typeof(UnityCliRequiredAttribute), true)) throw new FormatException("Missing required JSON field for " + type.Name + ": " + pair.Key + ".");
                    continue;
                }
                if (child.Type == TokenType.Null && pair.Value.IsDefined(typeof(UnityCliRequiredAttribute), true))
                    throw new FormatException("Required JSON field cannot be null for " + type.Name + ": " + pair.Key + ".");
                UnityCliSchemaDeriver.SetMemberValue(pair.Value, instance, ConvertToken(child, UnityCliSchemaDeriver.MemberType(pair.Value)));
            }
            return instance;
        }

        /// <summary>填充 array/list/interface collection，元素继续递归强类型转换。</summary>
        private static object FillList(Token token, Type type, Type elementType)
        {
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (IList)Activator.CreateInstance(listType);
            foreach (var item in token.ArrayValue) list.Add(ConvertToken(item, elementType));
            if (!type.IsArray) return list;
            var array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }

        /// <summary>填充 string-key dictionary，value 按声明类型递归转换。</summary>
        private static object FillDictionary(Token token, Type type, Type valueType)
        {
            var concreteType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
            var dictionary = (IDictionary)Activator.CreateInstance(concreteType);
            foreach (var pair in token.ObjectValue) dictionary.Add(pair.Key, ConvertToken(pair.Value, valueType));
            return dictionary;
        }

        private static object TokenToObject(Token token)
        {
            switch (token.Type)
            {
                case TokenType.Null: return null;
                case TokenType.True: return true;
                case TokenType.False: return false;
                case TokenType.String: return token.StringValue;
                case TokenType.Number:
                    long integer;
                    if (long.TryParse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
                    decimal largeInteger;
                    if (token.StringValue.IndexOf('.') < 0 && token.StringValue.IndexOf('e') < 0 && token.StringValue.IndexOf('E') < 0
                        && decimal.TryParse(token.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out largeInteger)) return largeInteger;
                    double number;
                    if (!double.TryParse(token.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException("JSON number is invalid.");
                    return number;
                case TokenType.Array: return token.ArrayValue.Select(TokenToObject).ToList();
                case TokenType.Object: return token.ObjectValue.ToDictionary(pair => pair.Key, pair => TokenToObject(pair.Value), StringComparer.Ordinal);
                default: throw new FormatException("Unsupported JSON token.");
            }
        }

        private static void WriteCanonicalValue(StringBuilder builder, object value)
        {
            if (value == null) { builder.Append("null"); return; }
            if (value is string text) { WriteString(builder, text); return; }
            if (value is bool boolean) { builder.Append(boolean ? "true" : "false"); return; }
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is float || value is double || value is decimal)
            {
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException("Canonical JSON does not support non-finite numbers.");
                if (number == 0) { builder.Append('0'); return; }
                builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is IDictionary dictionary)
            {
                var entries = new List<Tuple<string, object>>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add(Tuple.Create(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), entry.Value));
                }
                entries.Sort((left, right) => string.CompareOrdinal(left.Item1, right.Item1));
                builder.Append('{');
                for (var index = 0; index < entries.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    WriteString(builder, entries[index].Item1);
                    builder.Append(':');
                    WriteCanonicalValue(builder, entries[index].Item2);
                }
                builder.Append('}');
                return;
            }
            if (value is IEnumerable enumerable)
            {
                builder.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteCanonicalValue(builder, item);
                }
                builder.Append(']');
                return;
            }
            var properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetValue(value, null) != null)
                .OrderBy(property => ToCamelCase(property.Name), StringComparer.Ordinal)
                .ToArray();
            builder.Append('{');
            for (var index = 0; index < properties.Length; index++)
            {
                if (index > 0) builder.Append(',');
                WriteString(builder, ToCamelCase(properties[index].Name));
                builder.Append(':');
                WriteCanonicalValue(builder, properties[index].GetValue(value, null));
            }
            builder.Append('}');
        }

    }
}
#endif
