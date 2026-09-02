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
        /// <summary>类型 JSON Schema 生成器：为 live C# / Unity 类型生成带 $defs 的结构化 schema。</summary>
        internal static class TypeSchemaGenerator
        {
            private static readonly Dictionary<string, Type> ResolvedTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
            private static string cacheGeneration;

            internal sealed class Page
            {
                internal Dictionary<string, object> Data;
                internal bool Truncated;
                internal string NextCursor;
                internal string Generation;
            }

            /// <summary>根成员按稳定 ordinal 分页；递归 definition/member/reflection 全部共用调用方 budget。</summary>
            public static Page Generate(string typeName, int maxDepth, int maxMembers, int maxDefinitions, int pageSize, string cursor, UnityCliRequestBudget budget)
            {
                var type = ResolveType(typeName, budget);
                if (type == null) throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name such as UnityEngine.Transform or UnityEngine.Rigidbody.");
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public).Cast<MemberInfo>();
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.CanRead && property.GetIndexParameters().Length == 0).Cast<MemberInfo>();
                var members = fields.Concat(properties).OrderBy(member => member.Name, StringComparer.Ordinal).ToArray();
                var generation = UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Reflection() + "\0" + type.AssemblyQualifiedName + "\0" + maxDepth.ToString(CultureInfo.InvariantCulture)
                    + "\0" + maxMembers.ToString(CultureInfo.InvariantCulture) + "\0" + maxDefinitions.ToString(CultureInfo.InvariantCulture));
                var offset = 0;
                if (!string.IsNullOrEmpty(cursor))
                {
                    string position;
                    if (!UnityCliContinuationToken.TryParse(cursor, "type.schema", generation, out position)
                        || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset > members.Length)
                        throw new BridgeCommandException("E_CONTINUATION_STALE", "The type.schema continuation does not match the current assembly generation and limits.", "Start a new type.schema read without cursor.");
                }
                var context = new SchemaContext(type, maxDepth, maxMembers, maxDefinitions, budget);
                var rootProperties = new Dictionary<string, object>();
                var index = offset;
                while (index < members.Length && rootProperties.Count < pageSize)
                {
                    if (!context.TryMember() || !budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) break;
                    var member = members[index];
                    rootProperties[member.Name] = SchemaFor(MemberType(member), context, 1);
                    if (budget.IsExhausted) { rootProperties.Remove(member.Name); break; }
                    index++;
                }
                var schema = new Dictionary<string, object>
                {
                    { "type", "object" }, { "typeName", FriendlyTypeName(type) }, { "properties", rootProperties }
                };
                if (context.Definitions.Count > 0) schema["$defs"] = context.Definitions;
                var next = index < members.Length
                    ? UnityCliContinuationToken.Create("type.schema", generation, index.ToString(CultureInfo.InvariantCulture))
                    : null;
                return new Page
                {
                    Truncated = next != null || context.Truncated,
                    NextCursor = next,
                    Generation = generation,
                    Data = new Dictionary<string, object>
                    {
                        { "command", "type.schema" }, { "typeName", FriendlyTypeName(type) }, { "schema", schema },
                        { "memberCount", (long)members.Length }, { "returnedMemberCount", (long)rootProperties.Count },
                        { "definitionCount", (long)context.Definitions.Count }
                    }
                };
            }

            private static Type MemberType(MemberInfo member)
            {
                var field = member as FieldInfo;
                return field != null ? field.FieldType : ((PropertyInfo)member).PropertyType;
            }

            /// <summary>递归生成 schema；复杂嵌套类型通过 $defs/$ref 去重，避免循环类型无限展开。</summary>
            private static Dictionary<string, object> BuildSchema(Type type, SchemaContext context, int depth, bool isRoot)
            {
                var schema = SchemaProperties(type, context, depth);
                if (isRoot && context.Definitions.Count > 0) schema["$defs"] = context.Definitions;
                return schema;
            }

            /// <summary>按类型类别构造 JSON Schema 字段。</summary>
            private static Dictionary<string, object> SchemaProperties(Type type, SchemaContext context, int depth)
            {
                if (!context.Budget.TryConsumeNode()) { context.Truncated = true; return TruncatedSchema(type); }
                if (type == null) return new Dictionary<string, object> { { "type", "null" } };
                if (type.IsByRef || type.IsPointer) return new Dictionary<string, object> { { "type", "string" }, { "typeName", FriendlyTypeName(type) } };
                if (type.IsGenericParameter) return new Dictionary<string, object> { { "type", "object" }, { "typeName", type.Name }, { "genericParameter", true } };
                if (IsNullable(type))
                {
                    return new Dictionary<string, object>
                    {
                        { "anyOf", new List<object> { SchemaFor(Nullable.GetUnderlyingType(type), context, depth + 1), new Dictionary<string, object> { { "type", "null" } } } },
                        { "typeName", FriendlyTypeName(type) }
                    };
                }
                if (IsPrimitiveLike(type)) return new Dictionary<string, object> { { "type", JsonTypeFor(type) }, { "typeName", FriendlyTypeName(type) } };
                if (type.IsEnum)
                {
                    if (!context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; return TruncatedSchema(type); }
                    var values = new List<object>();
                    foreach (var value in Enum.GetNames(type))
                    {
                        if (!context.TryMember() || !context.Budget.TryReserveSerializedUtf8(value)) { context.Truncated = true; break; }
                        values.Add(value);
                    }
                    return new Dictionary<string, object> { { "type", "string" }, { "typeName", FriendlyTypeName(type) }, { "enum", values }, { "truncated", context.Truncated } };
                }

                if (TryGetDictionaryTypes(type, out var keyType, out var dictionaryValueType))
                {
                    var dictionarySchema = new Dictionary<string, object> { { "type", "object" }, { "typeName", FriendlyTypeName(type) } };
                    WriteGenericMetadata(dictionarySchema, type);
                    dictionarySchema["propertyNames"] = DictionaryKeySchema(keyType);
                    dictionarySchema["additionalProperties"] = depth >= context.MaxDepth ? TruncatedSchema(dictionaryValueType) : SchemaFor(dictionaryValueType, context, depth + 1);
                    return dictionarySchema;
                }

                var elementType = GetEnumerableElementType(type);
                if (elementType != null)
                {
                    var arraySchema = new Dictionary<string, object> { { "type", "array" }, { "typeName", FriendlyTypeName(type) } };
                    if (type.IsArray) arraySchema["arrayRank"] = (long)type.GetArrayRank();
                    WriteGenericMetadata(arraySchema, type);
                    arraySchema["items"] = depth >= context.MaxDepth ? TruncatedSchema(elementType) : SchemaFor(elementType, context, depth + 1);
                    return arraySchema;
                }

                var objectSchema = new Dictionary<string, object> { { "type", "object" }, { "typeName", FriendlyTypeName(type) } };
                WriteGenericMetadata(objectSchema, type);
                if (depth >= context.MaxDepth)
                {
                    objectSchema["truncated"] = true;
                    return objectSchema;
                }
                var properties = new Dictionary<string, object>();
                if (!context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; objectSchema["truncated"] = true; return objectSchema; }
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    if (!context.TryMember() || !context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; break; }
                    properties[field.Name] = SchemaFor(field.FieldType, context, depth + 1);
                    if (context.Budget.IsExhausted) break;
                }
                if (!context.Budget.IsExhausted && context.MemberCount < context.MaxMembers)
                {
                    if (!context.Budget.TryConsumeReflectionCall()) context.Truncated = true;
                    else foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(value => value.Name, StringComparer.Ordinal))
                    {
                        if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                        if (!context.TryMember() || !context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; break; }
                        properties[property.Name] = SchemaFor(property.PropertyType, context, depth + 1);
                        if (context.Budget.IsExhausted) break;
                    }
                }
                if (context.Truncated) objectSchema["truncated"] = true;
                objectSchema["properties"] = properties;
                return objectSchema;
            }

            /// <summary>复杂类型使用 $ref，基础/集合类型直接内联。</summary>
            private static object SchemaFor(Type type, SchemaContext context, int depth)
            {
                if (type == null || IsPrimitiveLike(type) || type.IsEnum || type.IsByRef || type.IsPointer || type.IsGenericParameter || IsNullable(type) || TryGetDictionaryTypes(type, out _, out _) || GetEnumerableElementType(type) != null)
                {
                    return BuildSchema(type, context, depth, false);
                }
                if (type == context.RootType) return new Dictionary<string, object> { { "$ref", "#" } };
                var key = DefinitionKey(type);
                if (!context.Definitions.ContainsKey(key) && !context.BuildingDefinitions.Contains(key))
                {
                    if (context.Definitions.Count >= context.MaxDefinitions || !context.Budget.TryConsumeReflectionCall())
                    {
                        context.Truncated = true;
                        return TruncatedSchema(type);
                    }
                    context.BuildingDefinitions.Add(key);
                    context.Definitions[key] = BuildSchema(type, context, depth, false);
                    context.BuildingDefinitions.Remove(key);
                }
                return new Dictionary<string, object> { { "$ref", "#/$defs/" + key } };
            }

            private static Dictionary<string, object> DictionaryKeySchema(Type keyType)
            {
                var schema = new Dictionary<string, object> { { "type", "string" } };
                if (keyType != null && keyType.IsEnum) schema["enum"] = Enum.GetNames(keyType).Cast<object>().ToList();
                else if (keyType != null) schema["typeName"] = FriendlyTypeName(keyType);
                return schema;
            }

            private static Dictionary<string, object> TruncatedSchema(Type type)
            {
                return new Dictionary<string, object> { { "type", "object" }, { "typeName", FriendlyTypeName(type) }, { "truncated", true } };
            }

            private static void WriteGenericMetadata(Dictionary<string, object> schema, Type type)
            {
                if (!type.IsGenericType) return;
                schema["genericTypeDefinition"] = FriendlyTypeName(type.GetGenericTypeDefinition());
                schema["genericArguments"] = type.GetGenericArguments().Select(FriendlyTypeName).Cast<object>().ToList();
            }

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

            private static Type GetEnumerableElementType(Type type)
            {
                if (type == null || type == typeof(string)) return null;
                if (type.IsArray) return type.GetElementType();
                foreach (var candidate in type.GetInterfaces().Concat(new[] { type }))
                {
                    if (!candidate.IsGenericType) continue;
                    var definition = candidate.GetGenericTypeDefinition();
                    if (definition == typeof(IEnumerable<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>)) return candidate.GetGenericArguments()[0];
                }
                return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
            }

            private static string JsonTypeFor(Type type)
            {
                if (type == typeof(bool)) return "boolean";
                if (type == typeof(string) || type == typeof(char) || type == typeof(DateTime)) return "string";
                if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte)) return "integer";
                if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
                return "object";
            }

            private static bool IsPrimitiveLike(Type type) => type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime);
            private static bool IsNullable(Type type) => type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);

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

            private static string DefinitionKey(Type type) => new string((FriendlyTypeName(type) ?? "Unknown").Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

            internal static Type ResolveType(string fullName)
            {
                return FindType(fullName);
            }

            /// <summary>assembly generation 改变时整体失效；命中缓存不再重复扫描 AppDomain。</summary>
            internal static Type ResolveType(string fullName, UnityCliRequestBudget budget)
            {
                if (string.IsNullOrEmpty(fullName)) return null;
                var generation = UnityCliReadGeneration.Reflection();
                if (!string.Equals(cacheGeneration, generation, StringComparison.Ordinal))
                {
                    ResolvedTypeCache.Clear();
                    cacheGeneration = generation;
                }
                Type cached;
                if (ResolvedTypeCache.TryGetValue(fullName, out cached)) return cached;
                if (!budget.TryConsumeReflectionCall()) return null;
                try { cached = Type.GetType(fullName, false); } catch { cached = null; }
                if (cached == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!budget.TryConsumeReflectionCall()) return null;
                        try { cached = assembly.GetType(fullName, false); } catch { cached = null; }
                        if (cached != null) break;
                    }
                }
                if (cached != null) ResolvedTypeCache[fullName] = cached;
                return cached;
            }

            private sealed class SchemaContext
            {
                public readonly Type RootType;
                public readonly int MaxDepth;
                public readonly int MaxMembers;
                public readonly int MaxDefinitions;
                public readonly UnityCliRequestBudget Budget;
                public readonly Dictionary<string, object> Definitions = new Dictionary<string, object>();
                public readonly HashSet<string> BuildingDefinitions = new HashSet<string>();
                public int MemberCount;
                public bool Truncated;

                public SchemaContext(Type rootType, int maxDepth, int maxMembers, int maxDefinitions, UnityCliRequestBudget budget)
                {
                    RootType = rootType; MaxDepth = maxDepth; MaxMembers = maxMembers; MaxDefinitions = maxDefinitions; Budget = budget;
                }

                public bool TryMember()
                {
                    if (MemberCount >= MaxMembers) { Truncated = true; return false; }
                    MemberCount++;
                    return true;
                }
            }
        }

        /// <summary>反射方法查找与调用：不限制 public/private/static/instance/namespace/assembly/Editor/Runtime 范围。</summary>
        internal static class ReflectionInvoker
        {
            internal sealed class MethodPage
            {
                internal List<ReflectionMethodDescriptor> Methods;
                internal int Total;
                internal string NextCursor;
                internal string Generation;
                internal bool Truncated;
            }

            /// <summary>MethodInfo array 只作 runtime reflection snapshot；descriptor 按 raw ordinal 逐项构造并受全局预算约束。</summary>
            public static MethodPage FindMethods(string typeName, string methodName, BindingFlags bindings, int pageSize, string cursor, UnityCliRequestBudget budget)
            {
                var type = TypeSchemaGenerator.ResolveType(typeName, budget);
                if (type == null)
                {
                    if (budget.IsExhausted) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name such as UnityEngine.Transform.");
                }
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var methods = type.GetMethods(bindings)
                    .Where(method => string.IsNullOrEmpty(methodName) || string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    .OrderBy(method => method.MetadataToken).ThenBy(method => method.Name, StringComparer.Ordinal).ToArray();
                var generation = UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Reflection() + "\0" + type.AssemblyQualifiedName + "\0"
                    + ((int)bindings).ToString(CultureInfo.InvariantCulture) + "\0" + methodName + "\0" + methods.Length.ToString(CultureInfo.InvariantCulture));
                var offset = 0;
                if (!string.IsNullOrEmpty(cursor))
                {
                    string position;
                    if (!UnityCliContinuationToken.TryParse(cursor, "reflection.method.find", generation, out position)
                        || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset > methods.Length)
                        throw new BridgeCommandException("E_CONTINUATION_STALE", "The reflection method continuation does not match the current assembly generation and selector.", "Start a new reflection.method.find read without cursor.");
                }
                var result = new List<ReflectionMethodDescriptor>();
                var index = offset;
                while (index < methods.Length && result.Count < pageSize)
                {
                    if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeNode() || !budget.TryConsumeReflectionCall()) break;
                    var method = methods[index];
                    var parameters = method.GetParameters();
                    var parameterResults = new List<ReflectionParameterDescriptor>();
                    var complete = true;
                    foreach (var parameter in parameters)
                    {
                        if (!budget.TryConsumeNode() || !budget.TryConsumeReflectionCall()) { complete = false; break; }
                        parameterResults.Add(new ReflectionParameterDescriptor { Name = parameter.Name, ParameterType = parameter.ParameterType.FullName, HasDefaultValue = parameter.HasDefaultValue });
                    }
                    if (!complete || !budget.TryReserveSerializedUtf8(method.Name) || !budget.TryReserveSerializedUtf8(method.ReturnType.FullName)) break;
                    result.Add(new ReflectionMethodDescriptor
                    {
                        Name = method.Name, IsStatic = method.IsStatic, IsPublic = method.IsPublic, IsPrivate = method.IsPrivate,
                        ReturnType = method.ReturnType.FullName, DeclaringType = method.DeclaringType.FullName, Parameters = parameterResults
                    });
                    index++;
                }
                var next = index < methods.Length ? UnityCliContinuationToken.Create("reflection.method.find", generation, index.ToString(CultureInfo.InvariantCulture)) : null;
                return new MethodPage { Methods = result, Total = methods.Length, NextCursor = next, Generation = generation, Truncated = next != null };
            }


            internal sealed class PreparedMethodCall
            {
                internal Type Type;
                internal MethodInfo Method;
                internal ParameterInfo[] Parameters;
                internal object[] Arguments;
                internal object Instance;
            }

            /// <summary>effect 前完整解析 type、overload、参数与 instance；不会调用目标方法。</summary>
            public static PreparedMethodCall PrepareCall(string typeName, string methodName, object argsValue, List<string> parameterTypes, Dictionary<string, object> instanceRef, BindingFlags bindings)
            {
                var type = TypeSchemaGenerator.ResolveType(typeName);
                if (type == null) throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name.");
                var method = ResolveCallMethod(type, methodName, argsValue, parameterTypes, bindings);
                RequireSynchronousResult(method);
                var parameters = method.GetParameters();
                var arguments = ResolveCallArguments(argsValue, parameters);
                object instance = null;
                if (!method.IsStatic)
                {
                    instance = ResolveCallInstance(instanceRef, type);
                    if (instance == null) throw new BridgeCommandException("E_METHOD_INSTANCE_REQUIRED", "Instance method " + methodName + " requires an instance reference.", "Pass instanceRef with instanceId/assetPath/path.");
                }
                return new PreparedMethodCall { Type = type, Method = method, Parameters = parameters, Arguments = arguments, Instance = instance };
            }

            /// <summary>跨 effect barrier 后只执行一次已准备的方法并返回审计值树。</summary>
            public static Dictionary<string, object> InvokePrepared(PreparedMethodCall call, UnityCliRequestBudget budget)
            {
                if (call == null) throw new ArgumentNullException("call");
                if (budget == null) throw new ArgumentNullException("budget");
                var data = new Dictionary<string, object>
                {
                    { "typeName", call.Type.FullName }, { "methodName", call.Method.Name },
                    { "isStatic", call.Method.IsStatic }, { "returnType", call.Method.ReturnType.FullName }
                };
                try
                {
                    var result = call.Method.Invoke(call.Instance, call.Arguments);
                    if (result is System.Threading.Tasks.Task || result is IEnumerator || result is AsyncOperation || result is CustomYieldInstruction)
                        throw new BridgeCommandException("E_METHOD_COMPLETION_UNPROVABLE", "The method returned an asynchronous/yield operation that has no immediate completion proof.", "Use a dedicated Tool with an explicit asynchronous completion predicate.");
                    data["result"] = BuildCallResult(result, budget);
                    data["parameters"] = call.Parameters.Select((parameter, index) => (object)new Dictionary<string, object> { { "name", parameter.Name }, { "value", BuildCallResult(call.Arguments[index], budget) } }).ToList();
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    throw new BridgeCommandException(
                        "E_METHOD_INVOCATION_FAILED",
                        inner.GetType().Name + ": " + inner.Message,
                        new Dictionary<string, object> { { "typeName", call.Type.FullName }, { "methodName", call.Method.Name }, { "errorType", inner.GetType().FullName } },
                        new[] { "Inspect the method contract and arguments before submitting a new request." });
                }
                catch (BridgeCommandException) { throw; }
                catch (Exception exception)
                {
                    throw new BridgeCommandException(
                        "E_METHOD_INVOCATION_FAILED",
                        exception.GetType().Name + ": " + exception.Message,
                        new Dictionary<string, object> { { "typeName", call.Type.FullName }, { "methodName", call.Method.Name }, { "errorType", exception.GetType().FullName } },
                        new[] { "Inspect the method contract and arguments before submitting a new request." });
                }
                return data;
            }

            /// <summary>completionProbe 只允许同步方法；void 由内部 observer 的 fresh after proof 覆盖。</summary>
            private static void RequireSynchronousResult(MethodInfo method)
            {
                var returnType = method.ReturnType;
                var fullName = returnType.FullName ?? string.Empty;
                if (typeof(System.Threading.Tasks.Task).IsAssignableFrom(returnType)
                    || fullName == "System.Threading.Tasks.ValueTask"
                    || fullName.StartsWith("System.Threading.Tasks.ValueTask`", StringComparison.Ordinal)
                    || typeof(IEnumerator).IsAssignableFrom(returnType)
                    || typeof(AsyncOperation).IsAssignableFrom(returnType)
                    || typeof(CustomYieldInstruction).IsAssignableFrom(returnType))
                    throw new BridgeCommandException(
                        "E_METHOD_COMPLETION_UNPROVABLE",
                        "reflection.method.call cannot prove immediate completion for awaitable, yield, or AsyncOperation returns: " + returnType.FullName + ".",
                        "Use a dedicated Tool with an explicit business predicate and proof source.");
            }

            // 参数可为位置数组或按参数名索引的对象；值转换复用 ObjectPatch 的 typed conversion。
            /// <summary>按显式参数类型或唯一参数数量选择 overload，歧义时要求调用方精确指定签名。</summary>
            private static MethodInfo ResolveCallMethod(Type type, string methodName, object argsValue, List<string> parameterTypeNames, BindingFlags bindings)
            {
                var candidates = type.GetMethods(bindings)
                    .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal) && !method.ContainsGenericParameters)
                    .ToList();
                if (candidates.Count == 0)
                {
                    throw new BridgeCommandException("E_METHOD_NOT_FOUND", "Method not found: " + type.FullName + "." + methodName,
                        "Run reflection.method.find to discover available methods.");
                }

                if (parameterTypeNames != null && parameterTypeNames.Count > 0)
                {
                    var parameterTypes = new List<Type>();
                    foreach (var parameterTypeName in parameterTypeNames)
                    {
                        var parameterType = TypeSchemaGenerator.ResolveType(parameterTypeName);
                        if (parameterType == null)
                        {
                            throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Parameter type not found: " + parameterTypeName,
                                "Use the full parameterType returned by reflection.method.find.");
                        }
                        parameterTypes.Add(parameterType);
                    }
                    candidates = candidates.Where(method => method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes)).ToList();
                }
                else
                {
                    var argumentCount = argsValue is List<object> positional ? positional.Count
                        : argsValue is Dictionary<string, object> named ? named.Count
                        : 0;
                    candidates = candidates.Where(method => method.GetParameters().Length == argumentCount).ToList();
                }

                if (candidates.Count == 1) return candidates[0];
                if (candidates.Count == 0)
                {
                    throw new BridgeCommandException("E_METHOD_NOT_FOUND", "No overload matches the supplied reflection method signature: " + type.FullName + "." + methodName,
                        new Dictionary<string, object> { { "typeName", type.FullName }, { "methodName", methodName }, { "parameterTypes", parameterTypeNames ?? new List<string>() } },
                        new[] { "Run reflection.method.find and pass the returned parameterType values as parameterTypes." });
                }

                var signatures = candidates.Select(method => method.Name + "(" + string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName).ToArray()) + ")").ToList();
                throw new BridgeCommandException("E_METHOD_AMBIGUOUS", "Multiple overloads match: " + type.FullName + "." + methodName,
                    new Dictionary<string, object> { { "typeName", type.FullName }, { "methodName", methodName }, { "candidateSignatures", signatures } },
                    new[] { "Run reflection.method.find and pass one returned signature's parameterType values as parameterTypes." });
            }

            private static object[] ResolveCallArguments(object argsValue, ParameterInfo[] parameters)
            {
                if (parameters.Length == 0) return new object[0];
                var parsed = argsValue as List<object> ?? new List<object>();
                var objForm = argsValue as Dictionary<string, object>;
                var result = new object[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    object value = null;
                    if (objForm != null && objForm.ContainsKey(parameter.Name)) value = objForm[parameter.Name];
                    else if (i < parsed.Count) value = parsed[i];
                    else if (parameter.HasDefaultValue) value = parameter.DefaultValue;
                    else value = parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
                    result[i] = UnityCliValueConverter.ConvertValue(value, parameter.ParameterType);
                }
                return result;
            }

            private static object ResolveCallInstance(Dictionary<string, object> instanceRef, Type expectedType)
            {
                if (instanceRef == null) return null;
                if (instanceRef.TryGetValue("instanceId", out var iid))
                {
                    var id = Convert.ToInt32(iid, CultureInfo.InvariantCulture);
                    if (id != 0)
                    {
                        var obj = EditorUtility.InstanceIDToObject(id);
                        if (obj != null && expectedType.IsInstanceOfType(obj)) return obj;
                    }
                }
                if (instanceRef.TryGetValue("assetPath", out var ap) && ap is string path)
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(path);
                    if (obj != null && expectedType.IsInstanceOfType(obj)) return obj;
                }
                if (instanceRef.TryGetValue("path", out var p) && p is string goPath && typeof(Component).IsAssignableFrom(expectedType))
                {
                    var go = ObjectRefResolver.FindGameObjectByPath(goPath);
                    if (go != null) return go.GetComponent(expectedType);
                }
                return null;
            }

            private static object BuildCallResult(object result, UnityCliRequestBudget budget)
            {
                if (result == null) return null;
                try
                {
                    return ObjectSerializer.Serialize(result, new ObjectSerializer.Options { MaxDepth = 2, IncludeFields = false, IncludeProperties = true, DeepSerialization = false }, budget);
                }
                catch (Exception exception)
                {
                    // 反射返回值不可投影时显式暴露原因，不能 ToString() 制造不稳定业务数据。
                    return new Dictionary<string, object> { { "unsupported", true }, { "reason", "serialization-failed" }, { "typeName", result.GetType().FullName }, { "error", exception.Message } };
                }
            }
        }

        public sealed class ReflectionMethodDescriptor
        {
            public string Name { get; set; }
            public bool IsStatic { get; set; }
            public bool IsPublic { get; set; }
            public bool IsPrivate { get; set; }
            public string ReturnType { get; set; }
            public string DeclaringType { get; set; }
            public List<ReflectionParameterDescriptor> Parameters { get; set; }
        }

        public sealed class ReflectionParameterDescriptor
        {
            public string Name { get; set; }
            public string ParameterType { get; set; }
            public bool HasDefaultValue { get; set; }
        }

        private static object MemberValue(Type type, object instance, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null) return property.GetValue(instance, null);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            return field == null ? null : field.GetValue(instance);
        }

        private static Dictionary<string, object> AssetReference(UnityEngine.Object asset)
        {
            if (asset == null) return null;
            var path = NormalizeAssetPath(AssetDatabase.GetAssetPath(asset));
            return new Dictionary<string, object>
            {
                { "name", asset.name }, { "path", path }, { "guid", string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path) }
            };
        }

        private static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            var type = Type.GetType(fullName);
            if (type != null) return type;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { type = assembly.GetType(fullName); }
                catch { /* 忽略加载失败的程序集 */ }
                if (type != null) return type;
            }
            return null;
        }
    }
}
#endif
