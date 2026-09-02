using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace UnityCli
{
    /// <summary>将嵌套异常归一化为有界、可循环终止的公开 cause chain。</summary>
    internal static class UnityCliExceptionProjection
    {
        internal const int MaxCauseDepth = 8;
        internal const int MaxCauseTextBytes = 64 * 1024;
        private const int MaxTypeBytes = 512;
        private const int MaxMessageBytes = 4096;
        private const int MaxStackTraceBytes = 16384;

        internal sealed class Result
        {
            internal string PublicMessage;
            internal string DeepestExceptionType;
            internal string DeepestMessage;
            internal string DeepestStackTrace;
            internal List<Dictionary<string, object>> Causes;
            internal bool Truncated;
            internal int CauseTextBytes;
        }

        internal static Result Project(Exception exception)
        {
            return Project(exception, value => value == null ? null : value.InnerException);
        }

        /// <summary>selector 仅供隔离测试构造循环；生产路径始终读取 Exception.InnerException。</summary>
        internal static Result Project(Exception exception, Func<Exception, Exception> selector)
        {
            if (exception == null) throw new ArgumentNullException("exception");
            if (selector == null) throw new ArgumentNullException("selector");

            var chain = new List<Exception>();
            var visited = new HashSet<Exception>(ReferenceComparer.Instance);
            var truncated = false;
            var current = exception;
            while (current != null && chain.Count < MaxCauseDepth)
            {
                if (!visited.Add(current))
                {
                    truncated = true;
                    break;
                }
                chain.Add(current);
                current = selector(current);
            }
            if (current != null) truncated = true;

            var remaining = MaxCauseTextBytes;
            var types = new string[chain.Count];
            var messages = new string[chain.Count];
            var stacks = new string[chain.Count];

            // 先从最深 cause 分配 type/message，确保最可操作的失败原因不会被外层 stack 挤掉。
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                messages[index] = TakeUtf8(chain[index].Message, Math.Min(MaxMessageBytes, remaining), ref remaining, ref truncated);
                types[index] = TakeUtf8(chain[index].GetType().FullName, Math.Min(MaxTypeBytes, remaining), ref remaining, ref truncated);
            }
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                stacks[index] = TakeUtf8(chain[index].StackTrace, Math.Min(MaxStackTraceBytes, remaining), ref remaining, ref truncated);
            }

            var causes = new List<Dictionary<string, object>>(chain.Count);
            for (var index = 0; index < chain.Count; index++)
            {
                causes.Add(new Dictionary<string, object>
                {
                    { "depth", (long)index },
                    { "exceptionType", types[index] },
                    { "message", messages[index] },
                    { "stackTrace", stacks[index] }
                });
            }

            var deepestIndex = chain.Count - 1;
            var deepestMessage = messages[deepestIndex];
            if (string.IsNullOrWhiteSpace(deepestMessage)) deepestMessage = chain[deepestIndex].GetType().Name;
            return new Result
            {
                PublicMessage = deepestMessage,
                DeepestExceptionType = types[deepestIndex],
                DeepestMessage = messages[deepestIndex],
                DeepestStackTrace = stacks[deepestIndex],
                Causes = causes,
                Truncated = truncated,
                CauseTextBytes = MaxCauseTextBytes - remaining
            };
        }

        private static string TakeUtf8(string value, int allowedBytes, ref int remaining, ref bool truncated)
        {
            value = value ?? string.Empty;
            if (allowedBytes <= 0)
            {
                if (value.Length > 0) truncated = true;
                return string.Empty;
            }
            var actualBytes = Encoding.UTF8.GetByteCount(value);
            if (actualBytes <= allowedBytes)
            {
                remaining -= actualBytes;
                return value;
            }

            truncated = true;
            var length = Math.Min(value.Length, allowedBytes);
            while (length > 0 && Encoding.UTF8.GetByteCount(value.Substring(0, length)) > allowedBytes) length--;
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            var bounded = value.Substring(0, length);
            remaining -= Encoding.UTF8.GetByteCount(bounded);
            return bounded;
        }

        private sealed class ReferenceComparer : IEqualityComparer<Exception>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(Exception left, Exception right) { return ReferenceEquals(left, right); }
            public int GetHashCode(Exception value) { return RuntimeHelpers.GetHashCode(value); }
        }
    }
}
