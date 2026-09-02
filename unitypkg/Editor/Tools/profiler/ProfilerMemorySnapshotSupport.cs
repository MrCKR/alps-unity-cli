#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;

namespace UnityCli
{
    public sealed class MemorySnapshotEntry
    {
        public string Path { get; set; }
        public long SizeBytes { get; set; }
        public string Created { get; set; }

        public static MemorySnapshotEntry FromFileInfo(FileInfo info)
        {
            return new MemorySnapshotEntry { Path = info.FullName, SizeBytes = info.Length, Created = info.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture) };
        }
    }

    public sealed class SnapshotDelta { public long SizeDeltaBytes { get; set; } public double TimeDeltaSeconds { get; set; } }

    /// <summary>Library/UnityCli/memory-snapshots 的唯一 path、retention、catalog 和 orphan owner。</summary>
    [InitializeOnLoad]
    internal static class ProfilerMemorySnapshotSupport
    {
        internal const int RetainedSnapshotCount = 100;
        internal const long RetainedSnapshotBytes = 8L * 1024L * 1024L * 1024L;
        internal const long AbsoluteSnapshotBytes = 4L * 1024L * 1024L * 1024L;
        private const int MaximumObservedFiles = RetainedSnapshotCount + 8;
        private static readonly object PublicationLock = new object();

        static ProfilerMemorySnapshotSupport()
        {
            CleanupOrphansNoThrow();
        }

        internal static string Root
        {
            get { return Path.GetFullPath(Path.Combine(ProfilerToolSupport.ProjectRoot(), "Library", "UnityCli", "memory-snapshots")); }
        }

        internal static string ResolveFinalPath(string requestedName, string requestId)
        {
            var name = string.IsNullOrWhiteSpace(requestedName) ? "snapshot_" + SafeName(requestId) + ".snap" : requestedName.Trim();
            if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) || !name.EndsWith(".snap", StringComparison.OrdinalIgnoreCase))
                throw Error("E_PROFILER_SNAPSHOT_PATH_INVALID", "Snapshot path must be one .snap file name without directories.", "Pass a file name such as capture.snap; artifacts always use Library/UnityCli/memory-snapshots.");
            var fullPath = Path.GetFullPath(Path.Combine(Root, name));
            if (!fullPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw Error("E_PROFILER_SNAPSHOT_PATH_INVALID", "Snapshot path resolves outside the governed root.", "Pass a simple .snap file name.");
            return fullPath;
        }

        internal static string PendingPath(string requestId)
        {
            return Path.Combine(Root, ".pending-" + SafeName(requestId) + ".snap");
        }

        internal static void PrepareRoot()
        {
            Directory.CreateDirectory(Root);
        }

        internal static FileInfo Publish(string capturedPath, string pendingPath, string finalPath)
        {
            var source = Path.GetFullPath(string.IsNullOrWhiteSpace(capturedPath) ? pendingPath : capturedPath);
            if (!string.Equals(source, Path.GetFullPath(pendingPath), StringComparison.OrdinalIgnoreCase))
            {
                DeleteNoThrow(source);
                throw Error("E_PROFILER_SNAPSHOT_ARTIFACT_INVALID", "MemoryProfiler callback returned an unexpected artifact path.", "Inspect the Memory Profiler package and retry.");
            }
            lock (PublicationLock)
            {
                var info = new FileInfo(source);
                if (!info.Exists || info.Length <= 0L) throw Error("E_PROFILER_SNAPSHOT_ARTIFACT_MISSING", "MemoryProfiler did not produce a non-empty snapshot.", "Check disk space and Unity Console before retrying.");
                if (info.Length > AbsoluteSnapshotBytes)
                {
                    DeleteNoThrow(source);
                    throw Error("E_PROFILER_SNAPSHOT_LIMIT_EXCEEDED", "Snapshot exceeds the 4 GiB per-artifact ceiling.", "Reduce the captured project/runtime scope before retrying.");
                }
                if (File.Exists(finalPath)) throw Error("E_OUTPUT_EXISTS", "Snapshot output already exists: " + finalPath, "Choose another snapshot file name.");
                File.Move(source, finalPath);
                var published = new FileInfo(finalPath);
                try { ApplyRetention(finalPath); }
                catch { DeleteNoThrow(finalPath); throw; }
                return published;
            }
        }

        /// <summary>每次 publication 后最多观察 108 个本产品文件，保留最新 100 且总量不超过 8 GiB。</summary>
        internal static void ApplyRetention(string newestPath)
        {
            var files = BoundedFiles();
            files.Sort((left, right) =>
            {
                var time = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                return time != 0 ? time : string.CompareOrdinal(right.Name, left.Name);
            });
            var retained = UnityCliRetentionPlanner.RetainedIndices(files.Select(file => file.Length).ToList(), RetainedSnapshotCount, RetainedSnapshotBytes);
            for (var index = 0; index < files.Count; index++)
            {
                if (retained.Contains(index)) continue;
                DeleteNoThrow(files[index].FullName);
            }
            if (!File.Exists(newestPath)) throw Error("E_PROFILER_SNAPSHOT_LIMIT_EXCEEDED", "The new snapshot cannot fit the latest-100/8-GiB retention policy.", "Reduce snapshot size before retrying.");
        }

        internal static List<FileInfo> Catalog()
        {
            if (!Directory.Exists(Root)) return new List<FileInfo>();
            var files = BoundedFiles();
            files.Sort((left, right) =>
            {
                var time = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                return time != 0 ? time : string.CompareOrdinal(right.Name, left.Name);
            });
            return files;
        }

        internal static string CatalogGeneration(List<FileInfo> files)
        {
            var values = files.Select(info => info.Name + "\0" + info.Length.ToString(CultureInfo.InvariantCulture) + "\0" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            return UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Session("profiler.memory-snapshot.list") + "\0" + string.Join("\n", values.ToArray()));
        }

        internal static void DeleteNoThrow(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath)) File.Delete(fullPath);
            }
            catch { }
        }

        private static List<FileInfo> BoundedFiles()
        {
            var result = new List<FileInfo>();
            if (!Directory.Exists(Root)) return result;
            foreach (var path in Directory.EnumerateFiles(Root, "*.snap", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal)) continue;
                result.Add(new FileInfo(path));
                if (result.Count > MaximumObservedFiles)
                    throw Error("E_PROFILER_SNAPSHOT_CATALOG_UNBOUNDED", "Snapshot catalog exceeds the governed retention recovery bound.", "Move non-UnityCLI snapshots out of Library/UnityCli/memory-snapshots, then retry.");
            }
            return result;
        }

        private static void CleanupOrphans()
        {
            if (!Directory.Exists(Root)) return;
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(Root, ".pending-*.snap", SearchOption.TopDirectoryOnly))
            {
                DeleteNoThrow(path);
                if (++count > 8) throw Error("E_PROFILER_SNAPSHOT_ORPHANS_UNBOUNDED", "Snapshot orphan count exceeds the active-request bound.", "Inspect the governed snapshot directory before retrying.");
            }
        }

        private static void CleanupOrphansNoThrow()
        {
            try { CleanupOrphans(); } catch { }
        }

        private static string SafeName(string value)
        {
            var source = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;
            var result = new string(source.Where(character => char.IsLetterOrDigit(character) || character == '-' || character == '_').Take(96).ToArray());
            return string.IsNullOrEmpty(result) ? "capture" : result;
        }

        internal static UnityCliBridge.BridgeCommandException Error(string code, string message, string nextAction)
        {
            return new UnityCliBridge.BridgeCommandException(code, message, nextAction);
        }
    }

    /// <summary>callback、timeout/cancel 和 late callback 竞争同一个 CAS terminal。</summary>
    internal sealed class MemorySnapshotTerminal
    {
        private int state;
        internal bool TryOwnCallback() { return Interlocked.CompareExchange(ref state, 1, 0) == 0; }
        internal bool TryAbandon() { return Interlocked.CompareExchange(ref state, 2, 0) == 0; }
        internal bool IsAbandoned { get { return Volatile.Read(ref state) == 2; } }
    }
}
#endif
