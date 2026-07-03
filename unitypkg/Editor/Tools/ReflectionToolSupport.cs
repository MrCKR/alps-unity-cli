#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli
{
    /// <summary>
    /// IUnityCliTool 工具共享的 JSON 解析 helper:将 JSON 字符串解析为
    /// Dictionary&lt;string, object&gt; / List&lt;object&gt; 树,供工具 Result 嵌入
    /// 已由内部 helper(如 ReflectionInvoker)产生的 JSON 输出。
    /// UnityCliSerializer.Serialize 能序列化 IDictionary/IList,输出干净 JSON。
    /// 设计依据:.plan/工具统一形态.md(自包含工具共享只读 helper)。
    /// </summary>
    public static class UnityCliToolJson
    {
        /// <summary>将 JSON 字符串解析为 object 树(字典/列表/标量)。null 或空返回 null。</summary>
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var index = 0;
            return ParseValue(json, ref index);
        }

        private static object ParseValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length) return null;
            var ch = json[index];
            if (ch == '{') return ParseObject(json, ref index);
            if (ch == '[') return ParseArray(json, ref index);
            if (ch == '"') return ParseString(json, ref index);
            if (ch == '-' || char.IsDigit(ch)) return ParseNumber(json, ref index);
            if (MatchLiteral(json, ref index, "true")) return true;
            if (MatchLiteral(json, ref index, "false")) return false;
            if (MatchLiteral(json, ref index, "null")) return null;
            return null;
        }

        private static Dictionary<string, object> ParseObject(string json, ref int index)
        {
            index++; // 跳过 {
            var dict = new Dictionary<string, object>();
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == '}') { index++; return dict; }
            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                var key = ParseString(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') return dict;
                index++;
                var value = ParseValue(json, ref index);
                dict[key] = value;
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index < json.Length && json[index] == '}') { index++; return dict; }
                return dict;
            }
            return dict;
        }

        private static List<object> ParseArray(string json, ref int index)
        {
            index++; // 跳过 [
            var list = new List<object>();
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == ']') { index++; return list; }
            while (index < json.Length)
            {
                var value = ParseValue(json, ref index);
                list.Add(value);
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index < json.Length && json[index] == ']') { index++; return list; }
                return list;
            }
            return list;
        }

        private static string ParseString(string json, ref int index)
        {
            index++; // 跳过开头 "
            var sb = new System.Text.StringBuilder();
            while (index < json.Length)
            {
                var ch = json[index++];
                if (ch == '"') return sb.ToString();
                if (ch == '\\' && index < json.Length)
                {
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
                                sb.Append((char)int.Parse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
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
            return sb.ToString();
        }

        private static object ParseNumber(string json, ref int index)
        {
            var start = index;
            if (json[index] == '-') index++;
            while (index < json.Length && char.IsDigit(json[index])) index++;
            var isFloat = false;
            if (index < json.Length && json[index] == '.')
            {
                isFloat = true;
                index++;
                while (index < json.Length && char.IsDigit(json[index])) index++;
            }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                isFloat = true;
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                while (index < json.Length && char.IsDigit(json[index])) index++;
            }
            var text = json.Substring(start, index - start);
            if (isFloat) return double.Parse(text, CultureInfo.InvariantCulture);
            long longVal;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out longVal)) return longVal;
            return double.Parse(text, CultureInfo.InvariantCulture);
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
    }
}
#endif
