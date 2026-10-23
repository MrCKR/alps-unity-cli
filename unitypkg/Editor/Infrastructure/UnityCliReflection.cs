#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>类型 JSON Schema 生成器：为 live C# / Unity 类型生成带 $defs 的结构化 schema。</summary>
        internal static class TypeSchemaGenerator
        {
            private const int MaximumSchemaMemberCandidates = 1024;
            private const int MaximumSchemaMemberMetadataUtf8Bytes = 64 * 1024;
            private const int MaximumSchemaCachedTypes = 128;
            private const int MaximumTypeHierarchyDepth = 32;
            private const int MaximumInterfaces = 256;
            private static readonly Dictionary<string, Type> ResolvedTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
            private static readonly Dictionary<Type, SchemaMemberCandidate[]> SchemaMemberCache = new Dictionary<Type, SchemaMemberCandidate[]>();
            private static string cacheGeneration;

            internal sealed class Page
            {
                internal Dictionary<string, object> Data;
                internal bool Truncated;
                internal string NextCursor;
                internal string Generation;
            }

            /// <summary>根类型先走统一分类；只有 object member 才按 generation-bound raw ordinal 分页。</summary>
            public static Page Generate(string typeName, int maxDepth, int maxMembers, int maxDefinitions, int pageSize, string cursor, UnityCliRequestBudget budget)
            {
                var type = ResolveType(typeName, budget);
                if (type == null)
                {
                    if (budget.IsExhausted) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name such as UnityEngine.Transform or UnityEngine.Rigidbody.");
                }
                var context = new SchemaContext(type, maxDepth, maxMembers, maxDefinitions, budget);
                var generation = UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Reflection() + "\0" + type.AssemblyQualifiedName + "\0"
                    + maxDepth.ToString(CultureInfo.InvariantCulture) + "\0" + maxMembers.ToString(CultureInfo.InvariantCulture) + "\0" + maxDefinitions.ToString(CultureInfo.InvariantCulture));
                if (!IsObjectSchemaRoot(type, context))
                {
                    if (!string.IsNullOrEmpty(cursor)) throw StaleSchemaCursor();
                    var rootSchema = BuildSchema(type, context, 0, true);
                    return new Page
                    {
                        Truncated = context.Truncated,
                        NextCursor = null,
                        Generation = generation,
                        Data = new Dictionary<string, object>
                        {
                            { "command", "debug.type-schema" }, { "typeName", FriendlyTypeName(type, context) }, { "schema", rootSchema },
                            { "memberCount", 0L }, { "returnedMemberCount", 0L }, { "definitionCount", (long)context.Definitions.Count }
                        }
                    };
                }

                var members = GetSchemaMemberCandidates(type, budget);
                generation = UnityCliContinuationToken.Fingerprint(generation + "\0" + SchemaMemberFingerprint(members));
                var index = 0;
                if (!string.IsNullOrEmpty(cursor)) ParseSchemaCursor(cursor, generation, members.Length, out index);
                var rootProperties = new Dictionary<string, object>();
                while (index < members.Length && rootProperties.Count < pageSize)
                {
                    if (!context.TryMember()) break;
                    var member = members[index];
                    if (!budget.TryConsumeReflectionCall() || !budget.TryReserveSerializedUtf8(member.Name)) break;
                    rootProperties[member.Name] = SchemaFor(member.ValueType, context, 1);
                    index++;
                    if (budget.IsExhausted) break;
                }
                if (rootProperties.Count == 0 && index < members.Length) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "scannedRecords");
                var typeDisplayName = FriendlyTypeName(type, context);
                var schema = new Dictionary<string, object> { { "type", "object" }, { "typeName", typeDisplayName }, { "properties", rootProperties } };
                if (context.Definitions.Count > 0) schema["$defs"] = context.Definitions;
                var next = index < members.Length ? UnityCliContinuationToken.Create("debug.type-schema", generation, "member:" + index.ToString(CultureInfo.InvariantCulture)) : null;
                return new Page
                {
                    Truncated = next != null || context.Truncated,
                    NextCursor = next,
                    Generation = generation,
                    Data = new Dictionary<string, object>
                    {
                        { "command", "debug.type-schema" }, { "typeName", typeDisplayName }, { "schema", schema },
                        { "memberCount", (long)members.Length }, { "returnedMemberCount", (long)rootProperties.Count },
                        { "definitionCount", (long)context.Definitions.Count }
                    }
                };
            }

            private static void ParseSchemaCursor(string cursor, string generation, int memberCount, out int index)
            {
                string position;
                index = -1;
                if (!UnityCliContinuationToken.TryParse(cursor, "debug.type-schema", generation, out position)
                    || !position.StartsWith("member:", StringComparison.Ordinal)
                    || !int.TryParse(position.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                    || index < 0 || index > memberCount) throw StaleSchemaCursor();
            }

            private static BridgeCommandException StaleSchemaCursor()
            {
                return new BridgeCommandException("E_CONTINUATION_STALE", "The debug.type-schema continuation does not match the current assembly generation and limits.", "Start a new debug.type-schema read without cursor.");
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
                if (!context.Budget.TryConsumeNode()) { context.Truncated = true; return TruncatedSchema(type, context); }
                if (type == null) return new Dictionary<string, object> { { "type", "null" } };
                if (type.IsByRef || type.IsPointer) return new Dictionary<string, object> { { "type", "string" }, { "typeName", FriendlyTypeName(type, context) } };
                if (type.IsGenericParameter) return new Dictionary<string, object> { { "type", "object" }, { "typeName", type.Name }, { "genericParameter", true } };
                if (IsNullable(type))
                {
                    return new Dictionary<string, object>
                    {
                        { "anyOf", new List<object> { SchemaFor(Nullable.GetUnderlyingType(type), context, depth + 1), new Dictionary<string, object> { { "type", "null" } } } },
                        { "typeName", FriendlyTypeName(type, context) }
                    };
                }
                if (IsPrimitiveLike(type)) return new Dictionary<string, object> { { "type", JsonTypeFor(type) }, { "typeName", FriendlyTypeName(type, context) } };
                if (type.IsEnum)
                {
                    if (!context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; return TruncatedSchema(type, context); }
                    var values = new List<object>();
                    foreach (var value in UnityCliReflectionSnapshot.EnumNames(type, context.Budget, MaximumSchemaMemberCandidates))
                    {
                        if (!context.TryMember() || !context.Budget.TryReserveSerializedUtf8(value)) { context.Truncated = true; break; }
                        values.Add(value);
                    }
                    var enumSchema = new Dictionary<string, object>
                    {
                        { "type", "string" }, { "typeName", FriendlyTypeName(type, context) }, { "enum", values },
                        { "truncated", context.Truncated }, { "continuationSupported", false }
                    };
                    if (context.Truncated) enumSchema["truncationReason"] = "non-resumable-enum-truncation";
                    return enumSchema;
                }

                if (TryGetDictionaryTypes(type, context, out var keyType, out var dictionaryValueType))
                {
                    var dictionarySchema = new Dictionary<string, object> { { "type", "object" }, { "typeName", FriendlyTypeName(type, context) } };
                    WriteGenericMetadata(dictionarySchema, type, context);
                    dictionarySchema["propertyNames"] = DictionaryKeySchema(keyType, context);
                    dictionarySchema["additionalProperties"] = depth >= context.MaxDepth ? TruncatedSchema(dictionaryValueType, context) : SchemaFor(dictionaryValueType, context, depth + 1);
                    return dictionarySchema;
                }

                var elementType = GetEnumerableElementType(type, context);
                if (elementType != null)
                {
                    var arraySchema = new Dictionary<string, object> { { "type", "array" }, { "typeName", FriendlyTypeName(type, context) } };
                    if (type.IsArray) arraySchema["arrayRank"] = (long)type.GetArrayRank();
                    WriteGenericMetadata(arraySchema, type, context);
                    arraySchema["items"] = depth >= context.MaxDepth ? TruncatedSchema(elementType, context) : SchemaFor(elementType, context, depth + 1);
                    return arraySchema;
                }

                var objectSchema = new Dictionary<string, object> { { "type", "object" }, { "typeName", FriendlyTypeName(type, context) } };
                WriteGenericMetadata(objectSchema, type, context);
                if (depth >= context.MaxDepth)
                {
                    objectSchema["truncated"] = true;
                    return objectSchema;
                }
                var properties = new Dictionary<string, object>();
                foreach (var member in GetSchemaMemberCandidates(type, context.Budget))
                {
                    if (!context.TryMember() || !context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; break; }
                    properties[member.Name] = SchemaFor(member.ValueType, context, depth + 1);
                    if (context.Budget.IsExhausted) break;
                }
                if (context.Truncated) objectSchema["truncated"] = true;
                objectSchema["properties"] = properties;
                return objectSchema;
            }

            /// <summary>复杂类型使用 $ref，基础/集合类型直接内联。</summary>
            private static object SchemaFor(Type type, SchemaContext context, int depth)
            {
                if (type == null || IsPrimitiveLike(type) || type.IsEnum || type.IsByRef || type.IsPointer || type.IsGenericParameter || IsNullable(type)
                    || TryGetDictionaryTypes(type, context, out _, out _) || GetEnumerableElementType(type, context) != null)
                {
                    return BuildSchema(type, context, depth, false);
                }
                if (type == context.RootType) return new Dictionary<string, object> { { "$ref", "#" } };
                var key = DefinitionKey(type, context);
                if (!context.Definitions.ContainsKey(key) && !context.BuildingDefinitions.Contains(key))
                {
                    if (!context.Budget.TryReserveSerializedUtf8(key)) { context.Truncated = true; return TruncatedSchema(type, context); }
                    if (context.Definitions.Count >= context.MaxDefinitions || !context.Budget.TryConsumeReflectionCall())
                    {
                        context.Truncated = true;
                        return TruncatedSchema(type, context);
                    }
                    context.BuildingDefinitions.Add(key);
                    context.Definitions[key] = BuildSchema(type, context, depth, false);
                    context.BuildingDefinitions.Remove(key);
                }
                return new Dictionary<string, object> { { "$ref", "#/$defs/" + key } };
            }

            private static Dictionary<string, object> DictionaryKeySchema(Type keyType, SchemaContext context)
            {
                var schema = new Dictionary<string, object> { { "type", "string" } };
                if (keyType != null && keyType.IsEnum)
                {
                    if (!context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; schema["truncated"] = true; return schema; }
                    var values = new List<object>();
                    foreach (var value in UnityCliReflectionSnapshot.EnumNames(keyType, context.Budget, MaximumSchemaMemberCandidates))
                    {
                        if (!context.TryMember() || !context.Budget.TryReserveSerializedUtf8(value)) { context.Truncated = true; schema["truncated"] = true; break; }
                        values.Add(value);
                    }
                    schema["enum"] = values;
                }
                else if (keyType != null) schema["typeName"] = FriendlyTypeName(keyType, context);
                return schema;
            }

            private static Dictionary<string, object> TruncatedSchema(Type type, SchemaContext context)
            {
                return new Dictionary<string, object> { { "type", "object" }, { "typeName", FriendlyTypeName(type, context) }, { "truncated", true } };
            }

            private static void WriteGenericMetadata(Dictionary<string, object> schema, Type type, SchemaContext context)
            {
                if (!type.IsGenericType) return;
                if (!context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; schema["truncated"] = true; return; }
                schema["genericTypeDefinition"] = FriendlyTypeName(type.GetGenericTypeDefinition(), context);
                var values = new List<object>();
                foreach (var argument in UnityCliReflectionSnapshot.GenericArguments(type, context.Budget, MaximumTypeHierarchyDepth))
                {
                    if (!context.TryMember()) { context.Truncated = true; schema["truncated"] = true; break; }
                    values.Add(FriendlyTypeName(argument, context));
                    if (context.Budget.IsExhausted) { context.Truncated = true; schema["truncated"] = true; break; }
                }
                schema["genericArguments"] = values;
            }

            private static bool TryGetDictionaryTypes(Type type, SchemaContext context, out Type keyType, out Type valueType)
            {
                keyType = null;
                valueType = null;
                if (type == null || type == typeof(string)) return false;
                if (!context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; return false; }
                if (TryGetDictionaryCandidate(type, context, out keyType, out valueType)) return true;
                var examined = 0;
                foreach (var candidate in UnityCliReflectionSnapshot.Interfaces(type, context.Budget, MaximumInterfaces))
                {
                    examined++;
                    if (!context.Budget.TryConsumeScannedRecord() || !context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; return false; }
                    if (TryGetDictionaryCandidate(candidate, context, out keyType, out valueType)) return true;
                }
                return false;
            }

            private static bool TryGetDictionaryCandidate(Type candidate, SchemaContext context, out Type keyType, out Type valueType)
            {
                keyType = null;
                valueType = null;
                if (!candidate.IsGenericType) return false;
                var definition = candidate.GetGenericTypeDefinition();
                if (definition != typeof(IDictionary<,>) && definition != typeof(IReadOnlyDictionary<,>)) return false;
                var arguments = UnityCliReflectionSnapshot.GenericArguments(candidate, context.Budget, 2);
                keyType = arguments[0];
                valueType = arguments[1];
                return true;
            }

            private static Type GetEnumerableElementType(Type type, SchemaContext context)
            {
                if (type == null || type == typeof(string)) return null;
                if (!context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; return null; }
                if (type.IsArray) return type.GetElementType();
                var direct = GetEnumerableCandidate(type, context);
                if (direct != null) return direct;
                var examined = 0;
                foreach (var candidate in UnityCliReflectionSnapshot.Interfaces(type, context.Budget, MaximumInterfaces))
                {
                    examined++;
                    if (!context.Budget.TryConsumeScannedRecord() || !context.Budget.TryConsumeReflectionCall()) { context.Truncated = true; return null; }
                    var element = GetEnumerableCandidate(candidate, context);
                    if (element != null) return element;
                }
                return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
            }

            private static Type GetEnumerableCandidate(Type candidate, SchemaContext context)
            {
                if (!candidate.IsGenericType) return null;
                var definition = candidate.GetGenericTypeDefinition();
                return definition == typeof(IEnumerable<>) || definition == typeof(IList<>) || definition == typeof(ICollection<>)
                    ? UnityCliReflectionSnapshot.GenericArguments(candidate, context.Budget, 1)[0]
                    : null;
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

            private static bool IsObjectSchemaRoot(Type type, SchemaContext context)
            {
                if (!context.Budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(context.Budget.ExhaustedDimension);
                if (type == null || type.IsByRef || type.IsPointer || type.IsGenericParameter || IsNullable(type) || IsPrimitiveLike(type) || type.IsEnum) return false;
                if (TryGetDictionaryTypes(type, context, out _, out _)) return false;
                if (context.Budget.IsExhausted) throw new UnityCliRequestBudgetExceededException(context.Budget.ExhaustedDimension);
                if (GetEnumerableElementType(type, context) != null) return false;
                if (context.Budget.IsExhausted) throw new UnityCliRequestBudgetExceededException(context.Budget.ExhaustedDimension);
                return true;
            }

            /// <summary>候选 metadata 按 reflection generation 缓存，构建与命中都对每个已检查成员计费。</summary>
            private static SchemaMemberCandidate[] GetSchemaMemberCandidates(Type type, UnityCliRequestBudget budget)
            {
                var generation = UnityCliReadGeneration.Reflection();
                if (!string.Equals(cacheGeneration, generation, StringComparison.Ordinal))
                {
                    ResolvedTypeCache.Clear();
                    SchemaMemberCache.Clear();
                    cacheGeneration = generation;
                }
                SchemaMemberCandidate[] cached;
                if (SchemaMemberCache.TryGetValue(type, out cached))
                {
                    if (!budget.TryConsumeScannedRecords(cached.Length) || !budget.TryConsumeReflectionCalls(cached.Length))
                        throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    return cached;
                }

                var candidates = new Dictionary<string, SchemaMemberCandidate>(StringComparer.Ordinal);
                var examined = 0;
                var metadataBytes = 0;
                var depth = 0;
                for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                {
                    if (depth++ >= MaximumTypeHierarchyDepth) throw new UnityCliRequestBudgetExceededException("reflectionCalls");
                    if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    foreach (var field in UnityCliReflectionSnapshot.DeclaredFields(current, budget, MaximumSchemaMemberCandidates))
                    {
                        ConsumeSchemaMemberMetadata(field.Name, field.FieldType, budget, ref examined, ref metadataBytes);
                        if (!field.IsPublic || field.IsStatic || candidates.ContainsKey(field.Name)) continue;
                        candidates[field.Name] = new SchemaMemberCandidate(field.Name, field.FieldType, depth, false);
                    }
                    foreach (var property in UnityCliReflectionSnapshot.DeclaredProperties(current, budget, MaximumSchemaMemberCandidates))
                    {
                        ConsumeSchemaMemberMetadata(property.Name, property.PropertyType, budget, ref examined, ref metadataBytes);
                        var getter = property.GetGetMethod(false);
                        if (getter == null || getter.IsStatic || property.GetIndexParameters().Length != 0) continue;
                        SchemaMemberCandidate existing;
                        if (candidates.TryGetValue(property.Name, out existing) && existing.Depth != depth) continue;
                        candidates[property.Name] = new SchemaMemberCandidate(property.Name, property.PropertyType, depth, true);
                    }
                }
                var result = candidates.Values.OrderBy(candidate => candidate.Name, StringComparer.Ordinal).ToArray();
                if (SchemaMemberCache.Count >= MaximumSchemaCachedTypes) SchemaMemberCache.Clear();
                SchemaMemberCache[type] = result;
                return result;
            }

            private static void ConsumeSchemaMemberMetadata(string name, Type valueType, UnityCliRequestBudget budget, ref int examined, ref int metadataBytes)
            {
                if (examined++ >= MaximumSchemaMemberCandidates) throw new UnityCliRequestBudgetExceededException("scannedRecords");
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                metadataBytes += Encoding.UTF8.GetByteCount(name ?? string.Empty);
                metadataBytes += Encoding.UTF8.GetByteCount(valueType == null ? string.Empty : valueType.AssemblyQualifiedName ?? valueType.Name);
                if (metadataBytes > MaximumSchemaMemberMetadataUtf8Bytes) throw new UnityCliRequestBudgetExceededException("serializedUtf8Bytes");
            }

            private static string SchemaMemberFingerprint(IEnumerable<SchemaMemberCandidate> members)
            {
                var builder = new StringBuilder();
                foreach (var member in members) builder.Append(member.Name).Append('\0').Append(member.ValueType == null ? string.Empty : member.ValueType.AssemblyQualifiedName).Append('\0');
                return UnityCliContinuationToken.Fingerprint(builder.ToString());
            }

            private sealed class SchemaMemberCandidate
            {
                internal readonly string Name;
                internal readonly Type ValueType;
                internal readonly int Depth;
                internal readonly bool IsProperty;

                internal SchemaMemberCandidate(string name, Type valueType, int depth, bool isProperty)
                {
                    Name = name;
                    ValueType = valueType;
                    Depth = depth;
                    IsProperty = isProperty;
                }
            }

            private static string FriendlyTypeName(Type type, SchemaContext context, int depth = 0)
            {
                if (type == null) return null;
                if (depth >= MaximumTypeHierarchyDepth) throw new UnityCliRequestBudgetExceededException("reflectionCalls");
                if (!context.Budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(context.Budget.ExhaustedDimension);
                string value;
                if (type.IsArray) value = FriendlyTypeName(type.GetElementType(), context, depth + 1) + "[]";
                else if (!type.IsGenericType) value = type.FullName ?? type.Name;
                else
                {
                    var definition = type.GetGenericTypeDefinition();
                    var baseName = definition.FullName ?? definition.Name;
                    var tick = baseName.IndexOf('`');
                    if (tick >= 0) baseName = baseName.Substring(0, tick);
                    var arguments = UnityCliReflectionSnapshot.GenericArguments(type, context.Budget, MaximumTypeHierarchyDepth);
                    var names = new string[arguments.Length];
                    for (var index = 0; index < arguments.Length; index++) names[index] = FriendlyTypeName(arguments[index], context, depth + 1);
                    value = baseName + "<" + string.Join(",", names) + ">";
                }
                if (!context.Budget.TryReserveSerializedUtf8(value)) throw new UnityCliRequestBudgetExceededException(context.Budget.ExhaustedDimension);
                return value;
            }

            private static string DefinitionKey(Type type, SchemaContext context)
            {
                var value = FriendlyTypeName(type, context) ?? "Unknown";
                var characters = value.ToCharArray();
                for (var index = 0; index < characters.Length; index++) if (!char.IsLetterOrDigit(characters[index])) characters[index] = '_';
                return new string(characters);
            }

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
                    SchemaMemberCache.Clear();
                    cacheGeneration = generation;
                }
                Type cached;
                if (ResolvedTypeCache.TryGetValue(fullName, out cached)) return cached;
                if (!budget.TryConsumeReflectionCall()) return null;
                try { cached = Type.GetType(fullName, false); } catch { cached = null; }
                if (cached == null)
                {
                    foreach (var assembly in UnityCliAssemblySnapshot.Get(budget))
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
            private const int MaximumExaminedCallMethods = 4096;
            private const int MaximumMatchingCallMethods = 128;
            private const int MaximumCallParameters = 256;
            private const int MaximumCallSignatureUtf8Bytes = 64 * 1024;
            private const int MaximumCallTypeHierarchyDepth = 32;

            internal sealed class MethodPage
            {
                internal List<ReflectionMethodDescriptor> Methods;
                internal int Total;
                internal string NextCursor;
                internal string Generation;
                internal bool Truncated;
            }

            /// <summary>MethodInfo array 是唯一 runtime reflection snapshot；过滤和 descriptor 从 raw ordinal 流式推进。</summary>
            public static MethodPage FindMethods(string typeName, string methodName, BindingFlags bindings, int pageSize, string cursor, UnityCliRequestBudget budget)
            {
                var type = TypeSchemaGenerator.ResolveType(typeName, budget);
                if (type == null)
                {
                    if (budget.IsExhausted) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name such as UnityEngine.Transform.");
                }
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var methods = UnityCliReflectionSnapshot.Methods(type, bindings, budget, MaximumExaminedCallMethods);
                var generation = UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Reflection() + "\0" + type.AssemblyQualifiedName + "\0"
                    + ((int)bindings).ToString(CultureInfo.InvariantCulture) + "\0" + methodName + "\0" + methods.Length.ToString(CultureInfo.InvariantCulture));
                var index = 0;
                if (!string.IsNullOrEmpty(cursor))
                {
                    string position;
                    if (!UnityCliContinuationToken.TryParse(cursor, "debug.method-find", generation, out position)
                        || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0 || index > methods.Length)
                        throw new BridgeCommandException("E_CONTINUATION_STALE", "The reflection method continuation does not match the current assembly generation and selector.", "Start a new debug.method-find read without cursor.");
                }
                var pageStartIndex = index;
                var result = new List<ReflectionMethodDescriptor>();
                while (index < methods.Length && result.Count < pageSize)
                {
                    if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) break;
                    var method = methods[index];
                    var name = method.Name;
                    if (!string.IsNullOrEmpty(methodName) && !string.Equals(name, methodName, StringComparison.Ordinal)) { index++; continue; }
                    if (!budget.TryConsumeNode() || !budget.TryConsumeReflectionCall()) break;
                    var parameters = method.GetParameters();
                    var returnType = method.ReturnType == null ? null : method.ReturnType.FullName;
                    var declaringType = method.DeclaringType == null ? null : method.DeclaringType.FullName;
                    // 声明类型所在程序集名是 hook.install 必需的精确目标字段；与方法签名同源解析，避免调用方另行反射或猜测。
                    var assemblyName = method.DeclaringType == null
                        ? (method.Module == null || method.Module.Assembly == null ? null : method.Module.Assembly.GetName().Name)
                        : method.DeclaringType.Assembly.GetName().Name;
                    if (!budget.TryReserveSerializedUtf8(name) || !budget.TryReserveSerializedUtf8(returnType) || !budget.TryReserveSerializedUtf8(declaringType) || !budget.TryReserveSerializedUtf8(assemblyName)) break;
                    var parameterResults = new List<ReflectionParameterDescriptor>();
                    var parametersComplete = true;
                    foreach (var parameter in parameters)
                    {
                        if (!budget.TryConsumeNode() || !budget.TryConsumeReflectionCall()) { parametersComplete = false; break; }
                        var parameterName = parameter.Name;
                        var parameterType = parameter.ParameterType == null ? null : parameter.ParameterType.FullName;
                        if (!budget.TryReserveSerializedUtf8(parameterName) || !budget.TryReserveSerializedUtf8(parameterType)) { parametersComplete = false; break; }
                        parameterResults.Add(new ReflectionParameterDescriptor { Name = parameterName, ParameterType = parameterType, HasDefaultValue = parameter.HasDefaultValue });
                    }
                    result.Add(new ReflectionMethodDescriptor
                    {
                        Name = name, IsStatic = method.IsStatic, IsPublic = method.IsPublic, IsPrivate = method.IsPrivate,
                        ReturnType = returnType, DeclaringType = declaringType, AssemblyName = assemblyName, ParameterCount = parameters.Length,
                        ParametersTruncated = !parametersComplete, Parameters = parameterResults
                    });
                    index++;
                    if (!parametersComplete) break;
                }
                if (index == pageStartIndex && index < methods.Length)
                    throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "scannedRecords");
                var next = index < methods.Length ? UnityCliContinuationToken.Create("debug.method-find", generation, index.ToString(CultureInfo.InvariantCulture)) : null;
                return new MethodPage { Methods = result, Total = methods.Length, NextCursor = next, Generation = generation, Truncated = next != null || budget.IsExhausted };
            }


            internal sealed class PreparedMethodCall
            {
                internal Type Type;
                internal MethodInfo Method;
                internal ParameterInfo[] Parameters;
                internal object[] Arguments;
                internal object Instance;
            }

            /// <summary>effect 前在同一 request budget 下解析 type、overload、参数与 instance；不会调用目标方法。</summary>
            public static PreparedMethodCall PrepareCall(string typeName, string methodName, object argsValue, List<string> parameterTypes, Dictionary<string, object> instanceRef, BindingFlags bindings, UnityCliRequestBudget budget)
            {
                if (budget == null) throw new ArgumentNullException("budget");
                var type = TypeSchemaGenerator.ResolveType(typeName, budget);
                if (type == null)
                {
                    if (budget.IsExhausted) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name.");
                }
                var candidate = ResolveCallMethod(type, methodName, argsValue, parameterTypes, bindings, budget);
                RequireSynchronousResult(candidate.Method, budget);
                var arguments = ResolveCallArguments(argsValue, candidate.Parameters, budget);
                object instance = null;
                if (!candidate.Method.IsStatic)
                {
                    instance = ResolveCallInstance(instanceRef, type, budget);
                    if (instance == null) throw new BridgeCommandException("E_METHOD_INSTANCE_REQUIRED", "Instance method " + methodName + " requires an instance reference.", "Pass instanceRef with instanceId/assetPath/path.");
                }
                PrebudgetInvocationResult(type, candidate.Method, candidate.Parameters, budget);
                return new PreparedMethodCall { Type = type, Method = candidate.Method, Parameters = candidate.Parameters, Arguments = arguments, Instance = instance };
            }

            /// <summary>effect 前为固定审计外壳和参数回读保留最低容量，避免确定可知的 post-effect 预算失败。</summary>
            private static void PrebudgetInvocationResult(Type type, MethodInfo method, ParameterInfo[] parameters, UnityCliRequestBudget budget)
            {
                budget.CheckpointOrThrow();
                var minimumNodes = checked(1L + parameters.Length);
                if (budget.RemainingNodes < minimumNodes) throw new UnityCliRequestBudgetExceededException("nodes");
                if (budget.RemainingGetters < parameters.Length) throw new UnityCliRequestBudgetExceededException("getters");
                if (budget.RemainingReflectionCalls < parameters.Length) throw new UnityCliRequestBudgetExceededException("reflectionCalls");
                var fixedBytes = 512L + checked((long)parameters.Length * 256L);
                if (!budget.TryReserveSerializedUtf8(type.FullName)
                    || !budget.TryReserveSerializedUtf8(method.Name)
                    || !budget.TryReserveSerializedUtf8(method.ReturnType.FullName)
                    || !budget.TryReserveSerializedUtf8Bytes(fixedBytes))
                    throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "serializedUtf8Bytes");
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
                catch (UnityCliRequestBudgetExceededException) { throw; }
                catch (OperationCanceledException) { throw; }
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
            private static void RequireSynchronousResult(MethodInfo method, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
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
                        "debug.method-invoke cannot prove immediate completion for awaitable, yield, or AsyncOperation returns: " + returnType.FullName + ".",
                        "Use a dedicated Tool with an explicit business predicate and proof source.");
            }

            // 参数可为位置数组或按参数名索引的对象；值转换复用 ObjectPatch 的 typed conversion。
            /// <summary>按 raw declared-method ordinal 流式筛选；候选、参数和签名都有硬上限。</summary>
            private static CallCandidate ResolveCallMethod(Type type, string methodName, object argsValue, List<string> parameterTypeNames, BindingFlags bindings, UnityCliRequestBudget budget)
            {
                var selectedParameterTypes = new List<Type>();
                if (parameterTypeNames != null)
                {
                    foreach (var parameterTypeName in parameterTypeNames)
                    {
                        var parameterType = TypeSchemaGenerator.ResolveType(parameterTypeName, budget);
                        if (parameterType == null)
                        {
                            if (budget.IsExhausted) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                            throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Parameter type not found: " + parameterTypeName, "Use the full parameterType returned by debug.method-find.");
                        }
                        selectedParameterTypes.Add(parameterType);
                    }
                }
                var argumentCount = argsValue is List<object> positional ? positional.Count : argsValue is Dictionary<string, object> named ? named.Count : 0;
                var candidates = new List<CallCandidate>();
                var examinedMethods = 0;
                var depth = 0;
                for (var current = type; current != null; current = current.BaseType)
                {
                    if (depth++ >= MaximumCallTypeHierarchyDepth) throw new UnityCliRequestBudgetExceededException("reflectionCalls");
                    if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    foreach (var method in UnityCliReflectionSnapshot.DeclaredMethods(current, budget, MaximumExaminedCallMethods))
                    {
                        if (examinedMethods++ >= MaximumExaminedCallMethods) throw new UnityCliRequestBudgetExceededException("scannedRecords");
                        if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        if (!MethodMatchesBindings(method, bindings, current == type) || !string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.ContainsGenericParameters) continue;
                        if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        var parameters = method.GetParameters();
                        if (parameters.Length > MaximumCallParameters) throw new UnityCliRequestBudgetExceededException("reflectionCalls");
                        var matches = selectedParameterTypes.Count > 0 ? parameters.Length == selectedParameterTypes.Count : parameters.Length == argumentCount;
                        for (var index = 0; index < parameters.Length; index++)
                        {
                            if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                            if (selectedParameterTypes.Count > 0 && parameters[index].ParameterType != selectedParameterTypes[index]) matches = false;
                        }
                        if (!matches) continue;
                        if (candidates.Count >= MaximumMatchingCallMethods) throw new UnityCliRequestBudgetExceededException("scannedRecords");
                        candidates.Add(new CallCandidate(method, parameters));
                    }
                }

                if (candidates.Count == 1) return candidates[0];
                if (candidates.Count == 0)
                {
                    throw new BridgeCommandException("E_METHOD_NOT_FOUND", "No overload matches the supplied reflection method signature: " + type.FullName + "." + methodName,
                        new Dictionary<string, object> { { "typeName", type.FullName }, { "methodName", methodName }, { "parameterTypes", parameterTypeNames ?? new List<string>() } },
                        new[] { "Run debug.method-find and pass the returned parameterType values as parameterTypes." });
                }

                var signatures = new List<string>();
                var signatureBytes = 0;
                foreach (var candidate in candidates)
                {
                    var parameterNames = new string[candidate.Parameters.Length];
                    for (var index = 0; index < candidate.Parameters.Length; index++) parameterNames[index] = candidate.Parameters[index].ParameterType.FullName;
                    var signature = candidate.Method.Name + "(" + string.Join(",", parameterNames) + ")";
                    signatureBytes += Encoding.UTF8.GetByteCount(signature);
                    if (signatureBytes > MaximumCallSignatureUtf8Bytes || !budget.TryReserveSerializedUtf8(signature)) throw new UnityCliRequestBudgetExceededException("serializedUtf8Bytes");
                    signatures.Add(signature);
                }
                throw new BridgeCommandException("E_METHOD_AMBIGUOUS", "Multiple overloads match: " + type.FullName + "." + methodName,
                    new Dictionary<string, object> { { "typeName", type.FullName }, { "methodName", methodName }, { "candidateSignatures", signatures } },
                    new[] { "Run debug.method-find and pass one returned signature's parameterType values as parameterTypes." });
            }

            private static bool MethodMatchesBindings(MethodInfo method, BindingFlags bindings, bool declaredOnTarget)
            {
                if (method.IsStatic && (bindings & BindingFlags.Static) == 0 || !method.IsStatic && (bindings & BindingFlags.Instance) == 0) return false;
                if (method.IsPublic && (bindings & BindingFlags.Public) == 0 || !method.IsPublic && (bindings & BindingFlags.NonPublic) == 0) return false;
                return declaredOnTarget || !method.IsPrivate;
            }

            private static object[] ResolveCallArguments(object argsValue, ParameterInfo[] parameters, UnityCliRequestBudget budget)
            {
                if (parameters.Length == 0) return new object[0];
                var parsed = argsValue as List<object> ?? new List<object>();
                var objForm = argsValue as Dictionary<string, object>;
                var result = new object[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
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

            private static object ResolveCallInstance(Dictionary<string, object> instanceRef, Type expectedType, UnityCliRequestBudget budget)
            {
                if (instanceRef == null) return null;
                if (instanceRef.TryGetValue("instanceId", out var iid))
                {
                    var id = Convert.ToInt32(iid, CultureInfo.InvariantCulture);
                    if (id != 0)
                    {
                        if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        var obj = EditorUtility.InstanceIDToObject(id);
                        if (obj != null && expectedType.IsInstanceOfType(obj)) return obj;
                    }
                }
                if (instanceRef.TryGetValue("assetPath", out var ap) && ap is string path)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var obj = AssetDatabase.LoadMainAssetAtPath(path);
                    if (obj != null && expectedType.IsInstanceOfType(obj)) return obj;
                }
                if (instanceRef.TryGetValue("path", out var p) && p is string goPath && typeof(Component).IsAssignableFrom(expectedType))
                {
                    var reference = new ReflectionInstanceReference { Path = goPath };
                    var go = ObjectRefResolver.ResolveGameObject(reference, budget);
                    if (go != null)
                    {
                        if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        return go.GetComponent(expectedType);
                    }
                }
                return null;
            }

            private sealed class CallCandidate
            {
                internal readonly MethodInfo Method;
                internal readonly ParameterInfo[] Parameters;

                internal CallCandidate(MethodInfo method, ParameterInfo[] parameters)
                {
                    Method = method;
                    Parameters = parameters;
                }
            }

            private sealed class ReflectionInstanceReference : IUnityCliObjectReferenceInput
            {
                public int? InstanceId { get { return null; } }
                public string AssetPath { get { return null; } }
                public string Guid { get { return null; } }
                public string Path { get; set; }
                public string Name { get { return null; } }
                public string Target { get { return null; } }
                public int? ComponentInstanceId { get { return null; } }
                public int? ComponentIndex { get { return null; } }
                public string Component { get { return null; } }
                public string ComponentType { get { return null; } }
            }

            private static object BuildCallResult(object result, UnityCliRequestBudget budget)
            {
                if (result == null) return null;
                try
                {
                    return ObjectSerializer.Serialize(result, new ObjectSerializer.Options { MaxDepth = 2, IncludeFields = false, IncludeProperties = true, DeepSerialization = false }, budget);
                }
                catch (UnityCliRequestBudgetExceededException) { throw; }
                catch (OperationCanceledException) { throw; }
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

            /// <summary>声明类型所在程序集名；与 declaringType 同源解析，可直接用作 hook.install 的精确 assemblyName。</summary>
            public string AssemblyName { get; set; }

            public long ParameterCount { get; set; }
            public bool ParametersTruncated { get; set; }
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

    /// <summary>先冻结带硬上限的反射集合，再允许调用方逐项计费或分页。</summary>
    internal static class UnityCliReflectionSnapshot
    {
        internal static FieldInfo[] DeclaredFields(Type type, UnityCliRequestBudget budget, int maximum)
        {
            if (type == null) throw new ArgumentNullException("type");
            budget.CheckpointOrThrow();
            var items = type.GetTypeInfo().DeclaredFields.ToArray();
            EnsureBounded(items.Length, maximum);
            return items;
        }

        internal static PropertyInfo[] DeclaredProperties(Type type, UnityCliRequestBudget budget, int maximum)
        {
            if (type == null) throw new ArgumentNullException("type");
            budget.CheckpointOrThrow();
            var items = type.GetTypeInfo().DeclaredProperties.ToArray();
            EnsureBounded(items.Length, maximum);
            return items;
        }

        internal static MethodInfo[] DeclaredMethods(Type type, UnityCliRequestBudget budget, int maximum)
        {
            if (type == null) throw new ArgumentNullException("type");
            budget.CheckpointOrThrow();
            var items = type.GetTypeInfo().DeclaredMethods.ToArray();
            EnsureBounded(items.Length, maximum);
            return items;
        }

        internal static MethodInfo[] Methods(Type type, BindingFlags bindings, UnityCliRequestBudget budget, int maximum)
        {
            if (type == null) throw new ArgumentNullException("type");
            budget.CheckpointOrThrow();
            var items = type.GetMethods(bindings);
            EnsureBounded(items.Length, maximum);
            return items;
        }

        internal static Type[] Interfaces(Type type, UnityCliRequestBudget budget, int maximum)
        {
            if (type == null) throw new ArgumentNullException("type");
            budget.CheckpointOrThrow();
            var items = type.GetInterfaces();
            EnsureBounded(items.Length, maximum);
            return items;
        }

        internal static Type[] GenericArguments(Type type, UnityCliRequestBudget budget, int maximum)
        {
            if (type == null) throw new ArgumentNullException("type");
            budget.CheckpointOrThrow();
            var items = type.GetGenericArguments();
            EnsureBounded(items.Length, maximum);
            return items;
        }

        internal static string[] EnumNames(Type type, UnityCliRequestBudget budget, int maximum)
        {
            if (type == null) throw new ArgumentNullException("type");
            budget.CheckpointOrThrow();
            var items = Enum.GetNames(type);
            EnsureBounded(items.Length, maximum);
            return items;
        }

        private static void EnsureBounded(int count, int maximum)
        {
            if (maximum < 0 || count > maximum) throw new UnityCliRequestBudgetExceededException("nodes");
        }
    }
}
#endif
