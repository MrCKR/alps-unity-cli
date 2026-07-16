#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli.Protocol
{
    public sealed partial class BridgeRequest
    {
        public static BridgeRequest DeserializeStrict(string json)
        {
            var request = UnityCli.UnityCliSerializer.Deserialize<BridgeRequest>(json);
            if (request == null || string.IsNullOrWhiteSpace(request.Command)) throw new FormatException("Bridge request requires a non-empty command.");
            if (string.IsNullOrWhiteSpace(request.RequestId)) throw new FormatException("Bridge request requires a non-empty requestId.");
            if (request.Input == null) request.Input = new Dictionary<string, object>(StringComparer.Ordinal);
            return request;
        }

        public T DeserializeArgs<T>()
        {
            return UnityCli.UnityCliSerializer.Deserialize<T>(UnityCli.UnityCliSerializer.Serialize(Input ?? new Dictionary<string, object>()));
        }

        public string RawJsonArg(string name)
        {
            var value = GetInputField(name);
            return value == null ? null : UnityCli.UnityCliSerializer.Serialize(value);
        }

        public string StringArg(string name)
        {
            var value = GetInputField(name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public int IntArg(string name, int fallback)
        {
            var value = OptionalIntArg(name);
            return value.HasValue ? value.Value : fallback;
        }

        public int? OptionalIntArg(string name)
        {
            var value = GetInputField(name);
            if (value == null) return null;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception exception) when (exception is FormatException || exception is InvalidCastException || exception is OverflowException) { return null; }
        }

        public bool BoolArg(string name, bool fallback)
        {
            var value = OptionalBoolArg(name);
            return value.HasValue ? value.Value : fallback;
        }

        // 读取可选布尔输入；类型不匹配时交由调用方使用默认值。
        public bool? OptionalBoolArg(string name)
        {
            var value = GetInputField(name);
            if (value == null) return null;
            if (value is bool boolean) return boolean;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch (Exception exception) when (exception is FormatException || exception is InvalidCastException) { return null; }
        }

        // 从传输层 execution 读取并校验 timeout，禁止业务 input 覆盖执行策略。
        public int ExecutionTimeoutMs(int fallback, int maximum)
        {
            var value = Execution == null ? null : Execution.TimeoutMs;
            if (!value.HasValue) return fallback;
            var timeoutMs = value.Value;
            if (double.IsNaN(timeoutMs) || double.IsInfinity(timeoutMs) || timeoutMs != Math.Truncate(timeoutMs) || timeoutMs <= 0 || timeoutMs > maximum || timeoutMs > int.MaxValue)
            {
                throw new UnityCli.UnityCliBridge.BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "execution.timeoutMs must be an integer between 1 and " + maximum.ToString(CultureInfo.InvariantCulture) + ".",
                    new Dictionary<string, object> { { "field", "execution.timeoutMs" }, { "maximum", maximum }, { "actual", timeoutMs } },
                    new[] { "Pass --timeout-ms with an integer between 1 and " + maximum.ToString(CultureInfo.InvariantCulture) + "." });
            }
            return (int)timeoutMs;
        }

        public bool SecurityConfirmed(string toolName, bool destructiveHint, bool supportsDryRun, string bridgeCommand, string liveDescriptorFingerprint = null)
        {
            var descriptor = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", toolName },
                { "destructiveHint", destructiveHint },
                { "supportsDryRun", supportsDryRun },
                { "bridgeCommand", bridgeCommand }
            };
            if (!string.IsNullOrEmpty(liveDescriptorFingerprint)) descriptor.Add("liveDescriptorFingerprint", liveDescriptorFingerprint);
            return Security != null
                && Security.Confirmed
                && string.Equals(Security.ToolName, toolName, StringComparison.Ordinal)
                && string.Equals(Security.DescriptorFingerprint, UnityCli.UnityCliSerializer.Sha256Canonical(descriptor), StringComparison.Ordinal)
                && string.Equals(Security.InputSha256, UnityCli.UnityCliSerializer.Sha256Canonical(Input), StringComparison.Ordinal);
        }

        private object GetInputField(string name)
        {
            var dictionary = Input as IDictionary<string, object>;
            object value;
            return dictionary != null && dictionary.TryGetValue(name, out value) ? value : null;
        }
    }
}
#endif
