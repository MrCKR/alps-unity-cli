#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        internal sealed class BridgeRequest
        {
            private readonly string body;

            internal BridgeRequest(string body, string requestId, string command)
            {
                this.body = body;
                RequestId = requestId;
                Command = command;
            }

            public string RequestId { get; private set; }
            public string Command { get; private set; }
            public bool DryRun { get { return BoolArg("dryRun", false); } }

            public static BridgeRequest Parse(string body)
            {
                return new BridgeRequest(body ?? string.Empty, ExtractString(body, "requestId"), ExtractString(body, "command"));
            }

            public string StringArg(string name)
            {
                return ExtractString(body, name);
            }

            /// <summary>
            /// Alias for StringArg that returns null for missing arguments.
            /// Provides naming consistency with OptionalIntArg/OptionalFloatArg/OptionalBoolArg.
            /// </summary>
            public string OptionalStringArg(string name)
            {
                return StringArg(name);
            }

            public int IntArg(string name, int fallback)
            {
                var parsed = OptionalIntArg(name);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public int? OptionalIntArg(string name)
            {
                var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(?:\\\")?(-?\\d+)(?:\\\")?");
                if (match.Success)
                {
                    int parsed;
                    if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                }
                return null;
            }

            public float FloatArg(string name, float fallback)
            {
                var parsed = OptionalFloatArg(name);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public float? OptionalFloatArg(string name)
            {
                var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(?:\\\")?(-?\\d+(?:\\.\\d+)?)(?:\\\")?");
                if (match.Success)
                {
                    float parsed;
                    if (float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                }
                return null;
            }

            public bool BoolArg(string name, bool fallback)
            {
                var parsed = OptionalBoolArg(name);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public bool? OptionalBoolArg(string name)
            {
                var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                return match.Success ? (bool?)string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) : null;
            }

            public bool HasArg(string name)
            {
                return Regex.IsMatch(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:");
            }

            public bool SecurityConfirmed(string expectedFingerprint)
            {
                return BoolArgAtPath(new[] { "security", "confirmed" }, false)
                    && string.Equals(StringArgAtPath(new[] { "security", "destructiveFingerprint" }), expectedFingerprint, StringComparison.Ordinal);
            }

            public string StringArgAtPath(string[] path)
            {
                var current = body;
                foreach (var segment in path ?? new string[0])
                {
                    current = JsonRawProperty(current, segment);
                    if (string.IsNullOrEmpty(current)) return null;
                }
                if (current.Length >= 2 && current[0] == (char)34 && current[current.Length - 1] == (char)34)
                {
                    return Unescape(current.Substring(1, current.Length - 2));
                }
                return null;
            }

            public string RawJsonArg(string name)
            {
                return ExtractRawJson(body, name);
            }

            public bool BoolArgAtPath(string[] path, bool fallback)
            {
                var current = body;
                foreach (var segment in path ?? new string[0])
                {
                    current = JsonRawProperty(current, segment);
                    if (string.IsNullOrEmpty(current)) return fallback;
                }
                if (string.Equals(current, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(current, "false", StringComparison.OrdinalIgnoreCase)) return false;
                return fallback;
            }

            private static string ExtractString(string json, string name)
            {
                var match = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
                return match.Success ? Unescape(match.Groups[1].Value) : null;
            }

            private static string ExtractRawJson(string json, string name)
            {
                var match = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:");
                if (!match.Success)
                {
                    return null;
                }
                var start = match.Index + match.Length;
                while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
                if (start >= json.Length) return null;
                var end = FindJsonValueEnd(json, start);
                return end > start ? json.Substring(start, end - start).Trim() : null;
            }

            private static string Unescape(string value)
            {
                var builder = new StringBuilder();
                for (var index = 0; index < value.Length; index++)
                {
                    var current = value[index];
                    if (current != '\\' || index + 1 >= value.Length)
                    {
                        builder.Append(current);
                        continue;
                    }

                    var escaped = value[++index];
                    switch (escaped)
                    {
                        case (char)34: builder.Append((char)34); break;
                        case '\\': builder.Append((char)92); break;
                        case '/': builder.Append('/'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        default:
                            builder.Append('\\');
                            builder.Append(escaped);
                            break;
                    }
                }
                return builder.ToString();
            }
        }
    }
}
#endif
