#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// debug 域统一事件存储的行为测试：并发 sequence、容量与缺口、投影边界、cursor 续读、采样与线程时钟规则。
    /// </summary>
    public sealed class DebugEventStoreTests
    {
        [SetUp]
        public void SetUp()
        {
            // 本夹具的多数用例自建 buffer，但时钟与 cursor 用例读的是同一个共享事件窗口：
            // 每个用例都从空窗口开始，才不会被前一个夹具留下的事件或缺口影响。
            DebugEventStore.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            DebugEventStore.Reset();
        }

        [Test]
        public void ConcurrentAppendsAllocateUniqueIncreasingSequencesWithoutLoss()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(maxEvents: 8192), GlobalBudget());
            const int threadCount = 8;
            const int perThread = 200;
            var threads = new List<Thread>();
            for (var index = 0; index < threadCount; index++)
            {
                var thread = new Thread(() =>
                {
                    for (var item = 0; item < perThread; item++) buffer.Append(Event("prefix", item));
                });
                threads.Add(thread);
                thread.Start();
            }
            foreach (var thread in threads) Assert.That(thread.Join(TimeSpan.FromSeconds(60)), Is.True, "concurrent appends must not deadlock");

            Assert.That(buffer.Count, Is.EqualTo(threadCount * perThread), "no append may be lost");
            Assert.That(buffer.LatestSequence, Is.EqualTo(threadCount * perThread), "the sequence space must not skip or repeat");

            var snapshot = buffer.Snapshot();
            Assert.That(snapshot.Count, Is.EqualTo(threadCount * perThread));
            var seen = new HashSet<long>();
            var previous = 0L;
            foreach (var entry in snapshot)
            {
                var sequence = (long)entry["sequence"];
                Assert.That(seen.Add(sequence), Is.True, "every sequence must be unique");
                Assert.That(sequence, Is.GreaterThan(previous), "ring order must match sequence order");
                previous = sequence;
            }
        }

        [Test]
        public void SessionCapacityDropsOldestEventsAndReportsGapsHonestly()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(maxEvents: 10), GlobalBudget());
            for (var index = 0; index < 25; index++) buffer.Append(Event("prefix", index));

            Assert.That(buffer.Count, Is.EqualTo(10));
            Assert.That(buffer.Dropped, Is.EqualTo(15));
            Assert.That(buffer.LatestSequence, Is.EqualTo(25));
            Assert.That(buffer.OldestAvailableSequence, Is.EqualTo(16));
            Assert.That(buffer.OldestAvailableCursor, Is.EqualTo(15), "the readable cursor starts one before the oldest retained event");
            Assert.That(buffer.Truncated, Is.True);
            Assert.That(buffer.Gaps.Count, Is.EqualTo(1));
            Assert.That(buffer.Gaps[0].FromSequence, Is.EqualTo(1));
            Assert.That(buffer.Gaps[0].ToSequence, Is.EqualTo(15));
            Assert.That(buffer.Gaps[0].Reason, Is.EqualTo("event-ring-capacity"));
            Assert.That(buffer.Coverage(), Does.Contain("retained 10 of 25"));
            Assert.That(buffer.Coverage(), Does.Contain("dropped 15"));
        }

        [Test]
        public void SingleStoreHonoursTheGlobalByteCapAndSharesItAcrossStores()
        {
            var budget = new UnityCliDebugGlobalBudget(2000L);
            var capped = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), budget);
            for (var index = 0; index < 10; index++) capped.Append(BigEvent("prefix", index));
            Assert.That(budget.RetainedBytes, Is.LessThanOrEqualTo(2000L), "the process-wide byte cap must hold");
            Assert.That(capped.GlobalRetainedBytes, Is.LessThanOrEqualTo(2000L));
            Assert.That(capped.Count, Is.LessThan(10), "the global cap must trim the oldest end");
            Assert.That(capped.Dropped, Is.GreaterThan(0));

            var shared = new UnityCliDebugGlobalBudget(4000L);
            var first = new UnityCliDebugEventBuffer("session-a", 0L, Capacity(), shared);
            var second = new UnityCliDebugEventBuffer("session-b", 0L, Capacity(), shared);
            for (var index = 0; index < 8; index++) first.Append(BigEvent("prefix", index));
            var seeded = first.Count;
            for (var index = 0; index < 8; index++) second.Append(BigEvent("postfix", index));
            Assert.That(second.Count, Is.GreaterThan(0));
            var peak = shared.RetainedBytes;
            first.Append(BigEvent("prefix", 99));
            Assert.That(shared.RetainedBytes, Is.LessThanOrEqualTo(peak), "an append must give bytes back to the shared process cap");
            Assert.That(first.Count, Is.GreaterThan(0));
            Assert.That(seeded, Is.GreaterThan(0));
        }

        [Test]
        public void AppendMaterialisesABoundedProjectionFrozenAtEnqueue()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), GlobalBudget());
            var wide = new List<object>();
            for (var index = 0; index < DebugLimits.MaxStoredProjectionItemsPerLevel + 32; index++) wide.Add(index);
            var payload = new Dictionary<string, object>
            {
                { "deep", DeepValue(DebugLimits.MaxStoredProjectionDepth + 4) },
                { "wide", wide },
                { "text", new string('x', DebugLimits.MaxStoredProjectionStringBytes * 4) }
            };
            var entry = Event("prefix", 1);
            entry.Fields = payload;
            var sequence = buffer.Append(entry);

            var stored = buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 10).Events.Find(item => item.Sequence == sequence);
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored.ProjectionTruncated, Is.True, "the store bound must be reported");
            var fields = stored.Fields as IDictionary<string, object>;
            Assert.That(fields, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetByteCount((string)fields["text"]), Is.LessThanOrEqualTo(DebugLimits.MaxStoredProjectionStringBytes));
            Assert.That(((IList<object>)fields["wide"]).Count, Is.LessThanOrEqualTo(DebugLimits.MaxStoredProjectionItemsPerLevel));
            Assert.That(Depth(fields["deep"]), Is.LessThanOrEqualTo(DebugLimits.MaxStoredProjectionDepth), "stored depth must stay inside the enqueue bound");
            Assert.That(stored.EstimatedBytes, Is.LessThanOrEqualTo(DebugLimits.MaxStoredEventBytes));

            // 入队后修改来源对象不得影响已入库的物化副本。
            var boundedText = (string)fields["text"];
            payload["text"] = "mutated-after-enqueue";
            wide.Clear();
            var reread = buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 10).Events.Find(item => item.Sequence == sequence);
            Assert.That(((IDictionary<string, object>)reread.Fields)["text"], Is.EqualTo(boundedText), "the stored projection must be frozen at enqueue time");
            Assert.That(((IList<object>)((IDictionary<string, object>)reread.Fields)["wide"]).Count, Is.GreaterThan(0));
        }

        [Test]
        public void AnOversizedEventLosesItsProjectionInsteadOfBeingStoredWhole()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(storedBytes: 1024), GlobalBudget());
            var payload = new Dictionary<string, object>();
            for (var index = 0; index < DebugLimits.MaxStoredProjectionItemsPerLevel; index++)
                payload["key" + index] = new string('y', DebugLimits.MaxStoredProjectionStringBytes);
            var entry = Event("prefix", 1);
            entry.Fields = payload;
            var sequence = buffer.Append(entry);
            var stored = buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 10).Events.Find(item => item.Sequence == sequence);
            Assert.That(stored.Fields, Is.Null, "a projection over the single event cap must be dropped, not partially stored");
            Assert.That(stored.ProjectionTruncated, Is.True);
            Assert.That(stored.Truncated, Is.True);
            Assert.That(stored.EstimatedBytes, Is.LessThanOrEqualTo(1024));

            var tight = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(storedBytes: 400), GlobalBudget());
            var longEntry = Event("prefix", 1);
            longEntry.FaultMessage = new string('z', DebugLimits.MaxStoredProjectionStringBytes);
            var tightSequence = tight.Append(longEntry);
            var tightStored = tight.Read(new UnityCliDebugEventFilter(), null, "session-test", 10).Events.Find(item => item.Sequence == tightSequence);
            Assert.That(Encoding.UTF8.GetByteCount(tightStored.FaultMessage), Is.LessThanOrEqualTo(400 / 16), "fixed string fields must also fit the single event cap");
            Assert.That(tightStored.Truncated, Is.True);
        }

        [Test]
        public void CursorPagingIsExclusiveNonConsumingAndRejectsStaleOrExpiredCursors()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), GlobalBudget());
            for (var index = 0; index < 6; index++) buffer.Append(Event(index % 2 == 0 ? "prefix" : "custom", index));
            var filter = new UnityCliDebugEventFilter { Kind = "prefix" };

            var start = buffer.CursorFor(filter, "session-test", 0L);
            var first = buffer.Read(filter, start, "session-test", 2);
            Assert.That(first.CursorStatus, Is.EqualTo("ok"));
            Assert.That(first.Events.Select(item => item.Sequence), Is.EqualTo(new long[] { 1, 3 }));
            Assert.That(first.HasMore, Is.True);
            Assert.That(first.NextScannedSequence, Is.EqualTo(4));

            var repeated = buffer.Read(filter, start, "session-test", 2);
            Assert.That(repeated.Events.Select(item => item.Sequence), Is.EqualTo(new long[] { 1, 3 }), "reading must not consume the shared cursor");
            Assert.That(buffer.Count, Is.EqualTo(6), "reading must not remove events");

            var next = buffer.CursorFor(filter, "session-test", first.NextScannedSequence);
            var second = buffer.Read(filter, next, "session-test", 2);
            Assert.That(second.Events.Select(item => item.Sequence), Is.EqualTo(new long[] { 5 }), "a filtered cursor must not re-scan older records");
            Assert.That(second.HasMore, Is.False);
            Assert.That(second.NextScannedSequence, Is.EqualTo(6));

            var otherFilter = new UnityCliDebugEventFilter { Kind = "custom" };
            var stale = buffer.Read(otherFilter, next, "session-test", 2);
            Assert.That(stale.CursorStatus, Is.EqualTo("stale"), "a cursor must not survive a filter change");
            var otherSession = buffer.Read(filter, next, "session-other", 2);
            Assert.That(otherSession.CursorStatus, Is.EqualTo("stale"), "a cursor must not survive a session change");

            buffer.Restore(null, 100L, null);
            buffer.Append(Event("prefix", 7));
            var expired = buffer.CursorFor(filter, "session-test", 50L);
            var page = buffer.Read(filter, expired, "session-test", 2);
            Assert.That(page.CursorStatus, Is.EqualTo("expired"));
            Assert.That(page.OldestAvailableCursor, Is.EqualTo(100L));
        }

        [Test]
        public void SampledStreamsAreReportedPerEventAndPerRetainedWindow()
        {
            var buffer = new UnityCliDebugEventBuffer("session-test", 0L, Capacity(), GlobalBudget());
            for (var index = 0; index < 3; index++)
            {
                var sampled = Event("prefix", index);
                sampled.Sampled = true;
                sampled.SampleEvery = 4;
                buffer.Append(sampled);
            }
            var page = buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 10);
            Assert.That(page.Sampled, Is.True);
            Assert.That(page.SampleEvery, Is.EqualTo(4), "a uniform sampling step is reported as-is");
            Assert.That(page.Coverage, Does.Contain("samplestep 4"));
            Assert.That(page.Events.All(item => item.Sampled && item.SampleEvery == 4), Is.True);

            var mixed = Event("custom", 9);
            mixed.Sampled = true;
            mixed.SampleEvery = 10;
            buffer.Append(mixed);
            var mixedPage = buffer.Read(new UnityCliDebugEventFilter(), null, "session-test", 10);
            Assert.That(mixedPage.SampleEvery, Is.EqualTo(0), "mixed sampling steps must not be reported as one step");
            Assert.That(mixedPage.Coverage, Does.Contain("samplestep 0"));
        }

        [Test]
        public void TheStoreRecordsOnlyProvenThreadIdentityAndClockProvenance()
        {
            DebugEventStore.ReadEvents(null); // 主线程路径绑定主线程身份与帧缓存

            var mainEntry = new UnityCliDebugEvent { SourceId = UnityCliDebugSources.Hook, Kind = "clock-rule-main", ExecutionPoint = "test:main", ClockKind = UnityCliDebugClocks.UnityFrame };
            DebugEventStore.Append(mainEntry);
            Assert.That(mainEntry.MainThread, Is.True);
            Assert.That(mainEntry.ThreadId, Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
            Assert.That(mainEntry.ClockKind, Is.EqualTo(UnityCliDebugClocks.UnityFrame));
            Assert.That(mainEntry.Frame, Is.EqualTo(DebugEventClocks.ReadUnityFrame()), "a main-thread event must record the real Unity frame");
            Assert.That(mainEntry.ClockValue, Is.EqualTo((double)mainEntry.Frame));
            Assert.That(mainEntry.LastObservedMainFrame, Is.EqualTo(0L));

            var offMain = new UnityCliDebugEvent { SourceId = UnityCliDebugSources.Hook, Kind = "clock-rule-off-main", ExecutionPoint = "test:off-main", ClockKind = UnityCliDebugClocks.UnityFrame, Frame = 12345L };
            var worker = new Thread(() => DebugEventStore.Append(offMain));
            worker.Start();
            Assert.That(worker.Join(TimeSpan.FromSeconds(30)), Is.True);
            Assert.That(offMain.MainThread, Is.False);
            Assert.That(offMain.ThreadId, Is.EqualTo(worker.ManagedThreadId));
            Assert.That(offMain.ClockKind, Is.EqualTo(UnityCliDebugClocks.Monotonic), "an off-main event may only use the monotonic clock");
            Assert.That(offMain.Frame, Is.EqualTo(0L), "an off-main event must not carry a frame read from a worker thread");
            Assert.That(offMain.LastObservedMainFrame, Is.EqualTo(mainEntry.Frame), "an off-main event may only reference the last observed main frame");
            Assert.That(offMain.ClockValue, Is.GreaterThan(0d));

            var tickEntry = new UnityCliDebugEvent { Kind = "clock-rule-tick", ExecutionPoint = "test:tick", ClockKind = UnityCliDebugClocks.EditorTick, ClockValue = 77d };
            DebugEventStore.Append(tickEntry);
            Assert.That(tickEntry.ClockKind, Is.EqualTo(UnityCliDebugClocks.EditorTick));
            Assert.That(tickEntry.ClockValue, Is.EqualTo(77d));

            var logicalEntry = new UnityCliDebugEvent { Kind = "clock-rule-logical", ExecutionPoint = "test:logical", ClockKind = UnityCliDebugClocks.ProjectLogical, ClockValue = 5d };
            DebugEventStore.Append(logicalEntry);
            Assert.That(logicalEntry.ClockKind, Is.EqualTo(UnityCliDebugClocks.ProjectLogical));
            Assert.That(logicalEntry.ClockValue, Is.EqualTo(5d));
        }

        [Test]
        public void ReadEventsMapsCursorFailureToTheStableCodesAndNeverRemovesEvents()
        {
            DebugEventStore.ReadEvents(null);
            var session = UnityCliBridge.EditorSessionId();
            DebugEventStore.Append(new UnityCliDebugEvent { SourceId = UnityCliDebugSources.Hook, Kind = "error-code-rule", ExecutionPoint = "test:error", HookId = "hook-code" });

            var first = DebugEventStore.ReadEvents(new DebugEventsRequest { Kind = "error-code-rule" });
            Assert.That(first.Events.Count, Is.GreaterThan(0));
            Assert.That(first.Events.All(item => item.Kind == "error-code-rule"), Is.True);
            Assert.That(first.EditorSessionId, Is.EqualTo(session));
            Assert.That(first.OldestAvailableSequence, Is.LessThanOrEqualTo(first.LatestSequence));
            Assert.That(first.Coverage, Is.Not.Empty);
            Assert.That(first.NextCursor, Is.Not.Null);

            var second = DebugEventStore.ReadEvents(new DebugEventsRequest { Kind = "error-code-rule" });
            Assert.That(second.Events.Count, Is.EqualTo(first.Events.Count), "reading must not remove events");

            var scope = UnityCliDebugEventBuffer.ScopeKey(new UnityCliDebugEventFilter { Kind = "error-code-rule" }, session);
            var staleCursor = UnityCliContinuationToken.Create(scope, session, "0");
            var stale = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugEventStore.ReadEvents(new DebugEventsRequest { Kind = "other-kind", Cursor = staleCursor }));
            Assert.That(stale.Code, Is.EqualTo("E_CONTINUATION_STALE"));

            var previousFloor = DebugEventStore.OldestAvailableSequence;
            var previousEntries = DebugEventStore.Snapshot();
            try
            {
                DebugEventStore.Restore(null, 500L, null);
                DebugEventStore.Append(new UnityCliDebugEvent { SourceId = UnityCliDebugSources.Hook, Kind = "error-code-rule", ExecutionPoint = "test:error", HookId = "hook-code" });
                var expiredCursor = UnityCliContinuationToken.Create(scope, session, "400");
                var expired = Assert.Throws<UnityCliBridge.BridgeCommandException>(() => DebugEventStore.ReadEvents(new DebugEventsRequest { Kind = "error-code-rule", Cursor = expiredCursor }));
                Assert.That(expired.Code, Is.EqualTo("E_HOOK_EVENT_CURSOR_EXPIRED"));
                var details = expired.Details as IDictionary<string, object>;
                Assert.That(details, Is.Not.Null);
                Assert.That(details.ContainsKey("oldestAvailableCursor"), Is.True);
                Assert.That(Convert.ToInt64(details["oldestAvailableCursor"]), Is.EqualTo(500L));
            }
            finally
            {
                DebugEventStore.Restore(previousEntries, previousFloor, null);
            }
        }

        [Test]
        public void AReportedAppendFailureIsCountedAndReadableInsteadOfSilent()
        {
            DebugEventStore.ReadEvents(null);
            var before = DebugEventStore.ReadEvents(new DebugEventsRequest()).AppendFailures;
            DebugEventStore.ReportAppendFailure(
                new UnityCliDebugEvent
                {
                    SourceId = UnityCliDebugSources.Hook,
                    Kind = "emit",
                    Name = "lost-emit",
                    FaultCode = "E_TEST_APPEND"
                },
                "TestAppendFailure");
            var after = DebugEventStore.ReadEvents(new DebugEventsRequest { Kind = "coverage" });
            Assert.That(after.AppendFailures, Is.EqualTo(before + 1L), "a reported append failure must be counted and readable through the single read path");

            // 计数必须可归因：同一次读必须能看到说明丢失事件的 coverage 事实，
            // “计数 +1 但找不到任何对应事实”是不允许的状态。
            DebugEventItem attributed = null;
            foreach (var item in after.Events)
            {
                if (!string.Equals(item.Name, DebugEventStore.CoverageEventName, StringComparison.Ordinal)) continue;
                var value = item.Value as IDictionary<string, object>;
                if (value == null || !value.ContainsKey("lostName")) continue;
                if (!string.Equals(Convert.ToString(value["lostName"], CultureInfo.InvariantCulture), "lost-emit", StringComparison.Ordinal)) continue;
                attributed = item;
                break;
            }
            Assert.That(attributed, Is.Not.Null, "a counted append failure must be attributable by its not-recorded coverage fact in the same window");
            var facts = attributed.Value as IDictionary<string, object>;
            Assert.That(Convert.ToString(facts["lostKind"], CultureInfo.InvariantCulture), Is.EqualTo("emit"));
            Assert.That(Convert.ToString(facts["faultCode"], CultureInfo.InvariantCulture), Is.EqualTo("E_TEST_APPEND"));
            Assert.That(Convert.ToString(facts["reason"], CultureInfo.InvariantCulture), Is.EqualTo("TestAppendFailure"));
        }

        [Test]
        public void PageSizeAndWaitBoundsComeFromTheSingleLimitsOwner()
        {
            Assert.That(DebugEventStore.PageSize(null), Is.EqualTo(DebugLimits.EventsDefaultLimit));
            Assert.That(DebugEventStore.PageSize(int.MaxValue), Is.EqualTo(DebugLimits.EventsMaxLimit));
            Assert.That(DebugEventStore.PageSize(0), Is.EqualTo(1));
            Assert.That(DebugEventStore.WaitMilliseconds(null), Is.EqualTo(0));
            Assert.That(DebugEventStore.WaitMilliseconds(int.MaxValue), Is.EqualTo(DebugLimits.EventsMaxWaitMs));
            Assert.That(DebugLimits.EventsMaxLimit, Is.GreaterThanOrEqualTo(DebugLimits.EventsDefaultLimit));
            Assert.That(DebugLimits.EventPageGetters(DebugLimits.EventsMaxLimit), Is.LessThanOrEqualTo(UnityCliRequestBudget.AbsoluteMaxGetters));
            Assert.That(DebugLimits.MaxStoredProjectionStringBytes, Is.LessThan(DebugLimits.MaxProjectionStringBytes));
            Assert.That(DebugLimits.MaxStoredProjectionDepth, Is.LessThan(DebugLimits.MaxProjectionDepth));
            Assert.That(DebugLimits.MaxStoredProjectionItemsPerLevel, Is.LessThan(DebugLimits.MaxProjectionItems));
            Assert.That(DebugLimits.MaxStoredEventBytes, Is.LessThan(DebugLimits.MaxEventBytes));
        }

        private static UnityCliDebugEvent Event(string kind, int payload)
        {
            return new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Hook,
                Kind = kind,
                HookId = "hook-test",
                Revision = 1,
                ExecutionPoint = "dispatch:" + kind,
                PlaySessionId = "play-1",
                ClockSegment = "play-1:seg-1",
                ClockKind = UnityCliDebugClocks.Monotonic,
                ClockValue = payload,
                Name = kind,
                Value = payload
            };
        }

        private static UnityCliDebugEvent BigEvent(string kind, int payload)
        {
            var entry = Event(kind, payload);
            entry.MethodIdentity = "Fixture." + kind + "(" + new string('m', 512) + ")";
            entry.FaultMessage = new string('f', 512);
            return entry;
        }

        private static Dictionary<string, object> DeepValue(int depth)
        {
            var root = new Dictionary<string, object>();
            var cursor = root;
            for (var index = 0; index < depth; index++)
            {
                var child = new Dictionary<string, object>();
                cursor["level" + index] = child;
                cursor = child;
            }
            cursor["leaf"] = "leaf";
            return root;
        }

        private static int Depth(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                var result = 1;
                foreach (var pair in dictionary) result = Math.Max(result, 1 + Depth(pair.Value));
                return result;
            }
            var sequence = value as IList<object>;
            if (sequence != null)
            {
                var result = 1;
                foreach (var item in sequence) result = Math.Max(result, 1 + Depth(item));
                return result;
            }
            return 0;
        }

        private static UnityCliDebugEventCapacity Capacity(int maxEvents = 4096, long sessionBytes = 8L * 1024 * 1024, int storedBytes = DebugLimits.MaxStoredEventBytes)
        {
            return new UnityCliDebugEventCapacity(
                maxEvents,
                sessionBytes,
                storedBytes,
                DebugLimits.MaxStoredProjectionDepth,
                DebugLimits.MaxStoredProjectionItemsPerLevel,
                DebugLimits.MaxStoredProjectionStringBytes,
                DebugLimits.MaxHistoryGaps);
        }

        private static UnityCliDebugGlobalBudget GlobalBudget(long maxBytes = 64L * 1024 * 1024)
        {
            return new UnityCliDebugGlobalBudget(maxBytes);
        }
    }
}
#endif
