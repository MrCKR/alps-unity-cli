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
            public QueueNode(string nodeId, string command, WorkClass workClass)
            {
                ValidateNodeId(nodeId);
                NodeId = nodeId;
                Class = Classify(command, workClass);
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

        /// <summary>只接受生产注册表裁决的闭集分类；SafeStatus命令不能被降级为普通读取。</summary>
        public static WorkClass Classify(string command, WorkClass workClass)
        {
            ValidateCommand(command);
            ValidateEnum(workClass, "workClass");
            var safeStatus = IsSafeStatusCommand(command);
            if (safeStatus && workClass != WorkClass.SafeStatus)
                throw new ArgumentException("SafeStatus command must use SafeStatus classification.", "workClass");
            if (!safeStatus && workClass == WorkClass.SafeStatus)
                throw new ArgumentException("Only the exact SafeStatus allowlist may use SafeStatus classification.", "workClass");
            return workClass;
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

        /// <summary>internal普通读取只允许不触发plan、cancel或writer控制的精确观察命令。</summary>
        public static bool IsOrdinaryReadInternalCommand(string command)
        {
            return string.Equals(command, "internal.navmesh.bake.snapshot", StringComparison.Ordinal)
                || string.Equals(command, "internal.occlusion.bake.snapshot", StringComparison.Ordinal);
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
