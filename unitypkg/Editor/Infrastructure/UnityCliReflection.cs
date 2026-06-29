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
        /// 类型 JSON Schema 生成器：为 live C# / Unity 类型生成带 $defs 的结构化 schema。
        /// </summary>
        internal static class TypeSchemaGenerator
        {
            public static string Generate(string typeName, int maxDepth)
            {
                var type = ResolveType(typeName);
                if (type == null)
                {
                    throw new BridgeCommandException("E_TYPE_NOT_FOUND",
                        "Type not found: " + typeName,
                        "Pass a full type name such as UnityEngine.Transform or UnityEngine.Rigidbody.");
                }

                var context = new SchemaContext(type, Math.Max(0, maxDepth));
                var schema = BuildSchema(type, context, 0, true);
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("command", "type.schema");
                w.Prop("typeName", FriendlyTypeName(type));
                w.PropRaw("schema", schema);
                w.Prop("definitionCount", (long)context.Definitions.Count);
                w.Prop("logPath", LogPath());
                return w.EndObject().ToString();
            }

            /// <summary>递归生成 schema；复杂嵌套类型通过 $defs/$ref 去重，避免循环类型无限展开。</summary>
            private static string BuildSchema(Type type, SchemaContext context, int depth, bool isRoot)
            {
                var w = new UnityCliJsonWriter().BeginObject();
                WriteSchemaProperties(w, type, context, depth, isRoot);
                if (isRoot && context.Definitions.Count > 0)
                {
                    w.PropObject("$defs", defs =>
                    {
                        foreach (var key in context.Definitions.Keys.OrderBy(k => k, StringComparer.Ordinal))
                        {
                            defs.PropRaw(key, context.Definitions[key]);
                        }
                    });
                }
                return w.EndObject().ToString();
            }

            /// <summary>按类型类别写入 JSON Schema 字段。</summary>
            private static void WriteSchemaProperties(UnityCliJsonWriter w, Type type, SchemaContext context, int depth, bool isRoot)
            {
                if (type == null)
                {
                    w.Prop("type", "null");
                    return;
                }
                if (type.IsByRef || type.IsPointer)
                {
                    w.Prop("type", "string").Prop("typeName", FriendlyTypeName(type));
                    return;
                }
                if (type.IsGenericParameter)
                {
                    w.Prop("type", "object").Prop("typeName", type.Name).Prop("genericParameter", true);
                    return;
                }
                if (IsNullable(type))
                {
                    var nullableValueType = Nullable.GetUnderlyingType(type);
                    w.PropArray("anyOf", arr => arr.RawValue(SchemaFor(nullableValueType, context, depth + 1)).BeginObject().Prop("type", "null").EndObject());
                    w.Prop("typeName", FriendlyTypeName(type));
                    return;
                }
                if (IsPrimitiveLike(type))
                {
                    w.Prop("type", JsonTypeFor(type)).Prop("typeName", FriendlyTypeName(type));
                    return;
                }
                if (type.IsEnum)
                {
                    w.Prop("type", "string").Prop("typeName", FriendlyTypeName(type));
                    w.PropArray("enum", arr => { foreach (var v in Enum.GetNames(type)) arr.Value(v); });
                    return;
                }

                Type keyType;
                Type dictionaryValueType;
                if (TryGetDictionaryTypes(type, out keyType, out dictionaryValueType))
                {
                    w.Prop("type", "object").Prop("typeName", FriendlyTypeName(type));
                    WriteGenericMetadata(w, type);
                    w.PropRaw("propertyNames", DictionaryKeySchema(keyType));
                    w.PropRaw("additionalProperties", depth >= context.MaxDepth ? TruncatedSchema(dictionaryValueType) : SchemaFor(dictionaryValueType, context, depth + 1));
                    return;
                }

                var elementType = GetEnumerableElementType(type);
                if (elementType != null)
                {
                    w.Prop("type", "array").Prop("typeName", FriendlyTypeName(type));
                    if (type.IsArray) w.Prop("arrayRank", (long)type.GetArrayRank());
                    WriteGenericMetadata(w, type);
                    w.PropRaw("items", depth >= context.MaxDepth ? TruncatedSchema(elementType) : SchemaFor(elementType, context, depth + 1));
                    return;
                }

                w.Prop("type", "object").Prop("typeName", FriendlyTypeName(type));
                WriteGenericMetadata(w, type);
                if (depth >= context.MaxDepth)
                {
                    w.Prop("truncated", true);
                    return;
                }
                w.PropObject("properties", props =>
                {
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(f => f.Name, StringComparer.Ordinal))
                    {
                        props.PropRaw(field.Name, SchemaFor(field.FieldType, context, depth + 1));
                    }
                    foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                        props.PropRaw(prop.Name, SchemaFor(prop.PropertyType, context, depth + 1));
                    }
                });
            }

            /// <summary>复杂类型使用 $ref，基础/集合类型直接内联。</summary>
            private static string SchemaFor(Type type, SchemaContext context, int depth)
            {
                if (type == null || IsPrimitiveLike(type) || type.IsEnum || type.IsByRef || type.IsPointer || type.IsGenericParameter || IsNullable(type) || TryGetDictionaryTypes(type, out _, out _) || GetEnumerableElementType(type) != null)
                {
                    return BuildSchema(type, context, depth, false);
                }
                if (type == context.RootType)
                {
                    return new UnityCliJsonWriter().BeginObject().Prop("$ref", "#").EndObject().ToString();
                }
                var key = DefinitionKey(type);
                if (!context.Definitions.ContainsKey(key) && !context.BuildingDefinitions.Contains(key))
                {
                    context.BuildingDefinitions.Add(key);
                    context.Definitions[key] = BuildSchema(type, context, depth, false);
                    context.BuildingDefinitions.Remove(key);
                }
                return new UnityCliJsonWriter().BeginObject().Prop("$ref", "#/$defs/" + key).EndObject().ToString();
            }

            /// <summary>为字典 key 生成 propertyNames schema；JSON object key 始终按字符串表达。</summary>
            private static string DictionaryKeySchema(Type keyType)
            {
                var w = new UnityCliJsonWriter().BeginObject();
                if (keyType != null && keyType.IsEnum)
                {
                    w.Prop("type", "string");
                    w.PropArray("enum", arr => { foreach (var v in Enum.GetNames(keyType)) arr.Value(v); });
                }
                else
                {
                    w.Prop("type", "string");
                    if (keyType != null) w.Prop("typeName", FriendlyTypeName(keyType));
                }
                return w.EndObject().ToString();
            }

            /// <summary>生成深度截断占位 schema，保留 typeName 便于 AI 后续调高 maxDepth。</summary>
            private static string TruncatedSchema(Type type)
            {
                return new UnityCliJsonWriter().BeginObject()
                    .Prop("type", "object")
                    .Prop("typeName", FriendlyTypeName(type))
                    .Prop("truncated", true)
                    .EndObject()
                    .ToString();
            }

            /// <summary>写入泛型定义和实参，帮助 AI 区分 List&lt;T&gt;、Dictionary&lt;K,V&gt; 等闭合类型。</summary>
            private static void WriteGenericMetadata(UnityCliJsonWriter w, Type type)
            {
                if (!type.IsGenericType) return;
                w.Prop("genericTypeDefinition", FriendlyTypeName(type.GetGenericTypeDefinition()));
                w.PropArray("genericArguments", arr => { foreach (var arg in type.GetGenericArguments()) arr.Value(FriendlyTypeName(arg)); });
            }

            /// <summary>识别 IDictionary/IReadOnlyDictionary，并返回 key/value 类型。</summary>
            private static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
            {
                keyType = null;
                valueType = null;
                if (type == null || type == typeof(string)) return false;
                foreach (var candidate in type.GetInterfaces().Concat(new[] { type }))
                {
                    if (!candidate.IsGenericType) continue;
                    var definition = candidate.GetGenericTypeDefinition();
                    if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                    {
                        var args = candidate.GetGenericArguments();
                        keyType = args[0];
                        valueType = args[1];
                        return true;
                    }
                }
                return false;
            }

            /// <summary>识别数组和常见泛型集合的元素类型，string 不视为集合。</summary>
            private static Type GetEnumerableElementType(Type type)
            {
                if (type == null || type == typeof(string)) return null;
                if (type.IsArray) return type.GetElementType();
                foreach (var candidate in type.GetInterfaces().Concat(new[] { type }))
                {
                    if (!candidate.IsGenericType) continue;
                    var definition = candidate.GetGenericTypeDefinition();
                    if (definition == typeof(IEnumerable<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>))
                    {
                        return candidate.GetGenericArguments()[0];
                    }
                }
                return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
            }

            /// <summary>把 CLR 基础类型映射到 JSON Schema 基础类型。</summary>
            private static string JsonTypeFor(Type type)
            {
                if (type == typeof(bool)) return "boolean";
                if (type == typeof(string) || type == typeof(char) || type == typeof(DateTime)) return "string";
                if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
                    || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte)) return "integer";
                if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
                return "object";
            }

            /// <summary>判断是否可安全内联为基础 JSON Schema，不进入 $defs。</summary>
            private static bool IsPrimitiveLike(Type type)
            {
                return type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime);
            }

            /// <summary>判断 Nullable&lt;T&gt;，用于输出 anyOf: [T, null]。</summary>
            private static bool IsNullable(Type type)
            {
                return type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
            }

            /// <summary>输出稳定、可读的类型名，泛型类型展开为 Foo&lt;Bar&gt;。</summary>
            private static string FriendlyTypeName(Type type)
            {
                if (type == null) return null;
                if (type.IsArray) return FriendlyTypeName(type.GetElementType()) + "[]";
                if (!type.IsGenericType) return type.FullName ?? type.Name;
                var baseName = type.GetGenericTypeDefinition().FullName ?? type.GetGenericTypeDefinition().Name;
                var tick = baseName.IndexOf('`');
                if (tick >= 0) baseName = baseName.Substring(0, tick);
                return baseName + "<" + string.Join(",", type.GetGenericArguments().Select(FriendlyTypeName).ToArray()) + ">";
            }

            /// <summary>把复杂类型名转换为可作为 $defs key 的稳定标识。</summary>
            private static string DefinitionKey(Type type)
            {
                var name = FriendlyTypeName(type) ?? "Unknown";
                return new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            }

            internal static Type ResolveType(string fullName)
            {
                return FindType(fullName);
            }

            private sealed class SchemaContext
            {
                public readonly Type RootType;
                public readonly int MaxDepth;
                public readonly Dictionary<string, string> Definitions = new Dictionary<string, string>();
                public readonly HashSet<string> BuildingDefinitions = new HashSet<string>();

                public SchemaContext(Type rootType, int maxDepth)
                {
                    RootType = rootType;
                    MaxDepth = maxDepth;
                }
            }
        }

        /// <summary>
        /// 反射方法查找与调用：对齐 IvanMurzak reflection-method-find/call。
        /// 不做范围限制（public/private/static/instance/namespace/assembly/Editor/Runtime 均可）。
        /// method.call 标高风险，要求 --yes，返回 target preview、audit、异常与结果序列化。
        /// </summary>
        internal static class ReflectionInvoker
        {
            /// <summary>查找方法：返回匹配的方法签名列表。</summary>
            public static string FindMethods(string typeName, string methodName, BindingFlags bindings)
            {
                var type = TypeSchemaGenerator.ResolveType(typeName);
                if (type == null)
                {
                    throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name such as UnityEngine.Transform.");
                }
                var methods = type.GetMethods(bindings)
                    .Where(m => string.IsNullOrEmpty(methodName) || string.Equals(m.Name, methodName, StringComparison.Ordinal))
                    .ToList();
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("command", "reflection.method.find");
                w.Prop("typeName", type.FullName);
                w.Prop("count", (long)methods.Count);
                w.PropArray("methods", arr =>
                {
                    foreach (var m in methods)
                    {
                        arr.BeginObject()
                            .Prop("name", m.Name)
                            .Prop("isStatic", m.IsStatic)
                            .Prop("isPublic", m.IsPublic)
                            .Prop("isPrivate", m.IsPrivate)
                            .Prop("returnType", m.ReturnType.FullName)
                            .Prop("declaringType", m.DeclaringType.FullName)
                            .PropArray("parameters", parr =>
                            {
                                foreach (var p in m.GetParameters())
                                {
                                    parr.BeginObject()
                                        .Prop("name", p.Name)
                                        .Prop("parameterType", p.ParameterType.FullName)
                                        .Prop("hasDefaultValue", p.HasDefaultValue)
                                        .EndObject();
                                }
                            })
                            .EndObject();
                    }
                });
                w.Prop("logPath", LogPath());
                return w.EndObject().ToString();
            }

            /// <summary>调用方法：返回结果、异常、audit。</summary>
            public static string CallMethod(string typeName, string methodName, bool isStatic, string argsJson, string instanceRefJson, BindingFlags bindings)
            {
                var type = TypeSchemaGenerator.ResolveType(typeName);
                if (type == null)
                {
                    throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name.");
                }
                var method = type.GetMethod(methodName, bindings);
                if (method == null)
                {
                    throw new BridgeCommandException("E_METHOD_NOT_FOUND",
                        "Method not found: " + typeName + "." + methodName,
                        "Run reflection.method.find to discover available methods.");
                }

                // 解析参数
                var paramList = method.GetParameters();
                var args = ResolveCallArguments(argsJson, paramList);

                // 解析实例（非静态）
                object instance = null;
                if (!method.IsStatic)
                {
                    instance = ResolveCallInstance(instanceRefJson, type);
                    if (instance == null)
                    {
                        throw new BridgeCommandException("E_METHOD_INSTANCE_REQUIRED",
                            "Instance method " + methodName + " requires an instance reference.",
                            "Pass instanceRef with instanceId/assetPath/path.");
                    }
                }

                var audit = new UnityCliJsonWriter().BeginObject();
                audit.Prop("command", "reflection.method.call");
                audit.Prop("typeName", type.FullName);
                audit.Prop("methodName", method.Name);
                audit.Prop("isStatic", method.IsStatic);
                audit.Prop("returnType", method.ReturnType.FullName);

                try
                {
                    var result = method.Invoke(instance, args);
                    audit.Prop("ok", true);
                    audit.PropRaw("result", SerializeCallResult(result));
                    audit.PropArray("parameters", arr =>
                    {
                        for (var i = 0; i < paramList.Length; i++)
                        {
                            arr.BeginObject().Prop("name", paramList[i].Name).PropRaw("value", SerializeCallResult(args[i])).EndObject();
                        }
                    });
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    audit.Prop("ok", false);
                    audit.Prop("error", inner.Message);
                    audit.Prop("errorType", inner.GetType().FullName);
                    audit.Prop("stackTrace", inner.StackTrace);
                }
                catch (Exception e)
                {
                    audit.Prop("ok", false);
                    audit.Prop("error", e.Message);
                    audit.Prop("errorType", e.GetType().FullName);
                }
                audit.Prop("logPath", LogPath());
                return audit.EndObject().ToString();
            }

            private static object[] ResolveCallArguments(string argsJson, ParameterInfo[] parameters)
            {
                if (parameters.Length == 0) return new object[0];
                var parsed = string.IsNullOrEmpty(argsJson) ? new List<object>() : (ParseArray(argsJson) ?? new List<object>());
                // 也支持对象形式 {argName: value}
                var objForm = string.IsNullOrEmpty(argsJson) ? null : ParseObject(argsJson);
                var result = new object[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    object value = null;
                    if (objForm != null && objForm.ContainsKey(p.Name))
                    {
                        value = objForm[p.Name];
                    }
                    else if (i < parsed.Count)
                    {
                        value = parsed[i];
                    }
                    else if (p.HasDefaultValue)
                    {
                        value = p.DefaultValue;
                    }
                    else
                    {
                        value = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                    }
                    result[i] = ObjectPatch.ConvertValue(value, p.ParameterType);
                }
                return result;
            }

            private static object ResolveCallInstance(string instanceRefJson, Type expectedType)
            {
                if (string.IsNullOrEmpty(instanceRefJson)) return null;
                var dict = ObjectPatch.ParseObject(instanceRefJson);
                if (dict == null) return null;
                if (dict.TryGetValue("instanceId", out var iid))
                {
                    var id = Convert.ToInt32(iid, CultureInfo.InvariantCulture);
                    if (id != 0)
                    {
                        var obj = EditorUtility.InstanceIDToObject(id);
                        if (obj != null && expectedType.IsInstanceOfType(obj)) return obj;
                    }
                }
                if (dict.TryGetValue("assetPath", out var ap) && ap is string path)
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(path);
                    if (obj != null && expectedType.IsInstanceOfType(obj)) return obj;
                }
                if (dict.TryGetValue("path", out var p) && p is string goPath && typeof(Component).IsAssignableFrom(expectedType))
                {
                    var go = ObjectRefResolver.FindGameObjectByPath(goPath);
                    if (go != null) return go.GetComponent(expectedType);
                }
                return null;
            }

            private static string SerializeCallResult(object result)
            {
                if (result == null) return "null";
                var options = new ObjectSerializer.Options { MaxDepth = 2, IncludeFields = false, IncludeProperties = true, DeepSerialization = false };
                try
                {
                    return ObjectSerializer.Serialize(result, options);
                }
                catch
                {
                    return UnityCliJsonWriter.Quote(Convert.ToString(result, CultureInfo.InvariantCulture));
                }
            }

            private static List<object> ParseArray(string json)
            {
                var trimmed = (json ?? "").Trim();
                if (trimmed.Length < 2 || trimmed[0] != '[') return null;
                var index = 1;
                var list = new List<object>();
                while (index < trimmed.Length && trimmed[index] != ']')
                {
                    list.Add(ParseValue(trimmed, ref index));
                    if (index < trimmed.Length && trimmed[index] == ',') index++;
                }
                return list;
            }

            private static Dictionary<string, object> ParseObject(string json)
            {
                var trimmed = (json ?? "").Trim();
                if (trimmed.Length < 2 || trimmed[0] != '{') return null;
                var index = 1;
                return ParseObjectAt(trimmed, ref index);
            }

            private static Dictionary<string, object> ParseObjectAt(string json, ref int index)
            {
                var dict = new Dictionary<string, object>();
                while (index < json.Length && json[index] != '}')
                {
                    var key = ParseString(json, ref index);
                    if (index < json.Length && json[index] == ':') index++;
                    var value = ParseValue(json, ref index);
                    if (key != null) dict[key] = value;
                    if (index < json.Length && json[index] == ',') index++;
                }
                if (index < json.Length && json[index] == '}') index++;
                return dict;
            }

            private static object ParseValue(string json, ref int index)
            {
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                if (index >= json.Length) return null;
                var ch = json[index];
                if (ch == '"') return ParseString(json, ref index);
                if (ch == '{') { index++; return ParseObjectAt(json, ref index); }
                if (ch == '[') return ParseArray(json, ref index);
                if (ch == '-' || char.IsDigit(ch)) return ParseNumber(json, ref index);
                if (MatchLit(json, ref index, "true")) return true;
                if (MatchLit(json, ref index, "false")) return false;
                if (MatchLit(json, ref index, "null")) return null;
                return null;
            }

            private static List<object> ParseArray(string json, ref int index)
            {
                index++;
                var list = new List<object>();
                while (index < json.Length && json[index] != ']')
                {
                    list.Add(ParseValue(json, ref index));
                    if (index < json.Length && json[index] == ',') index++;
                }
                if (index < json.Length && json[index] == ']') index++;
                return list;
            }

            private static string ParseString(string json, ref int index)
            {
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
                if (index >= json.Length || json[index] != '"') return null;
                index++;
                var sb = new System.Text.StringBuilder();
                while (index < json.Length)
                {
                    var c = json[index++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\' && index < json.Length) { sb.Append(json[index++]); }
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
                if (text.Contains(".")) return double.Parse(text, CultureInfo.InvariantCulture);
                return long.Parse(text, CultureInfo.InvariantCulture);
            }

            private static bool MatchLit(string json, ref int index, string lit)
            {
                if (index + lit.Length > json.Length) return false;
                if (string.CompareOrdinal(json, index, lit, 0, lit.Length) != 0) return false;
                index += lit.Length;
                return true;
            }
        }

        /// <summary>
        /// 审计工具：为高风险操作记录结构化 audit。
        /// </summary>
        internal static class AuditTrail
        {
            public static string Entry(string command, string action, string target, bool confirmed, string fingerprint)
            {
                var w = new UnityCliJsonWriter().BeginObject();
                w.Prop("command", command);
                w.Prop("action", action);
                w.Prop("target", target);
                w.Prop("confirmed", confirmed);
                if (!string.IsNullOrEmpty(fingerprint)) w.Prop("fingerprint", fingerprint);
                w.Prop("timestamp", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                w.Prop("logPath", LogPath());
                return w.EndObject().ToString();
            }
        }

        // ── 保留的原有反射辅助方法（被 ContextCommands/GraphicsCommands/EditorCommands 等复用） ──

        private static object MemberValue(Type type, object instance, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null) return property.GetValue(instance, null);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field == null ? null : field.GetValue(instance);
        }

        private static string AssetReferenceJson(UnityEngine.Object asset)
        {
            if (asset == null) return "null";
            var path = NormalizeAssetPath(AssetDatabase.GetAssetPath(asset));
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", asset.name)
                .Prop("path", path)
                .Prop("guid", string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path))
                .EndObject().ToString();
        }

        private static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            var type = Type.GetType(fullName);
            if (type != null)
            {
                return type;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName);
                }
                catch
                {
                    // 忽略加载失败的程序集
                }
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }
    }
}
#endif
