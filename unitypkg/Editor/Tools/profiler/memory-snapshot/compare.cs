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
            Description = "Compare two .snap memory snapshots at file-level metadata (size delta, time delta). For object-level diff, open both in the Memory Profiler window.",
            Group = "profiler",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Compare two snapshots", Input = new Request { SnapshotA = "snap1.snap", SnapshotB = "snap2.snap" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var snapshotAPath = request.SnapshotA;
            var snapshotBPath = request.SnapshotB;
            if (string.IsNullOrEmpty(snapshotAPath) || string.IsNullOrEmpty(snapshotBPath))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_PATH_REQUIRED",
                    "profiler.memory-snapshot.compare requires --snapshot-a and --snapshot-b.",
                    "Pass two .snap file paths.");
            }
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
