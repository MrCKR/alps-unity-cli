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
            if (request.ProtocolVersion != 2d) throw new FormatException("Bridge request requires protocolVersion=2.");
            if (string.IsNullOrWhiteSpace(request.InstanceId)) throw new FormatException("Bridge request requires a non-empty instanceId.");
            if (string.IsNullOrWhiteSpace(request.RequestId)) throw new FormatException("Bridge request requires a non-empty requestId.");
            if (!(request.Input is IDictionary<string, object>)) throw new FormatException("Bridge request requires input as an explicit JSON object.");
            if (request.Execution == null) throw new FormatException("Bridge request requires execution.");
            if (request.Execution.CompletionContractVersion != 1d) throw new FormatException("Bridge request requires completionContractVersion=1.");
            if (request.Execution.RequestDigestVersion != 1d) throw new FormatException("Bridge request requires requestDigestVersion=1.");
            DateTimeOffset deadline;
            if (string.IsNullOrWhiteSpace(request.Execution.AbsoluteDeadlineUtc)
                || !DateTimeOffset.TryParse(request.Execution.AbsoluteDeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline))
                throw new FormatException("Bridge request requires execution.absoluteDeadlineUtc as an ISO-8601 timestamp.");
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

        /// <summary>
        /// 从 immutable absolute deadline 计算 handler 剩余预算；排队、transition 与 listener recovery 已消耗的时间不会重置。
        /// maximum 只限制单个 Tool 的执行上限，不改变请求的绝对截止时间。
        /// </summary>
        public int ExecutionTimeoutMs(int fallback, int maximum)
        {
            int remainingMs;
            if (!TryDeadlineRemainingMs(out remainingMs))
            {
                throw new UnityCli.UnityCliBridge.BridgeCommandException(
                    "E_EXECUTION_TIMEOUT",
                    "The immutable request deadline elapsed before Unity execution started.",
                    new Dictionary<string, object> { { "absoluteDeadlineUtc", Execution == null ? null : Execution.AbsoluteDeadlineUtc } },
                    new[] { "Inspect editor.state and retry only after Unity is responsive." });
            }
            return Math.Max(1, Math.Min(maximum > 0 ? maximum : fallback, remainingMs));
        }

        /// <summary>返回当前 absolute deadline 的剩余毫秒；过期或无效时返回 false。</summary>
        public bool TryDeadlineRemainingMs(out int remainingMs)
        {
            remainingMs = 0;
            DateTimeOffset deadline;
            if (Execution == null
                || string.IsNullOrWhiteSpace(Execution.AbsoluteDeadlineUtc)
                || !DateTimeOffset.TryParse(Execution.AbsoluteDeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline))
                return false;
            var remaining = (deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
            if (remaining <= 0d) return false;
            remainingMs = (int)Math.Min(int.MaxValue, Math.Max(1d, Math.Ceiling(remaining)));
            return true;
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
