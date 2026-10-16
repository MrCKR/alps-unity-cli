#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 有界值投影：参数、结果、字段、Emit 与 State 共用同一 owner。
    ///
    /// 只读取已知安全的值；不调用用户 ToString、不调用属性 getter、不遍历任意对象图。
    /// 深度、集合条目、字符串长度和 State 总字节都有硬上限，超限只影响记录，不改变游戏调用。
    /// </summary>
    internal static class MethodHookProjection
    {
        internal sealed class Truncation
        {
            internal bool Truncated;
            internal void Mark() { Truncated = true; }
        }

        /// <summary>投影任意值供事件记录使用；默认允许读取 Unity 对象的显示身份（只在主线程使用）。</summary>
        internal static object Project(object value, Truncation truncation)
        {
            return Project(value, truncation, true);
        }

        /// <summary>
        /// 投影任意值供事件记录使用。
        /// allowUnityIdentity=false 时禁止读取 Unity 对象的名称与 instanceId（只能由主线程读取），
        /// 此时对象只投影为托管类型名，因此自动函数命中即不可能读取主线程 API。
        /// </summary>
        internal static object Project(object value, Truncation truncation, bool allowUnityIdentity)
        {
            return ProjectValue(value, 0, truncation ?? new Truncation(), allowUnityIdentity);
        }

        private static object ProjectValue(object value, int depth, Truncation truncation, bool allowUnityIdentity)
        {
            if (value == null) return null;
            var text = value as string;
            if (text != null)
            {
                var bounded = UnityCliBridge.BoundedText(text, DebugLimits.MaxProjectionStringBytes);
                if (bounded.Length != text.Length) truncation.Mark();
                return bounded;
            }
            if (value is bool) return value;
            if (value is int || value is long) return value;
            if (value is byte || value is sbyte || value is short || value is ushort || value is uint || value is ulong) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is char) return value.ToString();
            if (value is float) return ProjectFloating(Convert.ToDouble(value, CultureInfo.InvariantCulture), truncation);
            if (value is double) return ProjectFloating((double)value, truncation);
            if (value is decimal) return ProjectFloating(Convert.ToDouble(value, CultureInfo.InvariantCulture), truncation);
            if (value is Enum) return value.ToString();
            if (value is DateTime) return ((DateTime)value).ToString("O", CultureInfo.InvariantCulture);
            if (value is TimeSpan) return ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture);
            if (value is Guid) return ((Guid)value).ToString("D");
            if (value is Uri) return value.ToString();

            var unityObject = value as UnityEngine.Object;
            if (unityObject != null)
                return allowUnityIdentity ? ProjectUnityIdentity(unityObject, truncation) : "unity-object:" + (value.GetType().FullName ?? value.GetType().Name);

            if (value is Type) return "type:" + (((Type)value).FullName ?? ((Type)value).Name);
            if (value is Delegate) return "delegate:" + ((Delegate)value).GetType().Name;
            if (value is MethodBase) return "method:" + MethodHookMethodText.Describe((MethodBase)value);

            if (depth >= DebugLimits.MaxProjectionDepth)
            {
                truncation.Mark();
                return TypeOnly(value);
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                var count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count >= DebugLimits.MaxProjectionItems) { truncation.Mark(); break; }
                    var key = entry.Key as string;
                    if (key == null) { truncation.Mark(); continue; }
                    result[key] = ProjectValue(entry.Value, depth + 1, truncation, allowUnityIdentity);
                    count++;
                }
                return result;
            }

            var sequence = value as IEnumerable;
            if (sequence != null)
            {
                var result = new List<object>();
                foreach (var item in sequence)
                {
                    if (result.Count >= DebugLimits.MaxProjectionItems) { truncation.Mark(); break; }
                    result.Add(ProjectValue(item, depth + 1, truncation, allowUnityIdentity));
                }
                return result;
            }
            return TypeOnly(value);
        }

        /// <summary>非有限浮点值不是 JSON 值，显式转成名称而不伪造数字。</summary>
        private static object ProjectFloating(double number, Truncation truncation)
        {
            if (double.IsNaN(number)) { truncation.Mark(); return "NaN"; }
            if (double.IsInfinity(number)) { truncation.Mark(); return number > 0 ? "Infinity" : "-Infinity"; }
            return number;
        }

        /// <summary>
        /// Unity 对象显示身份的唯一读取点；只允许在主线程调用。
        /// worker 命中需要显示身份时必须走主线程补充事件，不得在命中线程调用本方法。
        /// </summary>
        internal static object ProjectUnityIdentity(UnityEngine.Object value, Truncation truncation)
        {
            // 只在对象仍然存活时读取标识；已销毁对象不触发 getter。
            try
            {
                var type = value.GetType().FullName;
                var name = value.name;
                if (name != null && name.Length > 256) { name = name.Substring(0, 256); truncation.Mark(); }
                return new Dictionary<string, object>
                {
                    { "type", type },
                    { "name", name },
                    { "instanceId", (long)value.GetInstanceID() }
                };
            }
            catch
            {
                truncation.Mark();
                return "unity-object:destroyed";
            }
        }

        private static string TypeOnly(object value)
        {
            return "object:" + (value.GetType().FullName ?? value.GetType().Name);
        }

        /// <summary>把值规范化为有界 JSON 值；非法类型或超限直接失败，不静默丢数据。</summary>
        internal static object NormalizeStateValue(object value)
        {
            return NormalizeStateValue(value, 0, "$");
        }

        private static object NormalizeStateValue(object value, int depth, string path)
        {
            if (value == null) return null;
            if (depth > DebugLimits.MaxStateDepth)
                throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "State value at " + path + " exceeds the maximum nesting depth.", null, new[] { "Keep callback State values shallow and JSON-shaped." });
            var text = value as string;
            if (text != null)
            {
                var bounded = UnityCliBridge.BoundedText(text, DebugLimits.MaxProjectionStringBytes);
                if (bounded.Length != text.Length)
                    throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "State string at " + path + " exceeds the declared byte budget.", null, new[] { "Store a shorter string or emit it as an event instead." });
                return bounded;
            }
            if (value is bool) return value;
            if (value is int || value is long) return value;
            if (value is byte || value is sbyte || value is short || value is ushort || value is uint || value is ulong) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is float || value is double || value is decimal)
            {
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new BridgeCommandException("E_SCHEMA_INVALID", "State value at " + path + " is not a finite JSON number.", null, new[] { "Store a finite number or a string." });
                return number;
            }
            if (value is Enum) return value.ToString();

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (result.Count >= DebugLimits.MaxStateContainerItems)
                        throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "State object at " + path + " exceeds the declared item budget.", null, new[] { "Store fewer keys in callback State." });
                    var key = entry.Key as string;
                    if (key == null)
                        throw new BridgeCommandException("E_SCHEMA_INVALID", "State object at " + path + " requires string keys.", null, new[] { "Use a string key for every State entry." });
                    result[key] = NormalizeStateValue(entry.Value, depth + 1, path + "." + key);
                }
                return result;
            }

            var sequence = value as IEnumerable;
            if (sequence != null && !(value is string))
            {
                var result = new List<object>();
                foreach (var item in sequence)
                {
                    if (result.Count >= DebugLimits.MaxStateContainerItems)
                        throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "State array at " + path + " exceeds the declared item budget.", null, new[] { "Store fewer items in callback State." });
                    result.Add(NormalizeStateValue(item, depth + 1, path + "[]"));
                }
                return result;
            }

            throw new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "State value at " + path + " is " + (value.GetType().FullName ?? value.GetType().Name) + ", which is not an accepted JSON value.", null,
                new[] { "Store only JSON primitives, strings, lists and dictionaries in callback State." });
        }

        /// <summary>估算值在 JSON 中的字节数，用于有界预算判断。</summary>
        internal static long EstimateBytes(object value)
        {
            return Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(value));
        }
    }

    /// <summary>
    /// 单个 hook 的有界 JSON State 字典。所有写入都按同一上限校验，
    /// 不接受的类型（delegate、Type、Unity 对象）与超限写入直接失败。
    /// </summary>
    internal sealed class MethodHookStateBag : IDictionary<string, object>
    {
        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly string hookId;
        /// <summary>State 可以被任意托管线程上的回调读写，因此所有访问共用同一把短锁。</summary>
        private readonly object gate = new object();

        internal MethodHookStateBag(string hookId, IDictionary<string, object> initial)
        {
            this.hookId = hookId;
            if (initial != null) foreach (var pair in initial) values[pair.Key] = pair.Value;
        }

        internal MethodHookStateBag(string hookId) : this(hookId, null) { }

        internal Dictionary<string, object> Snapshot() { lock (gate) return new Dictionary<string, object>(values, StringComparer.Ordinal); }

        internal long ByteSize { get { return MethodHookProjection.EstimateBytes(Snapshot()); } }

        private void Enforce(string key, object normalized)
        {
            lock (gate)
            {
                var previous = values.ContainsKey(key) ? values[key] : null;
                values[key] = normalized;
                var size = ByteSize;
                if (size <= DebugLimits.MaxStateBytes) return;
                if (previous == null) values.Remove(key);
                else values[key] = previous;
            }
            throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "Callback State for hook " + hookId + " exceeds its byte budget.", null, new[] { "Keep callback State smaller, or emit values as bounded events instead." });
        }

        private static string RequireKey(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Callback State requires a non-empty string key.", null, new[] { "Pass a non-empty string key." });
            if (key.Length > 256) throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "Callback State key exceeds its declared length budget.", null, new[] { "Use a shorter State key." });
            return key;
        }

        public object this[string key]
        {
            get { lock (gate) return values[key]; }
            set { Enforce(RequireKey(key), MethodHookProjection.NormalizeStateValue(value)); }
        }

        public ICollection<string> Keys { get { lock (gate) return new List<string>(values.Keys); } }
        public ICollection<object> Values { get { lock (gate) return new List<object>(values.Values); } }
        public int Count { get { lock (gate) return values.Count; } }
        public bool IsReadOnly { get { return false; } }

        public void Add(string key, object value) { Enforce(RequireKey(key), MethodHookProjection.NormalizeStateValue(value)); }
        public void Add(KeyValuePair<string, object> item) { Add(item.Key, item.Value); }
        public bool ContainsKey(string key) { lock (gate) return values.ContainsKey(key); }
        public bool Contains(KeyValuePair<string, object> item) { lock (gate) return ((ICollection<KeyValuePair<string, object>>)values).Contains(item); }
        public bool Remove(string key) { lock (gate) return values.Remove(key); }
        public bool Remove(KeyValuePair<string, object> item) { lock (gate) return ((ICollection<KeyValuePair<string, object>>)values).Remove(item); }
        public bool TryGetValue(string key, out object value) { lock (gate) return values.TryGetValue(key, out value); }
        public void Clear() { lock (gate) values.Clear(); }
        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) { lock (gate) ((ICollection<KeyValuePair<string, object>>)values).CopyTo(array, arrayIndex); }
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() { return ((IEnumerable<KeyValuePair<string, object>>)Snapshot()).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
}
#endif
