#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace UnityCli
{
    /// <summary>
    /// 反射驱动的协议序列化器(CLI 投递的隐藏桥接 helper)。
    ///
    /// 设计依据:.plan/协议框架.md
    /// - Serialize(object):反射 public 属性 → camelCase JSON;null/可空默认值省略(proto3 语义);必填值类型始终输出。
    /// - Deserialize&lt;T&gt;(string):JSON → token tree → 反射填充 T。
    /// - 演进自 UnityCliJsonWriter(复用其转义逻辑),但独立为反射驱动,替代散落各处的 .Prop() 手拼。
    /// - 对外隐藏:不进 describe/help/docs/公开 API,仅 Bridge 内部桥接基础设施。
    /// </summary>
    internal static class UnityCliSerializer
    {
        // ─── 序列化 ────────────────────────────────────────────────────────

        /// <summary>
        /// 将对象序列化为 camelCase JSON。null/可空默认值字段省略(proto3),
        /// 必填值类型(bool/double 等)即使为默认值也输出(保证 ok:false 等在线上)。
        /// </summary>
        public static string Serialize(object value)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value, forceObject: false);
            return builder.ToString();
        }

        // 写一个值。forceObject=true 时,即使是基本类型也按其属性展开(用于根对象)。
        private static void WriteValue(StringBuilder builder, object value, bool forceObject)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            var type = value.GetType();

            // 基本类型直接输出
            if (type == typeof(string)) { WriteString(builder, (string)value); return; }
            if (type == typeof(bool)) { builder.Append(((bool)value) ? "true" : "false"); return; }
            if (type == typeof(double)) { WriteNumber(builder, (double)value); return; }
            if (type == typeof(float)) { WriteNumber(builder, (float)value); return; }
            if (type == typeof(long)) { builder.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (type == typeof(int)) { builder.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }

            // 可空值类型:有值则输出底层,无值(null)已在上面返回
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) { WriteValue(builder, value, forceObject); return; }

            // 列表/数组
            if (value is IList list)
            {
                builder.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    WriteValue(builder, list[i], false);
                }
                builder.Append(']');
                return;
            }

            // DTO/POCO:反射属性
            WriteObject(builder, value, type);
        }

        // 写对象:反射 public 属性,camelCase 键,null 省略,必填值类型始终输出。
        private static void WriteObject(StringBuilder builder, object value, Type type)
        {
            builder.Append('{');
            var first = true;
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                // 只处理有 getter 的 public 属性
                if (!prop.CanRead) continue;

                var propValue = prop.GetValue(value, null);
                var propType = prop.PropertyType;

                // null 省略(proto3 语义):引用类型或可空值类型为 null 时跳过
                if (propValue == null)
                {
                    // 引用类型 null → 跳过;可空值类型 null → 跳过
                    // (必填值类型不会有 null)
                    continue;
                }

                // 可空值类型:取底层值判断是否默认。可空值类型为默认(如 bool? false)时是否省略?
                // 协议:可选字段(可空)省略 null;有值时输出(即使 false/0)。
                // 必填值类型(非可空):始终输出(即使默认值)。

                if (!first) builder.Append(',');
                first = false;
                WriteString(builder, ToCamelCase(prop.Name));
                builder.Append(':');
                WriteValue(builder, propValue, false);
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
            if (double.IsNaN(value) || double.IsInfinity(value)) { builder.Append("null"); return; }
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        // ─── 反序列化 ──────────────────────────────────────────────────────

        /// <summary>
        /// 将 JSON 反序列化为 T。基于轻量 token tree 解析器(演进自 UnityCliJsonWriter 的 parser),
        /// 再反射填充 T 的 public 属性。废除 BridgeRequest 的正则刮 JSON。
        /// </summary>
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json)) return default(T);
            var index = 0;
            var token = ParseToken(json, ref index);
            return (T)ConvertToken(token, typeof(T));
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
                if (ch == '\\')
                {
                    if (index >= json.Length) break;
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
                            if (index + 4 <= json.Length)
                            {
                                var hex = json.Substring(index, 4);
                                sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                index += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return new Token { Type = TokenType.String, StringValue = sb.ToString() };
        }

        private static Token ParseNumberToken(string json, ref int index)
        {
            var start = index;
            if (json[index] == '-') index++;
            while (index < json.Length && char.IsDigit(json[index])) index++;
            if (index < json.Length && json[index] == '.') { index++; while (index < json.Length && char.IsDigit(json[index])) index++; }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                while (index < json.Length && char.IsDigit(json[index])) index++;
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
                var keyToken = ParseStringToken(json, ref index); // key
                if (keyToken == null) return null;
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') return null;
                index++;
                var valueToken = ParseToken(json, ref index);
                obj[keyToken.StringValue] = valueToken;
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index < json.Length && json[index] == '}') { index++; return new Token { Type = TokenType.Object, ObjectValue = obj }; }
                return null;
            }
            return null;
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
                arr.Add(valueToken);
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index < json.Length && json[index] == ']') { index++; return new Token { Type = TokenType.Array, ArrayValue = arr }; }
                return null;
            }
            return null;
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
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        }

        // ─── token → 目标类型 ─────────────────────────────────────────────

        private static object ConvertToken(Token token, Type targetType)
        {
            if (token == null || token.Type == TokenType.Null) return GetDefault(targetType);

            var underlying = Nullable.GetUnderlyingType(targetType);
            var effectiveType = underlying ?? targetType;

            if (effectiveType == typeof(string)) return token.Type == TokenType.String ? token.StringValue : null;
            if (effectiveType == typeof(bool)) return token.Type == TokenType.True ? true : (token.Type == TokenType.False ? false : Convert.ToBoolean(token.StringValue, CultureInfo.InvariantCulture));
            if (effectiveType == typeof(double)) return token.Type == TokenType.Number ? double.Parse(token.StringValue, CultureInfo.InvariantCulture) : 0.0;
            if (effectiveType == typeof(float)) return token.Type == TokenType.Number ? float.Parse(token.StringValue, CultureInfo.InvariantCulture) : 0f;
            if (effectiveType == typeof(long)) return token.Type == TokenType.Number ? long.Parse(token.StringValue, CultureInfo.InvariantCulture) : 0L;
            if (effectiveType == typeof(int)) return token.Type == TokenType.Number ? int.Parse(token.StringValue, CultureInfo.InvariantCulture) : 0;

            // 嵌套对象:反射填充
            if (token.Type == TokenType.Object) return FillObject(token, effectiveType);

            // 列表
            if (token.Type == TokenType.Array && typeof(IList).IsAssignableFrom(effectiveType))
            {
                return FillList(token, effectiveType);
            }

            return GetDefault(targetType);
        }

        private static object FillObject(Token token, Type type)
        {
            var instance = Activator.CreateInstance(type);
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (!prop.CanWrite) continue;
                // JSON key 是 camelCase;属性名 PascalCase → camelCase 查找
                var key = ToCamelCase(prop.Name);
                if (!token.ObjectValue.TryGetValue(key, out var child)) continue;
                var value = ConvertToken(child, prop.PropertyType);
                prop.SetValue(instance, value, null);
            }
            return instance;
        }

        private static object FillList(Token token, Type type)
        {
            // 确定 元素类型
            Type elementType = typeof(object);
            if (type.IsArray) elementType = type.GetElementType();
            else if (type.IsGenericType) elementType = type.GetGenericArguments()[0];

            // 优先用 List<T>(生成的 DTO 用 List<T>)
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (IList)Activator.CreateInstance(listType);
            foreach (var item in token.ArrayValue)
            {
                list.Add(ConvertToken(item, elementType));
            }

            if (type.IsArray)
            {
                var array = Array.CreateInstance(elementType, list.Count);
                list.CopyTo(array, 0);
                return array;
            }
            return list;
        }

        private static object GetDefault(Type type)
        {
            if (type.IsValueType) return Activator.CreateInstance(type);
            return null;
        }
    }
}
#endif
