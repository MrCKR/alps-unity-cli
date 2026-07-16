#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// 供 core 与独立 Extension assembly 统一返回结构化 Bridge 错误。
        /// </summary>
        public sealed class BridgeCommandException : Exception
        {
            public BridgeCommandException(string code, string message, string nextAction = null, bool transient = false, int retryAfterMs = 0)
                : this(code, message, null, string.IsNullOrEmpty(nextAction) ? Array.Empty<string>() : new[] { nextAction }, transient, retryAfterMs)
            {
            }

            public BridgeCommandException(string code, string message, object details, IEnumerable<string> nextActions, bool transient = false, int retryAfterMs = 0)
                : base(message)
            {
                if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Bridge error code is required.", "code");
                Code = code;
                Details = details;
                NextActions = (nextActions ?? Array.Empty<string>()).Where(action => !string.IsNullOrWhiteSpace(action)).ToArray();
                Transient = transient;
                RetryAfterMs = retryAfterMs;
            }

            public string Code { get; private set; }
            public object Details { get; private set; }
            public IReadOnlyList<string> NextActions { get; private set; }
            public bool Transient { get; private set; }
            public int RetryAfterMs { get; private set; }
        }
    }
}
#endif
