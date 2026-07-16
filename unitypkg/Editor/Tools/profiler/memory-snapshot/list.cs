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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "profiler.memory-snapshot.list",
                Snapshots = snapshots,
                SearchedDirs = dirs
            });
        }
    }
}
#endif
