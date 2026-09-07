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
        private const int MaximumObservedFiles = 1024;
        private const int PendingSlotCount = 8;
        private const int MaximumLegacyOrphansPerPass = 1024;
        private static readonly object PublicationLock = new object();
        private static readonly object PendingLock = new object();
        private static readonly HashSet<string> ActivePendingSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PendingCleanup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int cleanupScheduled;
        internal static string RootOverrideForTests;
        internal static Action<string> DeleteFileOverrideForTests;

        static ProfilerMemorySnapshotSupport()
        {
            ScheduleOrphanCleanup();
        }

        internal static string Root
        {
            get
            {
                var root = string.IsNullOrWhiteSpace(RootOverrideForTests)
                    ? Path.Combine(ProfilerToolSupport.ProjectRoot(), "Library", "UnityCli", "memory-snapshots")
                    : RootOverrideForTests;
                return Path.GetFullPath(root);
            }
        }

        /// <summary>测试结束时清除故障注入和固定slot占用，避免跨case污染。</summary>
        internal static void ResetTestOverrides()
        {
            RootOverrideForTests = null;
            DeleteFileOverrideForTests = null;
            ProfilerToolSupport.MemoryProfilerTypeOverrideForTests = null;
            lock (PendingLock)
            {
                ActivePendingSlots.Clear();
                PendingCleanup.Clear();
            }
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

        /// <summary>每个domain最多占用8个固定pending slot；未收到callback的slot不复用，避免迟到writer污染新请求。</summary>
        internal static string AcquirePendingPath(string requestId)
        {
            PrepareRoot();
            var fingerprint = UnityCliContinuationToken.Fingerprint(requestId ?? string.Empty);
            var start = (int)(unchecked((uint)StringComparer.Ordinal.GetHashCode(fingerprint)) % PendingSlotCount);
            lock (PendingLock)
            {
                for (var offset = 0; offset < PendingSlotCount; offset++)
                {
                    var path = PendingSlotPath((start + offset) % PendingSlotCount);
                    if (!ActivePendingSlots.Add(path)) continue;
                    try
                    {
                        DeleteStrict(path, "E_PROFILER_SNAPSHOT_PENDING_CLEANUP_FAILED", "Failed to prepare the bounded snapshot pending slot.");
                        return path;
                    }
                    catch
                    {
                        ActivePendingSlots.Remove(path);
                        throw;
                    }
                }
            }
            throw Error("E_PROFILER_SNAPSHOT_PENDING_SLOTS_BUSY", "All bounded memory snapshot pending slots are awaiting callbacks.", "Wait for the outstanding Memory Profiler callbacks or reload the Editor domain before retrying.");
        }

        internal static void ReleasePendingPath(string path)
        {
            lock (PendingLock) ActivePendingSlots.Remove(Path.GetFullPath(path));
            RequestPendingCleanup(path);
        }

        internal static void RequestPendingCleanup(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                DeleteStrict(path, "E_PROFILER_SNAPSHOT_PENDING_CLEANUP_FAILED", "Failed to clean the bounded snapshot pending slot.");
                lock (PendingLock) PendingCleanup.Remove(Path.GetFullPath(path));
            }
            catch
            {
                lock (PendingLock)
                {
                    if (PendingCleanup.Count < PendingSlotCount) PendingCleanup.Add(Path.GetFullPath(path));
                }
                ScheduleOrphanCleanup();
            }
        }

        private static string PendingSlotPath(int slot)
        {
            return Path.Combine(Root, ".pending-" + slot.ToString("00", CultureInfo.InvariantCulture) + ".snap");
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
                try { ValidateSnapshotLength(length, requestedMaxBytes); }
                catch
                {
                    DeleteStrict(source, "E_PROFILER_SNAPSHOT_PENDING_CLEANUP_FAILED", "Failed to remove an oversized pending snapshot.");
                    throw;
                }
                if (File.Exists(finalPath)) throw Error("E_OUTPUT_EXISTS", "Snapshot output already exists: " + finalPath, "Choose another snapshot file name.");
                budget.CheckpointOrThrow();
                File.Move(source, finalPath);
                try
                {
                    budget.CheckpointOrThrow();
                    ApplyRetention(finalPath, budget);
                }
                catch
                {
                    DeleteStrict(finalPath, "E_PROFILER_SNAPSHOT_ROLLBACK_FAILED", "Snapshot retention failed and the published artifact could not be rolled back.");
                    throw;
                }
                var published = new FileInfo(finalPath);
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var publishedLength = published.Length;
                if (publishedLength <= 0L) throw Error("E_PROFILER_SNAPSHOT_ARTIFACT_MISSING", "Published snapshot is empty after retention verification.", "Inspect storage and retry the capture.");
                return published;
            }
        }

        /// <summary>在任何 publication 前复用同一 4 GiB 与调用方 ceiling 判定。</summary>
        internal static void ValidateSnapshotLength(long length, long requestedMaxBytes)
        {
            if (length <= 0L)
                throw Error("E_PROFILER_SNAPSHOT_ARTIFACT_MISSING", "MemoryProfiler did not produce a non-empty snapshot.", "Check disk space and Unity Console before retrying.");
            if (length > AbsoluteSnapshotBytes || length > requestedMaxBytes)
                throw Error("E_PROFILER_SNAPSHOT_LIMIT_EXCEEDED", "Snapshot exceeds the requested or 4 GiB per-artifact ceiling.", "Increase maxBytes within 4 GiB or reduce captured state.");
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
                DeleteStrict(files[index].FullName, "E_PROFILER_SNAPSHOT_RETENTION_DELETE_FAILED", "Failed to delete an artifact excluded by snapshot retention.");
            }
            VerifyRetentionPostcondition(newestPath, budget);
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

        /// <summary>publication/retention/rollback删除失败必须返回稳定错误，禁止吞掉后继续宣称成功。</summary>
        internal static void DeleteStrict(string path, string errorCode, string message)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("path is outside the governed snapshot root");
                if (File.Exists(fullPath))
                {
                    if (DeleteFileOverrideForTests != null) DeleteFileOverrideForTests(fullPath);
                    else File.Delete(fullPath);
                }
                if (File.Exists(fullPath)) throw new IOException("file still exists after deletion");
            }
            catch (Exception exception)
            {
                throw Error(errorCode, message + " " + exception.Message, "Inspect the exact governed file and storage permissions before retrying.");
            }
        }

        /// <summary>删除后重新枚举并验证count、total bytes、single file和newest存在性。</summary>
        private static void VerifyRetentionPostcondition(string newestPath, UnityCliRequestBudget budget)
        {
            var verified = BoundedFiles(budget);
            long totalBytes = 0L;
            var newestExists = false;
            foreach (var file in verified)
            {
                if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var length = file.Length;
                if (length <= 0L || length > AbsoluteSnapshotBytes)
                    throw Error("E_PROFILER_SNAPSHOT_RETENTION_VERIFY_FAILED", "Snapshot retention contains an empty or oversized artifact.", "Inspect the governed snapshot directory before retrying.");
                totalBytes = checked(totalBytes + length);
                if (string.Equals(file.FullName, newestPath, StringComparison.OrdinalIgnoreCase)) newestExists = true;
            }
            if (!newestExists || verified.Count > RetainedSnapshotCount || totalBytes > RetainedSnapshotBytes)
                throw Error("E_PROFILER_SNAPSHOT_RETENTION_VERIFY_FAILED", "Snapshot retention postcondition is not count<=100, total<=8GiB with the newest artifact present.", "Inspect the governed snapshot directory before retrying.");
            budget.CheckpointOrThrow();
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

        /// <summary>单个scheduler任务持续处理固定slot和旧candidate orphan；每tick工作有1024项硬上限。</summary>
        private static void ScheduleOrphanCleanup()
        {
            if (Interlocked.CompareExchange(ref cleanupScheduled, 1, 0) != 0) return;
            UnityCliWaitScheduler.Until(
                "memory-snapshot-orphan-cleanup",
                "profiler.memory-snapshot",
                CleanupOrphanPass,
                TimeSpan.FromDays(3650),
                default(CancellationToken));
        }

        internal static bool CleanupOrphanPass()
        {
            var allClean = true;
            List<string> pending;
            lock (PendingLock) pending = new List<string>(PendingCleanup);
            foreach (var path in pending)
            {
                try
                {
                    DeleteStrict(path, "E_PROFILER_SNAPSHOT_PENDING_CLEANUP_FAILED", "Failed to clean a bounded snapshot pending slot.");
                    lock (PendingLock) PendingCleanup.Remove(path);
                }
                catch { allClean = false; }
            }
            if (!Directory.Exists(Root)) return allClean && CompleteCleanupSchedule();
            var observed = 0;
            foreach (var path in Directory.EnumerateFiles(Root, ".pending-*.snap", SearchOption.TopDirectoryOnly))
            {
                if (++observed > MaximumLegacyOrphansPerPass) { allClean = false; break; }
                lock (PendingLock) if (ActivePendingSlots.Contains(Path.GetFullPath(path))) continue;
                try { DeleteStrict(path, "E_PROFILER_SNAPSHOT_PENDING_CLEANUP_FAILED", "Failed to clean a snapshot orphan."); }
                catch { allClean = false; }
            }
            return allClean && CompleteCleanupSchedule();
        }

        private static bool CompleteCleanupSchedule()
        {
            lock (PendingLock)
            {
                if (PendingCleanup.Count != 0) return false;
                Interlocked.Exchange(ref cleanupScheduled, 0);
                return true;
            }
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
}
#endif
