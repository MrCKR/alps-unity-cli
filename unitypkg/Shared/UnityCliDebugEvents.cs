using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// 调试事件使用的时钟种类。事件必须记录它实际使用的时钟种类与该时钟的值；
    /// off-main 线程只允许使用单调时钟，并额外携带最后一次在主线程观察到的帧号。
    /// </summary>
    public static class UnityCliDebugClocks
    {
        /// <summary>进程单调耗时（毫秒）；任何线程都可用。</summary>
        public const string Monotonic = "monotonic";

        /// <summary>编辑器中央 tick 计数；只在主线程推进会更新。</summary>
        public const string EditorTick = "editor-tick";

        /// <summary>Unity 真实帧号；只能在主线程读取。</summary>
        public const string UnityFrame = "unity-frame";

        /// <summary>项目自定义逻辑时钟；值由项目自己的逻辑边界推进。</summary>
        public const string ProjectLogical = "project-logical";

        /// <summary>debug 域已声明的时钟种类全集；声明、校验与读取都使用这一份列表。</summary>
        public static string[] All()
        {
            return new[] { Monotonic, EditorTick, UnityFrame, ProjectLogical };
        }

        /// <summary>该取值是否为已声明的时钟种类。</summary>
        public static bool IsDefined(string value)
        {
            return string.Equals(value, Monotonic, StringComparison.Ordinal)
                || string.Equals(value, EditorTick, StringComparison.Ordinal)
                || string.Equals(value, UnityFrame, StringComparison.Ordinal)
                || string.Equals(value, ProjectLogical, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 调试事件来源标识。目前有 hook、lifecycle、trigger、log 与 pause；新增来源只追加常量，不需要改公开 schema。
    /// </summary>
    public static class UnityCliDebugSources
    {
        /// <summary>method hook 捕获产生的事件。</summary>
        public const string Hook = "hook";

        /// <summary>hook 定义生命周期事件。</summary>
        public const string Lifecycle = "lifecycle";

        /// <summary>监听式触发事实：触发、排队、开始时沿与两个线程身份。</summary>
        public const string Trigger = "trigger";

        /// <summary>线程化日志捕获产生的事件；记录真实 callback 线程，不声称它是业务边界。</summary>
        public const string Log = "log";

        /// <summary>断点命中与暂停事实：snapshot、paused、终结、pause-timeout、pause-skipped、pause-suppressed。</summary>
        public const string Pause = "pause";
    }

    /// <summary>不可读的 sequence 区间；只描述保留窗口的缺口，不声称调用未发生。</summary>
    public sealed class UnityCliDebugGap
    {
        public long FromSequence { get; set; }
        public long ToSequence { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// 入库的调试事件。字段是公开实例字段，供任意线程写入后立即入库；
    /// 值投影必须在入库前物化，存储不允许保留活的业务对象引用。
    /// </summary>
    public sealed class UnityCliDebugEvent
    {
        // ---- 统一身份 ----

        /// <summary>全局单调 sequence；同一个存储实例内永不复用。</summary>
        public long Sequence;

        /// <summary>产生该事件的来源标识，见 UnityCliDebugSources。</summary>
        public string SourceId = string.Empty;

        /// <summary>来源内部的业务种类（hook：prefix/postfix/custom/fault/lifecycle）。</summary>
        public string Kind = string.Empty;

        /// <summary>捕获该事件时的位置描述，读取端据此区分同一来源的不同捕获点。</summary>
        public string ExecutionPoint = string.Empty;

        /// <summary>配置或程序版本；不适用时为 0。</summary>
        public long Revision;

        /// <summary>调用身份；不适用时为 0。</summary>
        public long InvocationId;

        /// <summary>产生该事件的托管线程 id。</summary>
        public int ThreadId;

        /// <summary>该事件是否在主线程产生。</summary>
        public bool MainThread = true;

        // ---- 时钟溯源 ----

        /// <summary>该事件实际使用的时钟种类，见 UnityCliDebugClocks。</summary>
        public string ClockKind = UnityCliDebugClocks.Monotonic;

        /// <summary>ClockKind 对应的时钟读数。</summary>
        public double ClockValue;

        /// <summary>主线程事件记录的 Unity 真实帧号；off-main 事件固定为 0。</summary>
        public long Frame;

        /// <summary>off-main 事件携带的最后一次主线程观察帧号；主线程事件固定为 0。</summary>
        public long LastObservedMainFrame;

        // ---- 业务分类 ----

        /// <summary>Play 运行段标识；Edit 模式下为空。</summary>
        public string PlaySessionId = string.Empty;

        /// <summary>逻辑时钟段标识。</summary>
        public string ClockSegment = string.Empty;

        /// <summary>调用耗时（毫秒）；不适用时为 0。</summary>
        public double ElapsedMilliseconds;

        // ---- 来源业务字段 ----

        /// <summary>hook 定义 id；非 hook 来源为空。</summary>
        public string HookId = string.Empty;

        /// <summary>回调尚未编译完成、命中只被记录时的标记。</summary>
        public bool CallbackPending;

        /// <summary>被观察实例的身份摘要。</summary>
        public string Instance = string.Empty;

        /// <summary>被拦截方法的稳定身份。</summary>
        public string MethodIdentity = string.Empty;

        /// <summary>有界参数投影。</summary>
        public object Arguments;

        /// <summary>有界结果投影。</summary>
        public object Result;

        /// <summary>有界字段投影。</summary>
        public object Fields;

        /// <summary>事件名称（lifecycle 名称或 fault owner）。</summary>
        public string Name = string.Empty;

        /// <summary>有界附加值投影（Emit 或 detail）。</summary>
        public object Value;

        /// <summary>稳定故障码；无故障时为空。</summary>
        public string FaultCode = string.Empty;

        /// <summary>故障描述；无故障时为空。</summary>
        public string FaultMessage = string.Empty;

        // ---- 完整性与采样 ----

        /// <summary>事件内容被边界截断（含入库投影截断与来源标记）。</summary>
        public bool Truncated;

        /// <summary>入库投影自身触发了深度、条目或字符串截断。</summary>
        public bool ProjectionTruncated;

        /// <summary>该事件来自采样流而不是完整流。</summary>
        public bool Sampled;

        /// <summary>来源当前的采样步长；1 表示完整流。</summary>
        public int SampleEvery = 1;

        /// <summary>入库后的估算字节数，由存储按入库边界计算。</summary>
        public int EstimatedBytes;
    }

    /// <summary>读取筛选；空值表示不筛选。cursor 绑定规范化筛选与 Editor Session。</summary>
    public sealed class UnityCliDebugEventFilter
    {
        public string SourceId;
        public string Kind;
        public string HookId;
        public string PlaySessionId;
        public int? ThreadId;
        public double? FromClockValue;
        public double? ToClockValue;
        public long? Revision;

        /// <summary>规范化筛选文本；cursor 作用域 hash 的唯一输入。</summary>
        public string Canonical()
        {
            var builder = new StringBuilder();
            builder.Append("source=").Append(SourceId ?? string.Empty);
            builder.Append("|kind=").Append(Kind ?? string.Empty);
            builder.Append("|hook=").Append(HookId ?? string.Empty);
            builder.Append("|run=").Append(PlaySessionId ?? string.Empty);
            builder.Append("|thread=").Append(ThreadId.HasValue ? ThreadId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
            builder.Append("|from=").Append(FromClockValue.HasValue ? FromClockValue.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty);
            builder.Append("|to=").Append(ToClockValue.HasValue ? ToClockValue.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty);
            builder.Append("|revision=").Append(Revision.HasValue ? Revision.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
            return builder.ToString();
        }

        /// <summary>该事件是否通过筛选；筛选只比较已公开的稳定字段。</summary>
        public bool Matches(UnityCliDebugEvent entry)
        {
            if (entry == null) return false;
            if (!string.IsNullOrEmpty(SourceId) && !string.Equals(entry.SourceId, SourceId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(Kind) && !string.Equals(entry.Kind, Kind, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(HookId) && !string.Equals(entry.HookId, HookId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(PlaySessionId) && !string.Equals(entry.PlaySessionId, PlaySessionId, StringComparison.Ordinal)) return false;
            if (ThreadId.HasValue && entry.ThreadId != ThreadId.Value) return false;
            if (Revision.HasValue && entry.Revision != Revision.Value) return false;
            if (FromClockValue.HasValue && entry.ClockValue < FromClockValue.Value) return false;
            if (ToClockValue.HasValue && entry.ClockValue > ToClockValue.Value) return false;
            return true;
        }
    }

    /// <summary>事件存储容量；除进程级字节账本外的边界由调用方从唯一的 debug 预算声明点传入。</summary>
    public sealed class UnityCliDebugEventCapacity
    {
        public UnityCliDebugEventCapacity(int maxEventsPerSession, long maxBytesPerSession, int maxStoredEventBytes, int maxProjectionDepth, int maxProjectionItemsPerLevel, int maxProjectionStringBytes, int maxGapRecords)
        {
            if (maxEventsPerSession < 1) throw new ArgumentOutOfRangeException("maxEventsPerSession");
            if (maxBytesPerSession < 1L) throw new ArgumentOutOfRangeException("maxBytesPerSession");
            if (maxStoredEventBytes < 1) throw new ArgumentOutOfRangeException("maxStoredEventBytes");
            if (maxProjectionDepth < 0) throw new ArgumentOutOfRangeException("maxProjectionDepth");
            if (maxProjectionItemsPerLevel < 1) throw new ArgumentOutOfRangeException("maxProjectionItemsPerLevel");
            if (maxProjectionStringBytes < 1) throw new ArgumentOutOfRangeException("maxProjectionStringBytes");
            if (maxGapRecords < 1) throw new ArgumentOutOfRangeException("maxGapRecords");
            MaxEventsPerSession = maxEventsPerSession;
            MaxBytesPerSession = maxBytesPerSession;
            MaxStoredEventBytes = maxStoredEventBytes;
            MaxProjectionDepth = maxProjectionDepth;
            MaxProjectionItemsPerLevel = maxProjectionItemsPerLevel;
            MaxProjectionStringBytes = maxProjectionStringBytes;
            MaxGapRecords = maxGapRecords;
        }

        public int MaxEventsPerSession { get; private set; }
        public long MaxBytesPerSession { get; private set; }
        public int MaxStoredEventBytes { get; private set; }
        public int MaxProjectionDepth { get; private set; }
        public int MaxProjectionItemsPerLevel { get; private set; }
        public int MaxProjectionStringBytes { get; private set; }
        public int MaxGapRecords { get; private set; }
    }

    /// <summary>整个进程共享的 debug 事件字节账本；只做原子加减，不持有事件。</summary>
    public sealed class UnityCliDebugGlobalBudget
    {
        private readonly long maximumBytes;
        private long retainedBytes;

        public UnityCliDebugGlobalBudget(long maximumBytes)
        {
            if (maximumBytes < 1L) throw new ArgumentOutOfRangeException("maximumBytes");
            this.maximumBytes = maximumBytes;
        }

        public long MaximumBytes { get { return maximumBytes; } }
        public long RetainedBytes { get { return Interlocked.Read(ref retainedBytes); } }

        /// <summary>登记新增占用；调用方随后读取 RetainedBytes 判断是否超限。</summary>
        public void Reserve(long bytes)
        {
            if (bytes > 0L) Interlocked.Add(ref retainedBytes, bytes);
        }

        /// <summary>释放淘汰占用的字节。</summary>
        public void Release(long bytes)
        {
            if (bytes > 0L) Interlocked.Add(ref retainedBytes, -bytes);
        }
    }

    /// <summary>
    /// 有界值投影：把任意值物化成只含 JSON 标量与容器的副本，并报告是否触发截断。
    /// 只读已知安全的值，不调用用户 ToString、不调用属性 getter、不遍历活的业务对象引用。
    /// </summary>
    public static class UnityCliDebugValueBounds
    {
        private sealed class Truncation
        {
            internal bool Value;
            internal void Mark() { Value = true; }
        }

        /// <summary>物化有界副本；返回的图与入参不共享任何可变对象。</summary>
        public static object Bound(object value, int maxDepth, int maxItemsPerLevel, int maxStringBytes, out bool truncated)
        {
            var truncation = new Truncation();
            var bounded = BoundValue(value, 0, maxDepth, maxItemsPerLevel, maxStringBytes, truncation);
            truncated = truncation.Value;
            return bounded;
        }

        /// <summary>按 UTF-8 字节上限截断字符串；不切分 surrogate pair。</summary>
        public static string BoundText(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || maxBytes <= 0) return string.Empty;
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
            var bytes = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var size = Encoding.UTF8.GetByteCount(value, index, 1);
                if (bytes + size > maxBytes) return value.Substring(0, index);
                bytes += size;
            }
            return value;
        }

        /// <summary>估算已物化值在 JSON 中的字节数；用于入库字节账本，不分配完整 JSON 文本。</summary>
        public static long EstimateBytes(object value)
        {
            if (value == null) return 4L;
            var text = value as string;
            if (text != null) return Encoding.UTF8.GetByteCount(text) + 2L;
            if (value is bool) return 5L;
            if (value is long || value is int || value is short || value is byte) return 20L;
            if (value is double || value is float || value is decimal) return 24L;
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                long total = 2L;
                foreach (DictionaryEntry entry in dictionary) total += EstimateBytes(entry.Key as string) + 3L + EstimateBytes(entry.Value) + 1L;
                return total;
            }
            var sequence = value as IEnumerable;
            if (sequence != null)
            {
                long total = 2L;
                foreach (var item in sequence) total += EstimateBytes(item) + 1L;
                return total;
            }
            return 32L;
        }

        private static object BoundValue(object value, int depth, int maxDepth, int maxItemsPerLevel, int maxStringBytes, Truncation truncation)
        {
            if (value == null) return null;
            var text = value as string;
            if (text != null)
            {
                var bounded = BoundText(text, maxStringBytes);
                if (bounded.Length != text.Length) truncation.Mark();
                return bounded;
            }
            if (value is bool) return value;
            if (value is int || value is long) return value;
            if (value is byte || value is sbyte || value is short || value is ushort || value is uint || value is ulong) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (value is char) return value.ToString();
            if (value is float) return BoundNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture), truncation);
            if (value is double) return BoundNumber((double)value, truncation);
            if (value is decimal) return BoundNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture), truncation);
            if (value is Enum) return value.ToString();
            if (value is DateTime) return ((DateTime)value).ToString("O", CultureInfo.InvariantCulture);
            if (value is TimeSpan) return ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture);
            if (value is Guid) return ((Guid)value).ToString("D");

            if (depth >= maxDepth)
            {
                truncation.Mark();
                return TypeOnly(value);
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (result.Count >= maxItemsPerLevel) { truncation.Mark(); break; }
                    var key = entry.Key as string;
                    if (key == null) { truncation.Mark(); continue; }
                    result[key] = BoundValue(entry.Value, depth + 1, maxDepth, maxItemsPerLevel, maxStringBytes, truncation);
                }
                return result;
            }

            var sequence = value as IEnumerable;
            if (sequence != null)
            {
                var result = new List<object>();
                foreach (var item in sequence)
                {
                    if (result.Count >= maxItemsPerLevel) { truncation.Mark(); break; }
                    result.Add(BoundValue(item, depth + 1, maxDepth, maxItemsPerLevel, maxStringBytes, truncation));
                }
                return result;
            }
            return TypeOnly(value);
        }

        /// <summary>非有限浮点值不是 JSON 值，显式转成名称而不是伪造数字。</summary>
        private static object BoundNumber(double number, Truncation truncation)
        {
            if (double.IsNaN(number)) { truncation.Mark(); return "NaN"; }
            if (double.IsInfinity(number)) { truncation.Mark(); return number > 0d ? "Infinity" : "-Infinity"; }
            return number;
        }

        private static string TypeOnly(object value)
        {
            var type = value.GetType();
            return "object:" + (type.FullName ?? type.Name);
        }
    }

    /// <summary>
    /// 一次原子捕获的保留窗口边界事实。判定类读取用它把“读到的事件区间”与
    /// “同一瞬间的淘汰、失败与缺口计数”固定在一起，避免两次读取拼成一个并不存在的区间。
    /// </summary>
    public sealed class UnityCliDebugEventBoundary
    {
        /// <summary>捕获时刻的尾部 sequence；该序号及其之前的事件已进入存储。</summary>
        public long Sequence;

        /// <summary>捕获时刻仍保留的最早事件 sequence；空环时等于 Sequence。</summary>
        public long OldestAvailableSequence;

        /// <summary>保留窗口内已淘汰的事件数（累计）。</summary>
        public long Dropped;

        /// <summary>当前保留的缺口条数。</summary>
        public int GapCount;

        /// <summary>保留窗口是否已被淘汰或截断。</summary>
        public bool Truncated;
    }

    /// <summary>一次有界读取的结果；CursorStatus 取值 ok/stale/expired，由调用方归约为稳定错误码。</summary>
    public sealed class UnityCliDebugEventPage
    {
        public List<UnityCliDebugEvent> Events = new List<UnityCliDebugEvent>();
        public bool HasMore;
        public long NextScannedSequence;
        public string CursorStatus = "ok";
        public long OldestAvailableSequence;
        public long OldestAvailableCursor;
        public long LatestSequence;
        public long Dropped;
        public bool Sampled;
        public int SampleEvery;
        public string Coverage = string.Empty;
    }

    /// <summary>
    /// debug 域统一事件存储：本类是托管调试事件的唯一真相，也是唯一 sequence 分配者。
    ///
    /// 约定：
    /// - 任意托管线程都可以追加；投影物化与字节估算在临界区外完成，临界区只做入队、计数与淘汰。
    /// - 临界区内不做用户回调、业务调用或磁盘 IO，因此不会与其他锁形成长临界区。
    /// - 读取不消费事件，多个 reader 各自持有 cursor；cursor 是 sequence 的排他下界。
    /// - 淘汰只从最旧端发生并记录为 gap；sequence 永不复用。
    /// - 本类不注册也不依赖任何帧回调，主线程不 tick 时仍然可追加与读取。
    /// </summary>
    public sealed class UnityCliDebugEventBuffer
    {
        /// <summary>固定字段字符串再收紧时的份额分母：固定字符串字段约十余个，按单条上限的该份额切分即可整条容纳。</summary>
        private const int FixedStringBudgetDivisor = 16;

        private readonly object gate = new object();
        private readonly Queue<UnityCliDebugEvent> events = new Queue<UnityCliDebugEvent>();
        private readonly List<UnityCliDebugGap> gaps = new List<UnityCliDebugGap>();
        private readonly UnityCliDebugEventCapacity capacity;
        private readonly UnityCliDebugGlobalBudget globalBudget;
        private string sessionId;
        private long sequence;
        private long retainedBytes;
        private long globalRetainedBytes;
        private long dropped;

        public UnityCliDebugEventBuffer(string sessionId, long sequenceFloor, UnityCliDebugEventCapacity capacity, UnityCliDebugGlobalBudget globalBudget)
        {
            if (capacity == null) throw new ArgumentNullException("capacity");
            this.capacity = capacity;
            this.globalBudget = globalBudget;
            this.sessionId = sessionId ?? string.Empty;
            sequence = sequenceFloor < 0L ? 0L : sequenceFloor;
        }

        public UnityCliDebugEventCapacity Capacity { get { return capacity; } }
        public string SessionId { get { lock (gate) return sessionId; } }
        public long LatestSequence { get { lock (gate) return sequence; } }
        public long Count { get { lock (gate) return events.Count; } }
        public long Dropped { get { lock (gate) return dropped; } }
        public long RetainedBytes { get { lock (gate) return retainedBytes; } }
        public long GlobalRetainedBytes { get { lock (gate) return globalRetainedBytes; } }
        public bool Truncated { get { lock (gate) return dropped > 0L || gaps.Count > 0; } }

        /// <summary>仍保留的最早事件 sequence；空环时等于当前尾部。</summary>
        public long OldestAvailableSequence
        {
            get
            {
                lock (gate) return events.Count == 0 ? sequence : events.Peek().Sequence;
            }
        }

        /// <summary>当前仍可续读的最早起点：最旧事件的 sequence - 1。</summary>
        public long OldestAvailableCursor
        {
            get
            {
                lock (gate) return events.Count == 0 ? sequence : events.Peek().Sequence - 1L;
            }
        }

        public IReadOnlyList<UnityCliDebugGap> Gaps { get { lock (gate) return gaps.ToArray(); } }

        /// <summary>把值投影物化到入库边界并分配 sequence 后入队；返回该事件的 sequence。</summary>
        public long Append(UnityCliDebugEvent entry)
        {
            if (entry == null)
            {
                lock (gate) return sequence;
            }
            // 投影物化与字节估算会遍历整棵投影图，必须在临界区外完成。
            Materialize(entry);

            lock (gate)
            {
                // sequence 分配与入队在同一短临界区内完成，保证环顺序与 sequence 顺序一致。
                entry.Sequence = ++sequence;
                events.Enqueue(entry);
                retainedBytes += entry.EstimatedBytes;
                if (globalBudget != null)
                {
                    globalBudget.Reserve(entry.EstimatedBytes);
                    globalRetainedBytes += entry.EstimatedBytes;
                }
                Trim();
                return entry.Sequence;
            }
        }

        /// <summary>记录不可读区间；缺口集合有界，只描述保留窗口不冒充调用缺失。</summary>
        public void RecordGap(long fromSequence, long toSequence, string reason)
        {
            if (toSequence < fromSequence) return;
            lock (gate) RecordGapLocked(fromSequence, toSequence, reason);
        }

        /// <summary>读取一页事件；cursor 绑定 Editor Session 与规范化筛选，读取不消费任何状态。</summary>
        public UnityCliDebugEventPage Read(UnityCliDebugEventFilter filter, string cursorText, string editorSessionId, int limit)
        {
            filter = filter ?? new UnityCliDebugEventFilter();
            var effectiveLimit = Math.Max(1, limit);
            var page = new UnityCliDebugEventPage();
            UnityCliDebugEvent[] snapshot;
            lock (gate)
            {
                snapshot = events.ToArray();
                page.OldestAvailableSequence = snapshot.Length == 0 ? sequence : snapshot[0].Sequence;
                page.OldestAvailableCursor = snapshot.Length == 0 ? sequence : snapshot[0].Sequence - 1L;
                page.LatestSequence = sequence;
                page.Dropped = dropped;
                page.Coverage = CoverageLocked();
                page.Sampled = SampleSummaryLocked(out var sampleEvery);
                page.SampleEvery = sampleEvery;
            }

            if (string.IsNullOrEmpty(cursorText))
            {
                // 无 cursor 时返回当前最后一页，按 sequence 升序。
                var matches = new List<UnityCliDebugEvent>();
                foreach (var entry in snapshot) if (filter.Matches(entry)) matches.Add(entry);
                var start = Math.Max(0, matches.Count - effectiveLimit);
                for (var index = start; index < matches.Count; index++) page.Events.Add(matches[index]);
                page.HasMore = false;
                page.NextScannedSequence = page.LatestSequence;
                return page;
            }

            var scope = ScopeKey(filter, editorSessionId);
            string position;
            long scanned;
            if (!UnityCliContinuationToken.TryParse(cursorText, scope, editorSessionId ?? string.Empty, out position)
                || !long.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out scanned))
            {
                page.CursorStatus = "stale";
                page.NextScannedSequence = page.LatestSequence;
                return page;
            }
            if (snapshot.Length > 0 && scanned < page.OldestAvailableCursor)
            {
                page.CursorStatus = "expired";
                page.NextScannedSequence = page.LatestSequence;
                return page;
            }

            var collected = new List<UnityCliDebugEvent>();
            var scannedSequence = scanned;
            foreach (var entry in snapshot)
            {
                if (entry.Sequence <= scanned) continue;
                scannedSequence = entry.Sequence;
                if (!filter.Matches(entry)) continue;
                if (collected.Count >= effectiveLimit)
                {
                    page.HasMore = true;
                    scannedSequence = entry.Sequence - 1L;
                    break;
                }
                collected.Add(entry);
            }
            page.Events = collected;
            page.NextScannedSequence = page.HasMore ? scannedSequence : page.LatestSequence;
            return page;
        }

        /// <summary>规范化筛选与 Editor Session 的 cursor 作用域。</summary>
        public static string ScopeKey(UnityCliDebugEventFilter filter, string editorSessionId)
        {
            var effective = filter ?? new UnityCliDebugEventFilter();
            return UnityCliContinuationToken.Fingerprint((editorSessionId ?? string.Empty) + "\n" + effective.Canonical());
        }

        /// <summary>构造指定位置的 cursor；作用域与 Editor Session 绑定，跨会话即失效。</summary>
        public string CursorFor(UnityCliDebugEventFilter filter, string editorSessionId, long scannedSequence)
        {
            return UnityCliContinuationToken.Create(ScopeKey(filter, editorSessionId), editorSessionId ?? string.Empty, scannedSequence.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>当前序列位置的起点 cursor；install 用它作为捕获起点。</summary>
        public string BeginCursor(UnityCliDebugEventFilter filter, string editorSessionId)
        {
            long tail;
            lock (gate) tail = sequence;
            return CursorFor(filter, editorSessionId, tail);
        }

        /// <summary>在单次临界区内捕获边界 sequence 与同一瞬间的淘汰、缺口计数。</summary>
        public UnityCliDebugEventBoundary CaptureBoundary()
        {
            lock (gate)
            {
                return new UnityCliDebugEventBoundary
                {
                    Sequence = sequence,
                    OldestAvailableSequence = events.Count == 0 ? sequence : events.Peek().Sequence,
                    Dropped = dropped,
                    GapCount = gaps.Count,
                    Truncated = dropped > 0L || gaps.Count > 0
                };
            }
        }

        /// <summary>
        /// 读取一个 sequence 区间内仍保留的事件，不消费也不移动任何 cursor。
        /// 区间两端都是排他/包含边界：返回 sequence 大于 afterExclusive 且不大于 upToInclusive 的条目。
        /// </summary>
        public List<UnityCliDebugEvent> Range(long afterExclusive, long upToInclusive)
        {
            var result = new List<UnityCliDebugEvent>();
            if (upToInclusive <= afterExclusive) return result;
            lock (gate)
            {
                foreach (var entry in events)
                {
                    if (entry.Sequence <= afterExclusive) continue;
                    if (entry.Sequence > upToInclusive) break;
                    result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>重载前导出有界快照；只写固定业务字段。</summary>
        public List<Dictionary<string, object>> Snapshot()
        {
            lock (gate)
            {
                var result = new List<Dictionary<string, object>>(events.Count);
                foreach (var entry in events) result.Add(ToData(entry));
                return result;
            }
        }

        /// <summary>从快照恢复；事件 floor 之前与 checkpoint 之后的区间由调用方记录为缺口。</summary>
        public void Restore(IEnumerable<Dictionary<string, object>> entries, long sequenceFloor, IEnumerable<UnityCliDebugGap> restoredGaps)
        {
            lock (gate)
            {
                ReleaseAllLocked();
                sequence = sequenceFloor < 0L ? 0L : sequenceFloor;
                if (restoredGaps != null) foreach (var gap in restoredGaps) RecordGapLocked(gap.FromSequence, gap.ToSequence, gap.Reason);
                if (entries == null) return;
                foreach (var data in entries)
                {
                    var entry = FromData(data);
                    if (entry == null) continue;
                    Materialize(entry);
                    if (entry.Sequence > sequence) sequence = entry.Sequence;
                    events.Enqueue(entry);
                    retainedBytes += entry.EstimatedBytes;
                    if (globalBudget != null)
                    {
                        globalBudget.Reserve(entry.EstimatedBytes);
                        globalRetainedBytes += entry.EstimatedBytes;
                    }
                }
                Trim();
            }
        }

        /// <summary>清空事件与缺口；用于 quitting 的 session 收尾。</summary>
        public void Reset()
        {
            lock (gate) ReleaseAllLocked();
        }

        /// <summary>覆盖率描述；机器可读字段在 page/DTO 上，这里只做稳定的人类可读补充。</summary>
        public string Coverage()
        {
            lock (gate) return CoverageLocked();
        }

        /// <summary>按入库边界物化一条事件：值投影、字符串与单条字节全部收敛到声明上限。</summary>
        private void Materialize(UnityCliDebugEvent entry)
        {
            var truncated = false;
            entry.Arguments = UnityCliDebugValueBounds.Bound(entry.Arguments, capacity.MaxProjectionDepth, capacity.MaxProjectionItemsPerLevel, capacity.MaxProjectionStringBytes, out truncated);
            entry.ProjectionTruncated |= truncated;
            entry.Result = UnityCliDebugValueBounds.Bound(entry.Result, capacity.MaxProjectionDepth, capacity.MaxProjectionItemsPerLevel, capacity.MaxProjectionStringBytes, out truncated);
            entry.ProjectionTruncated |= truncated;
            entry.Fields = UnityCliDebugValueBounds.Bound(entry.Fields, capacity.MaxProjectionDepth, capacity.MaxProjectionItemsPerLevel, capacity.MaxProjectionStringBytes, out truncated);
            entry.ProjectionTruncated |= truncated;
            entry.Value = UnityCliDebugValueBounds.Bound(entry.Value, capacity.MaxProjectionDepth, capacity.MaxProjectionItemsPerLevel, capacity.MaxProjectionStringBytes, out truncated);
            entry.ProjectionTruncated |= truncated;
            if (BoundFields(entry, capacity.MaxProjectionStringBytes)) entry.ProjectionTruncated = true;
            if (MeasureBytes(entry) > capacity.MaxStoredEventBytes)
            {
                // 投影本身超出单条上限时丢掉投影，只保留结构字段。
                entry.Arguments = null;
                entry.Result = null;
                entry.Fields = null;
                entry.Value = null;
                entry.Truncated = true;
                entry.ProjectionTruncated = true;
            }
            if (MeasureBytes(entry) > capacity.MaxStoredEventBytes)
            {
                // 固定字符串字段之和也必须能装进单条上限，因此按固定份额再收紧一次。
                if (BoundFields(entry, Math.Max(1, capacity.MaxStoredEventBytes / FixedStringBudgetDivisor))) entry.ProjectionTruncated = true;
                entry.Truncated = true;
            }
            entry.EstimatedBytes = (int)Math.Min(MeasureBytes(entry), capacity.MaxStoredEventBytes);
            entry.Truncated |= entry.ProjectionTruncated;
        }

        /// <summary>未截断的入库字节估算；用于判断单条事件是否超出上限。</summary>
        private long MeasureBytes(UnityCliDebugEvent entry)
        {
            return MeasureBytes(entry, long.MaxValue);
        }

        /// <summary>估算已物化事件在存储中的字节数；超过单条上限时按上限计账。</summary>
        private static long MeasureBytes(UnityCliDebugEvent entry, long maxStoredEventBytes)
        {
            if (entry == null) return 0L;
            long total = 320L;
            total += UnityCliDebugValueBounds.EstimateBytes(entry.SourceId);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.Kind);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.ExecutionPoint);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.HookId);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.PlaySessionId);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.ClockSegment);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.Instance);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.MethodIdentity);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.Name);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.FaultCode);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.FaultMessage);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.Arguments);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.Result);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.Fields);
            total += UnityCliDebugValueBounds.EstimateBytes(entry.Value);
            return total < 0L || maxStoredEventBytes <= 0L ? total : Math.Min(total, maxStoredEventBytes);
        }

        /// <summary>序列化为快照条目；只写固定业务字段。</summary>
        public static Dictionary<string, object> ToData(UnityCliDebugEvent entry)
        {
            return new Dictionary<string, object>
            {
                { "sequence", entry.Sequence }, { "sourceId", entry.SourceId ?? string.Empty },
                { "kind", entry.Kind ?? string.Empty }, { "executionPoint", entry.ExecutionPoint ?? string.Empty },
                { "revision", entry.Revision }, { "invocationId", entry.InvocationId },
                { "threadId", (long)entry.ThreadId }, { "mainThread", entry.MainThread },
                { "clockKind", entry.ClockKind ?? string.Empty }, { "clockValue", entry.ClockValue },
                { "frame", entry.Frame }, { "lastObservedMainFrame", entry.LastObservedMainFrame },
                { "hookId", entry.HookId ?? string.Empty },
                { "playSessionId", entry.PlaySessionId ?? string.Empty }, { "clockSegment", entry.ClockSegment ?? string.Empty },
                { "elapsedMilliseconds", entry.ElapsedMilliseconds },
                { "sampled", entry.Sampled }, { "sampleEvery", (long)entry.SampleEvery }, { "callbackPending", entry.CallbackPending },
                { "instance", entry.Instance ?? string.Empty }, { "methodIdentity", entry.MethodIdentity ?? string.Empty },
                { "arguments", entry.Arguments }, { "result", entry.Result }, { "fields", entry.Fields },
                { "name", entry.Name ?? string.Empty }, { "value", entry.Value },
                { "faultCode", entry.FaultCode ?? string.Empty }, { "faultMessage", entry.FaultMessage ?? string.Empty },
                { "truncated", entry.Truncated }, { "projectionTruncated", entry.ProjectionTruncated }
            };
        }

        /// <summary>从快照条目恢复；缺失或异形字段按缺省处理，不猜测历史。</summary>
        public static UnityCliDebugEvent FromData(IDictionary<string, object> data)
        {
            if (data == null) return null;
            var entry = new UnityCliDebugEvent
            {
                Sequence = Long(data, "sequence"),
                SourceId = Text(data, "sourceId"),
                Kind = Text(data, "kind"),
                ExecutionPoint = Text(data, "executionPoint"),
                Revision = Long(data, "revision"),
                InvocationId = Long(data, "invocationId"),
                ThreadId = (int)Long(data, "threadId"),
                MainThread = Bool(data, "mainThread", true),
                ClockKind = Text(data, "clockKind"),
                ClockValue = Number(data, "clockValue"),
                Frame = Long(data, "frame"),
                LastObservedMainFrame = Long(data, "lastObservedMainFrame"),
                HookId = Text(data, "hookId"),
                PlaySessionId = Text(data, "playSessionId"),
                ClockSegment = Text(data, "clockSegment"),
                ElapsedMilliseconds = Number(data, "elapsedMilliseconds"),
                Sampled = Bool(data, "sampled", false),
                SampleEvery = (int)Long(data, "sampleEvery"),
                CallbackPending = Bool(data, "callbackPending", false),
                Instance = Text(data, "instance"),
                MethodIdentity = Text(data, "methodIdentity"),
                Arguments = Raw(data, "arguments"),
                Result = Raw(data, "result"),
                Fields = Raw(data, "fields"),
                Name = Text(data, "name"),
                Value = Raw(data, "value"),
                FaultCode = Text(data, "faultCode"),
                FaultMessage = Text(data, "faultMessage"),
                Truncated = Bool(data, "truncated", false),
                ProjectionTruncated = Bool(data, "projectionTruncated", false)
            };
            if (entry.SampleEvery < 1) entry.SampleEvery = 1;
            if (string.IsNullOrEmpty(entry.ClockKind)) entry.ClockKind = UnityCliDebugClocks.Monotonic;
            if (entry.Sequence <= 0L) return null;
            return entry;
        }

        private string CoverageLocked()
        {
            var builder = new StringBuilder();
            builder.Append("retained ").Append(events.Count.ToString(CultureInfo.InvariantCulture)).Append(" of ")
                .Append((events.Count + dropped).ToString(CultureInfo.InvariantCulture)).Append(" events");
            if (events.Count > 0)
            {
                builder.Append(" (sequences ").Append(events.Peek().Sequence.ToString(CultureInfo.InvariantCulture))
                    .Append('-').Append(sequence.ToString(CultureInfo.InvariantCulture)).Append(')');
            }
            builder.Append(", dropped ").Append(dropped.ToString(CultureInfo.InvariantCulture));
            builder.Append(", gaps ").Append(gaps.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(", samplestep ").Append((SampleSummaryLocked(out var step) ? step : 0).ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        /// <summary>保留窗口的采样摘要：是否含采样事件，以及唯一的采样步长（混合时为 0）。</summary>
        private bool SampleSummaryLocked(out int sampleEvery)
        {
            var sampled = false;
            var step = 0;
            var mixed = false;
            foreach (var entry in events)
            {
                if (!entry.Sampled) continue;
                sampled = true;
                if (step == 0) step = entry.SampleEvery;
                else if (step != entry.SampleEvery) mixed = true;
            }
            sampleEvery = mixed ? 0 : step;
            return sampled;
        }

        /// <summary>把事件的固定字符串字段按给定字节上限收紧；返回是否发生截断。</summary>
        private static bool BoundFields(UnityCliDebugEvent entry, int maxBytes)
        {
            var truncated = false;
            entry.SourceId = Bound(entry.SourceId, maxBytes, ref truncated);
            entry.Kind = Bound(entry.Kind, maxBytes, ref truncated);
            entry.ExecutionPoint = Bound(entry.ExecutionPoint, maxBytes, ref truncated);
            entry.HookId = Bound(entry.HookId, maxBytes, ref truncated);
            entry.PlaySessionId = Bound(entry.PlaySessionId, maxBytes, ref truncated);
            entry.ClockSegment = Bound(entry.ClockSegment, maxBytes, ref truncated);
            entry.Instance = Bound(entry.Instance, maxBytes, ref truncated);
            entry.MethodIdentity = Bound(entry.MethodIdentity, maxBytes, ref truncated);
            entry.Name = Bound(entry.Name, maxBytes, ref truncated);
            entry.FaultCode = Bound(entry.FaultCode, maxBytes, ref truncated);
            entry.FaultMessage = Bound(entry.FaultMessage, maxBytes, ref truncated);
            return truncated;
        }

        private static string Bound(string value, int maxBytes, ref bool truncated)
        {
            var text = value ?? string.Empty;
            var bounded = UnityCliDebugValueBounds.BoundText(text, maxBytes);
            if (bounded.Length != text.Length) truncated = true;
            return bounded;
        }

        private void ReleaseAllLocked()
        {
            foreach (var entry in events)
            {
                if (globalBudget != null) globalBudget.Release(entry.EstimatedBytes);
            }
            events.Clear();
            gaps.Clear();
            retainedBytes = 0L;
            globalRetainedBytes = 0L;
            dropped = 0L;
        }

        private void Trim()
        {
            var trimmedFrom = -1L;
            var trimmedTo = -1L;
            while ((events.Count > capacity.MaxEventsPerSession
                    || retainedBytes > capacity.MaxBytesPerSession
                    || globalBudget != null && globalBudget.RetainedBytes > globalBudget.MaximumBytes)
                && events.Count > 1)
            {
                var droppedEntry = events.Dequeue();
                retainedBytes -= droppedEntry.EstimatedBytes;
                if (globalBudget != null)
                {
                    globalBudget.Release(droppedEntry.EstimatedBytes);
                    globalRetainedBytes -= droppedEntry.EstimatedBytes;
                }
                dropped++;
                if (trimmedFrom < 0L) trimmedFrom = droppedEntry.Sequence;
                trimmedTo = droppedEntry.Sequence;
            }
            if (trimmedFrom >= 0L) RecordGapLocked(trimmedFrom, trimmedTo, "event-ring-capacity");
        }

        private void RecordGapLocked(long fromSequence, long toSequence, string reason)
        {
            if (toSequence < fromSequence) return;
            var text = reason ?? string.Empty;
            if (gaps.Count > 0)
            {
                var last = gaps[gaps.Count - 1];
                // 相邻且同原因的区间直接合并，避免逐条淘汰把有界缺口列表用单条区间填满。
                if (string.Equals(last.Reason, text, StringComparison.Ordinal) && fromSequence <= last.ToSequence + 1L)
                {
                    if (toSequence > last.ToSequence) last.ToSequence = toSequence;
                    return;
                }
            }
            gaps.Add(new UnityCliDebugGap { FromSequence = fromSequence, ToSequence = toSequence, Reason = text });
            while (gaps.Count > capacity.MaxGapRecords) gaps.RemoveAt(0);
        }

        private static string Text(IDictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty; }
        private static long Long(IDictionary<string, object> data, string key) { long parsed; return long.TryParse(Text(data, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0L; }
        private static double Number(IDictionary<string, object> data, string key) { double parsed; return double.TryParse(Text(data, key), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0d; }
        private static bool Bool(IDictionary<string, object> data, string key, bool fallback) { bool parsed; return bool.TryParse(Text(data, key), out parsed) ? parsed : fallback; }
        private static object Raw(IDictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) ? value : null; }
    }
}
