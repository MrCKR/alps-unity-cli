#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UnityCli
{
    /// <summary>一条 hook 业务事件；不是请求日志，也不含 transport、listener 或审计字段。</summary>
    internal sealed class MethodHookEventRecord
    {
        internal long Sequence;
        internal long InvocationId;
        internal string HookId;
        internal long Revision;
        internal string Kind;
        internal string PlaySessionId;
        internal string ClockSegment;
        internal double ElapsedMilliseconds;
        internal long Frame;
        internal bool MainThread = true;
        internal bool Sampled = true;
        internal bool CallbackPending;
        internal string Instance;
        internal string MethodIdentity;
        internal object Arguments;
        internal object Result;
        internal object Fields;
        internal string Name;
        internal object Value;
        internal string FaultCode;
        internal string FaultMessage;
        internal bool Truncated;
        internal int EstimatedBytes;

        /// <summary>投影为公开业务字段；截断只在确有意为之时标记。</summary>
        internal MethodHookEventItem ToItem()
        {
            return new MethodHookEventItem
            {
                Sequence = Sequence,
                HookId = HookId,
                Revision = Revision,
                InvocationId = InvocationId,
                Kind = Kind,
                PlaySessionId = PlaySessionId,
                ClockSegment = ClockSegment,
                ElapsedMilliseconds = ElapsedMilliseconds,
                Frame = Frame,
                MainThread = MainThread,
                Sampled = Sampled,
                CallbackPending = CallbackPending,
                Instance = Instance,
                MethodIdentity = MethodIdentity,
                Arguments = Arguments,
                Result = Result,
                Fields = Fields,
                Name = Name,
                Value = Value,
                FaultCode = FaultCode,
                FaultMessage = FaultMessage,
                Truncated = Truncated
            };
        }

        /// <summary>序列化为快照条目；只写固定业务字段。</summary>
        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>
            {
                { "sequence", Sequence }, { "invocationId", InvocationId }, { "hookId", HookId ?? string.Empty },
                { "revision", Revision }, { "kind", Kind ?? string.Empty }, { "playSessionId", PlaySessionId ?? string.Empty },
                { "clockSegment", ClockSegment ?? string.Empty }, { "elapsedMilliseconds", ElapsedMilliseconds },
                { "frame", Frame }, { "mainThread", MainThread }, { "sampled", Sampled }, { "callbackPending", CallbackPending },
                { "instance", Instance ?? string.Empty }, { "methodIdentity", MethodIdentity ?? string.Empty },
                { "arguments", Arguments }, { "result", Result }, { "fields", Fields },
                { "name", Name ?? string.Empty }, { "value", Value },
                { "faultCode", FaultCode ?? string.Empty }, { "faultMessage", FaultMessage ?? string.Empty },
                { "truncated", Truncated }
            };
        }

        /// <summary>从快照条目恢复；缺失或异形字段按缺省处理，不猜测历史。</summary>
        internal static MethodHookEventRecord FromData(IDictionary<string, object> data)
        {
            if (data == null) return null;
            var record = new MethodHookEventRecord
            {
                Sequence = Long(data, "sequence"),
                InvocationId = Long(data, "invocationId"),
                HookId = Text(data, "hookId"),
                Revision = Long(data, "revision"),
                Kind = Text(data, "kind"),
                PlaySessionId = Text(data, "playSessionId"),
                ClockSegment = Text(data, "clockSegment"),
                ElapsedMilliseconds = Number(data, "elapsedMilliseconds"),
                Frame = Long(data, "frame"),
                MainThread = Bool(data, "mainThread", true),
                Sampled = Bool(data, "sampled", true),
                CallbackPending = Bool(data, "callbackPending", false),
                Instance = Text(data, "instance"),
                MethodIdentity = Text(data, "methodIdentity"),
                Arguments = RawValue(data, "arguments"),
                Result = RawValue(data, "result"),
                Fields = RawValue(data, "fields"),
                Name = Text(data, "name"),
                Value = RawValue(data, "value"),
                FaultCode = Text(data, "faultCode"),
                FaultMessage = Text(data, "faultMessage"),
                Truncated = Bool(data, "truncated", false)
            };
            if (record.Sequence <= 0) return null;
            return record;
        }

        private static string Text(IDictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty; }
        private static long Long(IDictionary<string, object> data, string key) { long parsed; return long.TryParse(Text(data, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0L; }
        private static double Number(IDictionary<string, object> data, string key) { double parsed; return double.TryParse(Text(data, key), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0d; }
        private static bool Bool(IDictionary<string, object> data, string key, bool fallback) { bool parsed; return bool.TryParse(Text(data, key), out parsed) ? parsed : fallback; }
        private static object RawValue(IDictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) ? value : null; }
    }

    /// <summary>事件查询筛选；cursor 与 scope 绑定同一规范化筛选。</summary>
    internal sealed class MethodHookEventFilter
    {
        internal string HookId;
        internal string Kind;
        internal string PlaySessionId;

        internal string ScopeKey(string editorSessionId)
        {
            return UnityCliSerializer.Sha256Canonical(new Dictionary<string, object>
            {
                { "session", editorSessionId ?? string.Empty },
                { "hookId", HookId ?? string.Empty },
                { "kind", Kind ?? string.Empty },
                { "playSessionId", PlaySessionId ?? string.Empty }
            });
        }

        internal bool Matches(MethodHookEventRecord record)
        {
            if (!string.IsNullOrEmpty(HookId) && !string.Equals(record.HookId, HookId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(Kind) && !string.Equals(record.Kind, Kind, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(PlaySessionId) && !string.Equals(record.PlaySessionId, PlaySessionId, StringComparison.Ordinal)) return false;
            return true;
        }
    }

    /// <summary>cursor 身份：Editor Session、规范化筛选与最后检查过的 sequence。</summary>
    internal sealed class MethodHookEventCursor
    {
        private const string Prefix = "mh1";

        internal string EditorSessionId;
        internal string ScopeHash;
        internal long ScannedSequence;

        internal string Encode()
        {
            var payload = Prefix + "|" + (EditorSessionId ?? string.Empty) + "|" + (ScopeHash ?? string.Empty) + "|" + ScannedSequence.ToString(CultureInfo.InvariantCulture);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        }

        internal static MethodHookEventCursor Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            byte[] raw;
            try { raw = Convert.FromBase64String(text); }
            catch { throw Stale(); }
            var parts = Encoding.UTF8.GetString(raw).Split('|');
            if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)) throw Stale();
            long scanned;
            if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out scanned) || scanned < 0) throw Stale();
            return new MethodHookEventCursor { EditorSessionId = parts[1], ScopeHash = parts[2], ScannedSequence = scanned };
        }

        internal static MethodHookCursorException Stale()
        {
            return new MethodHookCursorException("E_CONTINUATION_STALE", "The event continuation no longer matches the current Editor session or filters.", new[] { "Re-read the current tail of method-hook.events without a cursor." });
        }
    }

    /// <summary>cursor 相关的稳定失败，由 host 原样转成 Bridge 错误 envelope。</summary>
    internal sealed class MethodHookCursorException : Exception
    {
        internal MethodHookCursorException(string code, string message, string[] nextActions) : base(message)
        {
            Code = code;
            NextActions = nextActions ?? Array.Empty<string>();
        }

        internal string Code { get; }
        internal string[] NextActions { get; }
    }

    /// <summary>一次有界事件读取的结果。</summary>
    internal sealed class MethodHookEventPage
    {
        internal List<MethodHookEventRecord> Records = new List<MethodHookEventRecord>();
        internal bool HasMore;
        internal long NextScannedSequence;
        internal long OldestAvailableSequence;
    }

    /// <summary>
    /// 有界事件环。按条数与估算字节双限淘汰最旧记录，淘汰与 checkpoint 缺失都记录为 history gap；
    /// 读取不消费共享状态，多个 reader 可以各自独立续读。
    /// </summary>
    internal sealed class MethodHookEventBuffer
    {
        private readonly Queue<MethodHookEventRecord> records = new Queue<MethodHookEventRecord>();
        private readonly List<MethodHookGap> gaps = new List<MethodHookGap>();
        private long retainedBytes;
        private long lastSequence;

        internal long LastSequence { get { return lastSequence; } }
        internal long Count { get { return records.Count; } }

        /// <summary>当前仍可续读的最早起点：最旧事件的 sequence - 1。</summary>
        internal long OldestAvailableSequence
        {
            get
            {
                if (records.Count == 0) return lastSequence;
                return records.Peek().Sequence - 1;
            }
        }

        internal IReadOnlyList<MethodHookGap> Gaps { get { return gaps.ToArray(); } }

        /// <summary>追加上限内事件并返回其 sequence；超限时淘汰最旧记录并记录缺口。</summary>
        internal long Append(MethodHookEventRecord record)
        {
            if (record == null) return lastSequence;
            record.Sequence = ++lastSequence;
            record.EstimatedBytes = Estimate(record);
            records.Enqueue(record);
            retainedBytes += record.EstimatedBytes;
            var trimmedFrom = -1L;
            var trimmedTo = -1L;
            while ((records.Count > MethodHookLimits.MaxEventEntries || retainedBytes > MethodHookLimits.MaxEventRingBytes) && records.Count > 1)
            {
                var dropped = records.Dequeue();
                retainedBytes -= dropped.EstimatedBytes;
                if (trimmedFrom < 0) trimmedFrom = dropped.Sequence;
                trimmedTo = dropped.Sequence;
            }
            if (trimmedFrom >= 0) RecordGap(trimmedFrom, trimmedTo, "event-ring-capacity");
            return record.Sequence;
        }

        /// <summary>记录不可读区间；缺口集合有界，只描述保留窗口不冒充调用缺失。</summary>
        internal void RecordGap(long fromSequence, long toSequence, string reason)
        {
            if (toSequence < fromSequence) return;
            gaps.Add(new MethodHookGap { FromSequence = fromSequence, ToSequence = toSequence, Reason = reason });
            while (gaps.Count > MethodHookLimits.MaxHistoryGaps) gaps.RemoveAt(0);
        }

        /// <summary>读取一页事件；cursor 绑定 Editor Session 与规范化筛选。</summary>
        internal MethodHookEventPage Read(MethodHookEventFilter filter, string cursorText, string editorSessionId, int limit)
        {
            if (filter == null) filter = new MethodHookEventFilter();
            var effectiveLimit = limit <= 0 ? MethodHookLimits.EventsDefaultLimit : Math.Min(limit, MethodHookLimits.EventsMaxLimit);
            var cursor = MethodHookEventCursor.Decode(cursorText);
            var scope = filter.ScopeKey(editorSessionId);
            if (cursor != null)
            {
                if (!string.Equals(cursor.EditorSessionId, editorSessionId, StringComparison.Ordinal)
                    || !string.Equals(cursor.ScopeHash, scope, StringComparison.Ordinal))
                    throw MethodHookEventCursor.Stale();
            }
            var page = new MethodHookEventPage { OldestAvailableSequence = OldestAvailableSequence };
            var snapshot = records.ToArray();
            var tail = lastSequence;
            if (cursor != null && cursor.ScannedSequence + 1 < OldestAvailableSequence
                && snapshot.Length > 0 && cursor.ScannedSequence < snapshot[0].Sequence - 1)
            {
                throw new MethodHookCursorException(
                    "E_HOOK_EVENT_CURSOR_EXPIRED",
                    "The event continuation predates the retained event window.",
                    new[] { "Re-read method-hook.events without a cursor; oldestAvailableCursor reports the new readable start." });
            }

            if (cursor == null)
            {
                // 无 cursor 时返回当前最后一页，按 sequence 升序。
                var matches = snapshot.Where(filter.Matches).ToList();
                var start = Math.Max(0, matches.Count - effectiveLimit);
                page.Records = matches.Skip(start).ToList();
                page.HasMore = false;
                page.NextScannedSequence = tail;
                return page;
            }

            var after = cursor.ScannedSequence;
            var collected = new List<MethodHookEventRecord>();
            var scanned = after;
            foreach (var record in snapshot)
            {
                if (record.Sequence <= after) continue;
                scanned = record.Sequence;
                if (!filter.Matches(record)) continue;
                if (collected.Count >= effectiveLimit) { page.HasMore = true; scanned = record.Sequence - 1; break; }
                collected.Add(record);
            }
            page.Records = collected;
            page.NextScannedSequence = page.HasMore ? scanned : tail;
            return page;
        }

        /// <summary>当前序列位置的起点 cursor；install 用它作为捕获起点。</summary>
        internal string BeginCursor(MethodHookEventFilter filter, string editorSessionId)
        {
            var effective = filter ?? new MethodHookEventFilter();
            return new MethodHookEventCursor
            {
                EditorSessionId = editorSessionId,
                ScopeHash = effective.ScopeKey(editorSessionId),
                ScannedSequence = lastSequence
            }.Encode();
        }

        /// <summary>重载前导出有界快照。</summary>
        internal List<Dictionary<string, object>> Snapshot()
        {
            return records.Select(record => record.ToData()).ToList();
        }

        /// <summary>从快照恢复；事件 floor 之前与 checkpoint 之后的区间记录为缺口。</summary>
        internal void Restore(IEnumerable<Dictionary<string, object>> entries, long sequenceFloor, IEnumerable<MethodHookGap> restoredGaps)
        {
            records.Clear();
            retainedBytes = 0;
            lastSequence = sequenceFloor;
            if (restoredGaps != null) foreach (var gap in restoredGaps) RecordGap(gap.FromSequence, gap.ToSequence, gap.Reason);
            if (entries == null) return;
            foreach (var data in entries)
            {
                var record = MethodHookEventRecord.FromData(data);
                if (record == null) continue;
                record.EstimatedBytes = Estimate(record);
                records.Enqueue(record);
                retainedBytes += record.EstimatedBytes;
                if (record.Sequence > lastSequence) lastSequence = record.Sequence;
            }
        }

        /// <summary>清空事件与缺口；用于 quitting 的 session 收尾。</summary>
        internal void Reset()
        {
            records.Clear();
            gaps.Clear();
            retainedBytes = 0;
            lastSequence = 0;
        }

        private static int Estimate(MethodHookEventRecord record)
        {
            try
            {
                var bytes = Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(record.ToData()));
                return Math.Min(bytes, MethodHookLimits.MaxEventBytes);
            }
            catch
            {
                return MethodHookLimits.MaxEventBytes;
            }
        }
    }
}
#endif

