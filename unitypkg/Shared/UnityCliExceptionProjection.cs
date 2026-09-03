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
        internal const int MaxCauseTraversal = 256;
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
            internal int CauseTraversalCount;
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

            var retained = new List<CauseReference>(MaxCauseDepth);
            var visited = new HashSet<Exception>(ReferenceComparer.Instance);
            var truncated = false;
            var current = exception;
            Exception deepest = null;
            var deepestDepth = -1;
            var traversed = 0;
            while (current != null && traversed < MaxCauseTraversal)
            {
                if (!visited.Add(current))
                {
                    truncated = true;
                    break;
                }
                if (retained.Count < MaxCauseDepth - 1) retained.Add(new CauseReference(current, traversed));
                deepest = current;
                deepestDepth = traversed;
                traversed++;
                current = selector(current);
            }
            if (current != null || traversed > MaxCauseDepth) truncated = true;
            if (deepestDepth >= MaxCauseDepth - 1) retained.Add(new CauseReference(deepest, deepestDepth));

            var remaining = MaxCauseTextBytes;
            var types = new string[retained.Count];
            var messages = new string[retained.Count];
            var stacks = new string[retained.Count];

            // 只投影前七层与真实终端槽，并优先为终端 cause 分配 type/message bytes。
            for (var index = retained.Count - 1; index >= 0; index--)
            {
                messages[index] = TakeUtf8(retained[index].Exception.Message, Math.Min(MaxMessageBytes, remaining), ref remaining, ref truncated);
                types[index] = TakeUtf8(retained[index].Exception.GetType().FullName, Math.Min(MaxTypeBytes, remaining), ref remaining, ref truncated);
            }
            for (var index = retained.Count - 1; index >= 0; index--)
            {
                stacks[index] = TakeUtf8(retained[index].Exception.StackTrace, Math.Min(MaxStackTraceBytes, remaining), ref remaining, ref truncated);
            }

            var causes = new List<Dictionary<string, object>>(retained.Count);
            for (var index = 0; index < retained.Count; index++)
            {
                causes.Add(new Dictionary<string, object>
                {
                    { "depth", (long)retained[index].Depth },
                    { "exceptionType", types[index] },
                    { "message", messages[index] },
                    { "stackTrace", stacks[index] }
                });
            }

            var deepestIndex = retained.Count - 1;
            var deepestMessage = messages[deepestIndex];
            if (string.IsNullOrWhiteSpace(deepestMessage)) deepestMessage = string.IsNullOrWhiteSpace(types[deepestIndex]) ? "Exception" : types[deepestIndex];
            return new Result
            {
                PublicMessage = deepestMessage,
                DeepestExceptionType = types[deepestIndex],
                DeepestMessage = messages[deepestIndex],
                DeepestStackTrace = stacks[deepestIndex],
                Causes = causes,
                Truncated = truncated,
                CauseTextBytes = MaxCauseTextBytes - remaining,
                CauseTraversalCount = traversed
            };
        }

        private sealed class CauseReference
        {
            internal CauseReference(Exception exception, int depth) { Exception = exception; Depth = depth; }
            internal readonly Exception Exception;
            internal readonly int Depth;
        }

        private static string TakeUtf8(string value, int allowedBytes, ref int remaining, ref bool truncated)
        {
            value = value ?? string.Empty;
            if (allowedBytes <= 0)
            {
                if (value.Length > 0) truncated = true;
                return string.Empty;
            }

            var low = 0;
            var high = Math.Min(value.Length, allowedBytes);
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (Encoding.UTF8.GetByteCount(value, 0, middle) <= allowedBytes) low = middle;
                else high = middle - 1;
            }
            if (low > 0 && low < value.Length && char.IsHighSurrogate(value[low - 1]) && char.IsLowSurrogate(value[low])) low--;
            if (low < value.Length) truncated = true;
            if (low == value.Length)
            {
                remaining -= Encoding.UTF8.GetByteCount(value, 0, low);
                return value;
            }
            var bounded = value.Substring(0, low);
            remaining -= Encoding.UTF8.GetByteCount(bounded, 0, bounded.Length);
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
