#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// Input parsing helper for ProBuilder tool handlers.
    /// Extracts typed values from the JSON input string in UnityCliProjectToolContext.
    /// Uses simple regex-based extraction consistent with BridgeRequest patterns.
    /// </summary>
    internal static class ProBuilderInput
    {
        /// <summary>
        /// Represents an edge specified as a vertex pair {a, b}.
        /// </summary>
        public struct EdgePair
        {
            public int a;
            public int b;
        }
        public static string StringArg(string inputJson, string name)
        {
            if (string.IsNullOrEmpty(inputJson)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                inputJson,
                "\\\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
            return match.Success ? Unescape(match.Groups[1].Value) : null;
        }

        public static int? IntArg(string inputJson, string name)
        {
            if (string.IsNullOrEmpty(inputJson)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                inputJson,
                "\\\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\\\"\\s*:\\s*(-?\\d+)");
            if (match.Success)
            {
                int value;
                if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return value;
            }
            return null;
        }

        public static int IntArg(string inputJson, string name, int fallback)
        {
            return IntArg(inputJson, name) ?? fallback;
        }

        public static float? FloatArg(string inputJson, string name)
        {
            if (string.IsNullOrEmpty(inputJson)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                inputJson,
                "\\\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\\\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
            if (match.Success)
            {
                float value;
                if (float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return value;
            }
            return null;
        }

        public static float FloatArg(string inputJson, string name, float fallback)
        {
            return FloatArg(inputJson, name) ?? fallback;
        }

        public static bool BoolArg(string inputJson, string name, bool fallback = false)
        {
            if (string.IsNullOrEmpty(inputJson)) return fallback;
            var match = System.Text.RegularExpressions.Regex.Match(
                inputJson,
                "\\\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\\\"\\s*:\\s*(true|false)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success
                ? string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase)
                : fallback;
        }

        public static int[] IntArrayArg(string inputJson, string name)
        {
            var raw = RawProperty(inputJson, name);
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                raw = raw.Trim();
                if (!raw.StartsWith("[", StringComparison.Ordinal)) return null;
                var items = new List<int>();
                var inner = raw.Substring(1, raw.Length - (raw.EndsWith("]", StringComparison.Ordinal) ? 2 : 1)).Trim();
                if (string.IsNullOrEmpty(inner)) return items.ToArray();
                foreach (var part in inner.Split(','))
                {
                    int val;
                    if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                        items.Add(val);
                }
                return items.ToArray();
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Parse an array of edge vertex pair objects [{a:X,b:Y},...] from the input JSON.
        /// </summary>
        public static EdgePair[] EdgePairArrayArg(string inputJson, string name)
        {
            var raw = RawProperty(inputJson, name);
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                raw = raw.Trim();
                if (!raw.StartsWith("[", StringComparison.Ordinal)) return null;
                var items = new List<EdgePair>();
                // Extract each {a:X,b:Y} object
                var objPattern = new System.Text.RegularExpressions.Regex(
                    @"\{[^}]*\""a\""\s*:\s*(-?\d+)[^}]*\""b\""\s*:\s*(-?\d+)[^}]*\}");
                var matches = objPattern.Matches(raw);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    int a, b;
                    if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out a) &&
                        int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                    {
                        items.Add(new EdgePair { a = a, b = b });
                    }
                }
                return items.Count > 0 ? items.ToArray() : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Parse a single edge pair {a:X,b:Y} from the input JSON.
        /// </summary>
        public static EdgePair? EdgeArg(string inputJson, string name)
        {
            var raw = RawProperty(inputJson, name);
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                var aMatch = System.Text.RegularExpressions.Regex.Match(raw,
                    @"\""a\""\s*:\s*(-?\d+)");
                var bMatch = System.Text.RegularExpressions.Regex.Match(raw,
                    @"\""b\""\s*:\s*(-?\d+)");
                if (aMatch.Success && bMatch.Success)
                {
                    int a, b;
                    if (int.TryParse(aMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out a) &&
                        int.TryParse(bMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                    {
                        return new EdgePair { a = a, b = b };
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Parse an array of 3D point objects [{x:1,y:2,z:3},...] from the input JSON.
        /// </summary>
        public static List<Vector3> Vector3ListArg(string inputJson, string name)
        {
            var raw = RawProperty(inputJson, name);
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                raw = raw.Trim();
                if (!raw.StartsWith("[", StringComparison.Ordinal)) return null;
                var items = new List<Vector3>();
                // Extract each {x:X,y:Y,z:Z} object
                var objPattern = new System.Text.RegularExpressions.Regex(
                    @"\{[^}]*\""x\""\s*:\s*(-?\d+(?:\.\d+)?)[^}]*\""y\""\s*:\s*(-?\d+(?:\.\d+)?)[^}]*\""z\""\s*:\s*(-?\d+(?:\.\d+)?)[^}]*\}");
                var matches = objPattern.Matches(raw);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    float x, y, z;
                    if (float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                        float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                        float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                    {
                        items.Add(new Vector3(x, y, z));
                    }
                }
                return items.Count > 0 ? items : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Parse an array of objects like [{x:1,y:2,z:3},...] from the input JSON.
        /// Used for points, edges, etc.
        /// </summary>
        public static Vector3[] Vector3ArrayArg(string inputJson, string name)
        {
            // This is complex JSON parsing; for simplicity we extract individual coords
            // In practice the CLI serializes these as JSON arrays of objects
            return null; // Complex parsing handled in individual handlers
        }

        public static string RawProperty(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                json,
                "\\\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\\\"\\s*:");
            if (!match.Success) return null;
            var start = match.Index + match.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length) return null;
            var end = FindJsonValueEnd(json, start);
            return end > start ? json.Substring(start, end - start).Trim() : null;
        }

        /// <summary>
        /// Resolve a target GameObject from the input by name, path, or instance ID.
        /// </summary>
        public static GameObject ResolveTarget(string inputJson)
        {
            var target = StringArg(inputJson, "target");
            if (string.IsNullOrEmpty(target)) return null;

            // Try instance ID
            int instanceId;
            if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
            {
                var go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (go != null) return go;
            }

            // Try by hierarchy path
            if (target.Contains("/"))
            {
                var go = GameObject.Find(target);
                if (go != null) return go;
            }

            // Try by name (active scene)
            var found = GameObject.Find(target);
            if (found != null) return found;

            // Try searching all root objects
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name == target) return root;
                    var child = FindDescendant(root.transform, target);
                    if (child != null) return child.gameObject;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolve multiple targets (for combine-meshes, merge-objects).
        /// </summary>
        public static GameObject[] ResolveTargets(string inputJson)
        {
            // targets is a JSON array of strings
            var raw = RawProperty(inputJson, "targets");
            if (string.IsNullOrEmpty(raw)) return new GameObject[0];
            var results = new List<GameObject>();
            raw = raw.Trim();
            if (!raw.StartsWith("[", StringComparison.Ordinal)) return results.ToArray();
            // Simple extraction of quoted strings
            var matches = System.Text.RegularExpressions.Regex.Matches(raw, "\\\"((?:\\\\.|[^\\\"])*)\\\"");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var name = Unescape(m.Groups[1].Value);
                var go = FindGameObject(name);
                if (go != null) results.Add(go);
            }
            return results.ToArray();
        }

        private static GameObject FindDescendant(Transform parent, string name)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child.gameObject;
                var found = FindDescendant(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject FindGameObject(string name)
        {
            var go = GameObject.Find(name);
            if (go != null) return go;
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name == name) return root;
                    var desc = FindDescendant(root.transform, name);
                    if (desc != null) return desc;
                }
            }
            return null;
        }

        private static string Unescape(string value)
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length) { builder.Append(ch); continue; }
                var esc = value[++i];
                switch (esc)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    default: builder.Append('\\'); builder.Append(esc); break;
                }
            }
            return builder.ToString();
        }

        private static int FindJsonValueEnd(string json, int start)
        {
            if (start >= json.Length) return start;
            var ch = json[start];
            if (ch == '"') return FindStringEnd(json, start + 1);
            if (ch == '{' || ch == '[') return FindBalancedEnd(json, start);
            // Number, bool, null
            var i = start;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']' && !char.IsWhiteSpace(json[i]))
                i++;
            return i;
        }

        private static int FindStringEnd(string json, int start)
        {
            var i = start;
            while (i < json.Length)
            {
                if (json[i] == '\\') { i += 2; continue; }
                if (json[i] == '"') return i + 1;
                i++;
            }
            return json.Length;
        }

        private static int FindBalancedEnd(string json, int start)
        {
            var open = json[start];
            var close = open == '{' ? '}' : ']';
            var depth = 1;
            var i = start + 1;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '"') { i = FindStringEnd(json, i + 1); continue; }
                if (json[i] == open) depth++;
                else if (json[i] == close) depth--;
                i++;
            }
            return i;
        }
    }
}
#endif
