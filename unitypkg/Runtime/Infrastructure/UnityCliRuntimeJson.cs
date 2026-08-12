using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityCli
{
    /// <summary>Runtime listener使用的严格JSON；不依赖Editor、Newtonsoft或System.Web。</summary>
    internal static class UnityCliRuntimeJson
    {
        internal static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("JSON payload is empty.");
            var index = 0;
            var value = ReadValue(json, ref index);
            SkipWhitespace(json, ref index);
            if (index != json.Length) throw new FormatException("JSON payload contains trailing tokens.");
            return value;
        }

        internal static string Serialize(object value)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value);
            return builder.ToString();
        }

        internal static Dictionary<string, object> Object(object value, string message)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new FormatException(message);
            return result;
        }

        private static object ReadValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length) throw new FormatException("JSON payload is incomplete.");
            var token = json[index];
            if (token == '{') return ReadObject(json, ref index);
            if (token == '[') return ReadArray(json, ref index);
            if (token == '"') return ReadString(json, ref index);
            if (token == 't' && Consume(json, ref index, "true")) return true;
            if (token == 'f' && Consume(json, ref index, "false")) return false;
            if (token == 'n' && Consume(json, ref index, "null")) return null;
            if (token == '-' || (token >= '0' && token <= '9')) return ReadNumber(json, ref index);
            throw new FormatException("JSON token is invalid.");
        }

        private static Dictionary<string, object> ReadObject(string json, ref int index)
        {
            index++;
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}')) return result;
            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"') throw new FormatException("JSON object key must be a string.");
                var key = ReadString(json, ref index);
                if (result.ContainsKey(key)) throw new FormatException("JSON object contains a duplicate key: " + key + ".");
                SkipWhitespace(json, ref index);
                if (!TryConsume(json, ref index, ':')) throw new FormatException("JSON object key is missing a value separator.");
                result.Add(key, ReadValue(json, ref index));
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}')) return result;
                if (!TryConsume(json, ref index, ',')) throw new FormatException("JSON object is malformed.");
            }
        }

        private static List<object> ReadArray(string json, ref int index)
        {
            index++;
            var result = new List<object>();
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, ']')) return result;
            while (true)
            {
                result.Add(ReadValue(json, ref index));
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, ']')) return result;
                if (!TryConsume(json, ref index, ',')) throw new FormatException("JSON array is malformed.");
            }
        }

        private static string ReadString(string json, ref int index)
        {
            index++;
            var builder = new StringBuilder();
            while (index < json.Length)
            {
                var value = json[index++];
                if (value == '"') return builder.ToString();
                if (value < 0x20) throw new FormatException("JSON string contains an unescaped control character.");
                if (value != '\\') { builder.Append(value); continue; }
                if (index >= json.Length) throw new FormatException("JSON string ends with an incomplete escape.");
                switch (json[index++])
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > json.Length) throw new FormatException("JSON unicode escape is incomplete.");
                        int code;
                        if (!int.TryParse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw new FormatException("JSON unicode escape is invalid.");
                        builder.Append((char)code);
                        index += 4;
                        break;
                    default: throw new FormatException("JSON escape is invalid.");
                }
            }
            throw new FormatException("JSON string is not terminated.");
        }

        private static object ReadNumber(string json, ref int index)
        {
            var start = index;
            if (json[index] == '-') index++;
            if (index >= json.Length) throw new FormatException("JSON number is incomplete.");
            if (json[index] == '0')
            {
                index++;
                if (index < json.Length && char.IsDigit(json[index])) throw new FormatException("JSON number cannot contain a leading zero.");
            }
            else
            {
                if (!char.IsDigit(json[index])) throw new FormatException("JSON number is invalid.");
                while (index < json.Length && char.IsDigit(json[index])) index++;
            }
            if (index < json.Length && json[index] == '.')
            {
                index++;
                var fraction = index;
                while (index < json.Length && char.IsDigit(json[index])) index++;
                if (fraction == index) throw new FormatException("JSON number fraction is invalid.");
            }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                var exponent = index;
                while (index < json.Length && char.IsDigit(json[index])) index++;
                if (exponent == index) throw new FormatException("JSON number exponent is invalid.");
            }
            var literal = json.Substring(start, index - start);
            long integer;
            if (literal.IndexOf('.') < 0 && literal.IndexOf('e') < 0 && literal.IndexOf('E') < 0
                && long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
            double number;
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException("JSON number is non-finite or invalid.");
            return number;
        }

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null) { builder.Append("null"); return; }
            if (value is string) { WriteString(builder, (string)value); return; }
            if (value is bool) { builder.Append((bool)value ? "true" : "false"); return; }
            if (value is byte || value is short || value is int || value is long || value is sbyte || value is ushort || value is uint || value is ulong)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return;
            }
            if (value is float || value is double || value is decimal)
            {
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException("JSON does not support non-finite numbers.");
                builder.Append(number.ToString("R", CultureInfo.InvariantCulture)); return;
            }
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                builder.Append('{'); var first = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!(entry.Key is string)) throw new FormatException("JSON object keys must be strings.");
                    if (!first) builder.Append(','); first = false;
                    WriteString(builder, (string)entry.Key); builder.Append(':'); WriteValue(builder, entry.Value);
                }
                builder.Append('}'); return;
            }
            var items = value as IEnumerable;
            if (items != null)
            {
                builder.Append('['); var first = true;
                foreach (var item in items) { if (!first) builder.Append(','); first = false; WriteValue(builder, item); }
                builder.Append(']'); return;
            }
            throw new FormatException("Runtime JSON only supports dictionaries, lists, and primitive values.");
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
        }

        private static bool Consume(string value, ref int index, string token)
        {
            if (index + token.Length > value.Length || string.CompareOrdinal(value, index, token, 0, token.Length) != 0) return false;
            index += token.Length; return true;
        }
        private static bool TryConsume(string value, ref int index, char token)
        {
            if (index < value.Length && value[index] == token) { index++; return true; }
            return false;
        }
        private static void SkipWhitespace(string value, ref int index) { while (index < value.Length && char.IsWhiteSpace(value[index])) index++; }
    }
}
