#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>在共享 request budget 下深读 Unity/CLR 对象；任何展开、getter 或枚举前先裁决。</summary>
        internal static class ObjectSerializer
        {
            internal const int DefaultMaxDepth = 4;
            internal const int AbsoluteMaxDepth = 16;
            internal const int DefaultMaxArrayItems = 50;
            internal const int AbsoluteMaxArrayItems = 1000;
            internal const int AbsoluteMaxMembers = 256;
            internal const int AbsoluteMaxPaths = 128;
            private const int AbsoluteMaxMemberCandidates = 512;
            private const int AbsoluteMaxMemberMetadataUtf8Bytes = 64 * 1024;
            private const int AbsoluteMaxCachedMemberTypes = 128;
            private const int AbsoluteMaxTypeHierarchyDepth = 32;
            private static readonly object MemberCacheLock = new object();
            private static readonly Dictionary<Type, MemberCandidate[]> MemberCandidateCache = new Dictionary<Type, MemberCandidate[]>();
            private static string memberCacheGeneration;

            public sealed class Options
            {
                public int MaxDepth = DefaultMaxDepth;
                public int MaxArrayItems = DefaultMaxArrayItems;
                public bool IncludeFields = true;
                public bool IncludeProperties = true;
                public bool DeepSerialization = true;
                public List<string> Paths;
                public HashSet<string> NameFilters;
                public string ViewPath;
                public string NamePattern;
                public string TypeFilter;
            }

            public static object Serialize(object obj, Options options, UnityCliRequestBudget budget)
            {
                if (budget == null) throw new ArgumentNullException("budget");
                options = options ?? new Options();
                ValidateOptions(options);
                var active = new HashSet<object>(ReferenceComparer.Instance);
                SerializedMember root;
                if (options.Paths != null && options.Paths.Count > 0)
                {
                    root = BuildPathReadAggregate(obj, options, active, budget);
                }
                else
                {
                    if (!string.IsNullOrEmpty(options.ViewPath))
                    {
                        var normalized = NormalizeReadPath(options.ViewPath);
                        obj = TryReadPath(obj, normalized, budget, out var error);
                        if (error != null) return MemberToData(ErrorMember("$." + normalized, error));
                    }
                    var path = string.IsNullOrEmpty(options.ViewPath) ? "$" : "$." + NormalizeReadPath(options.ViewPath);
                    root = ToMember(obj, path, obj == null ? "null" : obj.GetType().FullName, options, active, 0, budget);
                }
                return MemberToData(root);
            }

            internal static int ParseMaxDepth(object value)
            {
                try
                {
                    var result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    if (result < 0 || result > AbsoluteMaxDepth) throw new OverflowException();
                    return result;
                }
                catch (Exception exception) when (exception is FormatException || exception is InvalidCastException || exception is OverflowException)
                {
                    throw new BridgeCommandException("E_OBJECT_READ_LIMIT_INVALID", "viewQuery.maxDepth must be an integer between 0 and 16.", "Pass a maxDepth in the documented range.");
                }
            }

            private static void ValidateOptions(Options options)
            {
                if (options.MaxDepth < 0 || options.MaxDepth > AbsoluteMaxDepth)
                    throw new BridgeCommandException("E_OBJECT_READ_LIMIT_INVALID", "maxDepth must be between 0 and 16.", "Pass a maxDepth in the documented range.");
                if (options.MaxArrayItems < 1 || options.MaxArrayItems > AbsoluteMaxArrayItems)
                    throw new BridgeCommandException("E_OBJECT_READ_LIMIT_INVALID", "maxArrayItems must be between 1 and 1000.", "Pass a maxArrayItems in the documented range.");
                if (options.Paths != null && options.Paths.Count > AbsoluteMaxPaths)
                    throw new BridgeCommandException("E_OBJECT_READ_LIMIT_INVALID", "paths accepts at most 128 entries.", "Split the exact member reads across requests.");
            }

            /// <summary>所有 paths 共用 active graph 和同一个 budget；输入顺序即稳定 continuation 顺序。</summary>
            private static SerializedMember BuildPathReadAggregate(object obj, Options options, HashSet<object> active, UnityCliRequestBudget budget)
            {
                var aggregate = NewMember("$", "UnityCli.PathReadAggregate", budget);
                if (aggregate.Truncated) return aggregate;
                var childOptions = WithoutPaths(options);
                for (var index = 0; index < options.Paths.Count; index++)
                {
                    var rawPath = options.Paths[index];
                    if (string.IsNullOrWhiteSpace(rawPath)) continue;
                    if (!budget.TryCheckpoint()) { aggregate.Truncated = true; aggregate.NextPath = rawPath; break; }
                    var normalizedPath = NormalizeReadPath(rawPath);
                    var value = TryReadPath(obj, normalizedPath, budget, out var error);
                    var path = string.IsNullOrEmpty(normalizedPath) ? "$" : "$." + normalizedPath;
                    var child = error == null
                        ? ToMember(value, path, value == null ? "null" : value.GetType().FullName, childOptions, active, 0, budget)
                        : ErrorMember(path, error);
                    if (!budget.TryReserveSerializedUtf8(rawPath)) { aggregate.Truncated = true; aggregate.NextPath = rawPath; aggregate.Error = BudgetError(budget); break; }
                    aggregate.Fields.Add(new SerializedMember.KV { Key = rawPath, Value = child });
                    if (child.Truncated && budget.IsExhausted)
                    {
                        aggregate.Truncated = true;
                        aggregate.NextPath = rawPath;
                        break;
                    }
                }
                return aggregate;
            }

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
                    NamePattern = options.NamePattern,
                    TypeFilter = options.TypeFilter
                };
            }

            private static string NormalizeReadPath(string path)
            {
                if (string.IsNullOrEmpty(path)) return string.Empty;
                var normalized = path.Trim();
                if (normalized.StartsWith("#/", StringComparison.Ordinal)) normalized = normalized.Substring(2);
                if (normalized.StartsWith("$.", StringComparison.Ordinal)) normalized = normalized.Substring(2);
                if (normalized.StartsWith("$/", StringComparison.Ordinal)) normalized = normalized.Substring(2);
                if (normalized.StartsWith("$[", StringComparison.Ordinal)) normalized = normalized.Substring(1);
                return normalized.Replace('/', '.').Trim('.');
            }

            private static object TryReadPath(object root, string path, UnityCliRequestBudget budget, out string error)
            {
                error = null;
                if (root == null) { error = "root object is null"; return null; }
                if (string.IsNullOrEmpty(path)) return root;
                var current = root;
                foreach (var rawSegment in path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!budget.TryConsumeNode()) { error = "request-budget-exhausted:" + budget.ExhaustedDimension; return null; }
                    current = ReadPathSegment(current, rawSegment, budget, out error);
                    if (error != null) return null;
                }
                return current;
            }

            private static object ReadPathSegment(object current, string segment, UnityCliRequestBudget budget, out string error)
            {
                error = null;
                if (current == null) { error = "encountered null before segment '" + segment + "'"; return null; }
                var name = segment;
                string indexer = null;
                var bracketIndex = segment.IndexOf('[');
                if (bracketIndex >= 0) { name = segment.Substring(0, bracketIndex); indexer = segment.Substring(bracketIndex).Trim(); }
                object value = current;
                if (!string.IsNullOrEmpty(name))
                {
                    var type = current.GetType();
                    if (!budget.TryConsumeReflectionCall()) { error = BudgetError(budget); return null; }
                    var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) { error = BudgetError(budget); return null; }
                        value = field.GetValue(current);
                    }
                    else
                    {
                        if (!budget.TryConsumeReflectionCall()) { error = BudgetError(budget); return null; }
                        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (property == null || !property.CanRead || property.GetIndexParameters().Length > 0) { error = "member not found or unreadable: " + name; return null; }
                        if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) { error = BudgetError(budget); return null; }
                        value = property.GetValue(current, null);
                    }
                }
                return string.IsNullOrEmpty(indexer) ? value : ReadIndexer(value, indexer, budget, out error);
            }

            private static object ReadIndexer(object value, string indexer, UnityCliRequestBudget budget, out string error)
            {
                error = null;
                if (value == null) { error = "cannot index null value"; return null; }
                var token = indexer.Trim();
                if (!token.StartsWith("[", StringComparison.Ordinal) || !token.EndsWith("]", StringComparison.Ordinal)) { error = "invalid index syntax: " + indexer; return null; }
                token = UnescapeDictionaryPathKey(token.Substring(1, token.Length - 2).Trim().Trim('"')); 
                if (value is IList list)
                {
                    if (!budget.TryConsumeGetter()) { error = BudgetError(budget); return null; }
                    var count = list.Count;
                    int index;
                    if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0 || index >= count) { error = "list index out of range: " + token; return null; }
                    if (!budget.TryConsumeGetter()) { error = BudgetError(budget); return null; }
                    return list[index];
                }
                if (value is IDictionary dictionary)
                {
                    var enumerator = dictionary.GetEnumerator();
                    try
                    {
                        while (budget.TryConsumeScannedRecord() && enumerator.MoveNext())
                        {
                            if (!budget.TryConsumeGetter()) { error = BudgetError(budget); return null; }
                            var entry = enumerator.Entry;
                            if (string.Equals(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), token, StringComparison.Ordinal)) return entry.Value;
                        }
                    }
                    finally { (enumerator as IDisposable)?.Dispose(); }
                    error = budget.IsExhausted ? BudgetError(budget) : "dictionary key not found: " + token;
                    return null;
                }
                error = "value is not indexable";
                return null;
            }

            private static SerializedMember ToMember(object obj, string path, string declaredType, Options options, HashSet<object> active, int depth, UnityCliRequestBudget budget)
            {
                var member = NewMember(path, declaredType ?? "null", budget);
                if (member.Truncated) return member;
                if (obj == null) { member.IsNull = true; return member; }
                var type = obj.GetType();
                member.TypeName = type.FullName;
                if (IsSimpleType(type) || obj is string)
                {
                    member.Value = obj;
                    if (!budget.TryReserveSerializedUtf8(Convert.ToString(obj, CultureInfo.InvariantCulture))) Truncate(member, budget, path);
                    return member;
                }

                // depth 与 cycle 必须先于 Unity 引用、集合枚举和反射成员展开。
                if (depth >= options.MaxDepth)
                {
                    member.Value = UnsupportedValue(type, "max-depth-reached");
                    member.Truncated = true;
                    member.NextPath = path;
                    return member;
                }
                var trackReference = !type.IsValueType;
                if (trackReference && !active.Add(obj))
                {
                    member.Error = "cycle-detected";
                    member.Truncated = true;
                    member.NextPath = path;
                    return member;
                }

                try
                {
                    if (obj is UnityEngine.Object unityObject)
                    {
                        member.IsUnityObject = true;
                        member.UnityObjectRef = UnityObjectRefJson(unityObject, budget);
                        if (options.DeepSerialization) ExpandMembers(obj, type, member, options, active, depth, budget);
                        return member;
                    }
                    if (obj is IList list) { ExpandList(list, member, options, active, depth, budget); return member; }
                    if (obj is IDictionary dictionary) { ExpandDictionary(dictionary, member, options, active, depth, budget); return member; }
                    if (obj is IEnumerable enumerable && !(obj is GameObject)) { ExpandEnumerable(enumerable, member, options, active, depth, budget); return member; }
                    ExpandMembers(obj, type, member, options, active, depth, budget);
                    return member;
                }
                finally
                {
                    if (trackReference) active.Remove(obj);
                }
            }

            private static SerializedMember NewMember(string path, string typeName, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeNode() || !budget.TryReserveSerializedUtf8Bytes(128L) || !budget.TryReserveSerializedUtf8(path) || !budget.TryReserveSerializedUtf8(typeName))
                    return new SerializedMember { Path = path, TypeName = typeName, Truncated = true, NextPath = path, Error = BudgetError(budget) };
                return new SerializedMember { Path = path, TypeName = typeName };
            }

            private static void ExpandList(IList list, SerializedMember member, Options options, HashSet<object> active, int depth, UnityCliRequestBudget budget)
            {
                member.IsArray = true;
                if (!budget.TryConsumeGetter()) { Truncate(member, budget, member.Path + "[0]"); return; }
                var count = list.Count;
                member.ArrayLength = count;
                member.HasKnownTotal = true;
                var limit = Math.Min(count, options.MaxArrayItems);
                for (var index = 0; index < limit; index++)
                {
                    var itemPath = member.Path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                    if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeGetter()) { Truncate(member, budget, itemPath); return; }
                    member.ArrayItems.Add(ToMember(list[index], itemPath, null, options, active, depth + 1, budget));
                    if (budget.IsExhausted) { Truncate(member, budget, itemPath); return; }
                }
                if (count > limit) { member.Truncated = true; member.NextPath = member.Path + "[" + limit.ToString(CultureInfo.InvariantCulture) + "]"; }
            }

            private static void ExpandDictionary(IDictionary dictionary, SerializedMember member, Options options, HashSet<object> active, int depth, UnityCliRequestBudget budget)
            {
                member.IsDictionary = true;
                if (!budget.TryConsumeGetter()) { Truncate(member, budget, member.Path); return; }
                var count = dictionary.Count;
                member.ArrayLength = count;
                member.HasKnownTotal = true;
                var enumerator = dictionary.GetEnumerator();
                var emitted = 0;
                try
                {
                    while (emitted < options.MaxArrayItems)
                    {
                        if (!budget.TryConsumeScannedRecord()) { Truncate(member, budget, member.Path); return; }
                        if (!enumerator.MoveNext()) return;
                        if (!budget.TryConsumeGetter()) { Truncate(member, budget, member.Path); return; }
                        var entry = enumerator.Entry;
                        var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                        var entryPath = DictionaryEntryPath(member.Path, key);
                        if (!budget.TryReserveSerializedUtf8(key)) { Truncate(member, budget, entryPath); return; }
                        member.DictionaryEntries.Add(new SerializedMember.KV { Key = key, Value = ToMember(entry.Value, entryPath, null, options, active, depth + 1, budget) });
                        emitted++;
                        if (budget.IsExhausted) { Truncate(member, budget, entryPath); return; }
                    }
                    if (emitted < count)
                    {
                        if (!budget.TryConsumeScannedRecord() || !enumerator.MoveNext() || !budget.TryConsumeGetter()) { Truncate(member, budget, member.Path); return; }
                        var nextEntry = enumerator.Entry;
                        member.Truncated = true;
                        member.NextPath = DictionaryEntryPath(member.Path, Convert.ToString(nextEntry.Key, CultureInfo.InvariantCulture));
                    }
                }
                finally { (enumerator as IDisposable)?.Dispose(); }
            }

            /// <summary>非 IList enumerable 只流式返回当前片段；它没有可重放的索引语义，截断时不发布 nextPath。</summary>
            private static void ExpandEnumerable(IEnumerable enumerable, SerializedMember member, Options options, HashSet<object> active, int depth, UnityCliRequestBudget budget)
            {
                member.IsArray = true;
                var enumerator = enumerable.GetEnumerator();
                var index = 0;
                try
                {
                    while (index < options.MaxArrayItems)
                    {
                        if (!budget.TryConsumeScannedRecord()) { TruncateNonResumableEnumerable(member, budget); return; }
                        if (!enumerator.MoveNext()) { member.ArrayLength = index; return; }
                        if (!budget.TryConsumeGetter()) { TruncateNonResumableEnumerable(member, budget); return; }
                        var itemPath = member.Path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                        member.ArrayItems.Add(ToMember(enumerator.Current, itemPath, null, options, active, depth + 1, budget));
                        index++;
                        if (budget.IsExhausted) { TruncateNonResumableEnumerable(member, budget); return; }
                    }
                    member.ArrayLength = index;
                    if (!budget.TryConsumeScannedRecord()) { TruncateNonResumableEnumerable(member, budget); return; }
                    if (!enumerator.MoveNext()) return;
                    member.Truncated = true;
                    member.NextPath = null;
                    member.Error = "non-resumable-enumerable-truncation";
                }
                finally { (enumerator as IDisposable)?.Dispose(); }
            }

            private static void ExpandMembers(object obj, Type type, SerializedMember member, Options options, HashSet<object> active, int depth, UnityCliRequestBudget budget)
            {
                var memberCount = 0;
                foreach (var candidate in GetMemberCandidates(type, budget))
                {
                    if ((candidate.IsProperty && !options.IncludeProperties) || (!candidate.IsProperty && !options.IncludeFields)) continue;
                    if (!MemberAllowed(candidate.Name, options)) continue;
                    if (memberCount >= AbsoluteMaxMembers) { member.Truncated = true; member.NextPath = member.Path + "." + candidate.Name; return; }
                    var candidatePath = member.Path + "." + candidate.Name;
                    if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) { Truncate(member, budget, candidatePath); return; }
                    try
                    {
                        var value = candidate.IsProperty
                            ? ((PropertyInfo)candidate.Member).GetValue(obj, null)
                            : ((FieldInfo)candidate.Member).GetValue(obj);
                        var child = ToMember(value, candidatePath, candidate.ValueTypeName, options, active, depth + 1, budget);
                        if (TypeAllowed(child.TypeName, options))
                        {
                            child.ReadOnly = candidate.IsProperty && !candidate.CanWrite;
                            child.Writable = candidate.IsProperty && candidate.CanWrite;
                            if (!budget.TryReserveSerializedUtf8(candidate.Name)) { Truncate(member, budget, candidatePath); return; }
                            var destination = candidate.IsProperty ? member.Props : member.Fields;
                            destination.Add(new SerializedMember.KV { Key = candidate.Name, Value = child });
                            memberCount++;
                        }
                        if (budget.IsExhausted) { Truncate(member, budget, candidatePath); return; }
                    }
                    catch (UnityCliRequestBudgetExceededException) { Truncate(member, budget, candidatePath); return; }
                    catch (Exception exception)
                    {
                        var destination = candidate.IsProperty ? member.Props : member.Fields;
                        destination.Add(new SerializedMember.KV { Key = candidate.Name, Value = ErrorMember(candidatePath, exception.Message) });
                        memberCount++;
                    }
                }
            }

            private static string DictionaryEntryPath(string path, string key)
            {
                var value = key ?? string.Empty;
                var builder = new StringBuilder(value.Length + 8);
                foreach (var character in value)
                {
                    if (char.IsLetterOrDigit(character) || character == '_' || character == '-') builder.Append(character);
                    else builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                }
                return path + "[\"" + builder + "\"]";
            }

            private static string UnescapeDictionaryPathKey(string value)
            {
                if (string.IsNullOrEmpty(value) || value.IndexOf("\\u", StringComparison.Ordinal) < 0) return value;
                var builder = new StringBuilder(value.Length);
                for (var index = 0; index < value.Length; index++)
                {
                    if (value[index] == '\\' && index + 5 < value.Length && value[index + 1] == 'u'
                        && int.TryParse(value.Substring(index + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        builder.Append((char)code);
                        index += 5;
                    }
                    else builder.Append(value[index]);
                }
                return builder.ToString();
            }

            /// <summary>逐项扫描 declared metadata，并把单次 domain reload 内的有界结果复用给后续读取。</summary>
            private static MemberCandidate[] GetMemberCandidates(Type type, UnityCliRequestBudget budget)
            {
                MemberCandidate[] cached;
                var generation = UnityCliReadGeneration.Reflection();
                lock (MemberCacheLock)
                {
                    if (!string.Equals(memberCacheGeneration, generation, StringComparison.Ordinal))
                    {
                        MemberCandidateCache.Clear();
                        memberCacheGeneration = generation;
                    }
                    if (MemberCandidateCache.TryGetValue(type, out cached))
                    {
                        if (!budget.TryConsumeScannedRecords(cached.Length)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                        return cached;
                    }
                }

                var candidates = new List<MemberCandidate>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                var metadataBytes = 0;
                var current = type;
                for (var depth = 0; current != null && depth < AbsoluteMaxTypeHierarchyDepth; depth++)
                {
                    if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    foreach (var field in current.GetTypeInfo().DeclaredFields)
                    {
                        ConsumeMemberCandidateBudget(budget);
                        if (!field.IsPublic || field.IsStatic || !names.Add("F:" + field.Name)) continue;
                        AddMemberCandidate(candidates, new MemberCandidate(field, false, true, field.FieldType), ref metadataBytes);
                    }
                    if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    foreach (var property in current.GetTypeInfo().DeclaredProperties)
                    {
                        ConsumeMemberCandidateBudget(budget);
                        var getter = property.GetMethod;
                        if (getter == null || !getter.IsPublic || getter.IsStatic || property.GetIndexParameters().Length > 0 || !names.Add("P:" + property.Name)) continue;
                        AddMemberCandidate(candidates, new MemberCandidate(property, true, property.CanWrite, property.PropertyType), ref metadataBytes);
                    }
                    if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    current = current.GetTypeInfo().BaseType;
                }
                if (current != null) throw MetadataLimit("type hierarchy depth");
                candidates.Sort((left, right) =>
                {
                    var nameOrder = string.CompareOrdinal(left.Name, right.Name);
                    return nameOrder != 0 ? nameOrder : left.IsProperty.CompareTo(right.IsProperty);
                });
                cached = candidates.ToArray();
                lock (MemberCacheLock)
                {
                    if (MemberCandidateCache.Count >= AbsoluteMaxCachedMemberTypes) MemberCandidateCache.Clear();
                    MemberCandidateCache[type] = cached;
                }
                return cached;
            }

            private static void ConsumeMemberCandidateBudget(UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            }

            private static void AddMemberCandidate(List<MemberCandidate> candidates, MemberCandidate candidate, ref int metadataBytes)
            {
                if (candidates.Count >= AbsoluteMaxMemberCandidates) throw MetadataLimit("member count");
                metadataBytes += Encoding.UTF8.GetByteCount(candidate.Name ?? string.Empty);
                metadataBytes += Encoding.UTF8.GetByteCount(candidate.ValueTypeName ?? string.Empty);
                if (metadataBytes > AbsoluteMaxMemberMetadataUtf8Bytes) throw MetadataLimit("metadata bytes");
                candidates.Add(candidate);
            }

            private static BridgeCommandException MetadataLimit(string dimension)
            {
                return new BridgeCommandException("E_REQUEST_BUDGET_EXCEEDED", "Object member metadata exceeded its bounded " + dimension + ".", "Use paths, nameFilters, or a narrower object type.");
            }

            private static void Truncate(SerializedMember member, UnityCliRequestBudget budget, string nextPath)
            {
                member.Truncated = true;
                member.NextPath = nextPath;
                if (member.Error == null && budget.IsExhausted) member.Error = BudgetError(budget);
            }

            private static void TruncateNonResumableEnumerable(SerializedMember member, UnityCliRequestBudget budget)
            {
                member.Truncated = true;
                member.NextPath = null;
                member.Error = budget.IsExhausted ? BudgetError(budget) + ";non-resumable-enumerable" : "non-resumable-enumerable-truncation";
            }

            private static string BudgetError(UnityCliRequestBudget budget) { return "request-budget-exhausted:" + (budget.ExhaustedDimension ?? "unknown"); }

            private static bool MemberAllowed(string name, Options options)
            {
                if (options.NameFilters != null && options.NameFilters.Count > 0) return options.NameFilters.Contains(name);
                if (string.IsNullOrEmpty(options.NamePattern)) return true;
                try { return Regex.IsMatch(name, options.NamePattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
                catch (RegexMatchTimeoutException) { throw new BridgeCommandException("E_OBJECT_READ_PATTERN_INVALID", "namePattern exceeded the bounded regex evaluation time.", "Use a simpler anchored namePattern."); }
            }

            private static bool TypeAllowed(string typeName, Options options)
            {
                return string.IsNullOrEmpty(options.TypeFilter) || (!string.IsNullOrEmpty(typeName) && typeName.IndexOf(options.TypeFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            private static bool IsSimpleType(Type type)
            {
                return type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan)
                    || type == typeof(Guid) || type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4)
                    || type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Rect) || type == typeof(Bounds) || type == typeof(LayerMask);
            }

            private static SerializedMember ErrorMember(string path, string message)
            {
                return new SerializedMember { Path = path, TypeName = "error", Error = message, Truncated = true, NextPath = path };
            }

            public static Dictionary<string, object> UnityObjectRefJson(UnityEngine.Object value, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var name = value.name;
                var typeName = value.GetType().FullName;
                if (!budget.TryReserveSerializedUtf8(name) || !budget.TryReserveSerializedUtf8(typeName)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var result = new Dictionary<string, object>
                {
                    { "instanceId", (long)value.GetInstanceID() }, { "name", name }, { "typeName", typeName }
                };
                if (value is GameObject gameObject)
                {
                    result["kind"] = "GameObject";
                    result["path"] = GameObjectFullPath(gameObject, budget);
                }
                else
                {
                    result["kind"] = value is Component ? "Component" : "Asset";
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var assetPath = AssetDatabase.GetAssetPath(value);
                    if (!string.IsNullOrEmpty(assetPath)) { result["assetPath"] = NormalizeAssetPath(assetPath); result["guid"] = AssetDatabase.AssetPathToGUID(assetPath); }
                }
                return result;
            }

            private static string GameObjectFullPath(GameObject gameObject, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var path = gameObject.name;
                var current = gameObject.transform.parent;
                while (current != null)
                {
                    if (!budget.TryConsumeNode() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    path = current.name + "/" + path;
                    current = current.parent;
                }
                return path;
            }

            private static Dictionary<string, object> MemberToData(SerializedMember member)
            {
                var data = new Dictionary<string, object> { { "path", member.Path }, { "typeName", member.TypeName } };
                if (member.Error != null) data["error"] = member.Error;
                if (member.Truncated) data["truncated"] = true;
                if (member.NextPath != null) data["nextPath"] = member.NextPath;
                if (member.IsNull) data["value"] = null;
                else if (member.Value != null) data["value"] = ValueData(member.Value);
                if (member.IsUnityObject) data["unityObjectRef"] = member.UnityObjectRef;
                if (member.IsArray)
                {
                    data["isArray"] = true;
                    if (member.HasKnownTotal) data["totalCount"] = (long)member.ArrayLength;
                    data["returnedCount"] = (long)member.ArrayItems.Count;
                    var items = new List<object>();
                    foreach (var item in member.ArrayItems) items.Add(MemberToData(item));
                    data["items"] = items;
                }
                if (member.IsDictionary)
                {
                    data["isDictionary"] = true;
                    if (member.HasKnownTotal) data["totalCount"] = (long)member.ArrayLength;
                    data["returnedCount"] = (long)member.DictionaryEntries.Count;
                    var entries = new List<object>();
                    foreach (var entry in member.DictionaryEntries) entries.Add(new Dictionary<string, object> { { "key", entry.Key }, { "value", MemberToData(entry.Value) } });
                    data["entries"] = entries;
                }
                if (member.Fields.Count > 0)
                {
                    var fields = new List<object>();
                    foreach (var field in member.Fields) fields.Add(new Dictionary<string, object> { { "name", field.Key }, { "value", MemberToData(field.Value) } });
                    data["fields"] = fields;
                }
                if (member.Props.Count > 0)
                {
                    var properties = new List<object>();
                    foreach (var property in member.Props)
                    {
                        var item = new Dictionary<string, object> { { "name", property.Key }, { "value", MemberToData(property.Value) } };
                        if (property.Value.ReadOnly) item["readOnly"] = true;
                        if (property.Value.Writable) item["writable"] = true;
                        properties.Add(item);
                    }
                    data["props"] = properties;
                }
                return data;
            }

            private static object ValueData(object value)
            {
                if (value is Enum) return Convert.ToString(value, CultureInfo.InvariantCulture);
                if (value is Vector2 v2) return new Dictionary<string, object> { { "x", JsonNumberData(v2.x) }, { "y", JsonNumberData(v2.y) } };
                if (value is Vector3 v3) return new Dictionary<string, object> { { "x", JsonNumberData(v3.x) }, { "y", JsonNumberData(v3.y) }, { "z", JsonNumberData(v3.z) } };
                if (value is Vector4 v4) return new Dictionary<string, object> { { "x", JsonNumberData(v4.x) }, { "y", JsonNumberData(v4.y) }, { "z", JsonNumberData(v4.z) }, { "w", JsonNumberData(v4.w) } };
                if (value is Quaternion quaternion) return new Dictionary<string, object> { { "x", JsonNumberData(quaternion.x) }, { "y", JsonNumberData(quaternion.y) }, { "z", JsonNumberData(quaternion.z) }, { "w", JsonNumberData(quaternion.w) } };
                if (value is Color color) return new Dictionary<string, object> { { "r", JsonNumberData(color.r) }, { "g", JsonNumberData(color.g) }, { "b", JsonNumberData(color.b) }, { "a", JsonNumberData(color.a) } };
                if (value is float single) return JsonNumberData(single);
                if (value is double number) return JsonNumberData(number);
                if (value is string || value is bool || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal) return value;
                return UnsupportedValue(value.GetType(), "unsupported-dynamic-value");
            }

            private static object JsonNumberData(double value)
            {
                if (double.IsNaN(value)) return "NaN";
                if (double.IsPositiveInfinity(value)) return "Infinity";
                if (double.IsNegativeInfinity(value)) return "-Infinity";
                return value;
            }

            private static Dictionary<string, object> UnsupportedValue(Type type, string reason)
            {
                return new Dictionary<string, object> { { "unsupported", true }, { "reason", reason }, { "typeName", type == null ? null : type.FullName } };
            }

            private sealed class ReferenceComparer : IEqualityComparer<object>
            {
                internal static readonly ReferenceComparer Instance = new ReferenceComparer();
                public new bool Equals(object left, object right) { return ReferenceEquals(left, right); }
                public int GetHashCode(object value) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value); }
            }

            private sealed class MemberCandidate
            {
                internal MemberCandidate(MemberInfo member, bool isProperty, bool canWrite, Type valueType)
                {
                    Member = member;
                    IsProperty = isProperty;
                    CanWrite = canWrite;
                    Name = member.Name;
                    ValueTypeName = valueType == null ? null : valueType.FullName;
                }

                internal MemberInfo Member { get; private set; }
                internal bool IsProperty { get; private set; }
                internal bool CanWrite { get; private set; }
                internal string Name { get; private set; }
                internal string ValueTypeName { get; private set; }
            }

            private sealed class SerializedMember
            {
                public string Path { get; set; }
                public string TypeName { get; set; }
                [UnityCliAnyJson] public object Value { get; set; }
                public bool IsNull { get; set; }
                public bool IsUnityObject { get; set; }
                [UnityCliAnyJson] public object UnityObjectRef { get; set; }
                public bool IsArray { get; set; }
                public bool IsDictionary { get; set; }
                public bool HasKnownTotal { get; set; }
                public int ArrayLength { get; set; }
                public bool Truncated { get; set; }
                public string NextPath { get; set; }
                public string Error { get; set; }
                public bool ReadOnly { get; set; }
                public bool Writable { get; set; }
                public List<KV> Fields { get; } = new List<KV>();
                public List<KV> Props { get; } = new List<KV>();
                public List<SerializedMember> ArrayItems { get; } = new List<SerializedMember>();
                public List<KV> DictionaryEntries { get; } = new List<KV>();
                public sealed class KV { public string Key { get; set; } public SerializedMember Value { get; set; } }
            }
        }
    }
}
#endif
