using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace UnityCli
{
    /// <summary>Bridge主线程队列的纯分类、容量与启动选择策略。</summary>
    public static class UnityCliBridgeQueuePolicy
    {
        public const int MaxBypassIdentities = 128;
        public const int MaxNodesExaminedPerTick = 128;
        public const int MaxStartsPerTick = 16;
        public const int MaxSafeStatusStartsPerTick = 4;

        public enum WorkClass
        {
            Mutation,
            OrdinaryRead,
            SafeStatus
        }

        public enum DurableMutationBarrier
        {
            Clear,
            Held,
            Unreadable
        }

        public enum BypassAdmissionOutcome
        {
            Enqueue,
            Attach,
            RejectCapacity,
            RejectIdentityMismatch
        }

        public sealed class QueueNode
        {
            public QueueNode(string nodeId, string command, bool mutation)
            {
                ValidateNodeId(nodeId);
                NodeId = nodeId;
                Class = Classify(command, mutation);
            }

            public string NodeId { get; }
            public WorkClass Class { get; }
        }

        public sealed class BypassIdentity
        {
            public BypassIdentity(string requestId, string requestDigest)
            {
                ValidateRequestId(requestId);
                ValidateDigest(requestDigest);
                RequestId = requestId;
                RequestDigest = requestDigest;
            }

            public string RequestId { get; }
            public string RequestDigest { get; }
        }

        public sealed class BypassAdmissionDecision
        {
            internal BypassAdmissionDecision(BypassAdmissionOutcome outcome, int existingIndex)
            {
                Outcome = outcome;
                ExistingIndex = existingIndex;
            }

            public BypassAdmissionOutcome Outcome { get; }
            public int ExistingIndex { get; }
        }

        public sealed class StartDecision
        {
            internal StartDecision(string nodeId, int queueIndex, WorkClass workClass)
            {
                NodeId = nodeId;
                QueueIndex = queueIndex;
                Class = workClass;
            }

            public string NodeId { get; }
            public int QueueIndex { get; }
            public WorkClass Class { get; }
        }

        public sealed class TickSelection
        {
            internal TickSelection(List<StartDecision> starts, int nodesExamined)
            {
                Starts = new ReadOnlyCollection<StartDecision>(starts.ToArray());
                NodesExamined = nodesExamined;
            }

            public IReadOnlyList<StartDecision> Starts { get; }
            public int NodesExamined { get; }
        }

        /// <summary>只把固定内部terminal status命令归为SafeStatus；声明为mutation时拒绝矛盾输入。</summary>
        public static WorkClass Classify(string command, bool mutation)
        {
            ValidateCommand(command);
            if (IsSafeStatusCommand(command))
            {
                if (mutation) throw new ArgumentException("SafeStatus command cannot be declared as a mutation.", "mutation");
                return WorkClass.SafeStatus;
            }
            return mutation ? WorkClass.Mutation : WorkClass.OrdinaryRead;
        }

        /// <summary>精确白名单不接受后缀、大小写或project-tool间接名称。</summary>
        public static bool IsSafeStatusCommand(string command)
        {
            switch (command)
            {
                case "internal.editor.compile.status":
                case "internal.editor.refresh.status":
                case "internal.editor.state.snapshot":
                case "internal.asset.refresh.status":
                case "internal.package.embed.status":
                case "internal.package.resolve.status":
                case "internal.test.run.status":
                case "internal.test.run.cancel.status":
                case "internal.navmesh.bake.status":
                case "internal.occlusion.bake.status":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>同requestId同digest只attach；新identity达到固定上限时拒绝。</summary>
        public static BypassAdmissionDecision DecideBypassAdmission(IReadOnlyList<BypassIdentity> activeAndQueued, BypassIdentity candidate)
        {
            if (activeAndQueued == null) throw new ArgumentNullException("activeAndQueued");
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (activeAndQueued.Count > MaxBypassIdentities) throw new ArgumentException("Bypass identity snapshot exceeds its fixed bound.", "activeAndQueued");

            var requestIds = new HashSet<string>(StringComparer.Ordinal);
            var existingIndex = -1;
            var exactMatch = false;
            for (var index = 0; index < activeAndQueued.Count; index++)
            {
                var existing = activeAndQueued[index];
                if (existing == null) throw new ArgumentException("Bypass identity snapshot contains null.", "activeAndQueued");
                if (!requestIds.Add(existing.RequestId)) throw new ArgumentException("Bypass identity snapshot contains duplicate requestId.", "activeAndQueued");
                if (!string.Equals(existing.RequestId, candidate.RequestId, StringComparison.Ordinal)) continue;
                existingIndex = index;
                exactMatch = string.Equals(existing.RequestDigest, candidate.RequestDigest, StringComparison.Ordinal);
            }

            if (existingIndex >= 0)
                return new BypassAdmissionDecision(exactMatch ? BypassAdmissionOutcome.Attach : BypassAdmissionOutcome.RejectIdentityMismatch, existingIndex);
            return activeAndQueued.Count == MaxBypassIdentities
                ? new BypassAdmissionDecision(BypassAdmissionOutcome.RejectCapacity, -1)
                : new BypassAdmissionDecision(BypassAdmissionOutcome.Enqueue, -1);
        }

        /// <summary>按队列顺序选择本tick唯一可移除并尝试启动的节点，不修改调用方集合。</summary>
        public static TickSelection SelectStarts(
            IReadOnlyList<QueueNode> queued,
            int runningOrdinaryReads,
            bool mutationRunning,
            DurableMutationBarrier durableMutationBarrier)
        {
            if (queued == null) throw new ArgumentNullException("queued");
            if (runningOrdinaryReads < 0) throw new ArgumentOutOfRangeException("runningOrdinaryReads");
            ValidateEnum(durableMutationBarrier, "durableMutationBarrier");

            var examined = Math.Min(queued.Count, MaxNodesExaminedPerTick);
            var examinedNodes = new List<QueueNode>(examined);
            var firstMutationIndex = -1;
            var earlierOrdinaryReadIndices = new List<int>();
            var safeStatusIndices = new List<int>(MaxSafeStatusStartsPerTick);
            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < examined; index++)
            {
                var node = queued[index];
                if (node == null) throw new ArgumentException("Examined queue prefix contains null.", "queued");
                if (!nodeIds.Add(node.NodeId)) throw new ArgumentException("Examined queue prefix contains duplicate nodeId.", "queued");
                examinedNodes.Add(node);
                if (firstMutationIndex < 0 && node.Class == WorkClass.Mutation)
                    firstMutationIndex = index;
                if (node.Class == WorkClass.SafeStatus && safeStatusIndices.Count < MaxSafeStatusStartsPerTick)
                    safeStatusIndices.Add(index);
                if (firstMutationIndex < 0 && node.Class == WorkClass.OrdinaryRead)
                    earlierOrdinaryReadIndices.Add(index);
            }

            var selected = new bool[examined];
            foreach (var index in safeStatusIndices) selected[index] = true;
            var remainingStarts = MaxStartsPerTick - safeStatusIndices.Count;
            var regularBarrierClear = !mutationRunning && durableMutationBarrier == DurableMutationBarrier.Clear;

            if (regularBarrierClear && remainingStarts > 0)
            {
                if (firstMutationIndex < 0 || earlierOrdinaryReadIndices.Count > 0)
                {
                    foreach (var index in earlierOrdinaryReadIndices)
                    {
                        if (remainingStarts == 0) break;
                        selected[index] = true;
                        remainingStarts -= 1;
                    }
                }
                else if (runningOrdinaryReads == 0)
                {
                    selected[firstMutationIndex] = true;
                    remainingStarts -= 1;
                }
            }

            var starts = new List<StartDecision>(MaxStartsPerTick - remainingStarts);
            for (var index = 0; index < examined; index++)
            {
                if (!selected[index]) continue;
                var node = examinedNodes[index];
                starts.Add(new StartDecision(node.NodeId, index, node.Class));
            }
            return new TickSelection(starts, examined);
        }

        private static void ValidateCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command) || command.Length > 256 || !string.Equals(command, command.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("Bridge command must be a non-empty exact value of at most 256 characters.", "command");
        }

        private static void ValidateNodeId(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 200)
                throw new ArgumentException("Queue nodeId is required and must be at most 200 characters.", "nodeId");
        }

        private static void ValidateRequestId(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200)
                throw new ArgumentException("Bypass requestId is required and must be at most 200 characters.", "requestId");
        }

        private static void ValidateDigest(string digest)
        {
            if (string.IsNullOrEmpty(digest) || digest.Length != 64)
                throw new ArgumentException("Bypass requestDigest must be a lowercase SHA-256 value.", "requestDigest");
            foreach (var character in digest)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                    throw new ArgumentException("Bypass requestDigest must be a lowercase SHA-256 value.", "requestDigest");
        }

        private static void ValidateEnum<T>(T value, string parameterName) where T : struct
        {
            if (!Enum.IsDefined(typeof(T), value)) throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
