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
        public sealed class BridgeRequest
        {
            private readonly string body;
            private readonly Dictionary<string, object> root;

            internal BridgeRequest(string body, string requestId, string command, Dictionary<string, object> root)
            {
                this.body = body ?? string.Empty;
                this.root = root ?? new Dictionary<string, object>(StringComparer.Ordinal);
                RequestId = requestId;
                Command = command;
            }

            public string RequestId { get; private set; }
            public string Command { get; private set; }
            public string Body { get { return body; } }
            public bool DryRun { get { return BoolArg("dryRun", false); } }

            public static BridgeRequest Parse(string body)
            {
                var parsed = UnityCliToolJson.Parse(body) as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
                return new BridgeRequest(body ?? string.Empty, GetString(parsed, "requestId"), GetString(parsed, "command"), parsed);
            }

            public string StringArg(string name)
            {
                return ConvertToString(GetField(name));
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
                return ConvertToInt(GetField(name));
            }

            public float FloatArg(string name, float fallback)
            {
                var parsed = OptionalFloatArg(name);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public float? OptionalFloatArg(string name)
            {
                return ConvertToFloat(GetField(name));
            }

            public bool BoolArg(string name, bool fallback)
            {
                var parsed = OptionalBoolArg(name);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public bool? OptionalBoolArg(string name)
            {
                return ConvertToBool(GetField(name));
            }

            public bool HasArg(string name)
            {
                return GetField(name) != null;
            }

            public bool SecurityConfirmed(string expectedFingerprint)
            {
                return BoolArgAtPath(new[] { "security", "confirmed" }, false)
                    && string.Equals(StringArgAtPath(new[] { "security", "destructiveFingerprint" }), expectedFingerprint, StringComparison.Ordinal);
            }

            public string StringArgAtPath(string[] path)
            {
                return ConvertToString(GetFieldAtPath(path));
            }

            public string RawJsonArg(string name)
            {
                var value = GetField(name);
                return value == null ? null : UnityCliSerializer.Serialize(value);
            }

            public bool BoolArgAtPath(string[] path, bool fallback)
            {
                var value = GetFieldAtPath(path);
                var parsed = ConvertToBool(value);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public T DeserializeArgs<T>()
            {
                object argsObj;
                if (!root.TryGetValue("args", out argsObj) || argsObj == null)
                {
                    return Activator.CreateInstance<T>();
                }
                return UnityCliSerializer.Deserialize<T>(UnityCliSerializer.Serialize(argsObj));
            }

            private object GetField(string name)
            {
                object value;
                if (root.TryGetValue(name, out value)) return value;
                object argsObj;
                if (root.TryGetValue("args", out argsObj) && argsObj is Dictionary<string, object> args && args.TryGetValue(name, out value))
                {
                    return value;
                }
                return null;
            }

            private object GetFieldAtPath(string[] path)
            {
                object current = root;
                foreach (var segment in path ?? new string[0])
                {
                    if (!(current is Dictionary<string, object> dict) || !dict.TryGetValue(segment, out current))
                    {
                        return null;
                    }
                }
                return current;
            }

            private static string GetString(Dictionary<string, object> dict, string key)
            {
                object value;
                return dict != null && dict.TryGetValue(key, out value) ? ConvertToString(value) : null;
            }

            private static string ConvertToString(object value)
            {
                if (value == null) return null;
                if (value is string s) return s;
                if (value is bool b) return b ? "true" : "false";
                if (value is long l) return l.ToString(CultureInfo.InvariantCulture);
                if (value is double d) return d.ToString(CultureInfo.InvariantCulture);
                if (value is int i) return i.ToString(CultureInfo.InvariantCulture);
                if (value is float f) return f.ToString(CultureInfo.InvariantCulture);
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            private static int? ConvertToInt(object value)
            {
                if (value == null) return null;
                if (value is int i) return i;
                if (value is long l) return (int)l;
                if (value is double d) return (int)d;
                if (value is float f) return (int)f;
                if (value is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                return null;
            }

            private static float? ConvertToFloat(object value)
            {
                if (value == null) return null;
                if (value is float f) return f;
                if (value is double d) return (float)d;
                if (value is int i) return i;
                if (value is long l) return l;
                if (value is string s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                return null;
            }

            private static bool? ConvertToBool(object value)
            {
                if (value == null) return null;
                if (value is bool b) return b;
                if (value is string s)
                {
                    if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return null;
            }
        }
    }
}
#endif
