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
            public static PatchResult ApplyPathPatches(object target, IList pathPatches)
            {
                var result = new PatchResult();
                if (pathPatches == null) return result;
                foreach (var patchValue in pathPatches)
                {
                    var patch = patchValue as Dictionary<string, object>;
                    if (patch == null)
                    {
                        result.Logs.Add("skipped: patch must be an object");
                        continue;
                    }
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
            public static PatchResult ApplyMergePatch(object target, object mergePatch)
            {
                var result = new PatchResult();
                var patch = mergePatch as Dictionary<string, object>;
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
            /// 应用单条 pathPatch。路径使用 slash 分隔(fieldA/fieldB)，支持 [i] 数组索引、[key] 字典键和可选 #/ 前缀。
            /// </summary>
            private static void ApplyPathPatch(object target, string path, string op, object value, bool hasValue, PatchResult result)
            {
                var normalized = (path ?? string.Empty).Trim();
                if (normalized.StartsWith("#/", StringComparison.Ordinal)) normalized = normalized.Substring(2);
                if (normalized.IndexOf('.') >= 0)
                {
                    throw new BridgeCommandException(
                        "E_SCHEMA_INVALID",
                        "pathPatches path must use '/' between member segments: " + path,
                        "Use paths such as transform/localPosition or items/[0]/name.");
                }
                var segments = ParsePathSegments(normalized);
                if (segments.Count == 0)
                {
                    result.Logs.Add("skipped: empty path");
                    return;
                }

                var frames = new List<NavigationFrame>();
                object current = target;
                for (var i = 0; i < segments.Count - 1; i++)
                {
                    var parent = current;
                    current = NavigateStep(parent, segments[i]);
                    if (current == null)
                    {
                        result.Logs.Add("skipped: " + path + " (intermediate null at segment " + i + ")");
                        return;
                    }
                    frames.Add(new NavigationFrame { Parent = parent, Segment = segments[i] });
                }

                var changedCount = result.ChangedPaths.Count;
                SetAtLeaf(current, segments[segments.Count - 1], value, hasValue, path, result);
                if (result.ChangedPaths.Count == changedCount) return;

                // Reflection 修改 struct 时只会改变 boxed copy；沿导航链逐层写回，直到到达 reference parent。
                for (var i = frames.Count - 1; i >= 0 && current != null && current.GetType().IsValueType; i--)
                {
                    WriteBackValueType(frames[i].Parent, frames[i].Segment, current, path);
                    current = frames[i].Parent;
                }
            }

            private struct PathSegment
            {
                public string Kind; // "member" | "index" | "key"
                public string Name;
                public int Index;
            }

            private struct NavigationFrame
            {
                public object Parent;
                public PathSegment Segment;
            }

            /// <summary>把已修改的 struct 写回其 member、list index 或 dictionary key 容器。</summary>
            private static void WriteBackValueType(object parent, PathSegment segment, object value, string path)
            {
                if (parent != null && segment.Kind == "member")
                {
                    var type = parent.GetType();
                    var property = type.GetProperty(segment.Name, BindingFlags.Instance | BindingFlags.Public);
                    if (property != null && property.CanWrite)
                    {
                        property.SetValue(parent, ConvertValue(value, property.PropertyType), null);
                        return;
                    }
                    var field = type.GetField(segment.Name, BindingFlags.Instance | BindingFlags.Public);
                    if (field != null)
                    {
                        field.SetValue(parent, ConvertValue(value, field.FieldType));
                        return;
                    }
                }
                else if (parent is IList list && segment.Kind == "index" && segment.Index >= 0 && segment.Index < list.Count)
                {
                    list[segment.Index] = value;
                    return;
                }
                else if (parent is IDictionary dictionary && segment.Kind == "key" && dictionary.Contains(segment.Name))
                {
                    dictionary[segment.Name] = value;
                    return;
                }

                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "pathPatches cannot write the modified value type back through path: " + path,
                    "Patch a writable parent member, list element, or dictionary entry.");
            }

            /// <summary>把 slash 路径解析为 member、index 与 dictionary key 段。</summary>
            private static List<PathSegment> ParsePathSegments(string path)
            {
                var segments = new List<PathSegment>();
                if (string.IsNullOrEmpty(path)) return segments;
                var rawParts = path.Split('/');
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

            private static object GetField(Dictionary<string, object> dict, string name)
            {
                object v;
                return dict != null && dict.TryGetValue(name, out v) ? v : null;
            }

            private static bool HasField(Dictionary<string, object> dict, string name)
            {
                return dict != null && dict.ContainsKey(name);
            }

            /// <summary>构造补丁审计值树，保留路径、日志与每项结构化审计记录。</summary>
            public static Dictionary<string, object> ResultData(PatchResult result)
            {
                return new Dictionary<string, object>
                {
                    { "changedPaths", result.ChangedPaths },
                    { "logs", result.Logs },
                    { "audit", result.AuditEntries }
                };
            }
        }
    }
}
#endif
