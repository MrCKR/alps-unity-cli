#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// 深序列化器：将任意 Unity Object / Component / C# 对象序列化为 SerializedMember 树。
        /// 对齐 IvanMurzak SerializedMember 模型：typeName/name/value/fields/props/path/readOnly/writable/error/truncated。
        /// 支持 maxDepth、paths 局部读取、数组/list/dictionary、Unity Object 引用、循环引用检测。
        /// </summary>
        internal static class ObjectSerializer
        {
            private const int DefaultMaxDepth = 4;
            private const int DefaultMaxArrayItems = 50;
            private const int AbsoluteMaxArrayItems = 1000;
            private const int MaxMembers = 256;

            /// <summary>序列化选项。</summary>
            public sealed class Options
            {
                public int MaxDepth = DefaultMaxDepth;
                public int MaxArrayItems = DefaultMaxArrayItems;
                public bool IncludeFields = true;
                public bool IncludeProperties = true;
                public bool DeepSerialization = true;
                public HashSet<string> Paths;
                public HashSet<string> NameFilters;
                public HashSet<string> TypeFilters;
                public string ViewPath;
                public string NamePattern;
                public string TypeFilter;
            }

            /// <summary>序列化根对象为 SerializedMember JSON，paths/viewQuery 存在时优先做范围化读取，避免大对象展开。</summary>
            public static string Serialize(object obj, Options options)
            {
                if (options == null) options = new Options();
                if (options.Paths != null && options.Paths.Count > 0)
                {
                    return MemberToJson(BuildPathReadAggregate(obj, options));
                }
                if (!string.IsNullOrEmpty(options.ViewPath))
                {
                    var viewRoot = TryReadPath(obj, NormalizeReadPath(options.ViewPath), out var error);
                    if (error != null)
                    {
                        return MemberToJson(ErrorMember("$" + "." + NormalizeReadPath(options.ViewPath), error));
                    }
                    obj = viewRoot;
                }
                var visited = new HashSet<int>();
                var member = ToMember(obj, string.IsNullOrEmpty(options.ViewPath) ? "$" : "$" + "." + NormalizeReadPath(options.ViewPath), obj == null ? "null" : obj.GetType().FullName, options, visited, 0);
                return MemberToJson(member);
            }

            /// <summary>按请求路径逐项读取，并用稳定 fields envelope 返回每个路径的结果或可恢复错误。</summary>
            private static SerializedMember BuildPathReadAggregate(object obj, Options options)
            {
                var aggregate = new SerializedMember
                {
                    Path = "$",
                    TypeName = "UnityCli.PathReadAggregate"
                };
                foreach (var rawPath in options.Paths)
                {
                    var normalizedPath = NormalizeReadPath(rawPath);
                    var value = TryReadPath(obj, normalizedPath, out var error);
                    aggregate.Fields.Add(new SerializedMember.KV
                    {
                        Key = rawPath,
                        Value = error == null
                            ? ToMember(value, string.IsNullOrEmpty(normalizedPath) ? "$" : "$" + "." + normalizedPath, value == null ? "null" : value.GetType().FullName, WithoutPaths(options), new HashSet<int>(), 0)
                            : ErrorMember(string.IsNullOrEmpty(normalizedPath) ? "$" : "$" + "." + normalizedPath, error)
                    });
                }
                return aggregate;
            }

            /// <summary>移除 paths 选项，避免读取到的子树再次被路径过滤。</summary>
            private static Options WithoutPaths(Options options)
            {
                return new Options
                {
                    MaxDepth = options.MaxDepth,
                    MaxArrayItems = options.MaxArrayItems,
                    IncludeFields = options.IncludeFields,
                    IncludeProperties = options.IncludeProperties,
                    DeepSerialization = options.DeepSerialization,
                    NameFilters = options.NameFilters,
                    TypeFilters = options.TypeFilters,
                    NamePattern = options.NamePattern,
                    TypeFilter = options.TypeFilter
                };
            }

            /// <summary>规范化 AI 常用路径语法，兼容 leading #/、$/、点路径和 / 路径。</summary>
            private static string NormalizeReadPath(string path)
            {
                if (string.IsNullOrEmpty(path)) return string.Empty;
                var normalized = path.Trim();
                if (normalized.StartsWith("#/", StringComparison.Ordinal)) normalized = normalized.Substring(2);
                if (normalized.StartsWith("$.", StringComparison.Ordinal)) normalized = normalized.Substring(2);
                if (normalized.StartsWith("$/", StringComparison.Ordinal)) normalized = normalized.Substring(2);
                normalized = normalized.Replace('/', '.');
                return normalized.Trim('.');
            }

            /// <summary>沿字段、属性、数组索引或字典 key 读取单一路径。</summary>
            private static object TryReadPath(object root, string path, out string error)
            {
                error = null;
                if (root == null)
                {
                    error = "root object is null";
                    return null;
                }
                if (string.IsNullOrEmpty(path)) return root;
                var current = root;
                foreach (var rawSegment in path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = ReadPathSegment(current, rawSegment, out error);
                    if (error != null) return null;
                }
                return current;
            }

            /// <summary>读取路径中的一个 segment，支持 field/property 与 name[index]。</summary>
            private static object ReadPathSegment(object current, string segment, out string error)
            {
                error = null;
                if (current == null)
                {
                    error = "encountered null before segment '" + segment + "'";
                    return null;
                }

                var name = segment;
                string indexer = null;
                var bracketIndex = segment.IndexOf('[');
                if (bracketIndex >= 0)
                {
                    name = segment.Substring(0, bracketIndex);
                    indexer = segment.Substring(bracketIndex).Trim();
                }

                object value = current;
                if (!string.IsNullOrEmpty(name))
                {
                    var type = current.GetType();
                    var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        value = field.GetValue(current);
                    }
                    else
                    {
                        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (prop == null || !prop.CanRead || prop.GetIndexParameters().Length > 0)
                        {
                            error = "member not found or unreadable: " + name;
                            return null;
                        }
                        value = prop.GetValue(current, null);
                    }
                }

                if (!string.IsNullOrEmpty(indexer))
                {
                    value = ReadIndexer(value, indexer, out error);
                }
                return value;
            }

            /// <summary>读取数组/list/dictionary 索引，格式为 [i] 或 [key]。</summary>
            private static object ReadIndexer(object value, string indexer, out string error)
            {
                error = null;
                if (value == null)
                {
                    error = "cannot index null value";
                    return null;
                }
                var token = indexer.Trim();
                if (!token.StartsWith("[", StringComparison.Ordinal) || !token.EndsWith("]", StringComparison.Ordinal))
                {
                    error = "invalid index syntax: " + indexer;
                    return null;
                }
                token = token.Substring(1, token.Length - 2).Trim().Trim('"');
                if (value is IList list)
                {
                    int index;
                    if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0 || index >= list.Count)
                    {
                        error = "list index out of range: " + token;
                        return null;
                    }
                    return list[index];
                }
                if (value is IDictionary dict)
                {
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (string.Equals(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), token, StringComparison.Ordinal))
                        {
                            return entry.Value;
                        }
                    }
                    error = "dictionary key not found: " + token;
                    return null;
                }
                error = "value is not indexable";
                return null;
            }

            private static SerializedMember ToMember(object obj, string path, string declaredType, Options options, HashSet<int> visited, int depth)
            {
                var member = new SerializedMember
                {
                    Path = path,
                    TypeName = declaredType ?? "null"
                };

                if (obj == null)
                {
                    member.Value = null;
                    member.IsNull = true;
                    return member;
                }

                var type = obj.GetType();
                member.TypeName = type.FullName;

                // 基元与简单值类型直接输出
                if (IsSimpleType(type))
                {
                    member.Value = obj;
                    return member;
                }

                // 字符串
                if (obj is string s)
                {
                    member.Value = s;
                    return member;
                }

                // Unity Object 引用：输出引用摘要，不无限递归
                if (obj is UnityEngine.Object uo)
                {
                    member.IsUnityObject = true;
                    member.UnityObjectRef = UnityObjectRefJson(uo);
                    // 允许在 depth 允许时浅层展开公共属性，但不递归到子 Unity Object
                    if (depth < options.MaxDepth && options.DeepSerialization)
                    {
                        ExpandMembers(obj, type, member, options, visited, depth);
                    }
                    return member;
                }

                // 数组 / IList
                if (obj is IList list)
                {
                    member.IsArray = true;
                    member.ArrayLength = list.Count;
                    var maxArrayItems = ClampMaxArrayItems(options.MaxArrayItems);
                    var limit = Math.Min(list.Count, maxArrayItems);
                    member.Truncated = list.Count > maxArrayItems;
                    for (var i = 0; i < limit; i++)
                    {
                        var itemPath = path + "[" + i + "]";
                        if (!PathAllowed(itemPath, options)) continue;
                        member.ArrayItems.Add(ToMember(list[i], itemPath, null, options, visited, depth + 1));
                    }
                    return member;
                }

                // IDictionary
                if (obj is IDictionary dict)
                {
                    member.IsDictionary = true;
                    member.ArrayLength = dict.Count;
                    var limit = 0;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (limit >= ClampMaxArrayItems(options.MaxArrayItems)) { member.Truncated = true; break; }
                        var keyStr = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                        var entryPath = path + "[" + keyStr + "]";
                        if (!PathAllowed(entryPath, options)) continue;
                        member.DictionaryEntries.Add(new SerializedMember.KV
                        {
                            Key = keyStr,
                            Value = ToMember(entry.Value, entryPath, null, options, visited, depth + 1)
                        });
                        limit++;
                    }
                    return member;
                }

                // IEnumerable (非 IList)
                if (obj is IEnumerable enumerable && !(obj is GameObject))
                {
                    member.IsArray = true;
                    var count = 0;
                    foreach (var item in enumerable)
                    {
                        if (count >= ClampMaxArrayItems(options.MaxArrayItems)) { member.Truncated = true; break; }
                        var itemPath = path + "[" + count + "]";
                        if (PathAllowed(itemPath, options))
                        {
                            member.ArrayItems.Add(ToMember(item, itemPath, null, options, visited, depth + 1));
                        }
                        count++;
                    }
                    member.ArrayLength = count;
                    return member;
                }

                // 复杂对象：展开字段/属性
                if (depth >= options.MaxDepth)
                {
                    member.Truncated = true;
                    member.Value = obj.ToString();
                    return member;
                }

                // 循环引用检测
                var hash = RuntimeHelpersHashCode(obj);
                if (visited.Contains(hash))
                {
                    member.Error = "cycle-detected";
                    member.Truncated = true;
                    return member;
                }
                visited.Add(hash);

                ExpandMembers(obj, type, member, options, visited, depth);
                return member;
            }

            private static void ExpandMembers(object obj, Type type, SerializedMember member, Options options, HashSet<int> visited, int depth)
            {
                var memberCount = 0;
                if (options.IncludeFields)
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (memberCount >= MaxMembers) { member.Truncated = true; break; }
                        if (!MemberAllowed(field.Name, options)) continue;
                        try
                        {
                            var value = field.GetValue(obj);
                            var child = ToMember(value, member.Path + "." + field.Name, field.FieldType.FullName, options, visited, depth + 1);
                            if (!TypeAllowed(child.TypeName, options)) continue;
                            member.Fields.Add(new SerializedMember.KV
                            {
                                Key = field.Name,
                                Value = child
                            });
                            memberCount++;
                        }
                        catch (Exception e)
                        {
                            member.Fields.Add(new SerializedMember.KV { Key = field.Name, Value = ErrorMember(member.Path + "." + field.Name, e.Message) });
                        }
                    }
                }

                if (options.IncludeProperties)
                {
                    foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (memberCount >= MaxMembers) { member.Truncated = true; break; }
                        if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                        if (!MemberAllowed(prop.Name, options)) continue;
                        try
                        {
                            var value = prop.GetValue(obj, null);
                            var child = ToMember(value, member.Path + "." + prop.Name, prop.PropertyType.FullName, options, visited, depth + 1);
                            if (!TypeAllowed(child.TypeName, options)) continue;
                            child.ReadOnly = !prop.CanWrite;
                            child.Writable = prop.CanWrite;
                            member.Props.Add(new SerializedMember.KV { Key = prop.Name, Value = child });
                            memberCount++;
                        }
                        catch (Exception e)
                        {
                            member.Props.Add(new SerializedMember.KV { Key = prop.Name, Value = ErrorMember(member.Path + "." + prop.Name, e.Message) });
                        }
                    }
                }
            }

            private static int ClampMaxArrayItems(int value)
            {
                return Math.Max(0, Math.Min(value <= 0 ? DefaultMaxArrayItems : value, AbsoluteMaxArrayItems));
            }

            private static bool PathAllowed(string path, Options options)
            {
                if (options.Paths == null || options.Paths.Count == 0) return true;
                foreach (var prefix in options.Paths)
                {
                    if (path == prefix || path.StartsWith(prefix + ".", StringComparison.Ordinal) || path.StartsWith(prefix + "[", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }

            private static bool MemberAllowed(string name, Options options)
            {
                if (options.NameFilters != null && options.NameFilters.Count > 0)
                {
                    return options.NameFilters.Contains(name);
                }
                if (!string.IsNullOrEmpty(options.NamePattern) && !Regex.IsMatch(name, options.NamePattern, RegexOptions.IgnoreCase))
                {
                    return false;
                }
                return true;
            }

            private static bool TypeAllowed(string typeName, Options options)
            {
                return string.IsNullOrEmpty(options.TypeFilter)
                    || (!string.IsNullOrEmpty(typeName) && typeName.IndexOf(options.TypeFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            private static bool IsSimpleType(Type type)
            {
                return type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime)
                    || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Vector2) || type == typeof(Vector3)
                    || type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Rect)
                    || type == typeof(Bounds) || type == typeof(LayerMask);
            }

            private static SerializedMember ErrorMember(string path, string message)
            {
                return new SerializedMember { Path = path, Error = message, Truncated = true };
            }

            private static int RuntimeHelpersHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }

            /// <summary>Unity Object 引用摘要 JSON。</summary>
            public static string UnityObjectRefJson(UnityEngine.Object uo)
            {
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("instanceId", uo.GetInstanceID());
                w.Prop("name", uo.name);
                w.Prop("typeName", uo.GetType().FullName);
                if (uo is GameObject go)
                {
                    w.Prop("kind", "GameObject");
                    w.Prop("path", GameObjectFullPath(go));
                }
                else
                {
                    w.Prop("kind", uo is Component ? "Component" : "Asset");
                    var assetPath = AssetDatabase.GetAssetPath(uo);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        w.Prop("assetPath", NormalizeAssetPath(assetPath));
                        w.Prop("guid", AssetDatabase.AssetPathToGUID(assetPath));
                    }
                }
                return w.EndObject().ToString();
            }

            private static string GameObjectFullPath(GameObject go)
            {
                var path = go.name;
                var t = go.transform.parent;
                while (t != null)
                {
                    path = t.name + "/" + path;
                    t = t.parent;
                }
                return path;
            }

            private static string MemberToJson(SerializedMember member)
            {
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("path", member.Path);
                w.Prop("typeName", member.TypeName);
                if (member.Error != null) w.Prop("error", member.Error);
                if (member.Truncated) w.Prop("truncated", true);
                if (member.IsNull) w.PropNull("value");
                else if (member.Value != null) WriteValue(w, member.Value);
                if (member.IsUnityObject) w.PropRaw("unityObjectRef", member.UnityObjectRef);
                if (member.IsArray)
                {
                    w.Prop("isArray", true);
                    w.Prop("arrayLength", (long)member.ArrayLength);
                    w.Prop("totalCount", (long)member.ArrayLength);
                    w.Prop("returnedCount", (long)member.ArrayItems.Count);
                    if (member.Truncated) w.Prop("nextPath", member.Path + "[" + member.ArrayItems.Count.ToString(CultureInfo.InvariantCulture) + "]");
                    w.PropArray("items", arr =>
                    {
                        foreach (var item in member.ArrayItems) arr.RawValue(MemberToJson(item));
                    });
                }
                if (member.IsDictionary)
                {
                    w.Prop("isDictionary", true);
                    w.Prop("totalCount", (long)member.ArrayLength);
                    w.Prop("returnedCount", (long)member.DictionaryEntries.Count);
                    w.PropArray("entries", arr =>
                    {
                        foreach (var entry in member.DictionaryEntries)
                        {
                            arr.BeginObject().Prop("key", entry.Key).PropRaw("value", MemberToJson(entry.Value)).EndObject();
                        }
                    });
                }
                if (member.Fields.Count > 0)
                {
                    w.PropArray("fields", arr =>
                    {
                        foreach (var f in member.Fields) arr.BeginObject().Prop("name", f.Key).PropRaw("value", MemberToJson(f.Value)).EndObject();
                    });
                }
                if (member.Props.Count > 0)
                {
                    w.PropArray("props", arr =>
                    {
                        foreach (var p in member.Props)
                        {
                            arr.BeginObject().Prop("name", p.Key).PropRaw("value", MemberToJson(p.Value));
                            if (p.Value.ReadOnly) arr.Prop("readOnly", true);
                            if (p.Value.Writable) arr.Prop("writable", true);
                            arr.EndObject();
                        }
                    });
                }
                return w.EndObject().ToString();
            }

            private static void WriteValue(UnityCliJsonWriter w, object value)
            {
                if (value is bool b) { w.Prop("value", b); return; }
                if (value is int i) { w.Prop("value", (long)i); return; }
                if (value is long l) { w.Prop("value", l); return; }
                if (value is float f) { w.Prop("value", f); return; }
                if (value is double d) { w.Prop("value", d); return; }
                if (value is Enum e) { w.Prop("value", Convert.ToString(e)); return; }
                if (value is Vector2 v2) { w.PropRaw("value", Vector2Json(v2)); return; }
                if (value is Vector3 v3) { w.PropRaw("value", Vector3Json(v3)); return; }
                if (value is Vector4 v4) { w.PropRaw("value", Vector4Json(v4)); return; }
                if (value is Quaternion q) { w.PropRaw("value", QuaternionJson(q)); return; }
                if (value is Color c) { w.PropRaw("value", ColorJson(c)); return; }
                if (value is string s) { w.Prop("value", s); return; }
                w.Prop("value", Convert.ToString(value, CultureInfo.InvariantCulture));
            }

            private static string Vector2Json(Vector2 v)
            {
                return new UnityCliJsonWriter().BeginObject().Prop("x", v.x).Prop("y", v.y).EndObject().ToString();
            }
            private static string Vector3Json(Vector3 v)
            {
                return new UnityCliJsonWriter().BeginObject().Prop("x", v.x).Prop("y", v.y).Prop("z", v.z).EndObject().ToString();
            }
            private static string Vector4Json(Vector4 v)
            {
                return new UnityCliJsonWriter().BeginObject().Prop("x", v.x).Prop("y", v.y).Prop("z", v.z).Prop("w", v.w).EndObject().ToString();
            }
            private static string QuaternionJson(Quaternion q)
            {
                return new UnityCliJsonWriter().BeginObject().Prop("x", q.x).Prop("y", q.y).Prop("z", q.z).Prop("w", q.w).EndObject().ToString();
            }
            private static string ColorJson(Color c)
            {
                return new UnityCliJsonWriter().BeginObject().Prop("r", c.r).Prop("g", c.g).Prop("b", c.b).Prop("a", c.a).EndObject().ToString();
            }

            /// <summary>SerializedMember 内存模型。</summary>
            private sealed class SerializedMember
            {
                public string Path;
                public string TypeName;
                public object Value;
                public bool IsNull;
                public bool IsUnityObject;
                public string UnityObjectRef;
                public bool IsArray;
                public bool IsDictionary;
                public int ArrayLength;
                public bool Truncated;
                public string Error;
                public bool ReadOnly;
                public bool Writable;
                public readonly List<KV> Fields = new List<KV>();
                public readonly List<KV> Props = new List<KV>();
                public readonly List<SerializedMember> ArrayItems = new List<SerializedMember>();
                public readonly List<KV> DictionaryEntries = new List<KV>();

                public sealed class KV
                {
                    public string Key;
                    public SerializedMember Value;
                }
            }
        }
    }
}
#endif
