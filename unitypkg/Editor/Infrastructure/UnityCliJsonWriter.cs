#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityCli
{
    internal sealed class UnityCliJsonWriter
    {
        private readonly StringBuilder builder = new StringBuilder();
        private readonly List<Frame> stack = new List<Frame>();
        private bool wroteRoot;

        private struct Frame
        {
            public char Type;
            public bool First;
        }

        public UnityCliJsonWriter BeginObject()
        {
            BeforeValue();
            builder.Append('{');
            stack.Add(new Frame { Type = 'o', First = true });
            return this;
        }

        public UnityCliJsonWriter EndObject()
        {
            builder.Append('}');
            if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            return this;
        }

        public UnityCliJsonWriter BeginArray()
        {
            BeforeValue();
            builder.Append('[');
            stack.Add(new Frame { Type = 'a', First = true });
            return this;
        }

        public UnityCliJsonWriter EndArray()
        {
            builder.Append(']');
            if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            return this;
        }

        public UnityCliJsonWriter Prop(string name, string value)
        {
            BeforeProperty(name);
            WriteString(value);
            return this;
        }

        public UnityCliJsonWriter Prop(string name, bool value)
        {
            BeforeProperty(name);
            builder.Append(value ? "true" : "false");
            return this;
        }

        public UnityCliJsonWriter Prop(string name, int value)
        {
            return Prop(name, (long)value);
        }

        public UnityCliJsonWriter Prop(string name, long value)
        {
            BeforeProperty(name);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public UnityCliJsonWriter Prop(string name, float value)
        {
            return Prop(name, (double)value);
        }

        public UnityCliJsonWriter Prop(string name, double value)
        {
            BeforeProperty(name);
            WriteNumber(value);
            return this;
        }

        public UnityCliJsonWriter Prop(string name, decimal value)
        {
            BeforeProperty(name);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public UnityCliJsonWriter PropNull(string name)
        {
            BeforeProperty(name);
            builder.Append("null");
            return this;
        }

        public UnityCliJsonWriter PropRaw(string name, string rawJson)
        {
            BeforeProperty(name);
            WriteRawOrString(rawJson);
            return this;
        }

        public UnityCliJsonWriter PropObject(string name, Action<UnityCliJsonWriter> write)
        {
            BeforeProperty(name);
            builder.Append('{');
            stack.Add(new Frame { Type = 'o', First = true });
            if (write != null) write(this);
            EndObject();
            return this;
        }

        public UnityCliJsonWriter PropArray(string name, Action<UnityCliJsonWriter> write)
        {
            BeforeProperty(name);
            builder.Append('[');
            stack.Add(new Frame { Type = 'a', First = true });
            if (write != null) write(this);
            EndArray();
            return this;
        }

        public UnityCliJsonWriter Value(string value)
        {
            BeforeValue();
            WriteString(value);
            return this;
        }

        public UnityCliJsonWriter Value(bool value)
        {
            BeforeValue();
            builder.Append(value ? "true" : "false");
            return this;
        }

        public UnityCliJsonWriter Value(long value)
        {
            BeforeValue();
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public UnityCliJsonWriter Value(double value)
        {
            BeforeValue();
            WriteNumber(value);
            return this;
        }

        public UnityCliJsonWriter NullValue()
        {
            BeforeValue();
            builder.Append("null");
            return this;
        }

        public UnityCliJsonWriter RawValue(string rawJson)
        {
            BeforeValue();
            WriteRawOrString(rawJson);
            return this;
        }

        public override string ToString()
        {
            return builder.ToString();
        }

        public static string Quote(string value)
        {
            if (value == null) return "null";
            var writer = new UnityCliJsonWriter();
            writer.WriteString(value);
            return writer.ToString();
        }

        public static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        public static string Number(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return "null";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "null";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static string RawOrString(string rawJson)
        {
            return IsCompleteJsonValue(rawJson) ? rawJson.Trim() : Quote(rawJson);
        }

        public static bool IsCompleteJsonValue(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            var index = 0;
            if (!ParseValue(json, ref index)) return false;
            SkipWhitespace(json, ref index);
            return index == json.Length;
        }

        private void BeforeValue()
        {
            if (stack.Count == 0)
            {
                wroteRoot = true;
                return;
            }
            var frame = stack[stack.Count - 1];
            if (frame.Type == 'a')
            {
                if (!frame.First) builder.Append(',');
                frame.First = false;
                stack[stack.Count - 1] = frame;
            }
        }

        private void BeforeProperty(string name)
        {
            if (stack.Count == 0 || stack[stack.Count - 1].Type != 'o')
            {
                if (wroteRoot) builder.Append(',');
            }
            var frame = stack.Count == 0 ? new Frame { Type = 'o', First = true } : stack[stack.Count - 1];
            if (!frame.First) builder.Append(',');
            frame.First = false;
            if (stack.Count > 0) stack[stack.Count - 1] = frame;
            WriteString(name);
            builder.Append(':');
        }

        private void WriteString(string value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }
            builder.Append('"');
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
                        if (ch < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }
                        break;
                }
            }
            builder.Append('"');
        }

        private void WriteNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                builder.Append("null");
                return;
            }
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private void WriteRawOrString(string rawJson)
        {
            if (IsCompleteJsonValue(rawJson))
            {
                builder.Append(rawJson.Trim());
                return;
            }
            WriteString(rawJson);
        }

        private static bool ParseValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length) return false;
            var ch = json[index];
            if (ch == '"') return ParseString(json, ref index);
            if (ch == '{') return ParseObject(json, ref index);
            if (ch == '[') return ParseArray(json, ref index);
            if (ch == '-' || char.IsDigit(ch)) return ParseNumber(json, ref index);
            return ParseLiteral(json, ref index, "true") || ParseLiteral(json, ref index, "false") || ParseLiteral(json, ref index, "null");
        }

        private static bool ParseObject(string json, ref int index)
        {
            index++;
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == '}')
            {
                index++;
                return true;
            }
            while (index < json.Length)
            {
                if (!ParseString(json, ref index)) return false;
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') return false;
                index++;
                if (!ParseValue(json, ref index)) return false;
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    SkipWhitespace(json, ref index);
                    continue;
                }
                if (index < json.Length && json[index] == '}')
                {
                    index++;
                    return true;
                }
                return false;
            }
            return false;
        }

        private static bool ParseArray(string json, ref int index)
        {
            index++;
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == ']')
            {
                index++;
                return true;
            }
            while (index < json.Length)
            {
                if (!ParseValue(json, ref index)) return false;
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    SkipWhitespace(json, ref index);
                    continue;
                }
                if (index < json.Length && json[index] == ']')
                {
                    index++;
                    return true;
                }
                return false;
            }
            return false;
        }

        private static bool ParseString(string json, ref int index)
        {
            if (index >= json.Length || json[index] != '"') return false;
            index++;
            while (index < json.Length)
            {
                var ch = json[index++];
                if (ch == '"') return true;
                if (ch < 0x20) return false;
                if (ch != '\\') continue;
                if (index >= json.Length) return false;
                var escaped = json[index++];
                if (escaped == '"' || escaped == '\\' || escaped == '/' || escaped == 'b' || escaped == 'f' || escaped == 'n' || escaped == 'r' || escaped == 't') continue;
                if (escaped != 'u') return false;
                for (var count = 0; count < 4; count++)
                {
                    if (index >= json.Length || !IsHex(json[index])) return false;
                    index++;
                }
            }
            return false;
        }

        private static bool ParseNumber(string json, ref int index)
        {
            if (json[index] == '-') index++;
            if (index >= json.Length) return false;
            if (json[index] == '0')
            {
                index++;
            }
            else if (json[index] >= '1' && json[index] <= '9')
            {
                while (index < json.Length && char.IsDigit(json[index])) index++;
            }
            else
            {
                return false;
            }
            if (index < json.Length && json[index] == '.')
            {
                index++;
                if (index >= json.Length || !char.IsDigit(json[index])) return false;
                while (index < json.Length && char.IsDigit(json[index])) index++;
            }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                if (index >= json.Length || !char.IsDigit(json[index])) return false;
                while (index < json.Length && char.IsDigit(json[index])) index++;
            }
            return true;
        }

        private static bool ParseLiteral(string json, ref int index, string literal)
        {
            if (index < 0 || index + literal.Length > json.Length) return false;
            if (string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0) return false;
            index += literal.Length;
            return true;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        }

        private static bool IsHex(char ch)
        {
            return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
        }
    }
}
#endif
