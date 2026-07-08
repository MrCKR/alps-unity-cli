#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// profiler.memory-snapshot.list — 列出 .snap 内存快照文件。
    /// 迁移自旧 RegisterCommand(ProfilerCommands.ProfilerMemorySnapshotList)为 IUnityCliTool 自包含。
    /// 只读文件元数据,不打开或修改快照。
    /// </summary>
    public class ProfilerMemorySnapshotListTool : IUnityCliTool<ProfilerMemorySnapshotListTool.Request, ProfilerMemorySnapshotListTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public List<MemorySnapshotEntry> Snapshots { get; set; }
            public List<string> SearchedDirs { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.memory-snapshot.list",
            Summary = "List .snap memory snapshot files in the default capture directories or a custom path.",
            Description = "List .snap memory snapshot files in the default capture directories or a custom path. This reads file metadata only and does not open or modify snapshots.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "List snapshots in default dirs", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var searchPath = request.Path;
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(searchPath))
            {
                dirs.Add(searchPath);
            }
            else
            {
                dirs.Add(Path.Combine(Application.temporaryCachePath, "MemoryCaptures"));
                dirs.Add(Path.Combine(Application.dataPath, "..", "MemoryCaptures"));
            }

            var snapshots = new List<MemorySnapshotEntry>();
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.snap"))
                {
                    var fi = new FileInfo(file);
                    snapshots.Add(new MemorySnapshotEntry
                    {
                        Path = fi.FullName,
                        SizeBytes = fi.Length,
                        Created = fi.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture)
                    });
                }
            }
            return new Result
            {
                Command = "profiler.memory-snapshot.list",
                Snapshots = snapshots,
                SearchedDirs = dirs
            };
        }
    }

    /// <summary>
    /// profiler.memory-snapshot.compare — 比较两个 .snap 快照的文件级元数据(大小/时间差)。
    /// 迁移自旧 RegisterCommand(ProfilerCommands.ProfilerMemorySnapshotCompare)为 IUnityCliTool 自包含。
    /// 对象级 diff 需在 Memory Profiler 窗口手动打开。
    /// </summary>
    public class ProfilerMemorySnapshotCompareTool : IUnityCliTool<ProfilerMemorySnapshotCompareTool.Request, ProfilerMemorySnapshotCompareTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string SnapshotA { get; set; }
            [UnityCliRequired] public string SnapshotB { get; set; }
            public string PathA { get; set; }
            public string PathB { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
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
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Compare two snapshots", Input = new Request { SnapshotA = "snap1.snap", SnapshotB = "snap2.snap" } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            // pathA/pathB 为 snapshotA/snapshotB 的别名,bridge spec 已映射;此处兼容直接调用
            var pathA = request.SnapshotA ?? request.PathA;
            var pathB = request.SnapshotB ?? request.PathB;
            if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_PATH_REQUIRED",
                    "profiler.memory-snapshot.compare requires --snapshot-a and --snapshot-b.",
                    "Pass two .snap file paths.");
            }
            if (!File.Exists(pathA))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_NOT_FOUND",
                    "Snapshot file not found: " + pathA,
                    "Check the path or run profiler.memory-snapshot.list.");
            }
            if (!File.Exists(pathB))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_NOT_FOUND",
                    "Snapshot file not found: " + pathB,
                    "Check the path or run profiler.memory-snapshot.list.");
            }

            var fiA = new FileInfo(pathA);
            var fiB = new FileInfo(pathB);
            return new Result
            {
                Command = "profiler.memory-snapshot.compare",
                SnapshotA = MemorySnapshotEntry.FromFileInfo(fiA),
                SnapshotB = MemorySnapshotEntry.FromFileInfo(fiB),
                Delta = new SnapshotDelta
                {
                    SizeDeltaBytes = fiB.Length - fiA.Length,
                    TimeDeltaSeconds = (fiB.CreationTimeUtc - fiA.CreationTimeUtc).TotalSeconds
                },
                Note = "File-level metadata comparison. For object-level diff, open both snapshots in the Memory Profiler window."
            };
        }
    }

    /// <summary>.snap 快照文件元数据条目(list/compare 共享)。</summary>
    public sealed class MemorySnapshotEntry
    {
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public string Created { get; set; }

        public static MemorySnapshotEntry FromFileInfo(FileInfo fi)
        {
            return new MemorySnapshotEntry
            {
                Path = fi.FullName,
                SizeBytes = fi.Length,
                Created = fi.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture)
            };
        }
    }

    /// <summary>两个快照的文件级差异。</summary>
    public sealed class SnapshotDelta
    {
        public long SizeDeltaBytes { get; set; }
        public double TimeDeltaSeconds { get; set; }
    }
}
#endif
