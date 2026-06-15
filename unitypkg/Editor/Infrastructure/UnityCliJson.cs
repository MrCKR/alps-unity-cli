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
        private static string CapabilitiesJson()
        {
            return "["
                + CapabilityJson("camera", CameraCommands) + ","
                + CapabilityJson("scene", SceneCommands) + ","
                + CapabilityJson("go", GameObjectCommands) + ","
                + CapabilityJson("asset", AssetCommands) + ","
                + CapabilityJson("diagnose", DiagnoseCommands) + ","
                + CapabilityJson("screenshot", ScreenshotCommands) + ","
                + CapabilityJson("prefab", PrefabCommands) + ","
                + CapabilityJson("ui", UiCommands) + ","
                + CapabilityJson("material", MaterialCommands) + ","
                + CapabilityJson("graphics", GraphicsCommands) + ","
                + CapabilityJson("physics", PhysicsCommands) + ","
                + CapabilityJson("profiler", ProfilerCommands) + ","
                + CapabilityJson("probuilder", ProBuilderCommands) + ","
                + CapabilityJson("animation", AnimationCommands) + ","
                + CapabilityJson("vfx", VfxCommands) + ","
                + CapabilityJson("shader", ShaderCommands) + ","
                + CapabilityJson("texture", TextureCommands) + ","
                + CapabilityJson("editor", EditorCommands) + ","
                + CapabilityJson("script", ScriptCommands) + ","
                + CapabilityJson("code", CodeCommands) + ","
                + CapabilityJson("custom-tool", CustomToolCommands)
                + "]";
        }

        private static bool IsMutatingCommand(string command)
        {
            return CommandRegistry.IsMutating(command);
        }

        private static string CapabilityJson(string domain, string[] commands)
        {
            var quotedCommands = new List<string>();
            foreach (var command in commands)
            {
                quotedCommands.Add(Quote(command));
            }
            return "{\"domain\":" + Quote(domain) + ",\"route\":\"POST /bridge/execute\",\"commands\":[" + string.Join(",", quotedCommands.ToArray()) + "]}";
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            using (var stream = request.InputStream)
            using (var reader = new StreamReader(stream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteJson(HttpListenerContext context, string json, int statusCode)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception exception)
            {
                WriteLog("write_json_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void TryWriteErrorJson(HttpListenerContext context, string code, string message, string nextAction)
        {
            try
            {
                WriteJson(context, ErrorJson(code, message, nextAction), 200);
            }
            catch (Exception exception)
            {
                WriteLog("try_write_error_json_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string SuccessJson(string dataJson)
        {
            return "{\"ok\":true,\"data\":" + dataJson + "}";
        }

        /// <summary>
        /// Transient error JSON with transient=true and retry_after_ms.
        /// Used for compile, import, domain reload, and play mode states.
        /// Matches ALP-164 frozen spec §安全、性能与生命周期 / transient 状态.
        /// </summary>
        private static string TransientErrorJson(string code, string message, string nextAction, int retryAfterMs)
        {
            return "{\"ok\":false,\"error\":{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + ",\"transient\":true,\"retry_after_ms\":" + retryAfterMs.ToString(CultureInfo.InvariantCulture) + "},\"nextActions\":[" + Quote(nextAction) + "]}";
        }

        private static string ErrorJson(string code, string message, string nextAction)
        {
            return "{\"ok\":false,\"error\":{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + "},\"nextActions\":[" + Quote(nextAction) + "]}";
        }

        private static string ErrorJsonWithDetails(string code, string message, string detailsJson, string nextAction)
        {
            return "{\"ok\":false,\"error\":{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + ",\"details\":" + (string.IsNullOrEmpty(detailsJson) ? "{}" : detailsJson) + "},\"nextActions\":[" + Quote(nextAction) + "]}";
        }

        private static string TargetAmbiguousErrorJson(string label, string target, TargetResolution resolution, string nextAction)
        {
            return "{\"ok\":false,\"error\":{\"code\":\"E_TARGET_AMBIGUOUS\",\"message\":" + Quote(label + " target is ambiguous: " + target) + ",\"target\":" + Quote(target) + ",\"matchCount\":" + resolution.Matches.Count.ToString(CultureInfo.InvariantCulture) + ",\"matches\":" + TargetMatchesJson(resolution.Matches) + "},\"nextActions\":[" + Quote(nextAction) + "]}";
        }

        private static string TargetMatchesJson(List<GameObject> matches)
        {
            var items = new List<string>();
            var limit = Math.Min(matches.Count, 10);
            for (var index = 0; index < limit; index++)
            {
                var gameObject = matches[index];
                items.Add("{\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Quote(gameObject.name) + ",\"path\":" + Quote(HierarchyPath(gameObject)) + ",\"components\":" + ComponentNamesJson(gameObject) + "}");
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string QuotePair(string key, string value)
        {
            return "\"" + key + "\":\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        private static string Quote(string value)
        {
            if (value == null)
            {
                return "null";
            }
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        private static string JsonLiteralOrString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "null";
            }
            var trimmed = value.Trim();
            if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) || (trimmed.StartsWith("[") && trimmed.EndsWith("]")) || trimmed == "true" || trimmed == "false" || trimmed == "null" || Regex.IsMatch(trimmed, "^-?\\d+(?:\\.\\d+)?$"))
            {
                return trimmed;
            }
            return Quote(value);
        }

        private static string StringArrayJson(string[] values)
        {
            var items = new List<string>();
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrEmpty(value))
                {
                    items.Add(Quote(value));
                }
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string JsonRawProperty(string json, string name)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(name)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return null;
            var index = 1;
            while (index < trimmed.Length - 1)
            {
                while (index < trimmed.Length && (char.IsWhiteSpace(trimmed[index]) || trimmed[index] == ',')) index++;
                if (index >= trimmed.Length - 1 || trimmed[index] != (char)34) return null;
                var keyEnd = FindJsonStringEnd(trimmed, index);
                if (keyEnd <= index) return null;
                var key = JsonUnescape(trimmed.Substring(index + 1, keyEnd - index - 1));
                index = keyEnd + 1;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                if (index >= trimmed.Length || trimmed[index] != ':') return null;
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                var valueEnd = FindJsonValueEnd(trimmed, index);
                if (valueEnd <= index) return null;
                if (string.Equals(key, name, StringComparison.Ordinal)) return trimmed.Substring(index, valueEnd - index).Trim();
                index = valueEnd;
            }
            return null;
        }

        private static string JsonStringProperty(string json, string name)
        {
            var raw = JsonRawProperty(json, name);
            if (string.IsNullOrEmpty(raw)) return null;
            var trimmed = raw.Trim();
            return trimmed.Length >= 2 && trimmed[0] == (char)34 && trimmed[trimmed.Length - 1] == (char)34 ? JsonUnescape(trimmed.Substring(1, trimmed.Length - 2)) : null;
        }

        private static string[] JsonStringArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return new string[0];
            var values = new List<string>();
            foreach (Match match in Regex.Matches(json, "\\\"((?:\\\\.|[^\\\"])*)\\\""))
            {
                values.Add(JsonUnescape(match.Groups[1].Value));
            }
            return values.ToArray();
        }

        private static string[] JsonObjectPropertyNames(string json)
        {
            if (string.IsNullOrEmpty(json)) return new string[0];
            var names = new List<string>();
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return names.ToArray();
            var index = 1;
            while (index < trimmed.Length - 1)
            {
                while (index < trimmed.Length && (char.IsWhiteSpace(trimmed[index]) || trimmed[index] == ',')) index++;
                if (index >= trimmed.Length - 1 || trimmed[index] != (char)34) break;
                var keyEnd = FindJsonStringEnd(trimmed, index);
                if (keyEnd <= index) break;
                names.Add(JsonUnescape(trimmed.Substring(index + 1, keyEnd - index - 1)));
                index = keyEnd + 1;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                if (index >= trimmed.Length || trimmed[index] != ':') break;
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                var valueEnd = FindJsonValueEnd(trimmed, index);
                if (valueEnd <= index) break;
                index = valueEnd;
            }
            return names.ToArray();
        }

        private static bool JsonValueMatchesType(string json, string expectedType)
        {
            var value = (json ?? string.Empty).Trim();
            if (expectedType == "object") return value.StartsWith("{") && value.EndsWith("}");
            if (expectedType == "array") return value.StartsWith("[") && value.EndsWith("]");
            if (expectedType == "string") return value.StartsWith("\"") && value.EndsWith("\"");
            if (expectedType == "boolean") return value == "true" || value == "false";
            if (expectedType == "null") return value == "null";
            if (expectedType == "integer") return Regex.IsMatch(value, "^-?\\d+$");
            if (expectedType == "number") return Regex.IsMatch(value, "^-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?$");
            return true;
        }

        private static int FindJsonStringEnd(string json, int start)
        {
            var escaping = false;
            for (var index = start + 1; index < json.Length; index++)
            {
                var ch = json[index];
                if (escaping) escaping = false;
                else if (ch == '\\') escaping = true;
                else if (ch == (char)34) return index;
            }
            return -1;
        }

        private static int FindJsonValueEnd(string json, int start)
        {
            var depth = 0;
            var inString = false;
            var escaping = false;
            for (var index = start; index < json.Length; index++)
            {
                var ch = json[index];
                if (inString)
                {
                    if (escaping) escaping = false;
                    else if (ch == '\\') escaping = true;
                    else if (ch == (char)34) inString = false;
                    continue;
                }
                if (ch == (char)34) { inString = true; continue; }
                if (ch == '{' || ch == '[') { depth++; continue; }
                if (ch == '}' || ch == ']')
                {
                    if (depth == 0) return index;
                    depth--;
                    if (depth == 0) return index + 1;
                    continue;
                }
                if (depth == 0 && ch == ',') return index;
            }
            return json.Length;
        }

        private static string JsonUnescape(string value)
        {
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Number(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Vector2Json(Vector2 value)
        {
            return "{\"x\":" + Number(value.x) + ",\"y\":" + Number(value.y) + "}";
        }

        private static string Vector3Json(Vector3 value)
        {
            return "{\"x\":" + Number(value.x) + ",\"y\":" + Number(value.y) + ",\"z\":" + Number(value.z) + "}";
        }

        private static string EffectJson(string type, string property, string value)
        {
            return "{\"type\":" + Quote(type) + ",\"property\":" + Quote(property) + ",\"value\":" + Quote(value) + "}";
        }

        private static List<string> QuoteArray(string[] values)
        {
            var items = new List<string>();
            foreach (var value in values)
            {
                items.Add(Quote(value));
            }
            return items;
        }
    }
}
#endif
