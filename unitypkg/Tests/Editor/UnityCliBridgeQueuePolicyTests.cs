using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnityCli.Tests
{
    /// <summary>验证BridgeWorkQueue复用的纯分类、容量与启动选择策略。</summary>
    public sealed class UnityCliBridgeQueuePolicyTests
    {
        private const string SafeStatusCommand = "internal.editor.compile.status";

        [Test]
        public void RunningMutationBlocksPendingMutationAndLaterReadsButSafeStatusPassesWithinBounds()
        {
            var queued = new[]
            {
                Node("mutation-b", UnityCliBridgeQueuePolicy.WorkClass.Mutation),
                Node("later-read", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead),
                Safe("status-0"), Safe("status-1"), Safe("status-2"),
                Safe("status-3"), Safe("status-4"), Safe("status-5")
            };

            var result = UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, true, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);

            Assert.That(Ids(result), Is.EqualTo(new[] { "status-0", "status-1", "status-2", "status-3" }));
            Assert.That(result.Starts.Select(item => item.QueueIndex), Is.EqualTo(new[] { 2, 3, 4, 5 }));
            Assert.That(result.Starts.Count, Is.LessThanOrEqualTo(UnityCliBridgeQueuePolicy.MaxStartsPerTick));
            Assert.That(result.Starts.Count(item => item.Class == UnityCliBridgeQueuePolicy.WorkClass.SafeStatus),
                Is.EqualTo(UnityCliBridgeQueuePolicy.MaxSafeStatusStartsPerTick));
            Assert.That(queued.Select(node => node.NodeId), Is.EqualTo(new[]
            {
                "mutation-b", "later-read", "status-0", "status-1", "status-2", "status-3", "status-4", "status-5"
            }));
        }

        [Test]
        public void ReadsBeforeMutationDrainBeforeItStartsAndLaterReadsStayBehind()
        {
            var readA = Node("read-a", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);
            var readB = Node("read-b", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);
            var mutation = Node("mutation-b", UnityCliBridgeQueuePolicy.WorkClass.Mutation);
            var laterRead = Node("later-read", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);

            var drain = UnityCliBridgeQueuePolicy.SelectStarts(
                new[] { readA, readB, mutation, laterRead }, 0, false,
                UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);
            Assert.That(Ids(drain), Is.EqualTo(new[] { "read-a", "read-b" }));

            var whileReadsRun = UnityCliBridgeQueuePolicy.SelectStarts(
                new[] { mutation, laterRead }, 2, false,
                UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);
            Assert.That(whileReadsRun.Starts, Is.Empty);

            var mutationStarts = UnityCliBridgeQueuePolicy.SelectStarts(
                new[] { mutation, laterRead }, 0, false,
                UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);
            Assert.That(Ids(mutationStarts), Is.EqualTo(new[] { "mutation-b" }));
            Assert.That(mutationStarts.Starts.Single().QueueIndex, Is.EqualTo(0));
        }

        [Test]
        public void ContinuousSafeStatusCannotStarveEligibleMutationAndAllTickBoundsHold()
        {
            var queued = Enumerable.Range(0, 20)
                .Select(index => Safe("status-" + index.ToString()))
                .Concat(new[] { Node("mutation-b", UnityCliBridgeQueuePolicy.WorkClass.Mutation) })
                .ToArray();

            var result = UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, false, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);

            Assert.That(Ids(result), Is.EqualTo(new[] { "status-0", "status-1", "status-2", "status-3", "mutation-b" }));
            Assert.That(result.Starts.Count, Is.LessThanOrEqualTo(UnityCliBridgeQueuePolicy.MaxStartsPerTick));
            Assert.That(result.Starts.Count(item => item.Class == UnityCliBridgeQueuePolicy.WorkClass.SafeStatus),
                Is.EqualTo(UnityCliBridgeQueuePolicy.MaxSafeStatusStartsPerTick));
            Assert.That(result.NodesExamined, Is.EqualTo(queued.Length));

            var nextTick = UnityCliBridgeQueuePolicy.SelectStarts(
                Enumerable.Range(20, 20).Select(index => Safe("status-" + index.ToString())).ToArray(),
                0, true, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);
            Assert.That(nextTick.Starts.Count, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxSafeStatusStartsPerTick));
            Assert.That(nextTick.Starts.All(item => item.Class == UnityCliBridgeQueuePolicy.WorkClass.SafeStatus), Is.True);

            var saturated = UnityCliBridgeQueuePolicy.SelectStarts(
                Enumerable.Range(40, 20).Select(index => Safe("status-" + index.ToString()))
                    .Concat(Enumerable.Range(0, 20).Select(index => Node("read-" + index.ToString(), UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead)))
                    .ToArray(),
                0, false, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);
            Assert.That(saturated.Starts.Count, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxStartsPerTick));
            Assert.That(saturated.Starts.Count(item => item.Class == UnityCliBridgeQueuePolicy.WorkClass.SafeStatus),
                Is.EqualTo(UnityCliBridgeQueuePolicy.MaxSafeStatusStartsPerTick));
        }

        [Test]
        public void New129thBypassIdentityIsRejectedWhileExactAttachRemainsAllowed()
        {
            var identities = Enumerable.Range(0, UnityCliBridgeQueuePolicy.MaxBypassIdentities)
                .Select(index => Identity(index, index))
                .ToArray();
            var originalOrder = identities.Select(identity => identity.RequestId).ToArray();

            var rejected = UnityCliBridgeQueuePolicy.DecideBypassAdmission(
                identities, Identity(UnityCliBridgeQueuePolicy.MaxBypassIdentities, UnityCliBridgeQueuePolicy.MaxBypassIdentities));
            Assert.That(rejected.Outcome, Is.EqualTo(UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.RejectCapacity));
            Assert.That(rejected.ExistingIndex, Is.EqualTo(-1));

            var attached = UnityCliBridgeQueuePolicy.DecideBypassAdmission(
                identities, Identity(UnityCliBridgeQueuePolicy.MaxBypassIdentities - 1, UnityCliBridgeQueuePolicy.MaxBypassIdentities - 1));
            Assert.That(attached.Outcome, Is.EqualTo(UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.Attach));
            Assert.That(attached.ExistingIndex, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxBypassIdentities - 1));

            var mismatch = UnityCliBridgeQueuePolicy.DecideBypassAdmission(
                identities, Identity(UnityCliBridgeQueuePolicy.MaxBypassIdentities - 1, 999));
            Assert.That(mismatch.Outcome, Is.EqualTo(UnityCliBridgeQueuePolicy.BypassAdmissionOutcome.RejectIdentityMismatch));
            Assert.That(mismatch.ExistingIndex, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxBypassIdentities - 1));
            Assert.That(identities.Select(identity => identity.RequestId), Is.EqualTo(originalOrder));
        }

        [Test]
        public void NodeScanBoundCountsActualIndexerVisits()
        {
            var queued = new CountingQueue(Enumerable.Range(0, UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick - 1)
                .Select(index => Node("read-" + index.ToString(), UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead))
                .Concat(new[] { Node("mutation-last", UnityCliBridgeQueuePolicy.WorkClass.Mutation) })
                .ToArray());

            var result = UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, false, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);

            Assert.That(queued.IndexerVisits, Is.LessThanOrEqualTo(UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick));
            Assert.That(queued.IndexerVisits, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick));
            Assert.That(result.NodesExamined, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick));
            Assert.That(result.Starts.Count, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxStartsPerTick));
        }

        [Test]
        public void EligibleNodeBeyondScanBoundDoesNotStart()
        {
            var queued = Enumerable.Range(0, UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick)
                .Select(index => Node("read-" + index.ToString(), UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead))
                .Concat(new[] { Safe("eligible-beyond-bound") })
                .ToArray();

            var result = UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, false, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Held);

            Assert.That(result.NodesExamined, Is.EqualTo(UnityCliBridgeQueuePolicy.MaxNodesExaminedPerTick));
            Assert.That(result.Starts, Is.Empty);
        }

        [Test]
        public void UnreadableDurableBarrierBlocksMutationAndOrdinaryReadsButOnlyExactSafeStatusPasses()
        {
            var queued = new[]
            {
                Node("mutation", UnityCliBridgeQueuePolicy.WorkClass.Mutation),
                Node("ordinary", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead),
                Safe("exact-status"),
                Classified("generic-status", "internal.unknown.status", false),
                Classified("snapshot", "internal.navmesh.bake.snapshot", false),
                Classified("plan", "internal.navmesh.bake.plan", false),
                Classified("cancel", "internal.navmesh.bake.cancel", false)
            };

            var result = UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, false, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Unreadable);

            Assert.That(Ids(result), Is.EqualTo(new[] { "exact-status" }));
            Assert.That(result.Starts.All(item => item.Class == UnityCliBridgeQueuePolicy.WorkClass.SafeStatus), Is.True);
        }

        [Test]
        public void MutationFifoIsPreservedAcrossReadPhases()
        {
            var beforeA = Node("read-before-a", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);
            var mutationA = Node("mutation-a", UnityCliBridgeQueuePolicy.WorkClass.Mutation);
            var between = Node("read-between", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);
            var mutationB = Node("mutation-b", UnityCliBridgeQueuePolicy.WorkClass.Mutation);
            var afterB = Node("read-after-b", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead);

            Assert.That(Ids(Select(new[] { beforeA, mutationA, between, mutationB, afterB })),
                Is.EqualTo(new[] { "read-before-a" }));
            Assert.That(Ids(Select(new[] { mutationA, between, mutationB, afterB })),
                Is.EqualTo(new[] { "mutation-a" }));

            var whileARuns = UnityCliBridgeQueuePolicy.SelectStarts(
                new[] { between, mutationB, afterB }, 0, true,
                UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);
            Assert.That(whileARuns.Starts, Is.Empty);

            Assert.That(Ids(Select(new[] { between, mutationB, afterB })),
                Is.EqualTo(new[] { "read-between" }));
            Assert.That(Ids(Select(new[] { mutationB, afterB })),
                Is.EqualTo(new[] { "mutation-b" }));
            Assert.That(Ids(Select(new[] { afterB })),
                Is.EqualTo(new[] { "read-after-b" }));
        }

        [Test]
        public void SafeStatusClassificationUsesOnlyTheClosedExactAllowlist()
        {
            var allowed = new[]
            {
                "internal.editor.compile.status",
                "internal.editor.refresh.status",
                "internal.editor.state.snapshot",
                "internal.asset.refresh.status",
                "internal.package.embed.status",
                "internal.package.resolve.status",
                "internal.test.run.status",
                "internal.test.run.cancel.status",
                "internal.navmesh.bake.status",
                "internal.occlusion.bake.status"
            };
            foreach (var command in allowed)
                Assert.That(UnityCliBridgeQueuePolicy.Classify(command, false),
                    Is.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.SafeStatus), command);

            var excluded = new[]
            {
                "internal.editor.compile.Status",
                "internal.editor.state.snapshot.extra",
                "internal.unknown.status",
                "project-tool.run",
                "profiler.status",
                "extension.status",
                "internal.navmesh.bake.plan",
                "internal.navmesh.bake.snapshot",
                "internal.navmesh.bake.cancel",
                "internal.test.run.cancel"
            };
            foreach (var command in excluded)
            {
                Assert.That(UnityCliBridgeQueuePolicy.IsSafeStatusCommand(command), Is.False, command);
                Assert.That(UnityCliBridgeQueuePolicy.Classify(command, false),
                    Is.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead), command);
            }

            Assert.That(UnityCliBridgeQueuePolicy.Classify("project-tool.run", true),
                Is.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.Mutation));
            Assert.Throws<ArgumentException>(() => UnityCliBridgeQueuePolicy.Classify(SafeStatusCommand, true));
        }

        [Test]
        public void EditorStateSnapshotProgressesWhenMutationOrDurableBarrierBlocksRegularWork()
        {
            var queued = new[]
            {
                Node("mutation", UnityCliBridgeQueuePolicy.WorkClass.Mutation),
                Node("ordinary", UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead),
                Classified("snapshot", "internal.editor.state.snapshot", false)
            };

            var mutationRunning = UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, true, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);
            Assert.That(Ids(mutationRunning), Is.EqualTo(new[] { "snapshot" }));

            var held = UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, false, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Held);
            Assert.That(Ids(held), Is.EqualTo(new[] { "snapshot" }));

            var unreadable = UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, false, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Unreadable);
            Assert.That(Ids(unreadable), Is.EqualTo(new[] { "snapshot" }));
        }

        [Test]
        public void CommandLengthMatchesCanonicalAdmissionBoundary()
        {
            Assert.That(UnityCliBridgeQueuePolicy.Classify(new string('c', 256), false),
                Is.EqualTo(UnityCliBridgeQueuePolicy.WorkClass.OrdinaryRead));
            Assert.Throws<ArgumentException>(() => UnityCliBridgeQueuePolicy.Classify(new string('c', 257), false));
        }

        private static UnityCliBridgeQueuePolicy.TickSelection Select(IReadOnlyList<UnityCliBridgeQueuePolicy.QueueNode> queued)
        {
            return UnityCliBridgeQueuePolicy.SelectStarts(
                queued, 0, false, UnityCliBridgeQueuePolicy.DurableMutationBarrier.Clear);
        }

        private static UnityCliBridgeQueuePolicy.QueueNode Node(string id, UnityCliBridgeQueuePolicy.WorkClass workClass)
        {
            if (workClass == UnityCliBridgeQueuePolicy.WorkClass.SafeStatus)
                throw new ArgumentException("Tests must create SafeStatus through its exact command.", "workClass");
            return new UnityCliBridgeQueuePolicy.QueueNode(
                id, "project-tool.run", workClass == UnityCliBridgeQueuePolicy.WorkClass.Mutation);
        }

        private static UnityCliBridgeQueuePolicy.QueueNode Safe(string id)
        {
            return Classified(id, SafeStatusCommand, false);
        }

        private static UnityCliBridgeQueuePolicy.QueueNode Classified(string id, string command, bool mutation)
        {
            return new UnityCliBridgeQueuePolicy.QueueNode(id, command, mutation);
        }

        private static UnityCliBridgeQueuePolicy.BypassIdentity Identity(int requestIndex, int digestIndex)
        {
            return new UnityCliBridgeQueuePolicy.BypassIdentity(
                "request-" + requestIndex.ToString(), digestIndex.ToString("x64"));
        }

        private static string[] Ids(UnityCliBridgeQueuePolicy.TickSelection selection)
        {
            return selection.Starts.Select(item => item.NodeId).ToArray();
        }

        private sealed class CountingQueue : IReadOnlyList<UnityCliBridgeQueuePolicy.QueueNode>
        {
            private readonly UnityCliBridgeQueuePolicy.QueueNode[] _nodes;

            public CountingQueue(UnityCliBridgeQueuePolicy.QueueNode[] nodes)
            {
                _nodes = nodes;
            }

            public int IndexerVisits { get; private set; }

            public int Count
            {
                get { return _nodes.Length; }
            }

            public UnityCliBridgeQueuePolicy.QueueNode this[int index]
            {
                get
                {
                    IndexerVisits += 1;
                    return _nodes[index];
                }
            }

            public IEnumerator<UnityCliBridgeQueuePolicy.QueueNode> GetEnumerator()
            {
                return ((IEnumerable<UnityCliBridgeQueuePolicy.QueueNode>)_nodes).GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
