#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityCli.Protocol
{
    internal static class ProtocolEnvelopeFactory
    {
        public static SuccessEnvelope Success(string requestId, object data)
        {
            return new SuccessEnvelope
            {
                Ok = true,
                RequestId = requestId ?? string.Empty,
                Data = data
            };
        }

        public static ErrorEnvelope Error(
            string requestId,
            string code,
            string message,
            object details = null,
            IEnumerable<string> nextActions = null,
            bool transient = false,
            double? retryAfterMs = null)
        {
            return new ErrorEnvelope
            {
                Ok = false,
                RequestId = requestId ?? string.Empty,
                Error = new ToolError
                {
                    Code = code,
                    Message = message,
                    Details = SanitizeErrorDetails(details),
                    NextActions = (nextActions ?? Array.Empty<string>()).Where(action => !string.IsNullOrWhiteSpace(action)).ToList(),
                    Transient = transient,
                    RetryAfterMs = transient ? (retryAfterMs ?? 5000d) : 0d
                }
            };
        }

        private static readonly HashSet<string> ForbiddenErrorDetailKeys = new HashSet<string>(new[]
        {
            "actualport", "audit", "bridgeendpoint", "bridgeendpointurl", "bridgelogpath", "bridgeurl",
            "commandline", "endpoint", "endpointurl", "hwnd", "listenergeneration", "listenerid", "logpath",
            "logs", "ownerhwnd", "port", "preferredport", "processid", "processstartedat",
            "sourcelistenergeneration", "windowscommandline"
        }, StringComparer.Ordinal);

        /// <summary>错误 details 只保留业务诊断；无法标准化的对象按 fail-closed 语义省略。</summary>
        private static object SanitizeErrorDetails(object details)
        {
            if (details == null) return null;
            try
            {
                return SanitizeErrorDetailNode(UnityCliSerializer.Parse(UnityCliSerializer.Serialize(details)));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>递归删除 transport、listener、进程、窗口和内部日志字段。</summary>
        private static object SanitizeErrorDetailNode(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                var sanitized = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var entry in dictionary)
                {
                    var normalized = new string((entry.Key ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                    if (!ForbiddenErrorDetailKeys.Contains(normalized)) sanitized[entry.Key] = SanitizeErrorDetailNode(entry.Value);
                }
                return sanitized;
            }
            var list = value as System.Collections.IList;
            if (list != null)
            {
                var sanitized = new List<object>(list.Count);
                foreach (var item in list) sanitized.Add(SanitizeErrorDetailNode(item));
                return sanitized;
            }
            return value;
        }
    }
}
#endif
