using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// 引擎无关的调试事件存储核心测试：环记账、缺口、cursor 算术、投影边界与恢复语义。
    /// 这些类型不引用 Unity API，因此可以在 Runtime 测试程序集内直接验证。
    /// </summary>
    public sealed class UnityCliDebugEventStoreTests
    {
        [Test]
        public void AppendAllocatesIncreasingSequencesAndTrimsOnlyTheOldestEnd()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(maxEvents: 4), new UnityCliDebugGlobalBudget(1024L * 1024L));
            for (var index = 0; index < 10; index++) Assert.That(buffer.Append(Event("prefix", index)), Is.EqualTo(index + 1));

            Assert.That(buffer.Count, Is.EqualTo(4));
            Assert.That(buffer.LatestSequence, Is.EqualTo(10));
            Assert.That(buffer.OldestAvailableSequence, Is.EqualTo(7));
            Assert.That(buffer.OldestAvailableCursor, Is.EqualTo(6));
            Assert.That(buffer.Dropped, Is.EqualTo(6));
            Assert.That(buffer.Truncated, Is.True);
            Assert.That(buffer.Gaps.Count, Is.EqualTo(1));
            Assert.That(buffer.Gaps[0].FromSequence, Is.EqualTo(1));
            Assert.That(buffer.Gaps[0].ToSequence, Is.EqualTo(6));
            Assert.That(buffer.RetainedBytes, Is.GreaterThan(0));

            var page = buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 10);
            Assert.That(page.Events.Select(item => item.Sequence), Is.EqualTo(new long[] { 7, 8, 9, 10 }));
            Assert.That(page.OldestAvailableSequence, Is.EqualTo(7));
            Assert.That(page.LatestSequence, Is.EqualTo(10));
            Assert.That(page.Dropped, Is.EqualTo(6));
            Assert.That(page.Coverage, Does.Contain("retained 4 of 10"));
        }

        [Test]
        public void ReadWithoutCursorReturnsTheLastPageAndFilteredReadsNeverRescan()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), new UnityCliDebugGlobalBudget(1024L * 1024L));
            for (var index = 0; index < 8; index++) buffer.Append(Event(index % 2 == 0 ? "prefix" : "custom", index));

            var tail = buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 3);
            Assert.That(tail.Events.Select(item => item.Sequence), Is.EqualTo(new long[] { 6, 7, 8 }));
            Assert.That(tail.HasMore, Is.False);
            Assert.That(tail.NextScannedSequence, Is.EqualTo(8));

            var filter = new UnityCliDebugEventFilter { Kind = "custom" };
            var start = buffer.BeginCursor(filter, "session-test");
            buffer.Append(Event("prefix", 8));
            buffer.Append(Event("custom", 9));
            var page = buffer.Read(filter, start, "session-test", 10);
            Assert.That(page.Events.Select(item => item.Sequence), Is.EqualTo(new long[] { 10 }), "only newly appended matching records are returned");
            Assert.That(buffer.Count, Is.EqualTo(10), "reading must not remove events");

            var repeated = buffer.Read(filter, start, "session-test", 10);
            Assert.That(repeated.Events.Select(item => item.Sequence), Is.EqualTo(new long[] { 10 }), "reading must not consume the cursor");

            var bySource = buffer.Read(new UnityCliDebugEventFilter { SourceId = UnityCliDebugSources.Lifecycle }, null, "session-test", 10);
            Assert.That(bySource.Events, Is.Empty, "a filter that matches nothing returns an empty page, not an error");
        }

        [Test]
        public void CursorStatusReportsStaleAndExpiredWithoutInventingEvents()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), new UnityCliDebugGlobalBudget(1024L * 1024L));
            for (var index = 0; index < 5; index++) buffer.Append(Event("prefix", index));
            var filter = new UnityCliDebugEventFilter { Kind = "prefix" };
            var cursor = buffer.CursorFor(filter, "session-test", 2L);

            var stale = buffer.Read(new UnityCliDebugEventFilter { Kind = "custom" }, cursor, "session-test", 2);
            Assert.That(stale.CursorStatus, Is.EqualTo("stale"));
            Assert.That(stale.Events, Is.Empty);
            var otherSession = buffer.Read(filter, cursor, "other-session", 2);
            Assert.That(otherSession.CursorStatus, Is.EqualTo("stale"));

            buffer.Restore(null, 40L, null);
            buffer.Append(Event("prefix", 9));
            var expired = buffer.Read(filter, buffer.CursorFor(filter, "session-test", 10L), "session-test", 2);
            Assert.That(expired.CursorStatus, Is.EqualTo("expired"));
            Assert.That(expired.OldestAvailableCursor, Is.EqualTo(40L));
            Assert.That(expired.Events, Is.Empty);
        }

        [Test]
        public void RestoreKeepsTheSequenceFloorAndNeverReusesOldNumbers()
        {
            var seeded = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), new UnityCliDebugGlobalBudget(1024L * 1024L));
            for (var index = 0; index < 6; index++) seeded.Append(Event("prefix", index));
            var snapshot = seeded.Snapshot();

            var restored = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), new UnityCliDebugGlobalBudget(1024L * 1024L));
            restored.Restore(snapshot, 6L, new[] { new UnityCliDebugGap { FromSequence = 1, ToSequence = 6, Reason = "checkpoint-truncated" } });
            Assert.That(restored.LatestSequence, Is.EqualTo(6));
            Assert.That(restored.Count, Is.EqualTo(6));
            Assert.That(restored.Gaps.Count, Is.EqualTo(1));
            Assert.That(restored.Gaps[0].Reason, Is.EqualTo("checkpoint-truncated"));
            Assert.That(restored.Append(Event("prefix", 10)), Is.EqualTo(7), "a restored floor must not be reused");

            var empty = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), new UnityCliDebugGlobalBudget(1024L * 1024L));
            empty.Restore(null, 12L, null);
            Assert.That(empty.Append(Event("prefix", 1)), Is.EqualTo(13));
        }

        [Test]
        public void BoundedProjectionCopiesValuesAndCapsDepthItemsAndStrings()
        {
            var deep = new Dictionary<string, object>();
            var cursor = deep;
            for (var index = 0; index < 8; index++)
            {
                var child = new Dictionary<string, object>();
                cursor["level" + index] = child;
                cursor = child;
            }
            var wide = Enumerable.Range(0, 200).Cast<object>().ToList();
            var payload = new Dictionary<string, object> { { "deep", deep }, { "wide", wide }, { "text", new string('x', 8192) } };

            var truncated = false;
            var bounded = UnityCliDebugValueBounds.Bound(payload, 4, 64, 2048, out truncated) as IDictionary<string, object>;
            Assert.That(truncated, Is.True);
            Assert.That(bounded, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetByteCount((string)bounded["text"]), Is.LessThanOrEqualTo(2048));
            Assert.That(((IList<object>)bounded["wide"]).Count, Is.EqualTo(64));
            Assert.That(Depth(bounded["deep"]), Is.LessThanOrEqualTo(4));

            var textLimited = false;
            var textBounded = UnityCliDebugValueBounds.Bound(new string('y', 100), 4, 64, 16, out textLimited);
            Assert.That(textLimited, Is.True);
            Assert.That(Encoding.UTF8.GetByteCount((string)textBounded), Is.LessThanOrEqualTo(16));

            Assert.That(UnityCliDebugValueBounds.BoundText("abcdef", 0), Is.Empty);
            Assert.That(UnityCliDebugValueBounds.BoundText("abcdef", 3), Is.EqualTo("abc"));
            Assert.That(UnityCliDebugValueBounds.EstimateBytes("abcd"), Is.EqualTo(6L));
            Assert.That(UnityCliDebugValueBounds.EstimateBytes(null), Is.EqualTo(4L));
        }

        [Test]
        public void StoredProjectionIsFrozenAtEnqueue()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), new UnityCliDebugGlobalBudget(1024L * 1024L));
            var source = new List<object> { "first", "second" };
            var entry = Event("prefix", 1);
            entry.Arguments = source;
            var sequence = buffer.Append(entry);

            source.Clear();
            source.Add("mutated");
            var stored = buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 5).Events.Find(item => item.Sequence == sequence);
            var arguments = stored.Arguments as IList<object>;
            Assert.That(arguments, Is.Not.Null);
            Assert.That(arguments.Select(value => (string)value), Is.EqualTo(new[] { "first", "second" }), "the stored projection must not alias the caller's list");
        }

        [Test]
        public void GlobalBudgetBoundsRetainedBytesAndIsReleasedOnEviction()
        {
            var budget = new UnityCliDebugGlobalBudget(1500L);
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(maxEvents: 64), budget);
            for (var index = 0; index < 12; index++) buffer.Append(BigEvent("prefix", index));
            Assert.That(budget.RetainedBytes, Is.LessThanOrEqualTo(1500L));
            Assert.That(buffer.GlobalRetainedBytes, Is.EqualTo(budget.RetainedBytes));
            Assert.That(buffer.Count, Is.LessThan(12), "the shared byte cap must evict the oldest end");
            Assert.That(buffer.Gaps.Count, Is.GreaterThan(0));

            buffer.Reset();
            Assert.That(budget.RetainedBytes, Is.EqualTo(0L), "reset must release every reserved byte");
            Assert.That(buffer.Count, Is.EqualTo(0L));
        }

        [Test]
        public void CoverageReportsSamplingAndGapBoundsHonestly()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(maxGaps: 2), new UnityCliDebugGlobalBudget(1024L * 1024L));
            buffer.RecordGap(1, 2, "events-checkpoint-missing");
            buffer.RecordGap(3, 4, "events-checkpoint-mismatch");
            buffer.RecordGap(5, 6, "checkpoint-truncated");
            Assert.That(buffer.Gaps.Count, Is.EqualTo(2), "the gap list is bounded");
            Assert.That(buffer.Gaps[0].FromSequence, Is.EqualTo(3));

            var sampled = Event("prefix", 1);
            sampled.Sampled = true;
            sampled.SampleEvery = 8;
            buffer.Append(sampled);
            Assert.That(buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 5).SampleEvery, Is.EqualTo(8));
            Assert.That(buffer.Coverage(), Does.Contain("samplestep 8"));
            Assert.That(buffer.Coverage(), Does.Contain("gaps 2"));
        }

        private static UnityCliDebugEvent Event(string kind, int payload)
        {
            return new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Hook,
                Kind = kind,
                ExecutionPoint = "dispatch:" + kind,
                ClockKind = UnityCliDebugClocks.Monotonic,
                ClockValue = payload,
                Name = kind,
                Value = payload
            };
        }

        private static UnityCliDebugEvent BigEvent(string kind, int payload)
        {
            var entry = Event(kind, payload);
            entry.MethodIdentity = "Fixture." + kind + "(" + new string('m', 256) + ")";
            return entry;
        }

        private static UnityCliDebugEventCapacity Capacity(int maxEvents = 4096, int maxGaps = 32)
        {
            return new UnityCliDebugEventCapacity(maxEvents, 8L * 1024 * 1024, 16 * 1024, 4, 64, 2 * 1024, maxGaps);
        }

        private static int Depth(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                var result = 1;
                foreach (var pair in dictionary) result = System.Math.Max(result, 1 + Depth(pair.Value));
                return result;
            }
            var sequence = value as IList<object>;
            if (sequence != null)
            {
                var result = 1;
                foreach (var item in sequence) result = System.Math.Max(result, 1 + Depth(item));
                return result;
            }
            return 0;
        }
    }
}
