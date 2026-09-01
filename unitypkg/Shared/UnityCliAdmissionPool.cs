using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UnityCli
{
    /// <summary>每个Unity target session独占的有界原子准入池；owner state是唯一生命周期真相。</summary>
    public sealed class UnityCliAdmissionPool
    {
        public enum AdmissionOwnerState { Granted, WorkItem, Coordinator, Continuation, Recovery, TerminalCleanup }
        public enum AcquireOutcome { Granted, AlreadyConsumed, Closed, Invalid, IdentityMismatch, RecoveryBlocked }
        public enum AuthorizeOutcome { Authorized, AlreadyConsumed, Missing, IdentityMismatch, Invalid, RecoveryBlocked }
        public enum ExactIdentityOutcome { ExactCurrent, DefinitelyAbsent, IdentityMismatchOrBlocked }
        public enum CompletionOutcome { ExactCurrentCompleted, DefinitelyAbsent, IdentityMismatchOrBlocked }

        public sealed class AcquireResult
        {
            public AcquireOutcome Outcome { get; internal set; }
            public string Token { get; internal set; }
            public int RetryAfterMs { get; internal set; }
            public int GrantExpiresInMs { get; internal set; }
            public bool NewlyGranted { get; internal set; }
        }

        public sealed class AuthorizeResult
        {
            public AuthorizeOutcome Outcome { get; internal set; }
            public ConsumedIdentity Identity { get; internal set; }
            public bool NewlyConsumed { get; internal set; }
        }

        public sealed class AdmissionRecord
        {
            public string InstanceId { get; set; }
            public string SessionId { get; set; }
            public string RequestId { get; set; }
            public string RootFingerprint { get; set; }
            public string RequestDigest { get; set; }
            public string Token { get; set; }
            public string ListenerId { get; set; }
            public long ListenerGeneration { get; set; }
            public long GrantExpiresAtMs { get; set; }
            public long ExecutionDeadlineMs { get; set; }
            public AdmissionOwnerState OwnerState { get; set; }
            public AdmissionOwnerState IntendedOwnerState { get; set; }
            public bool TransferCommitted { get; set; }
            public bool Consumed { get { return OwnerState != AdmissionOwnerState.Granted; } }
        }

        public sealed class ConsumedIdentity
        {
            public string RequestId { get; internal set; }
            public string RootFingerprint { get; internal set; }
            public string RequestDigest { get; internal set; }
            public string Token { get; internal set; }
            public string SessionId { get; internal set; }
            public string ListenerId { get; internal set; }
            public long ListenerGeneration { get; internal set; }
            public long ExecutionDeadlineMs { get; internal set; }
            public AdmissionOwnerState OwnerState { get; internal set; }
        }

        /// <summary>跨assembly owner只通过完整字段创建不可变transition identity。</summary>
        public static ConsumedIdentity CreateConsumedIdentity(string requestId, string rootFingerprint, string requestDigest, string token, string ownerSessionId, AdmissionOwnerState ownerState, string listenerId, long listenerGeneration, long executionDeadlineMs)
        {
            if (!ValidRequestId(requestId) || !IsSha256(rootFingerprint) || !IsSha256(requestDigest) || !IsToken(token)
                || string.IsNullOrWhiteSpace(ownerSessionId) || !ValidListener(listenerId, listenerGeneration) || executionDeadlineMs <= 0
                || ownerState == AdmissionOwnerState.Granted || !Enum.IsDefined(typeof(AdmissionOwnerState), ownerState))
                throw new ArgumentException("Admission consumed identity is invalid.");
            return new ConsumedIdentity { RequestId = requestId, RootFingerprint = rootFingerprint, RequestDigest = requestDigest, Token = token, SessionId = ownerSessionId, ListenerId = listenerId, ListenerGeneration = listenerGeneration, ExecutionDeadlineMs = executionDeadlineMs, OwnerState = ownerState };
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
                        if (existing.OwnerState == AdmissionOwnerState.Granted && (!string.Equals(existing.ListenerId, listenerId, StringComparison.Ordinal) || existing.ListenerGeneration != listenerGeneration))
                        {
                            records.Remove(requestId);
                            UpdateGateAfterRemoval();
                        }
                        else if (existing.Consumed) return Result(AcquireOutcome.AlreadyConsumed);
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
                        OwnerState = AdmissionOwnerState.Granted,
                        IntendedOwnerState = AdmissionOwnerState.Granted,
                        TransferCommitted = true
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

        /// <summary>Granted→WorkItem与完整identity冻结共享一个线性化点。</summary>
        public AuthorizeResult AuthorizeAndCapture(string requestId, string rootFingerprint, string requestDigest, string token, string listenerId, long listenerGeneration, long executionDeadlineMs)
        {
            lock (sync)
            {
                try
                {
                    CleanupExpiredGrants();
                    if (recoveryBlocked) return Authorized(AuthorizeOutcome.RecoveryBlocked);
                    if (!ValidRequestId(requestId) || !IsSha256(rootFingerprint) || !IsSha256(requestDigest) || !IsToken(token) || !ValidListener(listenerId, listenerGeneration) || executionDeadlineMs <= utcNowMs()) return Authorized(AuthorizeOutcome.Invalid);
                    AdmissionRecord entry;
                    if (!records.TryGetValue(requestId, out entry)) return Authorized(AuthorizeOutcome.Missing);
                    if (!string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal) || !FixedEquals(entry.Token, token)) return Authorized(AuthorizeOutcome.IdentityMismatch);
                    if (entry.Consumed)
                        return entry.ExecutionDeadlineMs == executionDeadlineMs && string.Equals(entry.RequestDigest, requestDigest, StringComparison.Ordinal)
                            ? Authorized(AuthorizeOutcome.AlreadyConsumed, Identity(entry))
                            : Authorized(AuthorizeOutcome.IdentityMismatch);
                    if (!string.Equals(entry.ListenerId, listenerId, StringComparison.Ordinal) || entry.ListenerGeneration != listenerGeneration)
                    {
                        records.Remove(requestId);
                        UpdateGateAfterRemoval();
                        return Authorized(AuthorizeOutcome.IdentityMismatch);
                    }
                    entry.RequestDigest = requestDigest;
                    entry.OwnerState = AdmissionOwnerState.WorkItem;
                    entry.IntendedOwnerState = AdmissionOwnerState.WorkItem;
                    entry.TransferCommitted = true;
                    entry.ExecutionDeadlineMs = executionDeadlineMs;
                    entry.GrantExpiresAtMs = 0;
                    return Authorized(AuthorizeOutcome.Authorized, Identity(entry), true);
                }
                catch
                {
                    recoveryBlocked = true;
                    gateClosed = true;
                    throw;
                }
            }
        }

        /// <summary>仅撤销本次新消费且尚未发布的WorkItem；完整identity避免删除后继。</summary>
        public bool CompensateAuthorization(ConsumedIdentity identity)
        {
            lock (sync)
            {
                AdmissionRecord entry;
                if (identity == null || !records.TryGetValue(identity.RequestId, out entry)) return true;
                if (!Matches(entry, identity) || entry.OwnerState != AdmissionOwnerState.WorkItem
                    || entry.IntendedOwnerState != AdmissionOwnerState.WorkItem || !entry.TransferCommitted) return false;
                records.Remove(identity.RequestId);
                UpdateGateAfterRemoval();
                return true;
            }
        }

        /// <summary>同listener generation的已消费root只做attach检查，不再次消费token。</summary>
        public bool AttachConsumed(string requestId, string rootFingerprint, string requestDigest, string listenerId, long listenerGeneration, long executionDeadlineMs)
        {
            lock (sync)
            {
                if (recoveryBlocked) return false;
                AdmissionRecord entry;
                return records.TryGetValue(requestId, out entry) && ValidConsumedRecord(entry)
                    && string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal)
                    && string.Equals(entry.RequestDigest, requestDigest, StringComparison.Ordinal)
                    && string.Equals(entry.ListenerId, listenerId, StringComparison.Ordinal) && entry.ListenerGeneration == listenerGeneration
                    && entry.ExecutionDeadlineMs == executionDeadlineMs;
            }
        }

        /// <summary>durable owner证明同root恢复后显式重绑listener generation，不改变owner。</summary>
        public bool RecoverConsumed(string requestId, string rootFingerprint, string requestDigest, string listenerId, long listenerGeneration, long executionDeadlineMs)
        {
            lock (sync)
            {
                if (recoveryBlocked || !ValidListener(listenerId, listenerGeneration)) return false;
                AdmissionRecord entry;
                if (!records.TryGetValue(requestId, out entry) || !ValidConsumedRecord(entry)
                    || !string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal)
                    || !string.Equals(entry.RequestDigest, requestDigest, StringComparison.Ordinal)
                    || entry.ExecutionDeadlineMs != executionDeadlineMs) return false;
                entry.ListenerId = listenerId;
                entry.ListenerGeneration = listenerGeneration;
                return true;
            }
        }

        /// <summary>marker与pool同时落在reload窗口时按完整identity重建唯一consumed record。</summary>
        public bool RestoreConsumed(ConsumedIdentity identity, AdmissionOwnerState intendedOwner, bool transferCommitted)
        {
            lock (sync)
            {
                if (recoveryBlocked || identity == null || identity.OwnerState == AdmissionOwnerState.Granted
                    || !Enum.IsDefined(typeof(AdmissionOwnerState), intendedOwner) || intendedOwner == AdmissionOwnerState.Granted
                    || !ValidRequestId(identity.RequestId) || !IsSha256(identity.RootFingerprint) || !IsSha256(identity.RequestDigest)
                    || !IsToken(identity.Token) || !string.Equals(identity.SessionId, sessionId, StringComparison.Ordinal)
                    || !ValidListener(identity.ListenerId, identity.ListenerGeneration) || identity.ExecutionDeadlineMs <= 0
                    || !IsLegalOwnerTransfer(identity.OwnerState, intendedOwner)) return false;
                AdmissionRecord existing;
                if (records.TryGetValue(identity.RequestId, out existing))
                {
                    var expectedIntendedOwner = transferCommitted ? identity.OwnerState : intendedOwner;
                    return Matches(existing, identity)
                        && existing.IntendedOwnerState == expectedIntendedOwner
                        && existing.TransferCommitted == transferCommitted;
                }
                if (records.Count >= maxActive) { gateClosed = true; return false; }
                records.Add(identity.RequestId, new AdmissionRecord
                {
                    InstanceId = instanceId,
                    SessionId = sessionId,
                    RequestId = identity.RequestId,
                    RootFingerprint = identity.RootFingerprint,
                    RequestDigest = identity.RequestDigest,
                    Token = identity.Token,
                    ListenerId = identity.ListenerId,
                    ListenerGeneration = identity.ListenerGeneration,
                    GrantExpiresAtMs = 0,
                    ExecutionDeadlineMs = identity.ExecutionDeadlineMs,
                    OwnerState = identity.OwnerState,
                    IntendedOwnerState = transferCommitted ? identity.OwnerState : intendedOwner,
                    TransferCommitted = transferCommitted
                });
                effectiveMaxActive = Math.Max(effectiveMaxActive, records.Count);
                if (records.Count >= maxActive) gateClosed = true;
                return true;
            }
        }

        /// <summary>冻结当前consumed identity；后续转移与完成仍必须提交全部字段和expected owner。</summary>
        public bool TryGetConsumedIdentity(string requestId, out ConsumedIdentity identity)
        {
            lock (sync)
            {
                identity = null;
                AdmissionRecord entry;
                if (!records.TryGetValue(requestId, out entry) || !ValidConsumedRecord(entry)) return false;
                identity = Identity(entry);
                return true;
            }
        }

        /// <summary>持久化层写完intent后登记expected→intended；重复同一intent幂等。</summary>
        public bool PrepareOwnerTransfer(ConsumedIdentity identity, AdmissionOwnerState intendedOwner)
        {
            lock (sync)
            {
                AdmissionRecord entry;
                if (identity == null || !records.TryGetValue(identity.RequestId, out entry) || !Matches(entry, identity)) return false;
                if (entry.TransferCommitted && entry.OwnerState == intendedOwner && entry.IntendedOwnerState == intendedOwner) return true;
                if (!entry.TransferCommitted) return entry.OwnerState == identity.OwnerState && entry.IntendedOwnerState == intendedOwner;
                if (!LegalTransfer(entry.OwnerState, intendedOwner)) return false;
                entry.IntendedOwnerState = intendedOwner;
                entry.TransferCommitted = false;
                return true;
            }
        }

        /// <summary>只提交已登记的exact transfer；已提交同一target时幂等返回冻结后继identity。</summary>
        public bool CommitOwnerTransfer(ConsumedIdentity identity, AdmissionOwnerState intendedOwner, out ConsumedIdentity successor)
        {
            lock (sync)
            {
                successor = null;
                AdmissionRecord entry;
                if (identity == null || !records.TryGetValue(identity.RequestId, out entry)) return false;
                if (entry.TransferCommitted && entry.OwnerState == intendedOwner && entry.IntendedOwnerState == intendedOwner
                    && MatchesIdentityFields(entry, identity) && (identity.OwnerState == intendedOwner || LegalTransfer(identity.OwnerState, intendedOwner)))
                {
                    successor = Identity(entry);
                    return true;
                }
                if (!Matches(entry, identity) || entry.TransferCommitted || entry.IntendedOwnerState != intendedOwner
                    || !LegalTransfer(entry.OwnerState, intendedOwner)) return false;
                entry.OwnerState = intendedOwner;
                entry.IntendedOwnerState = intendedOwner;
                entry.TransferCommitted = true;
                successor = Identity(entry);
                return true;
            }
        }

        /// <summary>transfer marker按冻结字段识别当前root；owner state由同一锁内的current record另行裁决。</summary>
        public ExactIdentityOutcome InspectExactIdentityFields(ConsumedIdentity identity)
        {
            lock (sync)
            {
                if (identity == null || recoveryBlocked) return ExactIdentityOutcome.IdentityMismatchOrBlocked;
                AdmissionRecord entry;
                if (!records.TryGetValue(identity.RequestId, out entry)) return ExactIdentityOutcome.DefinitelyAbsent;
                return ValidConsumedRecord(entry) && MatchesIdentityFields(entry, identity)
                    ? ExactIdentityOutcome.ExactCurrent
                    : ExactIdentityOutcome.IdentityMismatchOrBlocked;
            }
        }

        /// <summary>只有WorkItem、Continuation或TerminalCleanup可完成；返回值保留absent与mismatch差异。</summary>
        public CompletionOutcome CompleteTrusted(ConsumedIdentity identity)
        {
            lock (sync)
            {
                if (identity == null || recoveryBlocked) return CompletionOutcome.IdentityMismatchOrBlocked;
                AdmissionRecord entry;
                if (!records.TryGetValue(identity.RequestId, out entry)) return CompletionOutcome.DefinitelyAbsent;
                if (!ValidConsumedRecord(entry) || !Matches(entry, identity) || !CanComplete(entry.OwnerState))
                    return CompletionOutcome.IdentityMismatchOrBlocked;
                if (entry.OwnerState != AdmissionOwnerState.WorkItem
                    && (!entry.TransferCommitted || entry.IntendedOwnerState != entry.OwnerState))
                    return CompletionOutcome.IdentityMismatchOrBlocked;
                records.Remove(identity.RequestId);
                UpdateGateAfterRemoval();
                return CompletionOutcome.ExactCurrentCompleted;
            }
        }

        public static bool IsLegalOwnerTransfer(AdmissionOwnerState source, AdmissionOwnerState target)
        {
            return source == target || LegalTransfer(source, target);
        }

        /// <summary>未消费grant只能按完整identity执行Granted→removed。</summary>
        public bool Revoke(string requestId, string rootFingerprint, string token)
        {
            lock (sync)
            {
                AdmissionRecord entry;
                if (!records.TryGetValue(requestId, out entry)) return true;
                if (!RecordOwnedByPool(entry) || entry.OwnerState != AdmissionOwnerState.Granted
                    || !string.Equals(entry.RootFingerprint, rootFingerprint, StringComparison.Ordinal) || !FixedEquals(entry.Token, token)) return false;
                records.Remove(requestId);
                UpdateGateAfterRemoval();
                return true;
            }
        }

        public void RevokeUnconsumed(string listenerId, long listenerGeneration)
        {
            lock (sync)
            {
                var remove = records.Values.Where(entry => entry.OwnerState == AdmissionOwnerState.Granted && string.Equals(entry.ListenerId, listenerId, StringComparison.Ordinal) && entry.ListenerGeneration == listenerGeneration).Select(entry => entry.RequestId).ToArray();
                foreach (var requestId in remove) records.Remove(requestId);
                UpdateGateAfterRemoval();
            }
        }

        public Snapshot Capture()
        {
            lock (sync)
            {
                CleanupExpiredGrants();
                return SnapshotOf(records.Values.OrderBy(value => value.RequestId, StringComparer.Ordinal).Select(Clone).ToArray(), gateClosed, maxActive);
            }
        }

        /// <summary>reload快照排除可撤销grant；digest与active count只覆盖durable consumed owner。</summary>
        public Snapshot CaptureRecovery()
        {
            lock (sync)
            {
                CleanupExpiredGrants();
                var values = records.Values.Where(value => value.Consumed).OrderBy(value => value.RequestId, StringComparer.Ordinal).Select(Clone).ToArray();
                return SnapshotOf(values, values.Length >= maxActive || (gateClosed && values.Length > reopenAt), Math.Max(maxActive, values.Length));
            }
        }

        /// <summary>只恢复当前启动的consumed owner；未提交transfer由durable marker reconciliation裁决。</summary>
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

        private Snapshot SnapshotOf(AdmissionRecord[] values, bool closed, int digestMaxActive)
        {
            return new Snapshot
            {
                GateClosed = closed,
                Active = values.Length,
                MaxActive = effectiveMaxActive,
                ConfiguredMaxActive = digestMaxActive,
                ReopenAt = reopenAt,
                RecoveryBlocked = recoveryBlocked,
                Records = values,
                RecoveryDigest = ComputeRecoveryDigest(values, values.Length, closed, digestMaxActive, reopenAt)
            };
        }

        private void CleanupExpiredGrants()
        {
            var current = monotonicNowMs();
            var remove = records.Values.Where(entry => entry.OwnerState == AdmissionOwnerState.Granted && entry.GrantExpiresAtMs <= current).Select(entry => entry.RequestId).ToArray();
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
                Append(builder, value.InstanceId); Append(builder, value.SessionId); Append(builder, value.RequestId); Append(builder, value.RootFingerprint); Append(builder, value.RequestDigest); Append(builder, value.Token);
                Append(builder, value.ListenerId); Append(builder, value.ListenerGeneration.ToString(CultureInfo.InvariantCulture)); Append(builder, value.GrantExpiresAtMs.ToString(CultureInfo.InvariantCulture));
                Append(builder, value.ExecutionDeadlineMs.ToString(CultureInfo.InvariantCulture)); Append(builder, ((int)value.OwnerState).ToString(CultureInfo.InvariantCulture));
                Append(builder, ((int)value.IntendedOwnerState).ToString(CultureInfo.InvariantCulture)); Append(builder, value.TransferCommitted ? "1" : "0");
            }
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var result = new StringBuilder(64);
                foreach (var value in hash) result.Append(value.ToString("x2"));
                return result.ToString();
            }
        }

        private static bool LegalTransfer(AdmissionOwnerState source, AdmissionOwnerState target)
        {
            if (source == AdmissionOwnerState.WorkItem)
                return target == AdmissionOwnerState.Coordinator || target == AdmissionOwnerState.Continuation
                    || target == AdmissionOwnerState.Recovery || target == AdmissionOwnerState.TerminalCleanup;
            if (source == AdmissionOwnerState.Coordinator || source == AdmissionOwnerState.Recovery)
                return target == AdmissionOwnerState.TerminalCleanup;
            return false;
        }

        private static bool CanComplete(AdmissionOwnerState state)
        {
            return state == AdmissionOwnerState.WorkItem || state == AdmissionOwnerState.Continuation || state == AdmissionOwnerState.TerminalCleanup;
        }

        private static void Append(StringBuilder builder, string value)
        {
            var item = value ?? string.Empty;
            builder.Append(item.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item).Append('|');
        }

        private int RemainingGrantMs(AdmissionRecord value) { return checked((int)Math.Max(0, Math.Min(GrantTtlMs, value.GrantExpiresAtMs - monotonicNowMs()))); }
        private static AcquireResult Result(AcquireOutcome outcome, string token = null, int grantExpiresInMs = 0, bool newlyGranted = false) { return new AcquireResult { Outcome = outcome, Token = token, RetryAfterMs = RetryAfterMs, GrantExpiresInMs = grantExpiresInMs, NewlyGranted = newlyGranted }; }
        private static AuthorizeResult Authorized(AuthorizeOutcome outcome, ConsumedIdentity identity = null, bool newlyConsumed = false) { return new AuthorizeResult { Outcome = outcome, Identity = identity, NewlyConsumed = newlyConsumed }; }
        private bool RecordOwnedByPool(AdmissionRecord value) { return value != null && string.Equals(value.InstanceId, instanceId, StringComparison.Ordinal) && string.Equals(value.SessionId, sessionId, StringComparison.Ordinal) && ValidRequestId(value.RequestId) && IsSha256(value.RootFingerprint) && IsToken(value.Token) && ValidListener(value.ListenerId, value.ListenerGeneration); }
        private bool ValidConsumedRecord(AdmissionRecord value) { return RecordOwnedByPool(value) && value.Consumed && IsSha256(value.RequestDigest) && Enum.IsDefined(typeof(AdmissionOwnerState), value.OwnerState) && Enum.IsDefined(typeof(AdmissionOwnerState), value.IntendedOwnerState) && value.GrantExpiresAtMs == 0 && value.ExecutionDeadlineMs > 0 && ((value.TransferCommitted && value.OwnerState == value.IntendedOwnerState) || (!value.TransferCommitted && LegalTransfer(value.OwnerState, value.IntendedOwnerState))); }
        private AdmissionRecord Clone(AdmissionRecord value) { return new AdmissionRecord { InstanceId = value.InstanceId, SessionId = value.SessionId, RequestId = value.RequestId, RootFingerprint = value.RootFingerprint, RequestDigest = value.RequestDigest, Token = value.Token, ListenerId = value.ListenerId, ListenerGeneration = value.ListenerGeneration, GrantExpiresAtMs = value.GrantExpiresAtMs, ExecutionDeadlineMs = value.ExecutionDeadlineMs, OwnerState = value.OwnerState, IntendedOwnerState = value.IntendedOwnerState, TransferCommitted = value.TransferCommitted }; }
        private bool ValidRestoredRecord(AdmissionRecord value) { return ValidConsumedRecord(value); }
        private static ConsumedIdentity Identity(AdmissionRecord value) { return new ConsumedIdentity { RequestId = value.RequestId, RootFingerprint = value.RootFingerprint, RequestDigest = value.RequestDigest, Token = value.Token, SessionId = value.SessionId, ListenerId = value.ListenerId, ListenerGeneration = value.ListenerGeneration, ExecutionDeadlineMs = value.ExecutionDeadlineMs, OwnerState = value.OwnerState }; }
        private static bool Matches(AdmissionRecord value, ConsumedIdentity identity) { return MatchesIdentityFields(value, identity) && value.OwnerState == identity.OwnerState; }
        private static bool MatchesIdentityFields(AdmissionRecord value, ConsumedIdentity identity)
        {
            return value != null && identity != null
                && string.Equals(value.RequestId, identity.RequestId, StringComparison.Ordinal)
                && string.Equals(value.RootFingerprint, identity.RootFingerprint, StringComparison.Ordinal)
                && string.Equals(value.RequestDigest, identity.RequestDigest, StringComparison.Ordinal)
                && FixedEquals(value.Token, identity.Token)
                && string.Equals(value.SessionId, identity.SessionId, StringComparison.Ordinal)
                && value.ExecutionDeadlineMs == identity.ExecutionDeadlineMs;
        }
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
