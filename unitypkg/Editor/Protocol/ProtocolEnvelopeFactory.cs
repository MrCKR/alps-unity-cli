#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityCli.Protocol
{
    internal static class ProtocolEnvelopeFactory
    {
        public static SuccessEnvelope Success(string requestId, object data, double? durationMs = null)
        {
            return new SuccessEnvelope
            {
                Ok = true,
                RequestId = requestId ?? string.Empty,
                Data = data,
                DurationMs = durationMs
            };
        }

        public static ErrorEnvelope Error(
            string requestId,
            string code,
            string message,
            object details = null,
            IEnumerable<string> nextActions = null,
            bool transient = false,
            double? retryAfterMs = null,
            double? durationMs = null)
        {
            return new ErrorEnvelope
            {
                Ok = false,
                RequestId = requestId ?? string.Empty,
                DurationMs = durationMs,
                Error = new ToolError
                {
                    Code = code,
                    Message = message,
                    Details = details,
                    NextActions = (nextActions ?? Array.Empty<string>()).Where(action => !string.IsNullOrWhiteSpace(action)).ToList(),
                    Transient = transient,
                    RetryAfterMs = transient ? (retryAfterMs ?? 5000d) : 0d
                }
            };
        }
    }
}
#endif
