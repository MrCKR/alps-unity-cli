#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>
    /// ISSUE-108：观察读取的筛选、续读 scope 与预算降级语义。
    ///
    /// 这些用例直接调用 store 与 EditorToolSupport：筛选发生在分页之前，continuation scope 也由读取层负责，
    /// 只在 registry 层断言无法覆盖“游标是否绑定同一次查询”。
    /// 事件用例用唯一身份前缀隔离共享存储，避免与其它用例的历史事件互相干扰。
    /// </summary>
    public sealed class DebugReadFilterTests
    {
        private string prefix;

        /// <summary>每个用例使用独立前缀：事件存储跨用例与跨运行保留，共用前缀会让断言读到别的运行留下的事件。</summary>
        [SetUp]
        public void CreatePrefix()
        {
            prefix = "issue108:" + Guid.NewGuid().ToString("N").Substring(0, 8) + ":";
        }

        private UnityCliDebugEvent AppendTagged(string identity, string executionPoint, long invocationId)
        {
            var entry = new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Hook,
                Kind = "hit",
                ExecutionPoint = executionPoint,
                MethodIdentity = prefix + identity,
                InvocationId = invocationId,
                ClockKind = UnityCliDebugClocks.Monotonic
            };
            DebugEventStore.Append(entry);
            return entry;
        }

        private List<long> ReadSequences(DebugEventsRequest request)
        {
            request.MethodIdentityPrefix = request.MethodIdentityPrefix ?? prefix;
            request.Limit = DebugLimits.EventsMaxLimit;
            return DebugEventStore.ReadEvents(request).Events.Select(item => item.Sequence).ToList();
        }

        [Test]
        public void SequenceRangeSelectsAnInclusiveWindowAndRejectsAnInvertedRange()
        {
            var first = AppendTagged("seq:a", "point:a", 0L);
            var second = AppendTagged("seq:b", "point:b", 0L);
            var third = AppendTagged("seq:c", "point:c", 0L);

            Assert.That(ReadSequences(new DebugEventsRequest { FromSequence = second.Sequence, ToSequence = third.Sequence }),
                Is.EqualTo(new List<long> { second.Sequence, third.Sequence }),
                "the sequence range must be inclusive on both ends and must not leak older matches");

            var error = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                DebugEventStore.ReadEvents(new DebugEventsRequest { FromSequence = third.Sequence, ToSequence = first.Sequence }));
            Assert.That(error.Code, Is.EqualTo("E_SCHEMA_INVALID"), "an inverted sequence range must fail before scanning");
        }

        [Test]
        public void InvocationAndExecutionPointNarrowTheRead()
        {
            var one = AppendTagged("inv:a", "point:one", 4201L);
            var two = AppendTagged("inv:b", "point:two", 4202L);

            Assert.That(ReadSequences(new DebugEventsRequest { InvocationId = 4201L }),
                Is.EqualTo(new List<long> { one.Sequence }), "an invocation filter must select exactly that call");
            Assert.That(ReadSequences(new DebugEventsRequest { ExecutionPoint = "point:two" }),
                Is.EqualTo(new List<long> { two.Sequence }), "an execution point filter must be an exact match");
        }

        [Test]
        public void MethodIdentityPrefixSelectsOneProducersEventsOnly()
        {
            AppendTagged("prog-a:step", "point:a", 0L);
            var otherPrefix = prefix + "prog-b:";
            var entry = new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Hook,
                Kind = "hit",
                ExecutionPoint = "point:b",
                MethodIdentity = otherPrefix + "step",
                ClockKind = UnityCliDebugClocks.Monotonic
            };
            DebugEventStore.Append(entry);

            var page = DebugEventStore.ReadEvents(new DebugEventsRequest
            {
                MethodIdentityPrefix = prefix + "prog-a:",
                Limit = DebugLimits.EventsMaxLimit
            });
            Assert.That(page.Events.Count, Is.EqualTo(1), "a prefix filter must not match a neighbour with a longer shared prefix");
            Assert.That(page.Events[0].MethodIdentity, Is.EqualTo(prefix + "prog-a:step"));
        }

        [Test]
        public void MainThreadFilterSeparatesMainAndWorkerEvents()
        {
            var main = AppendTagged("thread:main", "point:main", 0L);
            var workerEntry = new UnityCliDebugEvent
            {
                SourceId = UnityCliDebugSources.Hook,
                Kind = "hit",
                ExecutionPoint = "point:worker",
                MethodIdentity = prefix + "thread:worker",
                ClockKind = UnityCliDebugClocks.Monotonic
            };
            var worker = new Thread(() => DebugEventStore.Append(workerEntry));
            worker.Start();
            Assert.That(worker.Join(TimeSpan.FromSeconds(30)), Is.True);

            Assert.That(ReadSequences(new DebugEventsRequest { MainThread = true }), Is.EqualTo(new List<long> { main.Sequence }),
                "mainThread=true must read only main-thread events");
            Assert.That(ReadSequences(new DebugEventsRequest { MainThread = false }), Is.EqualTo(new List<long> { workerEntry.Sequence }),
                "mainThread=false must read only worker-thread events");
        }

        [Test]
        public void ChangingFiltersInvalidatesTheReturnedContinuation()
        {
            AppendTagged("cursor:a", "point:a", 0L);
            AppendTagged("cursor:b", "point:b", 0L);

            var firstPage = DebugEventStore.ReadEvents(new DebugEventsRequest { MethodIdentityPrefix = prefix, Limit = 1 });
            Assert.That(firstPage.Events.Count, Is.EqualTo(1), "limit 1 must return one matching event");
            Assert.That(firstPage.NextCursor, Is.Not.Null, "a formed page must expose a continuation for its own scope");

            var error = Assert.Throws<UnityCliBridge.BridgeCommandException>(() =>
                DebugEventStore.ReadEvents(new DebugEventsRequest
                {
                    MethodIdentityPrefix = prefix,
                    ExecutionPoint = "point:b",
                    Cursor = firstPage.NextCursor
                }));
            Assert.That(error.Code, Is.EqualTo("E_CONTINUATION_STALE"),
                "a continuation must be bound to the filters that produced it");

            var sameScope = DebugEventStore.ReadEvents(new DebugEventsRequest
            {
                MethodIdentityPrefix = prefix,
                Limit = 1,
                Cursor = firstPage.NextCursor
            });
            // 同 scope 不抛 E_CONTINUATION_STALE 即为接受该 continuation。
            Assert.That(sameScope, Is.Not.Null, "the same filter scope must still accept its own continuation");
        }

        [Test]
        public void ConsolePageReportsScanExhaustionInsteadOfFailing()
        {
            // 每条记录消费一个 scannedRecord；上限 3 让扫描在取到任何匹配前耗尽。
            var budget = new UnityCliRequestBudget(
                new UnityCliReadBudgetInput { MaxScannedRecords = 3L, MaxGetters = 64L, MaxReflectionCalls = 64L, MaxSerializedUtf8Bytes = 8192L },
                5000L,
                CancellationToken.None);
            var snapshot = new NeverMatchingConsole(50);

            var page = EditorToolSupport.ReadConsolePage("error", "no-such-text", 10, budget, snapshot);

            Assert.That(page.Items, Is.Empty, "no entry matches the filter");
            Assert.That(page.ScanExhausted, Is.True, "an exhausted scan must be reported as a fact, not as a failure");
            Assert.That(page.Truncated, Is.True, "an exhausted scan is not a complete match set");
            Assert.That(page.ScannedEntries, Is.GreaterThan(0L), "the caller needs to know how far the scan reached");
            Assert.That(page.ScannedEntries, Is.LessThan(snapshot.Count), "the scan must stop before the whole snapshot when the budget is spent");
            Assert.That(page.RawEntryCount, Is.EqualTo((long)snapshot.Count));
            Assert.That(page.Note, Does.Contain("budget"), "the note must name the cause the caller can act on");
        }

        /// <summary>永远不匹配的 Console 适配器：每条记录只消费一个 scannedRecord。</summary>
        private sealed class NeverMatchingConsole : EditorToolSupport.IConsoleSnapshotAdapter
        {
            public NeverMatchingConsole(int count) { Count = count; }

            public int Count { get; private set; }

            public bool TryRead(int rawIndex, UnityCliRequestBudget budget, out ConsoleEntry result)
            {
                // 每条记录消费一个 scannedRecord，并返回一条不匹配 filter 的条目，让扫描在预算耗尽时停下。
                if (!budget.TryConsumeScannedRecord())
                {
                    result = null;
                    return false;
                }
                result = new ConsoleEntry { RawIndex = rawIndex, Type = "log", Message = "unmatched", File = string.Empty, Count = 1L };
                return true;
            }
        }
    }
}
#endif

