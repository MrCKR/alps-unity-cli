#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>Hook Definition 的公开状态闭集。</summary>
    internal enum MethodHookState
    {
        Armed,
        Dormant,
        Restoring,
        Disabled,
        Faulted
    }

    internal static class MethodHookStateText
    {
        internal static string Of(MethodHookState state)
        {
            switch (state)
            {
                case MethodHookState.Armed: return "armed";
                case MethodHookState.Dormant: return "dormant";
                case MethodHookState.Restoring: return "restoring";
                case MethodHookState.Disabled: return "disabled";
                default: return "faulted";
            }
        }

        internal static MethodHookState Parse(string value)
        {
            if (string.Equals(value, "armed", StringComparison.Ordinal)) return MethodHookState.Armed;
            if (string.Equals(value, "dormant", StringComparison.Ordinal)) return MethodHookState.Dormant;
            if (string.Equals(value, "restoring", StringComparison.Ordinal)) return MethodHookState.Restoring;
            if (string.Equals(value, "disabled", StringComparison.Ordinal)) return MethodHookState.Disabled;
            if (string.Equals(value, "faulted", StringComparison.Ordinal)) return MethodHookState.Faulted;
            return MethodHookState.Dormant;
        }
    }

    /// <summary>一个 exact 字段选择；Field 只在当前域解析，重挂必须重新解析。</summary>
    internal sealed class MethodHookCaptureField
    {
        internal string DeclaringType;
        internal string FieldName;
        internal FieldInfo Field;

        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "declaringType", DeclaringType ?? string.Empty },
                { "fieldName", FieldName ?? string.Empty }
            };
        }

        internal static MethodHookCaptureField FromData(object data)
        {
            var dictionary = data as IDictionary<string, object>;
            if (dictionary == null) return null;
            object value;
            var fieldName = dictionary.TryGetValue("fieldName", out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
            if (string.IsNullOrWhiteSpace(fieldName)) return null;
            var declaringType = dictionary.TryGetValue("declaringType", out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
            return new MethodHookCaptureField { DeclaringType = declaringType, FieldName = fieldName };
        }

        internal MethodHookCaptureFieldSpec ToSpec()
        {
            return new MethodHookCaptureFieldSpec { DeclaringType = string.IsNullOrEmpty(DeclaringType) ? null : DeclaringType, FieldName = FieldName };
        }
    }

    /// <summary>无自定义代码的捕获配置。</summary>
    internal sealed class MethodHookCapture
    {
        internal bool Arguments;
        internal bool Result;
        internal int SampleEvery = 1;
        internal List<MethodHookCaptureField> InstanceFields = new List<MethodHookCaptureField>();
        internal List<MethodHookCaptureField> StaticFields = new List<MethodHookCaptureField>();

        internal MethodHookCapture Clone()
        {
            return new MethodHookCapture
            {
                Arguments = Arguments,
                Result = Result,
                SampleEvery = SampleEvery,
                InstanceFields = new List<MethodHookCaptureField>(InstanceFields),
                StaticFields = new List<MethodHookCaptureField>(StaticFields)
            };
        }

        internal int FieldCount { get { return InstanceFields.Count + StaticFields.Count; } }

        /// <summary>至少一种捕获或回调行为必须有效。</summary>
        internal bool HasBehaviour(MethodHookCallbackSpec callbacks)
        {
            var hasCode = callbacks != null && (!string.IsNullOrWhiteSpace(callbacks.PrefixCode) || !string.IsNullOrWhiteSpace(callbacks.PostfixCode));
            return hasCode || Arguments || Result || FieldCount > 0;
        }

        internal MethodHookCaptureSummary Summarize()
        {
            var fields = new List<string>();
            foreach (var field in InstanceFields) fields.Add("instance:" + (string.IsNullOrEmpty(field.DeclaringType) ? string.Empty : field.DeclaringType + ".") + field.FieldName);
            foreach (var field in StaticFields) fields.Add("static:" + (string.IsNullOrEmpty(field.DeclaringType) ? string.Empty : field.DeclaringType + ".") + field.FieldName);
            return new MethodHookCaptureSummary
            {
                Arguments = Arguments,
                Result = Result,
                SampleEvery = SampleEvery,
                InstanceFieldCount = InstanceFields.Count,
                StaticFieldCount = StaticFields.Count,
                Fields = fields
            };
        }

        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "arguments", Arguments },
                { "result", Result },
                { "sampleEvery", (long)SampleEvery },
                { "instanceFields", InstanceFields.Select(field => (object)field.ToData()).ToList() },
                { "staticFields", StaticFields.Select(field => (object)field.ToData()).ToList() }
            };
        }

        internal static MethodHookCapture FromData(object data)
        {
            var dictionary = data as IDictionary<string, object>;
            if (dictionary == null) return null;
            var capture = new MethodHookCapture();
            object value;
            if (dictionary.TryGetValue("arguments", out value) && value is bool) capture.Arguments = (bool)value;
            if (dictionary.TryGetValue("result", out value) && value is bool) capture.Result = (bool)value;
            if (dictionary.TryGetValue("sampleEvery", out value))
            {
                long parsed;
                if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                    capture.SampleEvery = (int)Math.Min(parsed, MethodHookLimits.MaxSampleEvery);
            }
            var instanceFields = dictionary.TryGetValue("instanceFields", out value) ? value as IEnumerable<object> : null;
            if (instanceFields != null) foreach (var item in instanceFields) { var field = MethodHookCaptureField.FromData(item); if (field != null) capture.InstanceFields.Add(field); }
            var staticFields = dictionary.TryGetValue("staticFields", out value) ? value as IEnumerable<object> : null;
            if (staticFields != null) foreach (var item in staticFields) { var field = MethodHookCaptureField.FromData(item); if (field != null) capture.StaticFields.Add(field); }
            return capture;
        }
    }

    /// <summary>
    /// Hook Definition：某一 Editor Session 中获授权的拦截意图。
    /// 它不是当前域的 patch；Attachment 由 MethodHookTargetRuntime 表达并在重挂时重建。
    /// </summary>
    internal sealed class MethodHookDefinition
    {
        internal string HookId;
        internal long Revision = 1;
        internal string Label = string.Empty;
        internal int Priority;
        internal string Activation = "play";
        internal MethodHookTargetSpec TargetSpec;
        internal string TargetKey;
        internal MethodHookCapture Capture = new MethodHookCapture();
        internal string PrefixCode = string.Empty;
        internal string PostfixCode = string.Empty;
        internal bool EmitHitEvents = true;
        internal MethodHookState State = MethodHookState.Dormant;
        internal string FaultCode = string.Empty;
        internal string FaultMessage = string.Empty;
        internal long FaultRevision;
        internal long FaultInvocationId;
        internal string FaultOwner = string.Empty;
        internal long InstallSequence;
        internal long HitCount;
        internal long BypassCount;
        internal long SampleIndex;
        internal MethodHookStateBag StateBag;
        /// <summary>从快照恢复的 State 值；新域建立 State bag 时使用。</summary>
        internal Dictionary<string, object> RestoredStateValues = new Dictionary<string, object>(StringComparer.Ordinal);

        // 仅当前域有效；重挂必须重新解析与重新编译。
        internal MethodInfo Resolved;
        internal IUnityCliMethodHookCallback Callback;
        internal string CallbackKey = string.Empty;
        internal string PrefixHash = string.Empty;
        internal string PostfixHash = string.Empty;

        internal bool HasCallbacks { get { return !string.IsNullOrEmpty(PrefixCode) || !string.IsNullOrEmpty(PostfixCode); } }

        /// <summary>activation 与当前 Edit/Play 状态是否匹配。</summary>
        internal bool ActivationMatches(bool isPlaying)
        {
            return string.Equals(Activation, "editor-and-play", StringComparison.Ordinal) || isPlaying;
        }

        internal MethodHookListItem ToListItem()
        {
            return new MethodHookListItem
            {
                HookId = HookId,
                Label = Label,
                State = MethodHookStateText.Of(State),
                Revision = Revision,
                Activation = Activation,
                Priority = Priority,
                MethodIdentity = TargetSpec == null ? string.Empty : TargetSpec.TypeName + "." + TargetSpec.MethodName,
                HitCount = HitCount,
                FaultCode = string.IsNullOrEmpty(FaultCode) ? null : FaultCode
            };
        }

        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "hookId", HookId },
                { "revision", Revision },
                { "label", Label ?? string.Empty },
                { "priority", (long)Priority },
                { "activation", Activation ?? "play" },
                { "targetKey", TargetKey ?? string.Empty },
                { "target", TargetSpec == null ? null : new Dictionary<string, object>
                    {
                        { "assemblyName", TargetSpec.AssemblyName ?? string.Empty },
                        { "typeName", TargetSpec.TypeName ?? string.Empty },
                        { "methodName", TargetSpec.MethodName ?? string.Empty },
                        { "parameterTypes", (TargetSpec.ParameterTypes ?? new List<string>()).Cast<object>().ToList() },
                        { "returnType", TargetSpec.ReturnType ?? string.Empty },
                        { "isStatic", TargetSpec.IsStatic }
                    } },
                { "capture", Capture == null ? new MethodHookCapture().ToData() : Capture.ToData() },
                { "prefixCode", PrefixCode ?? string.Empty },
                { "postfixCode", PostfixCode ?? string.Empty },
                { "emitHitEvents", EmitHitEvents },
                { "state", MethodHookStateText.Of(State) },
                { "faultCode", FaultCode ?? string.Empty },
                { "faultMessage", FaultMessage ?? string.Empty },
                { "faultRevision", FaultRevision },
                { "faultInvocationId", FaultInvocationId },
                { "faultOwner", FaultOwner ?? string.Empty },
                { "installSequence", InstallSequence },
                { "hitCount", HitCount },
                { "bypassCount", BypassCount },
                { "stateValues", StateBag == null ? new Dictionary<string, object>() : StateBag.Snapshot() }
            };
        }

        internal static MethodHookDefinition FromData(object data)
        {
            var dictionary = data as IDictionary<string, object>;
            if (dictionary == null) return null;
            var definition = new MethodHookDefinition();
            object value;
            definition.HookId = dictionary.TryGetValue("hookId", out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
            if (string.IsNullOrWhiteSpace(definition.HookId)) return null;
            definition.Revision = Long(dictionary, "revision", 1);
            definition.Label = Text(dictionary, "label");
            definition.Priority = (int)Long(dictionary, "priority", 0);
            definition.Activation = Text(dictionary, "activation");
            if (string.IsNullOrEmpty(definition.Activation)) definition.Activation = "play";
            definition.TargetKey = Text(dictionary, "targetKey");
            var target = dictionary.TryGetValue("target", out value) ? value as IDictionary<string, object> : null;
            if (target == null) return null;
            definition.TargetSpec = new MethodHookTargetSpec
            {
                AssemblyName = Text(target, "assemblyName"),
                TypeName = Text(target, "typeName"),
                MethodName = Text(target, "methodName"),
                ParameterTypes = StringList(target, "parameterTypes"),
                ReturnType = Text(target, "returnType"),
                IsStatic = Bool(target, "isStatic", false)
            };
            definition.Capture = dictionary.TryGetValue("capture", out value) ? MethodHookCapture.FromData(value) : null;
            if (definition.Capture == null) definition.Capture = new MethodHookCapture();
            definition.PrefixCode = Text(dictionary, "prefixCode");
            definition.PostfixCode = Text(dictionary, "postfixCode");
            definition.EmitHitEvents = Bool(dictionary, "emitHitEvents", true);
            definition.State = MethodHookStateText.Parse(Text(dictionary, "state"));
            definition.FaultCode = Text(dictionary, "faultCode");
            definition.FaultMessage = Text(dictionary, "faultMessage");
            definition.FaultRevision = Long(dictionary, "faultRevision", 0);
            definition.FaultInvocationId = Long(dictionary, "faultInvocationId", 0);
            definition.FaultOwner = Text(dictionary, "faultOwner");
            definition.InstallSequence = Long(dictionary, "installSequence", 0);
            definition.HitCount = Long(dictionary, "hitCount", 0);
            definition.BypassCount = Long(dictionary, "bypassCount", 0);
            if (dictionary.TryGetValue("stateValues", out value))
            {
                var stateValues = value as IDictionary<string, object>;
                if (stateValues != null) foreach (var pair in stateValues) definition.RestoredStateValues[pair.Key] = pair.Value;
            }
            return definition;
        }

        private static string Text(IDictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty; }
        private static long Long(IDictionary<string, object> data, string key, long fallback) { long parsed; return long.TryParse(Text(data, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback; }
        private static bool Bool(IDictionary<string, object> data, string key, bool fallback) { bool parsed; return bool.TryParse(Text(data, key), out parsed) ? parsed : fallback; }

        private static List<string> StringList(IDictionary<string, object> data, string key)
        {
            object value;
            var result = new List<string>();
            if (!data.TryGetValue(key, out value)) return result;
            var items = value as IEnumerable<object>;
            if (items == null) return result;
            foreach (var item in items) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty);
            return result;
        }
    }

    /// <summary>
    /// 一个 exact target 在当前域的 Attachment。
    /// 该 target 下的多个逻辑定义共用同一套 Adapter 与 dispatcher；patch 只在最后一个定义离开时撤销。
    /// </summary>
    internal sealed class MethodHookTargetRuntime
    {
        internal string TargetKey;
        internal MethodHookTargetSpec Spec;
        internal MethodBase Method;
        internal string MethodIdentity;
        internal string AssemblyIdentity;
        internal bool Attached;
        internal bool GateOpen = true;
        internal readonly List<MethodHookDefinition> Definitions = new List<MethodHookDefinition>();

        /// <summary>确定顺序：priority 降序，再按持久安装顺序升序。</summary>
        internal List<MethodHookDefinition> Ordered()
        {
            return Definitions
                .OrderByDescending(definition => definition.Priority)
                .ThenBy(definition => definition.InstallSequence)
                .ToList();
        }
    }

    /// <summary>
    /// 一次 Invocation 的宿主实现。回调只通过 IUnityCliHookInvocation 读写本次调用；
    /// 参数与结果先暂存，回调正常返回后才提交，抛异常时不提交该回调尚未提交的变更。
    /// </summary>
    internal sealed class MethodHookInvocation : IUnityCliHookInvocation
    {
        private readonly MethodHookDefinition definition;
        private readonly object instance;
        private readonly object[] arguments;
        private readonly MethodHookInterceptionStamp stamp;
        private readonly bool isPrefix;
        private object[] stagedArguments;
        private object result;
        private bool hasStagedResult;
        private bool skipRequested;
        private readonly List<MethodHookEventRecord> emitted = new List<MethodHookEventRecord>();
        private readonly List<Dictionary<string, object>> fields = new List<Dictionary<string, object>>();
        private bool truncated;

        internal MethodHookInvocation(MethodHookDefinition definition, object instance, object[] arguments, object result, MethodHookInterceptionStamp stamp, bool isPrefix)
        {
            this.definition = definition;
            this.instance = instance;
            this.arguments = arguments ?? Array.Empty<object>();
            this.result = result;
            this.stamp = stamp ?? new MethodHookInterceptionStamp();
            this.isPrefix = isPrefix;
        }

        internal long InvocationId { get; set; }
        internal bool HasStagedArguments { get { return stagedArguments != null; } }
        internal object[] StagedArguments { get { return stagedArguments; } }
        internal bool HasStagedResult { get { return hasStagedResult; } }
        internal object StagedResult { get { return result; } }
        internal bool SkipRequested { get { return skipRequested; } }
        internal bool Truncated { get { return truncated; } }
        internal IReadOnlyList<MethodHookEventRecord> Emitted { get { return emitted; } }
        internal IReadOnlyList<Dictionary<string, object>> CapturedFields { get { return fields; } }

        /// <summary>记录捕获字段值；访问失败时给出具体字段错误。</summary>
        internal void AddCapturedField(FieldInfo field, object value)
        {
            fields.Add(new Dictionary<string, object>
            {
                { "field", (field.DeclaringType == null ? string.Empty : field.DeclaringType.FullName + ".") + field.Name },
                { "value", value }
            });
        }

        internal void AddEmitted(MethodHookEventRecord record) { emitted.Add(record); }

        public object Instance { get { return instance; } }
        public object[] Arguments { get { return stagedArguments ?? arguments; } }
        public object Result { get { return result; } set { result = value; hasStagedResult = true; } }
        public string HookId { get { return definition.HookId; } }
        public string MethodIdentity { get { return MethodHookMethodText.Describe(definition.Resolved); } }
        public string PlaySessionId { get { return stamp.PlaySessionId; } }
        public string ClockSegment { get { return stamp.ClockSegment; } }
        public double ElapsedMilliseconds { get { return stamp.ElapsedMilliseconds; } }
        public int Frame { get { return stamp.Frame; } }
        public bool IsPrefix { get { return isPrefix; } }
        public IDictionary<string, object> State { get { return definition.StateBag; } }

        public void SetArgument(int index, object value)
        {
            if (!isPrefix)
                throw new InvalidOperationException("SetArgument is only available in a prefix callback; the original call has already received its arguments.");
            if (index < 0 || index >= arguments.Length)
                throw new ArgumentOutOfRangeException("index", "Argument index " + index + " is outside the target signature.");
            var parameter = definition.Resolved.GetParameters()[index];
            if (value != null && !parameter.ParameterType.IsInstanceOfType(value))
            {
                var expected = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
                if (value != null && !expected.IsInstanceOfType(value))
                    throw new InvalidCastException("Argument " + index + " is " + value.GetType().FullName + " and cannot be assigned to " + expected.FullName + ".");
            }
            if (stagedArguments == null) stagedArguments = (object[])arguments.Clone();
            stagedArguments[index] = value;
        }

        public void SkipOriginal() { skipRequested = true; }

        public object ReadField(string declaringType, string fieldName)
        {
            var field = MethodHookTargetResolver.ResolveField(instance == null ? definition.Resolved.DeclaringType : instance.GetType(), declaringType, fieldName);
            try
            {
                return field.GetValue(field.IsStatic ? null : instance);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Field '" + fieldName + "' could not be read: " + exception.GetType().Name + ".", exception);
            }
        }

        public void WriteField(string declaringType, string fieldName, object value)
        {
            var field = MethodHookTargetResolver.ResolveField(instance == null ? definition.Resolved.DeclaringType : instance.GetType(), declaringType, fieldName);
            try
            {
                if (value != null && !field.FieldType.IsInstanceOfType(value))
                    throw new InvalidCastException("Field '" + fieldName + "' is " + field.FieldType.FullName + " and cannot accept " + value.GetType().FullName + ".");
                field.SetValue(field.IsStatic ? null : instance, value);
            }
            catch (BridgeCommandException) { throw; }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Field '" + fieldName + "' could not be written: " + exception.GetType().Name + ".", exception);
            }
        }

        public void Emit(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Emit requires a non-empty event name.");
            var truncation = new MethodHookProjection.Truncation();
            var projected = MethodHookProjection.Project(value, truncation);
            var record = new MethodHookEventRecord
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                InvocationId = InvocationId,
                Kind = "custom",
                PlaySessionId = stamp.PlaySessionId,
                ClockSegment = stamp.ClockSegment,
                ElapsedMilliseconds = stamp.ElapsedMilliseconds,
                Frame = stamp.Frame,
                MethodIdentity = MethodHookMethodText.Describe(definition.Resolved),
                Instance = InstanceIdentity(),
                Name = UnityCliBridge.BoundedText(name, 128),
                Value = projected,
                Truncated = truncation.Truncated
            };
            var size = record.Value == null ? 0 : MethodHookProjection.EstimateBytes(record.Value);
            if (size > MethodHookLimits.MaxEmitBytes)
            {
                record.Value = new Dictionary<string, object> { { "truncated", true }, { "bytes", size } };
                record.Truncated = true;
            }
            if (record.Truncated) truncated = true;
            emitted.Add(record);
        }

        internal string InstanceIdentity()
        {
            if (instance == null) return "static";
            var truncation = new MethodHookProjection.Truncation();
            var projected = MethodHookProjection.Project(instance, truncation);
            var dictionary = projected as IDictionary<string, object>;
            if (dictionary != null && dictionary.ContainsKey("name"))
            {
                var name = Convert.ToString(dictionary["name"], CultureInfo.InvariantCulture);
                return (instance.GetType().FullName ?? instance.GetType().Name) + ":" + name;
            }
            return instance.GetType().FullName ?? instance.GetType().Name;
        }
    }
}
#endif
