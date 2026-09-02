using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace UnityCli.Tests
{
    public sealed class UnityCliRequestBudgetTests
    {
        [Test]
        public void SparseHundredThousandConsoleShapeStopsBeforeMaterialization()
        {
            var clock = new ManualClock();
            var budget = Budget(clock, scans: 1000);
            var matches = 0;
            for (var index = 99999; index >= 0; index--)
            {
                if (!budget.TryConsumeScannedRecord()) break;
                if (index % 10000 == 0) matches++;
            }
            Assert.That(budget.Usage().ScannedRecords, Is.EqualTo(1000));
            Assert.That(matches, Is.EqualTo(0));
            Assert.That(budget.ExhaustedDimension, Is.EqualTo("scannedRecords"));
        }

        [Test]
        public void ManualMonotonicClockAndCancellationShareEveryCounter()
        {
            var clock = new ManualClock();
            var cancellation = new CancellationTokenSource();
            var budget = new UnityCliRequestBudget(new UnityCliReadBudgetInput { MaxMainThreadMilliseconds = 5 }, 1000, cancellation.Token, clock);
            Assert.That(budget.TryConsumeNode(), Is.True);
            clock.Advance(5);
            Assert.That(budget.TryConsumeGetter(), Is.False);
            Assert.That(budget.ExhaustedDimension, Is.EqualTo("mainThreadMilliseconds"));

            var cancelled = new UnityCliRequestBudget(null, 1000, cancellation.Token, clock);
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => cancelled.TryConsumeReflectionCall());
        }

        [Test]
        public void CyclicDeepWideEnumerableAndMultiplePathsUseOneSerializerBudget()
        {
            var cycle = new Node { Name = "root" };
            cycle.Next = cycle;
            var cycleJson = UnityCliSerializer.Serialize(UnityCliBridge.ObjectSerializer.Serialize(cycle,
                new UnityCliBridge.ObjectSerializer.Options { MaxDepth = 16, MaxArrayItems = 1000 }, Budget(new ManualClock(), nodes: 32)));
            StringAssert.Contains("cycle-detected", cycleJson);

            var enumerable = new CountingEnumerable(100000);
            var enumerableBudget = Budget(new ManualClock(), scans: 17, nodes: 64);
            var enumerableJson = UnityCliSerializer.Serialize(UnityCliBridge.ObjectSerializer.Serialize(enumerable,
                new UnityCliBridge.ObjectSerializer.Options { MaxDepth = 4, MaxArrayItems = 1000 }, enumerableBudget));
            Assert.That(enumerable.MoveNextCount, Is.EqualTo(17));
            StringAssert.Contains("nextPath", enumerableJson);

            var multi = new GetterFixture();
            var pathBudget = Budget(new ManualClock(), getters: 1, reflections: 32, nodes: 32);
            var pathJson = UnityCliSerializer.Serialize(UnityCliBridge.ObjectSerializer.Serialize(multi,
                new UnityCliBridge.ObjectSerializer.Options { Paths = new List<string> { "First", "Second" }, MaxDepth = 2 }, pathBudget));
            Assert.That(multi.GetterCount, Is.EqualTo(1));
            StringAssert.Contains("request-budget-exhausted", pathJson);
        }

        [Test]
        public void OversizedAndRepeatedCapturePlansFailBeforeTextureAllocation()
        {
            var requestBudget = Budget(new ManualClock());
            Assert.Throws<UnityCliBridge.BridgeCommandException>(() => ScreenshotToolSupport.PrepareCaptureBudget(8192, 8192, 1, false, null, requestBudget));
            var captures = ScreenshotToolSupport.PrepareCaptureBudget(512, 512, 2, false, null, Budget(new ManualClock()));
            captures.BeginCapture(512, 512);
            captures.BeginCapture(512, 512);
            Assert.Throws<UnityCliBridge.BridgeCommandException>(() => captures.BeginCapture(512, 512));
            Assert.That(captures.Plan.PeakTemporaryBytes, Is.LessThanOrEqualTo(ScreenshotToolSupport.DefaultMaxTemporaryBytes));
        }

        [Test]
        public void HighBranchProfilerAndLargeSceneShapesStopAtGlobalNodeBudget()
        {
            var profiler = SyntheticTree.Wide(depth: 2, width: 100);
            var profilerBudget = Budget(new ManualClock(), scans: 250, nodes: 200);
            Assert.That(Traverse(profiler, profilerBudget), Is.EqualTo(200));
            Assert.That(profilerBudget.ExhaustedDimension, Is.EqualTo("nodes"));

            var scene = SyntheticTree.Wide(depth: 4, width: 12);
            var sceneBudget = Budget(new ManualClock(), scans: 101, nodes: 101);
            Assert.That(Traverse(scene, sceneBudget), Is.EqualTo(101));
            Assert.That(sceneBudget.Usage().ScannedRecords, Is.EqualTo(101));
        }

        [Test]
        public void SnapshotRetentionKeepsLatestHundredAndHonorsBytePressure()
        {
            var sizes = Enumerable.Repeat(10L, 101).ToArray();
            var byCount = UnityCliRetentionPlanner.RetainedIndices(sizes, 100, 10000);
            Assert.That(byCount.Count, Is.EqualTo(100));
            Assert.That(byCount.Contains(100), Is.False);
            var byBytes = UnityCliRetentionPlanner.RetainedIndices(sizes, 100, 35);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, byBytes.OrderBy(value => value).ToArray());
        }

        [Test]
        public void BroadReflectionReturnsStableContinuationWithinCallBudget()
        {
            var budget = Budget(new ManualClock(), scans: 8, nodes: 64, reflections: 128);
            var page = UnityCliBridge.ReflectionInvoker.FindMethods("System.String", null,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, 100, null, budget);
            Assert.That(page.Methods.Count, Is.LessThanOrEqualTo(8));
            Assert.That(page.Truncated, Is.True);
            Assert.That(page.NextCursor, Is.Not.Null.And.Not.Empty);
            string position;
            Assert.That(UnityCliContinuationToken.TryParse(page.NextCursor, "reflection.method.find", page.Generation, out position), Is.True);
        }

        [Test]
        public void CallbackTerminalAcceptsExactlyOneWinnerAndLateCallbacksAreRejected()
        {
            var terminal = new MemorySnapshotTerminal();
            Assert.That(terminal.TryAbandon(), Is.True);
            Assert.That(terminal.TryOwnCallback(), Is.False);
            Assert.That(terminal.IsAbandoned, Is.True);
        }

        private static int Traverse(SyntheticTree root, UnityCliRequestBudget budget)
        {
            var count = 0;
            var stack = new Stack<SyntheticTree>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeNode()) break;
                var current = stack.Pop();
                count++;
                for (var index = current.Children.Count - 1; index >= 0; index--) stack.Push(current.Children[index]);
            }
            return count;
        }

        private static UnityCliRequestBudget Budget(ManualClock clock, long scans = 10000, long nodes = 4096, long getters = 2048, long reflections = 4096)
        {
            return new UnityCliRequestBudget(new UnityCliReadBudgetInput
            {
                MaxScannedRecords = scans,
                MaxNodes = nodes,
                MaxGetters = getters,
                MaxReflectionCalls = reflections,
                MaxMainThreadMilliseconds = 2000,
                MaxSerializedUtf8Bytes = 8 * 1024 * 1024
            }, 30000, CancellationToken.None, clock);
        }

        private sealed class ManualClock : IUnityCliMonotonicClock
        {
            public long ElapsedMilliseconds { get; private set; }
            public void Advance(long milliseconds) { ElapsedMilliseconds += milliseconds; }
        }

        private sealed class Node { public string Name { get; set; } public Node Next { get; set; } }

        private sealed class GetterFixture
        {
            public int GetterCount;
            public string First { get { GetterCount++; return "first"; } }
            public string Second { get { GetterCount++; return "second"; } }
        }

        private sealed class CountingEnumerable : IEnumerable
        {
            private readonly int count;
            internal CountingEnumerable(int count) { this.count = count; }
            internal int MoveNextCount { get; private set; }
            public IEnumerator GetEnumerator() { return new Counter(this); }

            private sealed class Counter : IEnumerator
            {
                private readonly CountingEnumerable owner;
                private int index = -1;
                internal Counter(CountingEnumerable owner) { this.owner = owner; }
                public object Current { get { return index; } }
                public bool MoveNext() { owner.MoveNextCount++; index++; return index < owner.count; }
                public void Reset() { index = -1; }
            }
        }

        private sealed class SyntheticTree
        {
            internal readonly List<SyntheticTree> Children = new List<SyntheticTree>();
            internal static SyntheticTree Wide(int depth, int width)
            {
                var root = new SyntheticTree();
                if (depth == 0) return root;
                for (var index = 0; index < width; index++) root.Children.Add(Wide(depth - 1, width));
                return root;
            }
        }
    }
}
