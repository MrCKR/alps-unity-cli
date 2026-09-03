using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UnityCli
{
    /// <summary>为数值协议字段声明公开最小值与最大值，并由 schema 与执行前校验共同强制。</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class UnityCliRangeAttribute : Attribute
    {
        public UnityCliRangeAttribute(long minimum, long maximum) { Minimum = minimum; Maximum = maximum; }
        public long Minimum { get; private set; }
        public long Maximum { get; private set; }
    }

    /// <summary>公开读取预算输入；调用方可以收紧预算，但不能超过产品冻结上限。</summary>
    public sealed class UnityCliReadBudgetInput
    {
        [UnityCliRange(1L, UnityCliRequestBudget.AbsoluteMaxScannedRecords)] public long? MaxScannedRecords { get; set; }
        [UnityCliRange(1L, UnityCliRequestBudget.AbsoluteMaxNodes)] public long? MaxNodes { get; set; }
        [UnityCliRange(1L, UnityCliRequestBudget.AbsoluteMaxGetters)] public long? MaxGetters { get; set; }
        [UnityCliRange(1L, UnityCliRequestBudget.AbsoluteMaxReflectionCalls)] public long? MaxReflectionCalls { get; set; }
        [UnityCliRange(1L, UnityCliRequestBudget.AbsoluteMaxMainThreadMilliseconds)] public long? MaxMainThreadMilliseconds { get; set; }
        [UnityCliRange(1L, UnityCliRequestBudget.AbsoluteMaxSerializedUtf8Bytes)] public long? MaxSerializedUtf8Bytes { get; set; }
    }

    /// <summary>请求预算实际消耗；只包含稳定计数，不包含内部时钟或取消状态。</summary>
    public sealed class UnityCliRequestBudgetUsage
    {
        public long ScannedRecords { get; set; }
        public long Nodes { get; set; }
        public long Getters { get; set; }
        public long ReflectionCalls { get; set; }
        public long MainThreadMilliseconds { get; set; }
        public long SerializedUtf8Bytes { get; set; }
        public string ExhaustedDimension { get; set; }
    }

    /// <summary>可注入的单调时钟，生产使用 Stopwatch，测试使用手工时钟。</summary>
    public interface IUnityCliMonotonicClock
    {
        long ElapsedMilliseconds { get; }
    }

    public sealed class UnityCliStopwatchClock : IUnityCliMonotonicClock
    {
        private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        public long ElapsedMilliseconds { get { return stopwatch.ElapsedMilliseconds; } }
    }

    /// <summary>遍历预算耗尽；上层将其归约为稳定 E_REQUEST_BUDGET_EXCEEDED。</summary>
    public sealed class UnityCliRequestBudgetExceededException : Exception
    {
        public UnityCliRequestBudgetExceededException(string dimension)
            : base("Request-wide Unity main-thread budget exhausted: " + dimension + ".")
        {
            Dimension = dimension;
        }

        public string Dimension { get; private set; }
    }

    /// <summary>
    /// 一次 Tool 请求共用的预算账本。每次原始读取前先消费对应维度；elapsed 使用单调时钟，
    /// cancellation 与所有维度共享同一终止点，禁止子路径或递归重新创建预算。
    /// </summary>
    public sealed class UnityCliRequestBudget
    {
        public const long DefaultMaxScannedRecords = 10000L;
        public const long AbsoluteMaxScannedRecords = 100000L;
        public const long DefaultMaxNodes = 4096L;
        public const long AbsoluteMaxNodes = 20000L;
        public const long DefaultMaxGetters = 2048L;
        public const long AbsoluteMaxGetters = 10000L;
        public const long DefaultMaxReflectionCalls = 4096L;
        public const long AbsoluteMaxReflectionCalls = 20000L;
        public const long DefaultMaxMainThreadMilliseconds = 250L;
        public const long AbsoluteMaxMainThreadMilliseconds = 2000L;
        public const long DefaultMaxSerializedUtf8Bytes = 1024L * 1024L;
        public const long AbsoluteMaxSerializedUtf8Bytes = 8L * 1024L * 1024L;

        private readonly IUnityCliMonotonicClock clock;
        private readonly CancellationToken cancellationToken;
        private readonly long maxScannedRecords;
        private readonly long maxNodes;
        private readonly long maxGetters;
        private readonly long maxReflectionCalls;
        private readonly long maxMainThreadMilliseconds;
        private readonly long maxSerializedUtf8Bytes;
        private long scannedRecords;
        private long nodes;
        private long getters;
        private long reflectionCalls;
        private long serializedUtf8Bytes;
        private long mainThreadMilliseconds;
        private long lastClockMilliseconds;
        private string exhaustedDimension;

        public UnityCliRequestBudget(UnityCliReadBudgetInput input, long executionTimeoutMilliseconds, CancellationToken cancellationToken, IUnityCliMonotonicClock clock = null)
        {
            input = input ?? new UnityCliReadBudgetInput();
            maxScannedRecords = Limit(input.MaxScannedRecords, DefaultMaxScannedRecords, AbsoluteMaxScannedRecords, "maxScannedRecords");
            maxNodes = Limit(input.MaxNodes, DefaultMaxNodes, AbsoluteMaxNodes, "maxNodes");
            maxGetters = Limit(input.MaxGetters, DefaultMaxGetters, AbsoluteMaxGetters, "maxGetters");
            maxReflectionCalls = Limit(input.MaxReflectionCalls, DefaultMaxReflectionCalls, AbsoluteMaxReflectionCalls, "maxReflectionCalls");
            var requestedElapsed = Limit(input.MaxMainThreadMilliseconds, DefaultMaxMainThreadMilliseconds, AbsoluteMaxMainThreadMilliseconds, "maxMainThreadMilliseconds");
            maxMainThreadMilliseconds = executionTimeoutMilliseconds > 0L ? Math.Min(requestedElapsed, executionTimeoutMilliseconds) : requestedElapsed;
            maxSerializedUtf8Bytes = Limit(input.MaxSerializedUtf8Bytes, DefaultMaxSerializedUtf8Bytes, AbsoluteMaxSerializedUtf8Bytes, "maxSerializedUtf8Bytes");
            this.cancellationToken = cancellationToken;
            this.clock = clock ?? new UnityCliStopwatchClock();
            lastClockMilliseconds = this.clock.ElapsedMilliseconds;
        }

        public long MaxSerializedUtf8Bytes { get { return maxSerializedUtf8Bytes; } }
        public bool IsExhausted { get { return exhaustedDimension != null; } }
        public string ExhaustedDimension { get { return exhaustedDimension; } }

        public bool TryConsumeScannedRecord() { return TryConsume(ref scannedRecords, maxScannedRecords, "scannedRecords"); }
        public bool TryConsumeNode() { return TryConsume(ref nodes, maxNodes, "nodes"); }
        public bool TryConsumeGetter() { return TryConsume(ref getters, maxGetters, "getters"); }
        public bool TryConsumeReflectionCall() { return TryConsume(ref reflectionCalls, maxReflectionCalls, "reflectionCalls"); }

        /// <summary>在原始读取前检查取消与单调 elapsed；false 表示调用方必须返回 continuation。</summary>
        public bool TryCheckpoint()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.ElapsedMilliseconds;
            if (now > lastClockMilliseconds) mainThreadMilliseconds += now - lastClockMilliseconds;
            lastClockMilliseconds = now;
            if (mainThreadMilliseconds < maxMainThreadMilliseconds) return exhaustedDimension == null;
            Exhaust("mainThreadMilliseconds");
            return false;
        }

        /// <summary>异步框架 wait 返回后重置采样基线；等待时间不属于 Unity 主线程工作。</summary>
        public void ResumeAfterWait()
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastClockMilliseconds = clock.ElapsedMilliseconds;
        }

        public void CheckpointOrThrow()
        {
            if (!TryCheckpoint()) throw new UnityCliRequestBudgetExceededException(exhaustedDimension);
        }

        /// <summary>构造动态输出时预留确定的 UTF-8 文本字节；最终 JSON 仍由框架做精确检查。</summary>
        public bool TryReserveSerializedUtf8(string value)
        {
            return TryReserveSerializedUtf8Bytes(value == null ? 4L : Encoding.UTF8.GetByteCount(value));
        }

        /// <summary>为 JSON 结构或已知编码结果预留确定字节，避免先分配完整字符串。</summary>
        public bool TryReserveSerializedUtf8Bytes(long bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCheckpoint()) return false;
            if (bytes < 0L || bytes > maxSerializedUtf8Bytes - serializedUtf8Bytes)
            {
                Exhaust("serializedUtf8Bytes");
                return false;
            }
            serializedUtf8Bytes += bytes;
            return true;
        }

        /// <summary>Tool 返回后按最终 JSON 做精确 UTF-8 ceiling 裁决。</summary>
        public void ObserveFinalSerializedUtf8Bytes(long bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes < 0L || bytes > maxSerializedUtf8Bytes)
            {
                Exhaust("serializedUtf8Bytes");
                throw new UnityCliRequestBudgetExceededException("serializedUtf8Bytes");
            }
            serializedUtf8Bytes = bytes;
        }

        public UnityCliRequestBudgetUsage Usage()
        {
            TryCheckpoint();
            return new UnityCliRequestBudgetUsage
            {
                ScannedRecords = scannedRecords,
                Nodes = nodes,
                Getters = getters,
                ReflectionCalls = reflectionCalls,
                MainThreadMilliseconds = Math.Max(0L, mainThreadMilliseconds),
                SerializedUtf8Bytes = serializedUtf8Bytes,
                ExhaustedDimension = exhaustedDimension
            };
        }

        private bool TryConsume(ref long current, long maximum, string dimension)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCheckpoint()) return false;
            if (current >= maximum)
            {
                Exhaust(dimension);
                return false;
            }
            current += 1L;
            return true;
        }

        private void Exhaust(string dimension)
        {
            if (exhaustedDimension == null) exhaustedDimension = dimension;
        }

        private static long Limit(long? requested, long fallback, long maximum, string field)
        {
            var value = requested ?? fallback;
            if (value < 1L || value > maximum)
                throw new ArgumentOutOfRangeException(field, value, field + " must be between 1 and " + maximum.ToString(CultureInfo.InvariantCulture) + ".");
            return value;
        }
    }

    /// <summary>输入按 newest-first 排序，返回同时满足 count/bytes 的稳定保留索引。</summary>
    public static class UnityCliRetentionPlanner
    {
        public static HashSet<int> RetainedIndices(IReadOnlyList<long> sizes, int maximumCount, long maximumBytes)
        {
            if (sizes == null) throw new ArgumentNullException("sizes");
            if (maximumCount < 1) throw new ArgumentOutOfRangeException("maximumCount");
            if (maximumBytes < 1L) throw new ArgumentOutOfRangeException("maximumBytes");
            var result = new HashSet<int>();
            long bytes = 0L;
            for (var index = 0; index < sizes.Count && result.Count < maximumCount; index++)
            {
                var size = sizes[index];
                if (size < 0L) throw new ArgumentOutOfRangeException("sizes");
                if (size > maximumBytes - bytes) break;
                bytes += size;
                result.Add(index);
            }
            return result;
        }
    }

    /// <summary>稳定 continuation 的无状态编码；generation/scope 不匹配时调用方必须拒绝。</summary>
    public static class UnityCliContinuationToken
    {
        public static string Create(string scope, string generation, string position)
        {
            return "ucli1." + Encode(scope) + "." + Encode(generation) + "." + Encode(position ?? string.Empty);
        }

        public static bool TryParse(string token, string expectedScope, string expectedGeneration, out string position)
        {
            position = null;
            if (string.IsNullOrEmpty(token) || token.Length > 4096) return false;
            var parts = token.Split('.');
            if (parts.Length != 4 || parts[0] != "ucli1") return false;
            string scope;
            string generation;
            try
            {
                scope = Decode(parts[1]);
                generation = Decode(parts[2]);
                position = Decode(parts[3]);
            }
            catch
            {
                position = null;
                return false;
            }
            return string.Equals(scope, expectedScope, StringComparison.Ordinal)
                && string.Equals(generation, expectedGeneration, StringComparison.Ordinal);
        }

        public static string Fingerprint(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Decode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 1: throw new FormatException("Invalid base64url continuation component.");
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
    }
}
