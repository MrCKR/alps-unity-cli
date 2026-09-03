#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
            try
            {
                var name = string.IsNullOrWhiteSpace(requestedName) ? "snapshot_" + SafeName(requestId) + ".snap" : requestedName.Trim();
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
                    || !name.EndsWith(".snap", StringComparison.OrdinalIgnoreCase) || IsReservedWindowsName(Path.GetFileNameWithoutExtension(name)))
                    throw Error("E_PROFILER_SNAPSHOT_PATH_INVALID", "Snapshot path must be one valid .snap file name without directories.", "Pass a file name such as capture.snap; artifacts always use Library/UnityCli/memory-snapshots.");
                var fullPath = Path.GetFullPath(Path.Combine(Root, name));
                if (!fullPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw Error("E_PROFILER_SNAPSHOT_PATH_INVALID", "Snapshot path resolves outside the governed root.", "Pass a simple .snap file name.");
                return fullPath;
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (Exception)
            {
                throw Error("E_PROFILER_SNAPSHOT_PATH_INVALID", "Snapshot path is not a valid governed .snap file name.", "Pass a simple file name such as capture.snap.");
            }
        }

        private static bool IsReservedWindowsName(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            var normalized = value.TrimEnd(' ', '.').ToUpperInvariant();
            var dot = normalized.IndexOf('.');
            if (dot >= 0) normalized = normalized.Substring(0, dot);
            if (normalized == "CON" || normalized == "PRN" || normalized == "AUX" || normalized == "NUL") return true;
            if (normalized.Length == 4 && (normalized.StartsWith("COM", StringComparison.Ordinal) || normalized.StartsWith("LPT", StringComparison.Ordinal)))
                return normalized[3] >= '1' && normalized[3] <= '9';
            return false;
        }

        internal static string PendingPath(string requestId)
        {
            return Path.Combine(Root, ".pending-" + UnityCliContinuationToken.Fingerprint(requestId ?? string.Empty) + ".snap");
        }

        internal static void PrepareRoot()
        {
            Directory.CreateDirectory(Root);
        }

        internal static FileInfo Publish(string capturedPath, string pendingPath, string finalPath, long requestedMaxBytes, UnityCliRequestBudget budget)
        {
            var source = Path.GetFullPath(string.IsNullOrWhiteSpace(capturedPath) ? pendingPath : capturedPath);
            if (!string.Equals(source, Path.GetFullPath(pendingPath), StringComparison.OrdinalIgnoreCase))
                throw Error("E_PROFILER_SNAPSHOT_ARTIFACT_INVALID", "MemoryProfiler callback returned an unexpected artifact path.", "Inspect the Memory Profiler package and retry.");
            lock (PublicationLock)
            {
                budget.CheckpointOrThrow();
                var info = new FileInfo(source);
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var exists = info.Exists;
                if (!exists) throw Error("E_PROFILER_SNAPSHOT_ARTIFACT_MISSING", "MemoryProfiler did not produce a non-empty snapshot.", "Check disk space and Unity Console before retrying.");
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var length = info.Length;
                if (length <= 0L) throw Error("E_PROFILER_SNAPSHOT_ARTIFACT_MISSING", "MemoryProfiler did not produce a non-empty snapshot.", "Check disk space and Unity Console before retrying.");
                if (length > AbsoluteSnapshotBytes || length > requestedMaxBytes)
                {
                    DeleteNoThrow(source);
                    throw Error("E_PROFILER_SNAPSHOT_LIMIT_EXCEEDED", "Snapshot exceeds the requested or 4 GiB per-artifact ceiling.", "Increase maxBytes within 4 GiB or reduce captured state.");
                }
                if (File.Exists(finalPath)) throw Error("E_OUTPUT_EXISTS", "Snapshot output already exists: " + finalPath, "Choose another snapshot file name.");
                budget.CheckpointOrThrow();
                File.Move(source, finalPath);
                var published = new FileInfo(finalPath);
                try
                {
                    budget.CheckpointOrThrow();
                    ApplyRetention(finalPath, budget);
                }
                catch { DeleteNoThrow(finalPath); throw; }
                return published;
            }
        }

        /// <summary>每次 publication 后最多观察 108 个本产品文件，保留连续的最新 100 且总量不超过 8 GiB。</summary>
        internal static void ApplyRetention(string newestPath, UnityCliRequestBudget budget)
        {
            var files = BoundedFiles(budget);
            SortNewest(files, budget);
            var newestIndex = -1;
            for (var index = 0; index < files.Count; index++)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (string.Equals(files[index].FullName, newestPath, StringComparison.OrdinalIgnoreCase)) { newestIndex = index; break; }
            }
            if (newestIndex > 0)
            {
                var newest = files[newestIndex];
                files.RemoveAt(newestIndex);
                files.Insert(0, newest);
            }
            var sizes = new List<long>(files.Count);
            foreach (var file in files)
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                sizes.Add(file.Length);
            }
            var retained = UnityCliRetentionPlanner.RetainedIndices(sizes, RetainedSnapshotCount, RetainedSnapshotBytes);
            budget.CheckpointOrThrow();
            for (var index = 0; index < files.Count; index++)
            {
                if (retained.Contains(index)) continue;
                DeleteNoThrow(files[index].FullName);
            }
            if (!File.Exists(newestPath)) throw Error("E_PROFILER_SNAPSHOT_LIMIT_EXCEEDED", "The new snapshot cannot fit the latest-100/8-GiB retention policy.", "Reduce snapshot size before retrying.");
        }

        internal static List<FileInfo> Catalog(UnityCliRequestBudget budget)
        {
            if (!Directory.Exists(Root)) return new List<FileInfo>();
            var files = BoundedFiles(budget);
            SortNewest(files, budget);
            return files;
        }

        internal static string CatalogGeneration(List<FileInfo> files, UnityCliRequestBudget budget)
        {
            var values = new List<string>(files.Count);
            foreach (var info in files)
            {
                if (!budget.TryConsumeGetter() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var value = info.Name + "\0" + info.Length.ToString(CultureInfo.InvariantCulture) + "\0" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                if (!budget.TryReserveSerializedUtf8(value)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                values.Add(value);
            }
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

        private static void SortNewest(List<FileInfo> files, UnityCliRequestBudget budget)
        {
            var entries = new List<SnapshotSortEntry>(files.Count);
            foreach (var file in files)
            {
                if (!budget.TryConsumeGetter() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                entries.Add(new SnapshotSortEntry { File = file, LastWriteTimeUtc = file.LastWriteTimeUtc, Name = file.Name });
            }
            entries.Sort((left, right) =>
            {
                var time = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                return time != 0 ? time : string.CompareOrdinal(right.Name, left.Name);
            });
            files.Clear();
            foreach (var entry in entries) files.Add(entry.File);
        }

        private sealed class SnapshotSortEntry
        {
            internal FileInfo File;
            internal DateTime LastWriteTimeUtc;
            internal string Name;
        }

        private static List<FileInfo> BoundedFiles(UnityCliRequestBudget budget)
        {
            var result = new List<FileInfo>();
            if (!Directory.Exists(Root)) return result;
            foreach (var path in Directory.EnumerateFiles(Root, "*.snap", SearchOption.TopDirectoryOnly))
            {
                if (budget != null && !budget.TryConsumeScannedRecord()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
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
            var result = new StringBuilder(Math.Min(96, source.Length));
            for (var index = 0; index < source.Length && result.Length < 96; index++)
            {
                var character = source[index];
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_') result.Append(character);
            }
            return result.Length == 0 ? "capture" : result.ToString();
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
