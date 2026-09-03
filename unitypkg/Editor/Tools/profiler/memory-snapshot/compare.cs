#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityCli
{
    public class ProfilerMemorySnapshotCompareTool : IUnityCliTool<ProfilerMemorySnapshotCompareTool.Request, ProfilerMemorySnapshotCompareTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string SnapshotA { get; set; }
            [UnityCliRequired] public string SnapshotB { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public MemorySnapshotEntry SnapshotA { get; set; }
            public MemorySnapshotEntry SnapshotB { get; set; }
            public SnapshotDelta Delta { get; set; }
            public string Note { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.memory-snapshot.compare",
            Summary = "Compare two .snap memory snapshots at file-level metadata (size delta, time delta).",
            Description = "Compare file metadata for two snapshot file names under Library/UnityCli/memory-snapshots. Arbitrary paths are rejected. Reads share the request node/getter/time/UTF-8 budget. For object-level diff, open both governed artifacts in the Memory Profiler window. Read-only in Edit and Play Mode.",
            Group = "profiler",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Compare two snapshots", Input = new Request { SnapshotA = "snap1.snap", SnapshotB = "snap2.snap" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.SnapshotA) || string.IsNullOrEmpty(request.SnapshotB))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_PATH_REQUIRED",
                    "profiler.memory-snapshot.compare requires --snapshot-a and --snapshot-b.",
                    "Pass two .snap file names.");
            }
            var snapshotAPath = ProfilerMemorySnapshotSupport.ResolveFinalPath(request.SnapshotA, ctx.RequestId);
            var snapshotBPath = ProfilerMemorySnapshotSupport.ResolveFinalPath(request.SnapshotB, ctx.RequestId);
            if (!File.Exists(snapshotAPath))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_NOT_FOUND",
                    "Snapshot file not found: " + snapshotAPath,
                    "Check the path or run profiler.memory-snapshot.list.");
            }
            if (!File.Exists(snapshotBPath))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_NOT_FOUND",
                    "Snapshot file not found: " + snapshotBPath,
                    "Check the path or run profiler.memory-snapshot.list.");
            }

            var budget = ctx.UseReadBudget(request.Budget);
            if (!budget.TryConsumeNode() || !budget.TryConsumeNode() || !budget.TryConsumeGetter() || !budget.TryConsumeGetter())
                throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var fiA = new FileInfo(snapshotAPath);
            var fiB = new FileInfo(snapshotBPath);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                SnapshotA = MemorySnapshotEntry.FromFileInfo(fiA),
                SnapshotB = MemorySnapshotEntry.FromFileInfo(fiB),
                Delta = new SnapshotDelta
                {
                    SizeDeltaBytes = fiB.Length - fiA.Length,
                    TimeDeltaSeconds = (fiB.CreationTimeUtc - fiA.CreationTimeUtc).TotalSeconds
                },
                Note = "File-level metadata comparison. For object-level diff, open both snapshots in the Memory Profiler window."
            });
        }
    }
}
#endif
