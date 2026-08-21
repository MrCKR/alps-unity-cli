using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UnityCli
{
    /// <summary>每个Unity target session独占的有界原子准入池；terminal proof由既有业务owner持有。</summary>
    public sealed class UnityCliAdmissionPool
    {
        public enum AdmissionState { Granted, Consumed }
        public enum AdmissionOwnerKind { WorkItem, Coordinator, Recovery }
        public enum AcquireOutcome { Granted, AlreadyConsumed, Closed, Invalid, IdentityMismatch, RecoveryBlocked }
        public enum AuthorizeOutcome { Authorized, AlreadyConsumed, Missing, Expired, IdentityMismatch, Invalid, RecoveryBlocked }

        public sealed class AcquireResult
        {
            public AcquireOutcome Outcome { get; internal set; }
            public string Token { get; internal set; }
            public int RetryAfterMs { get; internal set; }
            public int GrantExpiresInMs { get; internal set; }
            public bool NewlyGranted { get; internal set; }
        }

        public sealed class AdmissionRecord
        {
            public string InstanceId { get; set; }
            public string SessionId { get; set; }
            public string RequestId { get; set; }
            public string RootFingerprint { get; set; }
            public string Token { get; set; }
            public string ListenerId { get; set; }
            public long ListenerGeneration { get; set; }
            public long GrantExpiresAtMs { get; set; }
            public long ExecutionDeadlineMs { get; set; }
            public AdmissionState State { get; set; }
            public AdmissionOwnerKind OwnerKind { get; set; }
            public bool Consumed { get { return State == AdmissionState.Consumed; } }
        }

        public sealed class ConsumedIdentity
        {
            public string RequestId { get; internal set; }
            public string RootFingerprint { get; internal set; }
            public string Token { get; internal set; }
            public string SessionId { get; internal set; }
            public AdmissionOwnerKind OwnerKind { get; internal set; }
        }

        public sealed class Snapshot
        {
            public bool GateClosed { get; internal set; }
            public int Active { get; internal set; }
            public int MaxActive { get; internal set; }
            public int ConfiguredMaxActive { get; internal set; }
            public int ReopenAt { get; internal set; }
            public bool RecoveryBlocked { get; internal set; }
            public string RecoveryDigest { get; internal set; }
            public IReadOnlyList<AdmissionRecord> Records { get; internal set; }
        }

        private const int GrantTtlMs = 2000;
        private const int RetryAfterMs = 100;
        private readonly object sync = new object();
        private readonly Dictionary<string, AdmissionRecord> records = new Dictionary<string, AdmissionRecord>(StringComparer.Ordinal);
        private readonly Func<long> monotonicNowMs;
        private readonly Func<long> utcNowMs;
        private readonly string instanceId;
        private readonly string sessionId;
        private readonly int maxActive;
        private readonly int reopenAt;
        private int effectiveMaxActive;
        private bool gateClosed;
        private bool recoveryBlocked;

        public UnityCliAdmissionPool(string instanceId, string sessionId, int maxActive, int reopenAt, Func<long> monotonicNowMs = null, Func<long> utcNowMs = null)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Admission pool instanceId is required.", "instanceId");
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Admission pool sessionId is required.", "sessionId");
            if (maxActive < 2 || reopenAt < 1 || reopenAt >= maxActive) throw new ArgumentOutOfRangeException("maxActive", "Admission pool requires maxActive >= 2 and 1 <= reopenAt < maxActive.");
            this.instanceId = instanceId;
            this.sessionId = sessionId;
            this.maxActive = maxActive;
            this.reopenAt = reopenAt;
            effectiveMaxActive = maxActive;
            this.monotonicNowMs = monotonicNowMs ?? MonotonicMilliseconds;
            this.utcNowMs = utcNowMs ?? delegate { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); };
        }

        /// <summary>检查高低水位并在同一锁内发放一次性token；池外等待者不进入内存。</summary>
        public AcquireResult Acquire(string requestId, string rootFingerprint, string listenerId, long listenerGeneration)
        {
            lock (sync)
            {
                try
                {
                    CleanupExpiredGrants();
                    if (recoveryBlocked) return Result(AcquireOutcome.RecoveryBlocked);
                    if (!ValidRequestId(requestId) || !IsSha256(rootFingerprint) || !ValidListener(listenerId, listenerGeneration)) return Result(AcquireOutcome.Invalid);

                    AdmissionRecord existing;
                    if (records.TryGetValue(requestId, out existing))
                    {
                        if (!string.Equals(existing.RootFingerprint, rootFingerprint, StringComparison.Ordinal)) return Result(AcquireOutcome.IdentityMismatch);
                        if (existing.State == AdmissionState.Granted && (!string.Equals(existing.ListenerId, listenerId, StringComparison.Ordinal) || existing.ListenerGeneration != listenerGeneration))
                        {
                            records.Remove(requestId);
                            UpdateGateAfterRemoval();
                        }
                        else if (existing.State == AdmissionState.Consumed) return Result(AcquireOutcome.AlreadyConsumed);
                        else return Result(AcquireOutcome.Granted, existing.Token, RemainingGrantMs(existing), false);
                    }

                    if (gateClosed)
                    {
                        if (records.Count <= reopenAt) gateClosed = false;
                        else return Result(AcquireOutcome.Closed);
                    }
                    if (records.Count >= maxActive)
                    {
                        gateClosed = true;
                        return Result(AcquireOutcome.Closed);
                    }

                    var token = NewToken();
                    records.Add(requestId, new AdmissionRecord
                    {
                        InstanceId = instanceId,
                        SessionId = sessionId,
                        RequestId = requestId,
                        RootFingerprint = rootFingerprint,
                        Token = token,
                        ListenerId = listenerId,
                        ListenerGeneration = listenerGeneration,
                        GrantExpiresAtMs = checked(monotonicNowMs() + GrantTtlMs),
                        ExecutionDeadlineMs = 0,
                        State = AdmissionState.Granted,
                        OwnerKind = AdmissionOwnerKind.WorkItem
                    });
                    if (records.Count == maxActive) gateClosed = true;
                    return Result(AcquireOutcome.Granted, token, GrantTtlMs, true);
                }
                catch
                {
                    recoveryBlocked = true;
                    gateClosed = true;
                    throw;
                }
            }
        }

        /// <summary>首次业务POST只允许一次Granted→Consumed；generation mismatch原子撤销旧grant。</summary>
        public AuthorizeOutcome Authorize(string requestId, string rootFingerprint, string token, string listenerId, long listenerGeneration, long executionDeadlineMs)
        {
            lock (sync)
            {
                try
                {
                    CleanupExpiredGrants();
                    if (recoveryBlocked) return AuthorizeOutcome.RecoveryBlocked;
                    if (!ValidRequestId(requestId) || !IsSha256(rootFingerprint) || !IsToken(token) || !ValidListener(listenerId, listenerGeneration) || executionDeadlineMs <= utcNowMs()) return AuthorizeOutcome.Invalid;
                    AdmissionRecord entry;
                    if (!records.TryGetValue(requestId, out entry)) return AuthorizeOutcome.Missing;
                    if (!string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal) || !FixedEquals(entry.Token, token)) return AuthorizeOutcome.IdentityMismatch;
                    if (entry.State == AdmissionState.Consumed)
                        return entry.ExecutionDeadlineMs == executionDeadlineMs ? AuthorizeOutcome.AlreadyConsumed : AuthorizeOutcome.IdentityMismatch;
                    if (entry.GrantExpiresAtMs <= monotonicNowMs())
                    {
                        records.Remove(requestId);
                        UpdateGateAfterRemoval();
                        return AuthorizeOutcome.Expired;
                    }
                    if (!string.Equals(entry.ListenerId, listenerId, StringComparison.Ordinal) || entry.ListenerGeneration != listenerGeneration)
                    {
                        records.Remove(requestId);
                        UpdateGateAfterRemoval();
                        return AuthorizeOutcome.IdentityMismatch;
                    }
                    entry.State = AdmissionState.Consumed;
                    entry.OwnerKind = AdmissionOwnerKind.WorkItem;
                    entry.ExecutionDeadlineMs = executionDeadlineMs;
                    entry.GrantExpiresAtMs = 0;
                    return AuthorizeOutcome.Authorized;
                }
                catch
                {
                    recoveryBlocked = true;
                    gateClosed = true;
                    throw;
                }
            }
        }

        /// <summary>同listener generation的已消费root只做attach检查，不再次消费token。</summary>
        public bool AttachConsumed(string requestId, string rootFingerprint, string listenerId, long listenerGeneration, long executionDeadlineMs)
        {
            lock (sync)
            {
                if (recoveryBlocked) return false;
                AdmissionRecord entry;
                return records.TryGetValue(requestId, out entry) && entry.State == AdmissionState.Consumed
                    && string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal)
                    && string.Equals(entry.ListenerId, listenerId, StringComparison.Ordinal) && entry.ListenerGeneration == listenerGeneration
                    && entry.ExecutionDeadlineMs == executionDeadlineMs;
            }
        }

        /// <summary>durable owner证明同root恢复后显式重绑listener generation，不改变active。</summary>
        public bool RecoverConsumed(string requestId, string rootFingerprint, string listenerId, long listenerGeneration, long executionDeadlineMs)
        {
            lock (sync)
            {
                if (recoveryBlocked || !ValidListener(listenerId, listenerGeneration)) return false;
                AdmissionRecord entry;
                if (!records.TryGetValue(requestId, out entry) || entry.State != AdmissionState.Consumed
                    || !string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal)
                    || entry.ExecutionDeadlineMs != executionDeadlineMs) return false;
                entry.ListenerId = listenerId;
                entry.ListenerGeneration = listenerGeneration;
                return true;
            }
        }

        /// <summary>terminal owner在写marker/绑定状态时冻结独立identity，禁止完成时自读当前owner。</summary>
        public bool TryGetConsumedIdentity(string requestId, out ConsumedIdentity identity)
        {
            lock (sync)
            {
                identity = null;
                AdmissionRecord entry;
                if (!records.TryGetValue(requestId, out entry) || !ValidConsumedRecord(entry)) return false;
                identity = new ConsumedIdentity { RequestId = entry.RequestId, RootFingerprint = entry.RootFingerprint, Token = entry.Token, SessionId = entry.SessionId, OwnerKind = entry.OwnerKind };
                return true;
            }
        }

        /// <summary>只有当前WorkItem owner可按完整identity原子转移给coordinator或reload recovery。</summary>
        public bool TransferOwner(string requestId, string rootFingerprint, string token, string ownerSessionId, AdmissionOwnerKind targetOwner)
        {
            lock (sync)
            {
                if (targetOwner == AdmissionOwnerKind.WorkItem) return false;
                AdmissionRecord entry;
                if (!records.TryGetValue(requestId, out entry) || !ValidConsumedRecord(entry)
                    || !string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal) || !FixedEquals(entry.Token, token)
                    || !string.Equals(entry.SessionId, ownerSessionId, StringComparison.Ordinal)) return false;
                if (entry.OwnerKind == targetOwner) return true;
                if (entry.OwnerKind != AdmissionOwnerKind.WorkItem) return false;
                entry.OwnerKind = targetOwner;
                return true;
            }
        }

        /// <summary>Unity terminal owner必须提交冻结的完整identity与owner kind，旧owner不能删除同requestId后继。</summary>
        public bool CompleteTrusted(string requestId, string rootFingerprint, string token, string ownerSessionId, AdmissionOwnerKind ownerKind)
        {
            lock (sync)
            {
                AdmissionRecord entry;
                if (!records.TryGetValue(requestId, out entry)) return true;
                if (!ValidConsumedRecord(entry) || !string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal)
                    || !FixedEquals(entry.Token, token) || !string.Equals(entry.SessionId, ownerSessionId, StringComparison.Ordinal)
                    || entry.OwnerKind != ownerKind) return false;
                records.Remove(requestId);
                UpdateGateAfterRemoval();
                return true;
            }
        }

        /// <summary>未消费grant只能按完整identity执行Granted→revoked→removed。</summary>
        public bool Revoke(string requestId, string rootFingerprint, string token)
        {
            lock (sync)
            {
                AdmissionRecord entry;
                if (!records.TryGetValue(requestId, out entry)) return true;
                if (!RecordOwnedByPool(entry) || entry.State != AdmissionState.Granted || !string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal) || !FixedEquals(entry.Token, token)) return false;
                records.Remove(requestId);
                UpdateGateAfterRemoval();
                return true;
            }
        }

        public void RevokeUnconsumed(string listenerId, long listenerGeneration)
        {
            lock (sync)
            {
                var remove = records.Values.Where(entry => entry.State == AdmissionState.Granted && string.Equals(entry.ListenerId, listenerId, StringComparison.Ordinal) && entry.ListenerGeneration == listenerGeneration).Select(entry => entry.RequestId).ToArray();
                foreach (var requestId in remove) records.Remove(requestId);
                UpdateGateAfterRemoval();
            }
        }

        public Snapshot Capture()
        {
            lock (sync)
            {
                CleanupExpiredGrants();
                var values = records.Values.OrderBy(value => value.RequestId, StringComparer.Ordinal).Select(Clone).ToArray();
                return new Snapshot
                {
                    GateClosed = gateClosed,
                    Active = values.Length,
                    MaxActive = effectiveMaxActive,
                    ConfiguredMaxActive = maxActive,
                    ReopenAt = reopenAt,
                    RecoveryBlocked = recoveryBlocked,
                    Records = values,
                    RecoveryDigest = ComputeRecoveryDigest(values, values.Length, gateClosed, maxActive, reopenAt)
                };
            }
        }

        /// <summary>只在全部consumed active、声明count和digest验证成功后原子替换；不提供清空恢复fallback。</summary>
        public void Restore(IEnumerable<AdmissionRecord> values, int expectedActiveCount, bool wasClosed, string expectedRecoveryDigest, int sourceMaxActive, int sourceReopenAt)
        {
            lock (sync)
            {
                try
                {
                    if (sourceMaxActive < 2 || sourceReopenAt < 1 || sourceReopenAt >= sourceMaxActive) throw new InvalidOperationException("Admission source configuration is invalid.");
                    if (expectedActiveCount < 0 || expectedActiveCount > sourceMaxActive) throw new InvalidOperationException("Admission active count is invalid.");
                    var restored = new Dictionary<string, AdmissionRecord>(StringComparer.Ordinal);
                    foreach (var value in values ?? Array.Empty<AdmissionRecord>())
                    {
                        if (restored.Count >= sourceMaxActive || !ValidRestoredRecord(value)) throw new InvalidOperationException("Admission restore record is invalid.");
                        restored.Add(value.RequestId, Clone(value));
                    }
                    if (restored.Count != expectedActiveCount) throw new InvalidOperationException("Admission active count does not match its declaration.");
                    var actualDigest = ComputeRecoveryDigest(restored.Values, expectedActiveCount, wasClosed, sourceMaxActive, sourceReopenAt);
                    if (!IsSha256(expectedRecoveryDigest) || !FixedEquals(actualDigest, expectedRecoveryDigest)) throw new InvalidOperationException("Admission recovery digest does not match the restored state.");
                    records.Clear();
                    foreach (var pair in restored) records.Add(pair.Key, pair.Value);
                    recoveryBlocked = false;
                    effectiveMaxActive = Math.Max(maxActive, records.Count);
                    gateClosed = wasClosed || records.Count > maxActive;
                    if (gateClosed && records.Count <= reopenAt) { gateClosed = false; effectiveMaxActive = maxActive; }
                    if (!gateClosed && records.Count >= maxActive) gateClosed = true;
                }
                catch
                {
                    recoveryBlocked = true;
                    gateClosed = true;
                    throw;
                }
            }
        }

        public void BlockRecovery() { lock (sync) { recoveryBlocked = true; gateClosed = true; } }
        public void Reset() { lock (sync) { records.Clear(); effectiveMaxActive = maxActive; gateClosed = false; recoveryBlocked = false; } }

        private void CleanupExpiredGrants()
        {
            var current = monotonicNowMs();
            var remove = records.Values.Where(entry => entry.State == AdmissionState.Granted && entry.GrantExpiresAtMs <= current).Select(entry => entry.RequestId).ToArray();
            foreach (var requestId in remove) records.Remove(requestId);
            if (remove.Length > 0) UpdateGateAfterRemoval();
        }

        private void UpdateGateAfterRemoval()
        {
            if (recoveryBlocked) { gateClosed = true; return; }
            if (gateClosed && records.Count <= reopenAt) { gateClosed = false; effectiveMaxActive = maxActive; }
        }

        private string ComputeRecoveryDigest(IEnumerable<AdmissionRecord> values, int expectedActiveCount, bool wasClosed, int digestMaxActive, int digestReopenAt)
        {
            var builder = new StringBuilder();
            Append(builder, instanceId); Append(builder, sessionId); Append(builder, digestMaxActive.ToString(CultureInfo.InvariantCulture));
            Append(builder, digestReopenAt.ToString(CultureInfo.InvariantCulture)); Append(builder, expectedActiveCount.ToString(CultureInfo.InvariantCulture)); Append(builder, wasClosed ? "1" : "0");
            foreach (var value in values.OrderBy(item => item.RequestId, StringComparer.Ordinal))
            {
                Append(builder, value.InstanceId); Append(builder, value.SessionId); Append(builder, value.RequestId); Append(builder, value.RootFingerprint); Append(builder, value.Token);
                Append(builder, value.ListenerId); Append(builder, value.ListenerGeneration.ToString(CultureInfo.InvariantCulture)); Append(builder, value.GrantExpiresAtMs.ToString(CultureInfo.InvariantCulture));
                Append(builder, value.ExecutionDeadlineMs.ToString(CultureInfo.InvariantCulture)); Append(builder, ((int)value.State).ToString(CultureInfo.InvariantCulture)); Append(builder, ((int)value.OwnerKind).ToString(CultureInfo.InvariantCulture));
            }
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var result = new StringBuilder(64);
                foreach (var value in hash) result.Append(value.ToString("x2"));
                return result.ToString();
            }
        }

        private static void Append(StringBuilder builder, string value)
        {
            var item = value ?? string.Empty;
            builder.Append(item.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item).Append('|');
        }

        private int RemainingGrantMs(AdmissionRecord value) { return checked((int)Math.Max(0, Math.Min(GrantTtlMs, value.GrantExpiresAtMs - monotonicNowMs()))); }
        private static AcquireResult Result(AcquireOutcome outcome, string token = null, int grantExpiresInMs = 0, bool newlyGranted = false) { return new AcquireResult { Outcome = outcome, Token = token, RetryAfterMs = RetryAfterMs, GrantExpiresInMs = grantExpiresInMs, NewlyGranted = newlyGranted }; }
        private bool RecordOwnedByPool(AdmissionRecord value) { return value != null && string.Equals(value.InstanceId, instanceId, StringComparison.Ordinal) && string.Equals(value.SessionId, sessionId, StringComparison.Ordinal) && ValidRequestId(value.RequestId) && IsSha256(value.RootFingerprint) && IsToken(value.Token) && ValidListener(value.ListenerId, value.ListenerGeneration); }
        private bool ValidConsumedRecord(AdmissionRecord value) { return RecordOwnedByPool(value) && value.State == AdmissionState.Consumed && Enum.IsDefined(typeof(AdmissionOwnerKind), value.OwnerKind) && value.GrantExpiresAtMs == 0 && value.ExecutionDeadlineMs > 0; }
        private AdmissionRecord Clone(AdmissionRecord value) { return new AdmissionRecord { InstanceId = value.InstanceId, SessionId = value.SessionId, RequestId = value.RequestId, RootFingerprint = value.RootFingerprint, Token = value.Token, ListenerId = value.ListenerId, ListenerGeneration = value.ListenerGeneration, GrantExpiresAtMs = value.GrantExpiresAtMs, ExecutionDeadlineMs = value.ExecutionDeadlineMs, State = value.State, OwnerKind = value.OwnerKind }; }
        private bool ValidRestoredRecord(AdmissionRecord value) { return ValidConsumedRecord(value); }
        private static bool ValidListener(string listenerId, long generation) { return !string.IsNullOrWhiteSpace(listenerId) && listenerId.Length <= 200 && generation >= 1; }

        private static string NewToken()
        {
            var bytes = new byte[32];
            using (var generator = RandomNumberGenerator.Create()) generator.GetBytes(bytes);
            var result = new StringBuilder(64);
            foreach (var value in bytes) result.Append(value.ToString("x2"));
            return result.ToString();
        }

        private static bool FixedEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            var leftBytes = Encoding.UTF8.GetBytes(left); var rightBytes = Encoding.UTF8.GetBytes(right);
            if (leftBytes.Length != rightBytes.Length) return false;
            var difference = 0; for (var index = 0; index < leftBytes.Length; index++) difference |= leftBytes[index] ^ rightBytes[index];
            return difference == 0;
        }

        private static long MonotonicMilliseconds() { return checked((long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000d / System.Diagnostics.Stopwatch.Frequency))); }
        private static bool ValidRequestId(string value) { return !string.IsNullOrWhiteSpace(value) && value.Length <= 200; }
        private static bool IsToken(string value) { return IsHex(value, 64); }
        private static bool IsSha256(string value) { return IsHex(value, 64); }
        private static bool IsHex(string value, int length) { if (string.IsNullOrEmpty(value) || value.Length != length) return false; foreach (var character in value) if (!Uri.IsHexDigit(character)) return false; return true; }
    }
}
