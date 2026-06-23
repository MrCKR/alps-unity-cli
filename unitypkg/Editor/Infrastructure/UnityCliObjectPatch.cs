#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// 对象补丁引擎：支持 pathPatches 与 jsonPatch (JSON Merge Patch, RFC 7386)。
        /// 被 object.modify / component.modify / asset.modify 复用，保证只有一套 patch 语义。
        /// </summary>
        internal static class ObjectPatch
        {
            public sealed class PatchResult
            {
                public readonly List<string> ChangedPaths = new List<string>();
                public readonly List<string> Logs = new List<string>();
                public readonly List<object> AuditEntries = new List<object>();
            }

            /// <summary>应用 pathPatches：每项 {path, value, op}。op 默认 replace，支持 replace/add/remove。</summary>
            public static PatchResult ApplyPathPatches(object target, string pathPatchesJson)
            {
                var result = new PatchResult();
                if (string.IsNullOrEmpty(pathPatchesJson)) return result;
                var patches = ParseArray(pathPatchesJson);
                foreach (var patch in patches)
                {
                    var path = GetField(patch, "path") as string;
                    var op = (GetField(patch, "op") as string) ?? "replace";
                    var hasValue = HasField(patch, "value");
                    var value = GetField(patch, "value");
                    if (string.IsNullOrEmpty(path))
                    {
                        result.Logs.Add("skipped: patch missing path");
                        continue;
                    }
                    ApplyPathPatch(target, path, op, hasValue ? value : null, hasValue, result);
                }
                return result;
            }

            /// <summary>应用 JSON Merge Patch (RFC 7386)：递归合并对象字段。</summary>
            public static PatchResult ApplyMergePatch(object target, string mergePatchJson)
            {
                var result = new PatchResult();
                if (string.IsNullOrEmpty(mergePatchJson)) return result;
                var patch = ParseObject(mergePatchJson);
                if (patch == null) return result;
                ApplyMergePatchRecursive(target, patch, "$", result);
                return result;
            }

            private static void ApplyMergePatchRecursive(object target, Dictionary<string, object> patch, string currentPath, PatchResult result)
            {
                var type = target.GetType();
                foreach (var kv in patch)
                {
                    var name = kv.Key;
                    var value = kv.Value;
                    var memberPath = currentPath + "." + name;
                    if (value == null)
                    {
                        // null 表示删除/置默认
                        SetMember(target, name, null, false, memberPath, result);
                        continue;
                    }
                    // 嵌套对象：尝试递归合并
                    if (value is Dictionary<string, object> nested)
                    {
                        var member = GetMemberValue(target, name);
                        if (member != null && !IsSimpleValue(member))
                        {
                            ApplyMergePatchRecursive(member, nested, memberPath, result);
                            continue;
                        }
                    }
                    SetMember(target, name, value, true, memberPath, result);
                }
            }

            /// <summary>
            /// 应用单条 pathPatch。对齐 IvanMurzak ReflectorNet 路径语法:
            /// slash 分隔(fieldA/fieldB)、[i] 数组索引、[key] 字典键、前导 #/ 去除。
            /// 同时兼容 dot 分隔(fieldA.fieldB)以保持向后兼容。
            /// </summary>
            private static void ApplyPathPatch(object target, string path, string op, object value, bool hasValue, PatchResult result)
            {
                var normalized = (path ?? string.Empty).Trim();
                if (normalized.StartsWith("#/", StringComparison.Ordinal)) normalized = normalized.Substring(2);
                else if (normalized.StartsWith("#", StringComparison.Ordinal)) normalized = normalized.Substring(1);
                var segments = ParsePathSegments(normalized);
                if (segments.Count == 0)
                {
                    result.Logs.Add("skipped: empty path");
                    return;
                }
                object current = target;
                for (var i = 0; i < segments.Count - 1; i++)
                {
                    current = NavigateStep(current, segments[i]);
                    if (current == null)
                    {
                        result.Logs.Add("skipped: " + path + " (intermediate null at segment " + i + ")");
                        return;
                    }
                }
                SetAtLeaf(current, segments[segments.Count - 1], value, hasValue, path, result);
            }

            private struct PathSegment
            {
                public string Kind; // "member" | "index" | "key"
                public string Name;
                public int Index;
            }

            /// <summary>
            /// 把路径解析为段序列。支持 "a/b"、"a.b"(兼容)、"a/[0]/c"、"config/[timeout]"、"arr[0]"。
            /// </summary>
            private static List<PathSegment> ParsePathSegments(string path)
            {
                var segments = new List<PathSegment>();
                if (string.IsNullOrEmpty(path)) return segments;
                // 含 / 则按 / 分隔(对齐 Ivan);否则按 . 分隔(向后兼容)
                var sep = path.IndexOf('/') >= 0 ? '/' : '.';
                var rawParts = path.Split(sep);
                foreach (var raw in rawParts)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    var remaining = raw;
                    var bracketPos = remaining.IndexOf('[');
                    if (bracketPos > 0)
                    {
                        var memberName = remaining.Substring(0, bracketPos);
                        if (!string.IsNullOrEmpty(memberName)) segments.Add(new PathSegment { Kind = "member", Name = memberName });
                        remaining = remaining.Substring(bracketPos);
                    }
                    else if (bracketPos < 0)
                    {
                        segments.Add(new PathSegment { Kind = "member", Name = remaining });
                        continue;
                    }
                    while (remaining.StartsWith("[", StringComparison.Ordinal))
                    {
                        var close = remaining.IndexOf(']');
                        if (close < 0) break;
                        var inside = remaining.Substring(1, close - 1);
                        int idx;
                        if (int.TryParse(inside, out idx))
                            segments.Add(new PathSegment { Kind = "index", Index = idx });
                        else
                            segments.Add(new PathSegment { Kind = "key", Name = inside });
                        remaining = remaining.Substring(close + 1);
                    }
                }
                return segments;
            }

            private static object NavigateStep(object current, PathSegment seg)
            {
                if (current == null) return null;
                switch (seg.Kind)
                {
                    case "member": return GetMemberValue(current, seg.Name);
                    case "index":
                        if (current is IList list && seg.Index >= 0 && seg.Index < list.Count) return list[seg.Index];
                        return null;
                    case "key":
                        if (current is IDictionary dict && dict.Contains(seg.Name)) return dict[seg.Name];
                        return null;
                }
                return null;
            }

            private static void SetAtLeaf(object target, PathSegment leaf, object value, bool hasValue, string path, PatchResult result)
            {
                switch (leaf.Kind)
                {
                    case "member":
                        SetMember(target, leaf.Name, value, hasValue, path, result);
                        break;
                    case "index":
                        if (target is IList list && leaf.Index >= 0 && leaf.Index < list.Count)
                        {
                            var elemType = target.GetType().IsArray ? target.GetType().GetElementType() : typeof(object);
                            list[leaf.Index] = hasValue ? ConvertValue(value, elemType) : null;
                            result.ChangedPaths.Add(path);
                            result.AuditEntries.Add(new { path, op = "replace", index = (long)leaf.Index });
                        }
                        else
                        {
                            result.Logs.Add("skipped: " + path + " (index out of range or target not a list)");
                        }
                        break;
                    case "key":
                        if (target is IDictionary dict)
                        {
                            dict[leaf.Name] = hasValue ? value : null;
                            result.ChangedPaths.Add(path);
                            result.AuditEntries.Add(new { path, op = "replace", key = leaf.Name });
                        }
                        else
                        {
                            result.Logs.Add("skipped: " + path + " (target not a dictionary)");
                        }
                        break;
                }
            }

            private static void SetMember(object target, string name, object value, bool hasValue, string path, PatchResult result)
            {
                var type = target.GetType();
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite)
                {
                    var converted = hasValue ? ConvertValue(value, property.PropertyType) : GetDefault(property.PropertyType);
                    property.SetValue(target, converted, null);
                    result.ChangedPaths.Add(path);
                    result.AuditEntries.Add(new { path, op = "replace", member = name });
                    return;
                }
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                if (field != null)
                {
                    var converted = hasValue ? ConvertValue(value, field.FieldType) : GetDefault(field.FieldType);
                    field.SetValue(target, converted);
                    result.ChangedPaths.Add(path);
                    result.AuditEntries.Add(new { path, op = "replace", member = name });
                    return;
                }
                result.Logs.Add("skipped: " + path + " (no writable public member '" + name + "' on " + type.Name + ")");
            }

            private static object GetMemberValue(object target, string name)
            {
                if (target == null) return null;
                var type = target.GetType();
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanRead)
                {
                    try { return property.GetValue(target, null); } catch { return null; }
                }
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
                return field != null ? field.GetValue(target) : null;
            }

            private static bool IsSimpleValue(object value)
            {
                var t = value.GetType();
                return t.IsPrimitive || t.IsEnum || value is string || value is Vector2 || value is Vector3;
            }

            internal static object ConvertValue(object value, Type targetType)
            {
                if (value == null) return null;
                if (targetType.IsInstanceOfType(value)) return value;
                if (targetType == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
                if (targetType == typeof(bool)) return ToBool(value);
                if (targetType == typeof(int)) return ToInt(value);
                if (targetType == typeof(long)) return ToLong(value);
                if (targetType == typeof(float)) return ToFloat(value);
                if (targetType == typeof(double)) return ToDouble(value);
                if (targetType.IsEnum) return Enum.Parse(targetType, Convert.ToString(value, CultureInfo.InvariantCulture), true);
                if (targetType == typeof(Vector2)) return ParseVector2Value(value);
                if (targetType == typeof(Vector3)) return ParseVector3Value(value);
                if (targetType == typeof(Vector4)) return ParseVector4Value(value);
                if (targetType == typeof(Color)) return ParseColorValue(value);
                if (targetType == typeof(Quaternion)) return ParseQuaternionValue(value);
                // Unity Object 引用解析
                if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
                {
                    return ResolveUnityObjectValue(value, targetType);
                }
                return value;
            }

            private static UnityEngine.Object ResolveUnityObjectValue(object value, Type targetType)
            {
                if (value is UnityEngine.Object uo) return uo;
                var dict = value as Dictionary<string, object>;
                if (dict != null)
                {
                    if (dict.TryGetValue("instanceId", out var iid))
                    {
                        var id = ToInt(iid);
                        if (id != 0) return EditorUtility.InstanceIDToObject(id);
                    }
                    if (dict.TryGetValue("assetPath", out var ap) && ap is string path)
                    {
                        return AssetDatabase.LoadMainAssetAtPath(path);
                    }
                    if (dict.TryGetValue("guid", out var g) && g is string guid)
                    {
                        return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                    }
                }
                if (value is string s)
                {
                    return AssetDatabase.LoadMainAssetAtPath(s);
                }
                return null;
            }

            private static object GetDefault(Type type)
            {
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }

            private static bool ToBool(object value)
            {
                if (value is bool b) return b;
                if (value is string s) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            private static int ToInt(object value)
            {
                if (value is int i) return i;
                if (value is long l) return (int)l;
                if (value is double d) return (int)d;
                return int.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            private static long ToLong(object value)
            {
                if (value is long l) return l;
                if (value is int i) return i;
                if (value is double d) return (long)d;
                return long.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            private static float ToFloat(object value)
            {
                if (value is float f) return f;
                if (value is double d) return (float)d;
                if (value is int i) return i;
                return float.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            private static double ToDouble(object value)
            {
                if (value is double d) return d;
                if (value is float f) return f;
                if (value is int i) return i;
                return double.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }

            private static Vector2 ParseVector2Value(object value)
            {
                var dict = value as Dictionary<string, object>;
                if (dict != null) return new Vector2(GetF(dict, "x"), GetF(dict, "y"));
                return ParseVector2(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            private static Vector3 ParseVector3Value(object value)
            {
                var dict = value as Dictionary<string, object>;
                if (dict != null) return new Vector3(GetF(dict, "x"), GetF(dict, "y"), GetF(dict, "z"));
                return ParseVector3(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            private static Vector4 ParseVector4Value(object value)
            {
                var dict = value as Dictionary<string, object>;
                if (dict != null) return new Vector4(GetF(dict, "x"), GetF(dict, "y"), GetF(dict, "z"), GetF(dict, "w"));
                return Vector4.zero;
            }
            private static Color ParseColorValue(object value)
            {
                var dict = value as Dictionary<string, object>;
                if (dict != null) return new Color(GetF(dict, "r"), GetF(dict, "g"), GetF(dict, "b"), GetF(dict, "a", 1f));
                return Color.white;
            }
            private static Quaternion ParseQuaternionValue(object value)
            {
                var dict = value as Dictionary<string, object>;
                if (dict != null) return new Quaternion(GetF(dict, "x"), GetF(dict, "y"), GetF(dict, "z"), GetF(dict, "w"));
                return Quaternion.identity;
            }
            private static float GetF(Dictionary<string, object> dict, string key, float fallback = 0f)
            {
                object v;
                return dict != null && dict.TryGetValue(key, out v) ? ToFloat(v) : fallback;
            }

            // ── 简易 JSON 解析（复用 BridgeRequest 的正则不足，这里用轻量解析器处理嵌套） ──
            internal static List<Dictionary<string, object>> ParseArray(string json)
            {
                var result = new List<Dictionary<string, object>>();
                var trimmed = (json ?? "").Trim();
                if (trimmed.Length < 2 || trimmed[0] != '[') return result;
                var index = 1;
                SkipWhite(trimmed, ref index);
                while (index < trimmed.Length && trimmed[index] != ']')
                {
                    var obj = ParseObjectAt(trimmed, ref index);
                    if (obj != null) result.Add(obj);
                    SkipWhite(trimmed, ref index);
                    if (index < trimmed.Length && trimmed[index] == ',') { index++; SkipWhite(trimmed, ref index); }
                }
                return result;
            }

            internal static Dictionary<string, object> ParseObject(string json)
            {
                var trimmed = (json ?? "").Trim();
                if (trimmed.Length < 2 || trimmed[0] != '{') return null;
                var index = 1;
                return ParseObjectAt(trimmed, ref index);
            }

            private static Dictionary<string, object> ParseObjectAt(string json, ref int index)
            {
                SkipWhite(json, ref index);
                if (index >= json.Length || json[index] != '{') return null;
                index++;
                var dict = new Dictionary<string, object>();
                SkipWhite(json, ref index);
                while (index < json.Length && json[index] != '}')
                {
                    var key = ParseString(json, ref index);
                    SkipWhite(json, ref index);
                    if (index < json.Length && json[index] == ':') index++;
                    SkipWhite(json, ref index);
                    var value = ParseValue(json, ref index);
                    if (key != null) dict[key] = value;
                    SkipWhite(json, ref index);
                    if (index < json.Length && json[index] == ',') { index++; SkipWhite(json, ref index); }
                }
                if (index < json.Length && json[index] == '}') index++;
                return dict;
            }

            private static object ParseValue(string json, ref int index)
            {
                SkipWhite(json, ref index);
                if (index >= json.Length) return null;
                var ch = json[index];
                if (ch == '"') return ParseString(json, ref index);
                if (ch == '{') return ParseObjectAt(json, ref index);
                if (ch == '[') return ParseArrayAt(json, ref index);
                if (ch == '-' || char.IsDigit(ch)) return ParseNumber(json, ref index);
                if (MatchLiteral(json, ref index, "true")) return true;
                if (MatchLiteral(json, ref index, "false")) return false;
                if (MatchLiteral(json, ref index, "null")) return null;
                return null;
            }

            private static List<object> ParseArrayAt(string json, ref int index)
            {
                index++;
                var list = new List<object>();
                SkipWhite(json, ref index);
                while (index < json.Length && json[index] != ']')
                {
                    list.Add(ParseValue(json, ref index));
                    SkipWhite(json, ref index);
                    if (index < json.Length && json[index] == ',') { index++; SkipWhite(json, ref index); }
                }
                if (index < json.Length && json[index] == ']') index++;
                return list;
            }

            private static string ParseString(string json, ref int index)
            {
                SkipWhite(json, ref index);
                if (index >= json.Length || json[index] != '"') return null;
                index++;
                var start = index;
                var sb = new System.Text.StringBuilder();
                while (index < json.Length)
                {
                    var c = json[index++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\' && index < json.Length)
                    {
                        var e = json[index++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }

            private static object ParseNumber(string json, ref int index)
            {
                var start = index;
                if (json[index] == '-') index++;
                while (index < json.Length && (char.IsDigit(json[index]) || json[index] == '.' || json[index] == 'e' || json[index] == 'E' || json[index] == '+' || json[index] == '-'))
                    index++;
                var text = json.Substring(start, index - start);
                if (text.Contains(".") || text.Contains("e") || text.Contains("E"))
                {
                    double d;
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (object)d : null;
                }
                long l;
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? (object)l : null;
            }

            private static bool MatchLiteral(string json, ref int index, string literal)
            {
                if (index + literal.Length > json.Length) return false;
                if (string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0) return false;
                index += literal.Length;
                return true;
            }

            private static void SkipWhite(string json, ref int index)
            {
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            }

            private static object GetField(Dictionary<string, object> dict, string name)
            {
                object v;
                return dict != null && dict.TryGetValue(name, out v) ? v : null;
            }

            private static bool HasField(Dictionary<string, object> dict, string name)
            {
                return dict != null && dict.ContainsKey(name);
            }

            /// <summary>将 PatchResult 序列化为 audit JSON。</summary>
            public static string ResultToJson(PatchResult result)
            {
                var w = new UnityCliJsonWriter().BeginObject();
                w.PropArray("changedPaths", arr => { foreach (var p in result.ChangedPaths) arr.Value(p); });
                w.PropArray("logs", arr => { foreach (var l in result.Logs) arr.Value(l); });
                w.PropArray("audit", arr =>
                {
                    foreach (var entry in result.AuditEntries)
                    {
                        var dict = entry as Dictionary<string, object>;
                        if (dict == null) continue;
                        arr.BeginObject();
                        foreach (var kv in dict) { arr.Prop(kv.Key, Convert.ToString(kv.Value, CultureInfo.InvariantCulture)); }
                        arr.EndObject();
                    }
                });
                return w.EndObject().ToString();
            }
        }
    }
}
#endif
