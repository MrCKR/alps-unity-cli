#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private const int InstanceCleanupMaxScanned = 2000;
        private const int InstanceCleanupMaxDeleted = 20;
        private const int InstanceCleanupRetainedOffline = 100;
        private const int InstanceCleanupRetainedDamaged = 20;
        private const int InstanceCleanupRetentionDays = 30;
        private static bool instanceCleanupAttempted;
        private static readonly UnityCliToolCompletionContract InstanceCleanupCompletion = new UnityCliToolCompletionContract(
            "internal.instances.prune", "control-plane", "direct-terminal", "atomic", "bounded-cleanup-summary",
            "writer-owned-registry-reinspection", "caller-responsibility", "E_EXECUTION_TIMEOUT", "caller-responsibility",
            "atomic-typed-result-v1", "required", true);

        private sealed class InstanceCleanupCandidate
        {
            public string Path;
            public string InstanceId;
            public DateTimeOffset LastActivity;
            public bool Damaged;
        }

        private sealed class InstanceCleanupSummary
        {
            public int Scanned;
            public int Offline;
            public int Damaged;
            public int Preserved;
            public int Deleted;
            public int Deferred;
            public int Remaining;
            public bool Truncated;
        }

        /// <summary>每个domain只尝试一次自动清理；其他Unity持有全局锁时直接让其负责。</summary>
        private static void TryRunAutomaticInstanceCleanup()
        {
            if (instanceCleanupAttempted) return;
            instanceCleanupAttempted = true;
            try
            {
                InstanceCleanupSummary summary;
                var acquired = TryExecuteInstanceCleanup(null, out summary);
                if (!acquired)
                {
                    WriteLog("instance_cleanup_deferred reason=global-lock");
                    return;
                }
                WriteLog("instance_cleanup_completed scanned=" + summary.Scanned.ToString(CultureInfo.InvariantCulture)
                    + " offline=" + summary.Offline.ToString(CultureInfo.InvariantCulture)
                    + " damaged=" + summary.Damaged.ToString(CultureInfo.InvariantCulture)
                    + " preserved=" + summary.Preserved.ToString(CultureInfo.InvariantCulture)
                    + " deleted=" + summary.Deleted.ToString(CultureInfo.InvariantCulture)
                    + " deferred=" + summary.Deferred.ToString(CultureInfo.InvariantCulture)
                    + " truncated=" + (summary.Truncated ? "true" : "false"));
            }
            catch (Exception exception)
            {
                WriteLog("instance_cleanup_failed kind=" + exception.GetType().Name);
            }
        }

        /// <summary>跨Unity只允许一个清理owner；零等待失败由调用方延期或返回busy。</summary>
        private static bool TryExecuteInstanceCleanup(Action beginEffect, out InstanceCleanupSummary summary)
        {
            InstanceCleanupSummary value = null;
            var acquired = TryWithCurrentUserMutex("Local\\UnityCli.Registry.Cleanup", 0, delegate
            {
                if (beginEffect != null) beginEffect();
                value = RunInstanceCleanup();
            });
            summary = value;
            return acquired;
        }

        /// <summary>扫描和删除均有硬上限；活跃、不可确认、链接或锁竞争记录只保留。</summary>
        private static InstanceCleanupSummary RunInstanceCleanup()
        {
            var summary = new InstanceCleanupSummary();
            var directory = InstancesDir();
            if (!Directory.Exists(directory)) return summary;
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidates = new List<InstanceCleanupCandidate>();
            using (var enumerator = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).GetEnumerator())
            {
                while (summary.Scanned < InstanceCleanupMaxScanned && enumerator.MoveNext())
                {
                    var fullPath = Path.GetFullPath(enumerator.Current);
                    var parent = Path.GetDirectoryName(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var fileName = Path.GetFileName(fullPath);
                    string candidateInstanceId;
                    if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)
                        || !TryInstanceIdFromFileName(fileName, out candidateInstanceId)) continue;
                    summary.Scanned += 1;
                    if (string.Equals(candidateInstanceId, InstanceId(), StringComparison.Ordinal))
                    {
                        summary.Preserved += 1;
                        continue;
                    }
                    InstanceCleanupCandidate candidate;
                    if (!TryInspectCleanupCandidate(fullPath, candidateInstanceId, out candidate))
                    {
                        summary.Preserved += 1;
                        continue;
                    }
                    if (candidate.Damaged) summary.Damaged += 1;
                    else summary.Offline += 1;
                    candidates.Add(candidate);
                }
                if (enumerator.MoveNext()) summary.Truncated = true;
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-InstanceCleanupRetentionDays);
            var offline = candidates.Where(value => !value.Damaged).OrderBy(value => value.LastActivity).ThenBy(value => value.InstanceId, StringComparer.Ordinal).ToList();
            var damaged = candidates.Where(value => value.Damaged).OrderBy(value => value.LastActivity).ThenBy(value => value.InstanceId, StringComparer.Ordinal).ToList();
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PlanCleanup(offline, InstanceCleanupRetainedOffline, cutoff, planned);
            PlanCleanup(damaged, InstanceCleanupRetainedDamaged, cutoff, planned);
            summary.Deferred += Math.Max(0, planned.Count - InstanceCleanupMaxDeleted);

            foreach (var candidate in candidates.Where(value => planned.Contains(value.Path)).OrderBy(value => value.LastActivity).ThenBy(value => value.InstanceId, StringComparer.Ordinal).Take(InstanceCleanupMaxDeleted))
            {
                var deleted = false;
                var locked = false;
                try
                {
                    locked = TryWithCurrentUserMutex("Local\\UnityCli.Registry." + candidate.InstanceId, 0, delegate
                    {
                        InstanceCleanupCandidate current;
                        if (!TryInspectCleanupCandidate(candidate.Path, candidate.InstanceId, out current)) return;
                        if (current.Damaged != candidate.Damaged || current.LastActivity > candidate.LastActivity) return;
                        File.Delete(candidate.Path);
                        deleted = true;
                    });
                }
                catch
                {
                    deleted = false;
                }
                if (deleted) summary.Deleted += 1;
                else if (!locked || File.Exists(candidate.Path)) summary.Deferred += 1;
            }
            summary.Remaining = Math.Max(0, summary.Scanned - summary.Deleted);
            return summary;
        }

        private static void PlanCleanup(List<InstanceCleanupCandidate> ordered, int retained, DateTimeOffset cutoff, HashSet<string> planned)
        {
            var excess = Math.Max(0, ordered.Count - retained);
            for (var index = 0; index < ordered.Count; index++)
            {
                if (index < excess || ordered[index].LastActivity <= cutoff) planned.Add(ordered[index].Path);
            }
        }

        /// <summary>返回false表示活跃、不可确认、链接、占用或不安全路径，调用方必须保留。</summary>
        private static bool TryInspectCleanupCandidate(string path, string expectedInstanceId, out InstanceCleanupCandidate candidate)
        {
            candidate = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                var fallbackActivity = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                if (info.Length > RegistryMaxBytes)
                {
                    candidate = CleanupCandidate(path, expectedInstanceId, fallbackActivity, true);
                    return true;
                }

                Dictionary<string, object> record;
                try
                {
                    record = UnityCliSerializer.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                }
                catch (FormatException)
                {
                    candidate = CleanupCandidate(path, expectedInstanceId, fallbackActivity, true);
                    return true;
                }
                if (record == null || ReadLong(record, "version") != 3L || ReadLong(record, "canonicalizationVersion") != 1L)
                {
                    candidate = CleanupCandidate(path, expectedInstanceId, fallbackActivity, true);
                    return true;
                }
                var identity = ReadDictionary(record, "identity");
                var canonicalPath = ReadString(identity, "canonicalProjectPath");
                var actualInstanceId = ReadString(identity, "instanceId");
                if (string.IsNullOrEmpty(canonicalPath)
                    || !string.Equals(actualInstanceId, expectedInstanceId, StringComparison.Ordinal)
                    || !string.Equals("unity-" + Sha256Utf8(canonicalPath), expectedInstanceId, StringComparison.Ordinal))
                {
                    candidate = CleanupCandidate(path, expectedInstanceId, fallbackActivity, true);
                    return true;
                }
                object rawSessions;
                var sessionValues = record.TryGetValue("sessions", out rawSessions) ? rawSessions as List<object> : null;
                if (sessionValues == null)
                {
                    candidate = CleanupCandidate(path, expectedInstanceId, fallbackActivity, true);
                    return true;
                }
                var sessions = sessionValues.OfType<Dictionary<string, object>>().ToList();
                if (sessions.Count != sessionValues.Count || sessions.Any(IsProcessClaimAliveOrUnverifiable)) return false;
                DateTimeOffset publishedAt;
                if (!DateTimeOffset.TryParse(ReadString(record, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out publishedAt))
                {
                    candidate = CleanupCandidate(path, expectedInstanceId, fallbackActivity, true);
                    return true;
                }
                candidate = CleanupCandidate(path, expectedInstanceId, publishedAt, false);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch { return false; }
        }

        private static InstanceCleanupCandidate CleanupCandidate(string path, string instanceId, DateTimeOffset lastActivity, bool damaged)
        {
            return new InstanceCleanupCandidate { Path = path, InstanceId = instanceId, LastActivity = lastActivity, Damaged = damaged };
        }

        /// <summary>私有Bridge命令让公开instances prune只触发Unity writer，不把删除权交给CLI。</summary>
        private static void RegisterInstanceCleanupCommand(BridgeCommandRegistry registry)
        {
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "internal.instances.prune",
                "internal",
                true,
                30000,
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InstanceCleanupSummary summary;
                    if (!TryExecuteInstanceCleanup(delegate { BeginDirectEffect(request.RequestId); }, out summary))
                        throw new BridgeCommandException("E_UNITY_BUSY", "Another Unity instance is cleaning historical instance records.", null, new[] { "Retry unitycli instances prune after the current cleanup finishes." }, true, 100);
                    var result = new Dictionary<string, object>
                    {
                        { "removed", (long)summary.Deleted },
                        { "remaining", (long)summary.Remaining },
                        { "deferred", (long)summary.Deferred },
                        { "scanned", (long)summary.Scanned },
                        { "truncated", summary.Truncated },
                        { "retention", new Dictionary<string, object>
                            {
                                { "offlineMax", (long)InstanceCleanupRetainedOffline },
                                { "damagedMax", (long)InstanceCleanupRetainedDamaged },
                                { "maxAgeDays", (long)InstanceCleanupRetentionDays },
                                { "scanLimit", (long)InstanceCleanupMaxScanned },
                                { "deleteLimit", (long)InstanceCleanupMaxDeleted }
                            }
                        }
                    };
                    RecordDirectCompletionEvidence(request.RequestId, "internal.instances.prune", InstanceCleanupCompletion, result);
                    return new System.Threading.Tasks.ValueTask<object>(result);
                });
        }

        private static bool IsInstanceCleanupCommand(string command)
        {
            return string.Equals(command, "internal.instances.prune", StringComparison.Ordinal);
        }

        private static UnityCliToolCompletionContract InstanceCleanupCompletionContract()
        {
            return InstanceCleanupCompletion;
        }

        private static bool TryInstanceIdFromFileName(string fileName, out string instanceId)
        {
            instanceId = null;
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            var value = fileName.Substring(0, fileName.Length - 5);
            if (!value.StartsWith("unity-", StringComparison.Ordinal) || value.Length != 70) return false;
            for (var index = 6; index < value.Length; index++)
            {
                var ch = value[index];
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
            }
            instanceId = value;
            return true;
        }
    }
}
#endif
