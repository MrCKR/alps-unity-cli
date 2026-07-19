#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        // request-journal-v1 is append-only. New continuations are optional fields in v1.
        private const int RequestJournalVersion = 1;
        private const int RequestJournalMutexTimeoutMs = 5000;
        private const int RequestJournalActiveLimit = 256;
        private const int RequestJournalTotalLimit = 4096;
        private const long RequestJournalByteLimit = 64L * 1024L * 1024L;
        private static readonly TimeSpan RequestJournalTerminalTtl = TimeSpan.FromDays(7);
        private static readonly TimeSpan RequestJournalRecoveryGrace = TimeSpan.FromHours(24);

        private sealed class RequestJournalDecision
        {
            public bool Created;
            public bool Execute;
            public bool Outcome;
            public bool ResumeProof;
            public bool ResumeFinalize;
            public object Continuation;
            public object TerminalEnvelope;
        }

        private sealed class RequestJournalRecord
        {
            public string RequestId;
            public string InstanceId;
            public string EditorSessionId;
            public string Command;
            public string ToolName;
            public string RequestDigest;
            public string ExecutionClass;
            public string CompletionDriverId;
            public DateTimeOffset AbsoluteDeadlineUtc;
            public string State;
            public string Phase;
            public DateTimeOffset? AcceptedAt;
            public DateTimeOffset? StartedAt;
            public DateTimeOffset? EndedAt;
            public string ContinuationKind;
            public string ContinuationSha256;
            public object Continuation;
            public string ProofKind;
            public string ProofSha256;
            public object ProvenEnvelope;
            public object TerminalEnvelope;
            // compile 是 request journal 的一个 continuation，不是第二个 operation 存储。
            public Dictionary<string, object> CompileOperation;
        }

        /// <summary>v1 digest 严格绑定 instanceId 和 HTTP 收到的原始字节，绝不规范化 JSON。</summary>
        private static string ComputeRequestDigest(byte[] rawBytes, string instanceId)
        {
            if (rawBytes == null) throw new ArgumentNullException("rawBytes");
            if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Instance ID is required.", "instanceId");
            var prefix = Encoding.UTF8.GetBytes("unity-cli-request-v1");
            var instance = Encoding.UTF8.GetBytes(instanceId);
            var bytes = new byte[prefix.Length + 1 + instance.Length + 1 + rawBytes.Length];
            Buffer.BlockCopy(prefix, 0, bytes, 0, prefix.Length);
            Buffer.BlockCopy(instance, 0, bytes, prefix.Length + 1, instance.Length);
            Buffer.BlockCopy(rawBytes, 0, bytes, prefix.Length + 1 + instance.Length + 1, rawBytes.Length);
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        /// <summary>queue 的唯一准入点；同 requestId 的终态只返回已持久化 envelope。</summary>
        private static RequestJournalDecision AcceptOrRecover(
            BridgeRequest request,
            byte[] rawBytes,
            UnityCliToolCompletionContract contract,
            Action beforeAccess = null,
            Action beforeCreate = null,
            Action<RequestJournalRecord> initializeCreate = null,
            Func<RequestJournalRecord, RequestJournalDecision, bool> prepareRecovered = null)
        {
            if (request == null || contract == null) throw new ArgumentNullException(request == null ? "request" : "contract");
            if (!string.Equals(request.InstanceId, InstanceId(), StringComparison.Ordinal))
                throw new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Request instance identity does not match this editor.", "Refresh instance discovery and send the request to the matching instance.");
            var digest = ComputeRequestDigest(rawBytes, request.InstanceId);
            RequestJournalDecision result = null;
            WithRequestJournalMutex(() =>
            {
                CleanupRequestJournal(DateTimeOffset.UtcNow);
                var path = RequestJournalPath(request.RequestId);
                if (!File.Exists(path))
                {
                    if (beforeCreate != null) beforeCreate();
                    if (beforeAccess != null) beforeAccess();
                    var createdRecord = NewRequestJournalRecord(request, contract, digest);
                    if (initializeCreate != null) initializeCreate(createdRecord);
                    EnsureRequestJournalCapacity(SerializedRecordSize(createdRecord));
                    WriteRequestJournalRecord(path, createdRecord);
                    result = new RequestJournalDecision { Created = true, Execute = true };
                    return;
                }
                var record = ReadRequestJournalRecord(path, request.RequestId);
                if (!string.Equals(record.RequestDigest, digest, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_REQUEST_ID_REUSED", "requestId is already bound to different request bytes.", "Generate a new requestId before retrying this command.");
                if (beforeAccess != null) beforeAccess();
                result = RecoverInterrupted(record, EditorSessionId());
                var prepared = prepareRecovered != null && prepareRecovered(record, result);
                if (result.TerminalEnvelope != null || prepared) WriteRequestJournalRecord(path, record);
            });
            return result;
        }

        /// <summary>effect 前的 durable 边界；调用方必须在实际 Unity mutation 前调用。</summary>
        private static void MarkAcceptedBeforeEffect(string requestId)
        {
            AdvanceRequestJournal(requestId, record =>
            {
                if (IsTerminal(record)) return;
                if (record.Phase != "received" && record.Phase != "accepted_before_effect") throw JournalCorrupt("Request cannot be accepted after effect start.");
                record.Phase = "accepted_before_effect";
                record.State = "accepted";
                if (!record.AcceptedAt.HasValue) record.AcceptedAt = DateTimeOffset.UtcNow;
            });
        }

        /// <summary>effect 前最后一次 durable 写；可恢复 driver 必须同时持久化非敏感 continuation。</summary>
        private static void MarkEffectStarted(string requestId, string continuationKind = null, object continuation = null)
        {
            if ((continuationKind == null) != (continuation == null)) throw new ArgumentException("Continuation kind and value must be supplied together.");
            var continuationJson = continuation == null ? null : UnityCliSerializer.Serialize(continuation);
            var continuationHash = continuationJson == null ? null : Sha256Hex(Encoding.UTF8.GetBytes(continuationJson));
            AdvanceRequestJournal(requestId, record =>
            {
                if (IsTerminal(record)) throw JournalCorrupt("Effect cannot start after a terminal result was persisted.");
                if (record.Phase != "accepted_before_effect") throw JournalCorrupt("Effect start requires accepted_before_effect phase.");
                record.Phase = "effect_started";
                record.State = "running";
                record.StartedAt = DateTimeOffset.UtcNow;
                record.ContinuationKind = continuationKind;
                record.ContinuationSha256 = continuationHash;
                record.Continuation = continuation;
            });
        }

        private static void MarkEffectProven(string requestId, string proofKind, byte[] proofBytes, object terminalEnvelope)
        {
            if (string.IsNullOrWhiteSpace(proofKind) || proofBytes == null || terminalEnvelope == null) throw new ArgumentException("Proof kind, bytes, and terminal envelope are required.");
            terminalEnvelope = NormalizeTerminalEnvelope(terminalEnvelope);
            ValidateTerminalEnvelope(terminalEnvelope, requestId);
            AdvanceRequestJournal(requestId, record =>
            {
                if (IsTerminal(record)) return;
                if (record.Phase != "effect_started" && record.Phase != "effect_proven") throw JournalCorrupt("Effect proof requires effect_started phase.");
                record.Phase = "effect_proven";
                record.State = "running";
                record.ProofKind = proofKind;
                using (var sha = SHA256.Create()) record.ProofSha256 = Hex(sha.ComputeHash(proofBytes));
                record.ProvenEnvelope = terminalEnvelope;
            });
        }

        /// <summary>终态写入是一次裁决：已有 durable terminal 时必须返回其 canonical envelope。</summary>
        private static object PersistTerminal(string requestId, object envelope, string terminalState)
        {
            if (envelope == null) throw new ArgumentNullException("envelope");
            try
            {
                envelope = NormalizeTerminalEnvelope(envelope);
                ValidateTerminalEnvelope(envelope, requestId);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Terminal envelope cannot be persisted: " + exception.Message, "Return a protocol envelope with the matching requestId before completing the request.");
            }
            if (terminalState != "completed" && terminalState != "failed" && terminalState != "cancelled" && terminalState != "outcome_unknown")
                throw new ArgumentException("Invalid terminal state.", "terminalState");
            object canonicalEnvelope = null;
            AdvanceRequestJournal(requestId, record =>
            {
                if (IsTerminal(record))
                {
                    canonicalEnvelope = record.TerminalEnvelope;
                    return;
                }
                if (terminalState == "completed" && record.Phase != "effect_proven")
                    throw JournalCorrupt("A successful terminal result requires effect_proven.");
                var persistedState = terminalState;
                var persistedEnvelope = envelope;
                if (record.Phase == "effect_proven" && (terminalState == "outcome_unknown" || terminalState == "cancelled"))
                {
                    persistedState = "failed";
                    persistedEnvelope = ProvenFinalizationFailureEnvelope(requestId, "A post-proof failure occurred while finalizing the stable Editor generation.");
                }
                record.State = persistedState;
                record.Phase = "result_persisted";
                record.EndedAt = DateTimeOffset.UtcNow;
                record.ProvenEnvelope = null;
                record.TerminalEnvelope = persistedEnvelope;
                canonicalEnvelope = record.TerminalEnvelope;
            });
            return canonicalEnvelope;
        }

        /// <summary>超时在 journal mutex 内按 durable effect barrier 原子裁决，避免检查与 terminal 写入之间的竞态。</summary>
        private static object PersistTimeoutByEffectBarrier(string requestId, object beforeEffectEnvelope)
        {
            beforeEffectEnvelope = NormalizeTerminalEnvelope(beforeEffectEnvelope);
            ValidateTerminalEnvelope(beforeEffectEnvelope, requestId);
            object canonicalEnvelope = null;
            AdvanceRequestJournal(requestId, record =>
            {
                if (IsTerminal(record))
                {
                    canonicalEnvelope = record.TerminalEnvelope;
                    return;
                }
                if (record.Phase == "effect_proven" && record.ProvenEnvelope != null)
                {
                    if (string.Equals(record.ToolName, "editor.compile", StringComparison.Ordinal))
                    {
                        record.State = ProvenEnvelopeSucceeded(record.ProvenEnvelope) ? "completed" : "failed";
                        record.TerminalEnvelope = record.ProvenEnvelope;
                    }
                    else
                    {
                        record.State = "failed";
                        record.TerminalEnvelope = ProtocolEnvelopeFactory.Error(
                            requestId,
                            "E_UNITY_TRANSITION_TIMEOUT",
                            "The Tool effect was proven, but the Editor did not reach stable finalization before the immutable deadline.",
                            new Dictionary<string, object> { { "effectProven", true } },
                            new[] { "Inspect editor.state and the proven business state before submitting a new request." });
                    }
                }
                else if (record.Phase == "effect_started")
                {
                    record.State = "outcome_unknown";
                    record.TerminalEnvelope = OutcomeUnknownEnvelope(requestId);
                }
                else
                {
                    record.State = "failed";
                    record.TerminalEnvelope = beforeEffectEnvelope;
                }
                record.Phase = "result_persisted";
                record.EndedAt = DateTimeOffset.UtcNow;
                record.ProvenEnvelope = null;
                canonicalEnvelope = record.TerminalEnvelope;
            });
            return canonicalEnvelope;
        }

        /// <summary>started signal 可能早于 journal acceptance；无记录时不得把 HTTP timeout 写成内部错误。</summary>
        private static bool TryPersistTimeoutByEffectBarrier(string requestId, object beforeEffectEnvelope, out object terminalEnvelope)
        {
            var exists = false;
            WithRequestJournalMutex(() => { exists = File.Exists(RequestJournalPath(requestId)); });
            if (!exists) { terminalEnvelope = null; return false; }
            terminalEnvelope = PersistTimeoutByEffectBarrier(requestId, beforeEffectEnvelope);
            return true;
        }

        /// <summary>HTTP timeout 先读取 durable terminal，避免与完成线程竞争时发送非 canonical response。</summary>
        private static bool TryGetRequestJournalTerminal(string requestId, out string serializedEnvelope)
        {
            string observedEnvelope = null;
            var found = false;
            WithRequestJournalMutex(() =>
            {
                var path = RequestJournalPath(requestId);
                if (!File.Exists(path)) return;
                var record = ReadRequestJournalRecord(path, requestId);
                if (!IsTerminal(record)) return;
                observedEnvelope = UnityCliSerializer.Serialize(record.TerminalEnvelope);
                found = true;
            });
            serializedEnvelope = observedEnvelope;
            return found;
        }

        /// <summary>将重启中断归约为可执行或 deterministic terminal，绝不推测 effect 已成功。</summary>
        private static RequestJournalDecision RecoverInterrupted(RequestJournalRecord record, string currentSession)
        {
            if (IsTerminal(record)) return new RequestJournalDecision { TerminalEnvelope = record.TerminalEnvelope, Outcome = record.State == "outcome_unknown" };
            if (record.Phase == "effect_proven" && record.ProvenEnvelope != null)
            {
                // 普通 Tool 在同一 session 仍须完成统一 stable finalization；compile 的 proof 已包含最终 ready 观察。
                if (!string.Equals(record.ToolName, "editor.compile", StringComparison.Ordinal)
                    && string.Equals(record.EditorSessionId, currentSession, StringComparison.Ordinal))
                    return new RequestJournalDecision { ResumeFinalize = true, TerminalEnvelope = record.ProvenEnvelope };
                if (!string.Equals(record.ToolName, "editor.compile", StringComparison.Ordinal))
                {
                    record.State = "failed";
                    record.Phase = "result_persisted";
                    record.EndedAt = DateTimeOffset.UtcNow;
                    record.TerminalEnvelope = ProtocolEnvelopeFactory.Error(
                        record.RequestId,
                        "E_UNITY_TRANSITION_TIMEOUT",
                        "The Tool effect was proven, but the originating Editor session ended before stable finalization.",
                        new Dictionary<string, object> { { "effectProven", true } },
                        new[] { "Inspect editor.state and the proven business state before submitting a new request." });
                    record.ProvenEnvelope = null;
                    return new RequestJournalDecision { TerminalEnvelope = record.TerminalEnvelope };
                }
                record.State = ProvenEnvelopeSucceeded(record.ProvenEnvelope) ? "completed" : "failed";
                record.Phase = "result_persisted";
                record.EndedAt = DateTimeOffset.UtcNow;
                record.TerminalEnvelope = record.ProvenEnvelope;
                record.ProvenEnvelope = null;
                return new RequestJournalDecision { TerminalEnvelope = record.TerminalEnvelope };
            }
            if (record.Phase == "effect_started"
                && string.Equals(record.ToolName, "editor.compile", StringComparison.Ordinal)
                && string.Equals(record.EditorSessionId, currentSession, StringComparison.Ordinal)
                && record.CompileOperation != null)
                return new RequestJournalDecision { Execute = true };
            if (record.Phase == "effect_started"
                && string.Equals(record.EditorSessionId, currentSession, StringComparison.Ordinal)
                && SupportsProofRecovery(record.ToolName)
                && record.Continuation != null
                && !string.IsNullOrWhiteSpace(record.ContinuationKind)
                && IsSha256(record.ContinuationSha256))
            {
                return new RequestJournalDecision { Execute = true, ResumeProof = true, Continuation = record.Continuation };
            }
            if (record.Phase == "effect_started" || record.Phase == "effect_proven")
            {
                record.State = "outcome_unknown";
                record.Phase = "result_persisted";
                record.EndedAt = DateTimeOffset.UtcNow;
                record.TerminalEnvelope = OutcomeUnknownEnvelope(record.RequestId);
                record.ProvenEnvelope = null;
                return new RequestJournalDecision { TerminalEnvelope = record.TerminalEnvelope, Outcome = true };
            }
            if (!string.Equals(record.EditorSessionId, currentSession, StringComparison.Ordinal))
            {
                record.State = "cancelled";
                record.Phase = "result_persisted";
                record.EndedAt = DateTimeOffset.UtcNow;
                record.TerminalEnvelope = CancelledEnvelope(record.RequestId);
                return new RequestJournalDecision { TerminalEnvelope = record.TerminalEnvelope };
            }
            return new RequestJournalDecision { Execute = true };
        }

        private static void AdvanceRequestJournal(string requestId, Action<RequestJournalRecord> change)
        {
            if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("Request ID is required.", "requestId");
            WithRequestJournalMutex(() =>
            {
                var path = RequestJournalPath(requestId);
                if (!File.Exists(path)) throw new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Request journal record is missing.", "Retry with a new requestId after checking the editor data directory.");
                var record = ReadRequestJournalRecord(path, requestId);
                change(record);
                WriteRequestJournalRecord(path, record);
            });
        }

        /// <summary>异常路径只按 durable phase 判断 effect 是否可能开始，不依赖内存中的 handler 状态。</summary>
        private static bool RequestJournalEffectStarted(string requestId)
        {
            var started = false;
            WithRequestJournalMutex(() =>
            {
                var path = RequestJournalPath(requestId);
                if (!File.Exists(path)) return;
                var record = ReadRequestJournalRecord(path, requestId);
                started = record.Phase == "effect_started" || record.Phase == "effect_proven" || (record.Phase == "result_persisted" && record.StartedAt.HasValue);
            });
            return started;
        }

        private static RequestJournalRecord NewRequestJournalRecord(BridgeRequest request, UnityCliToolCompletionContract contract, string digest)
        {
            DateTimeOffset deadline;
            if (!DateTimeOffset.TryParse(request.Execution.AbsoluteDeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline))
                throw JournalCorrupt("Request deadline is invalid.");
            return new RequestJournalRecord
            {
                RequestId = request.RequestId, InstanceId = request.InstanceId, EditorSessionId = EditorSessionId(), Command = request.Command,
                ToolName = contract.ToolName, RequestDigest = digest, ExecutionClass = contract.ExecutionClass,
                CompletionDriverId = contract.CompletionDriverId, AbsoluteDeadlineUtc = deadline, State = "created", Phase = "received"
            };
        }

        private static void WithRequestJournalMutex(Action action)
        {
            WithCurrentUserMutex("Local\\UnityCli.RequestJournal." + InstanceId(), RequestJournalMutexTimeoutMs, action);
        }

        // Compilation callbacks share one durable, strictly increasing sequence across domain reloads.
        internal static long NextCompilationEventSequence()
        {
            long next = 0;
            WithRequestJournalMutex(() =>
            {
                var path = Path.Combine(RequestJournalDirectory(), "compile-event-sequence.txt");
                long current = 0;
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim();
                    if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out current) || current < 0 || text != current.ToString(CultureInfo.InvariantCulture))
                        throw JournalCorrupt("Compilation event sequence sidecar is corrupt.");
                }
                next = checked(current + 1);
                DurableWriteText(path, next.ToString(CultureInfo.InvariantCulture));
            });
            return next;
        }

        /// <summary>读取严格 durable 回调序列，domain reload 后 broker 从这里续接。</summary>
        internal static long CurrentCompilationEventSequence()
        {
            long current = 0;
            WithRequestJournalMutex(() =>
            {
                var path = Path.Combine(RequestJournalDirectory(), "compile-event-sequence.txt");
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path).Trim();
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out current) || current < 0 || text != current.ToString(CultureInfo.InvariantCulture))
                    throw JournalCorrupt("Compilation event sequence sidecar is corrupt.");
            });
            return current;
        }

        private static string RequestJournalDirectory() { return Path.Combine(DataDir(), "requests", InstanceId()); }
        private static string RequestJournalPath(string requestId)
        {
            if (!IsSafeId(requestId)) throw new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Request ID is not safe for journal storage.", "Use a non-empty requestId containing only letters, digits, dot, underscore, or hyphen.");
            return Path.Combine(RequestJournalDirectory(), requestId + ".json");
        }

        private static bool IsSafeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200) return false;
            return value.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-');
        }

        private static bool IsTerminal(RequestJournalRecord record) { return record.Phase == "result_persisted"; }

        private static object CancelledEnvelope(string requestId)
        {
            return ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_CANCELLED", "The request was cancelled because the editor session changed before any effect began.", null, new[] { "Submit a new requestId to the current editor session." });
        }

        private static object OutcomeUnknownEnvelope(string requestId)
        {
            return ProtocolEnvelopeFactory.Error(requestId, "E_REQUEST_OUTCOME_UNKNOWN", "The editor stopped after an effect may have begun; the request will not be replayed.", null, new[] { "Inspect the Unity state before deciding whether to submit a new request." });
        }

        private static object ProvenFinalizationFailureEnvelope(string requestId, string reason)
        {
            return ProtocolEnvelopeFactory.Error(
                requestId,
                "E_UNITY_TRANSITION_TIMEOUT",
                "The Tool effect was proven, but Unity did not reach a stable terminal generation before the deadline.",
                new Dictionary<string, object> { { "effectProven", true }, { "reason", reason } },
                new[] { "Inspect editor.state and the affected business state before submitting a new request." });
        }

        private static void CleanupRequestJournal(DateTimeOffset now)
        {
            if (!Directory.Exists(RequestJournalDirectory())) return;
            foreach (var path in Directory.GetFiles(RequestJournalDirectory(), "*.json"))
            {
                RequestJournalRecord record;
                try { record = ReadRequestJournalRecord(path, Path.GetFileNameWithoutExtension(path)); }
                catch { continue; } // 损坏记录绝不由清理逻辑覆盖或删除。
                if (IsTerminal(record) && record.EndedAt.HasValue && now - record.EndedAt.Value > RequestJournalTerminalTtl) File.Delete(path);
                else if (!IsTerminal(record) && now > record.AbsoluteDeadlineUtc + RequestJournalRecoveryGrace)
                {
                    var decision = RecoverInterrupted(record, "expired-session");
                    WriteRequestJournalRecord(path, record);
                }
            }
        }

        private static void EnsureRequestJournalCapacity(long candidateBytes)
        {
            if (!Directory.Exists(RequestJournalDirectory())) return;
            var entries = new List<Tuple<string, RequestJournalRecord, long>>();
            foreach (var path in Directory.GetFiles(RequestJournalDirectory(), "*.json"))
            {
                var record = ReadRequestJournalRecord(path, Path.GetFileNameWithoutExtension(path));
                entries.Add(Tuple.Create(path, record, new FileInfo(path).Length));
            }
            var active = entries.Count(entry => !IsTerminal(entry.Item2));
            if (active >= RequestJournalActiveLimit)
                throw new BridgeCommandException("E_REQUEST_JOURNAL_CAPACITY", "Request journal active-entry capacity is exhausted.", "Wait for active requests to reach a terminal result before submitting a new mutation.");

            var totalBytes = entries.Sum(entry => entry.Item3);
            foreach (var terminal in entries.Where(entry => IsTerminal(entry.Item2)).OrderBy(entry => entry.Item2.EndedAt ?? DateTimeOffset.MinValue).ToList())
            {
                if (entries.Count + 1 <= RequestJournalTotalLimit && totalBytes + candidateBytes <= RequestJournalByteLimit) break;
                File.Delete(terminal.Item1);
                entries.Remove(terminal);
                totalBytes -= terminal.Item3;
            }
            if (entries.Count + 1 > RequestJournalTotalLimit || totalBytes + candidateBytes > RequestJournalByteLimit)
                throw new BridgeCommandException("E_REQUEST_JOURNAL_CAPACITY", "Request journal capacity is exhausted by active or recoverable entries.", "Wait for active requests to become terminal; active entries are never evicted to admit new mutations.");
        }

        private static long SerializedRecordSize(RequestJournalRecord record)
        {
            return Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(RequestJournalData(record)) + "\n");
        }

        private static void WriteRequestJournalRecord(string path, RequestJournalRecord record)
        {
            var text = UnityCliSerializer.Serialize(RequestJournalData(record)) + "\n";
            DurableWriteText(path, text);
        }

        private static Dictionary<string, object> RequestJournalData(RequestJournalRecord r)
        {
            var result = new Dictionary<string, object>
            {
                { "version", (long)RequestJournalVersion }, { "requestId", r.RequestId }, { "instanceId", r.InstanceId }, { "editorSessionId", r.EditorSessionId },
                { "command", r.Command }, { "toolName", r.ToolName }, { "digestVersion", 1L }, { "requestDigest", r.RequestDigest },
                { "executionClass", r.ExecutionClass }, { "completionDriverId", r.CompletionDriverId }, { "absoluteDeadlineUtc", r.AbsoluteDeadlineUtc.ToString("O", CultureInfo.InvariantCulture) },
                { "state", r.State }, { "phase", r.Phase }
            };
            if (r.AcceptedAt.HasValue) result.Add("acceptedAt", r.AcceptedAt.Value.ToString("O", CultureInfo.InvariantCulture));
            if (r.StartedAt.HasValue) result.Add("startedAt", r.StartedAt.Value.ToString("O", CultureInfo.InvariantCulture));
            if (r.EndedAt.HasValue) result.Add("endedAt", r.EndedAt.Value.ToString("O", CultureInfo.InvariantCulture));
            if (r.ContinuationKind != null) result.Add("continuationKind", r.ContinuationKind);
            if (r.ContinuationSha256 != null) result.Add("continuationSha256", r.ContinuationSha256);
            if (r.Continuation != null) result.Add("continuation", r.Continuation);
            if (r.ProofKind != null) result.Add("proofKind", r.ProofKind);
            if (r.ProofSha256 != null) result.Add("proofSha256", r.ProofSha256);
            if (r.ProvenEnvelope != null) result.Add("provenEnvelope", r.ProvenEnvelope);
            if (r.TerminalEnvelope != null) result.Add("terminalEnvelope", r.TerminalEnvelope);
            if (r.CompileOperation != null) result.Add("compileOperation", r.CompileOperation);
            return result;
        }

        private static RequestJournalRecord ReadRequestJournalRecord(string path, string expectedRequestId)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) throw new InvalidDataException("UTF-8 BOM is not permitted.");
                var data = UnityCliSerializer.Parse(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>;
                if (data == null) throw new InvalidDataException("Journal root is not an object.");
                var allowed = new HashSet<string>(new[] { "version", "requestId", "instanceId", "editorSessionId", "command", "toolName", "digestVersion", "requestDigest", "executionClass", "completionDriverId", "absoluteDeadlineUtc", "state", "phase", "acceptedAt", "startedAt", "endedAt", "continuationKind", "continuationSha256", "continuation", "proofKind", "proofSha256", "provenEnvelope", "terminalEnvelope", "compileOperation" }, StringComparer.Ordinal);
                if (data.Keys.Any(key => !allowed.Contains(key))) throw new InvalidDataException("Journal has unknown fields.");
                var compileOperation = data.ContainsKey("compileOperation") ? data["compileOperation"] as Dictionary<string, object> : null;
                if (data.ContainsKey("compileOperation") && compileOperation == null) throw new InvalidDataException("compileOperation must be an object.");
                if (compileOperation != null) ValidateCompileOperation(compileOperation);
                var record = new RequestJournalRecord {
                    RequestId = RequiredString(data, "requestId"), InstanceId = RequiredString(data, "instanceId"), EditorSessionId = RequiredString(data, "editorSessionId"), Command = RequiredString(data, "command"), ToolName = RequiredString(data, "toolName"), RequestDigest = RequiredString(data, "requestDigest"), ExecutionClass = RequiredString(data, "executionClass"), CompletionDriverId = RequiredString(data, "completionDriverId"), State = RequiredString(data, "state"), Phase = RequiredString(data, "phase"), AbsoluteDeadlineUtc = RequiredTime(data, "absoluteDeadlineUtc"), AcceptedAt = OptionalTime(data, "acceptedAt"), StartedAt = OptionalTime(data, "startedAt"), EndedAt = OptionalTime(data, "endedAt"), ContinuationKind = OptionalString(data, "continuationKind"), ContinuationSha256 = OptionalString(data, "continuationSha256"), Continuation = data.ContainsKey("continuation") ? data["continuation"] : null, ProofKind = OptionalString(data, "proofKind"), ProofSha256 = OptionalString(data, "proofSha256"), ProvenEnvelope = data.ContainsKey("provenEnvelope") ? data["provenEnvelope"] : null, TerminalEnvelope = data.ContainsKey("terminalEnvelope") ? data["terminalEnvelope"] : null, CompileOperation = compileOperation };
                if (RequiredLong(data, "version") != RequestJournalVersion || RequiredLong(data, "digestVersion") != 1L) throw new InvalidDataException("Unsupported journal version.");
                if (record.CompileOperation != null && (!string.Equals(record.ToolName, "editor.compile", StringComparison.Ordinal) || !string.Equals(record.Command, "internal.editor.compile.start", StringComparison.Ordinal))) throw new InvalidDataException("compileOperation owner is invalid.");
                if (record.CompileOperation != null && !string.Equals(TextValue(record.CompileOperation, "runId"), "compile-" + record.RequestDigest.Substring(0, 32), StringComparison.Ordinal)) throw new InvalidDataException("compileOperation runId does not match the request digest.");
                if (!string.Equals(record.RequestId, expectedRequestId, StringComparison.Ordinal) || !string.Equals(record.InstanceId, InstanceId(), StringComparison.Ordinal) || !IsSafeId(record.RequestId) || !IsPrefixedLowerHex(record.EditorSessionId, "session-", 64) || !IsSha256(record.RequestDigest) || (record.ProofSha256 != null && !IsSha256(record.ProofSha256))) throw new InvalidDataException("Journal identity or digest is invalid.");
                var hasContinuation = record.Continuation != null || record.ContinuationKind != null || record.ContinuationSha256 != null;
                if (hasContinuation && (record.Continuation == null || string.IsNullOrWhiteSpace(record.ContinuationKind) || !IsSha256(record.ContinuationSha256))) throw new InvalidDataException("Journal continuation fields are incomplete.");
                if (hasContinuation && !string.Equals(Sha256Hex(Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(record.Continuation))), record.ContinuationSha256, StringComparison.Ordinal)) throw new InvalidDataException("Journal continuation digest does not match its value.");
                if (hasContinuation && !record.StartedAt.HasValue) throw new InvalidDataException("Journal continuation exists before effect_started.");
                if (!(record.State == "created" || record.State == "accepted" || record.State == "running" || record.State == "completed" || record.State == "failed" || record.State == "cancelled" || record.State == "outcome_unknown") || !(record.Phase == "received" || record.Phase == "accepted_before_effect" || record.Phase == "effect_started" || record.Phase == "effect_proven" || record.Phase == "result_persisted")) throw new InvalidDataException("Journal state or phase is invalid.");
                if (IsTerminal(record) != (record.TerminalEnvelope != null) || (record.Phase == "result_persisted" && !record.EndedAt.HasValue)) throw new InvalidDataException("Journal terminal fields are inconsistent.");
                if (record.Phase == "effect_proven" && record.ProvenEnvelope == null) throw new InvalidDataException("Effect-proven phase has no recoverable envelope.");
                if (record.Phase != "effect_proven" && record.ProvenEnvelope != null) throw new InvalidDataException("Recoverable proof envelope exists outside effect_proven.");
                if (IsTerminal(record) && !(record.State == "completed" || record.State == "failed" || record.State == "cancelled" || record.State == "outcome_unknown")) throw new InvalidDataException("Journal terminal state is invalid.");
                if (record.Phase == "received" && (record.State != "created" || record.AcceptedAt.HasValue || record.StartedAt.HasValue || record.EndedAt.HasValue)) throw new InvalidDataException("received phase fields are inconsistent.");
                if (record.Phase == "accepted_before_effect" && (record.State != "accepted" || !record.AcceptedAt.HasValue || record.StartedAt.HasValue || record.EndedAt.HasValue)) throw new InvalidDataException("accepted phase fields are inconsistent.");
                if ((record.Phase == "effect_started" || record.Phase == "effect_proven") && (record.State != "running" || !record.AcceptedAt.HasValue || !record.StartedAt.HasValue || record.EndedAt.HasValue)) throw new InvalidDataException("running phase fields are inconsistent.");
                if (record.Phase == "effect_proven" && (string.IsNullOrWhiteSpace(record.ProofKind) || !IsSha256(record.ProofSha256))) throw new InvalidDataException("effect proof fields are inconsistent.");
                if (record.State == "completed" && (string.IsNullOrWhiteSpace(record.ProofKind) || !IsSha256(record.ProofSha256))) throw new InvalidDataException("completed result has no durable proof.");
                if (record.ProvenEnvelope != null) ValidateTerminalEnvelope(record.ProvenEnvelope, record.RequestId);
                if (record.TerminalEnvelope != null) ValidateTerminalEnvelope(record.TerminalEnvelope, record.RequestId);
                return record;
            }
            catch (BridgeCommandException) { throw; }
            catch (Exception exception) { throw new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", "Request journal record is corrupt: " + exception.Message, "Inspect or remove only the corrupt request journal record, then retry with a new requestId."); }
        }

        /// <summary>compile continuation 逐字段执行 closed-schema 校验，损坏状态不得以默认值继续。</summary>
        private static void ValidateCompileOperation(Dictionary<string, object> operation)
        {
            var allowed = new HashSet<string>(new[] { "schemaVersion", "sequence", "runId", "phase", "lease", "refresh", "path", "forceUpdate", "importRecursive", "includeWarnings", "assemblies", "diagnostics", "stableFrames", "stableSequence", "stableSince", "lastBrokerSequence", "triggerEventSequence", "startedCycle", "startedSequence", "startedAt", "finishedCycle", "finishedSequence", "finishedAt", "reloadObserved", "callbackError" }, StringComparer.Ordinal);
            var required = new HashSet<string>(new[] { "schemaVersion", "sequence", "runId", "phase", "lease", "refresh", "path", "forceUpdate", "importRecursive", "includeWarnings", "assemblies", "diagnostics", "stableFrames", "stableSequence", "lastBrokerSequence", "triggerEventSequence", "callbackError" }, StringComparer.Ordinal);
            if (operation.Keys.Any(key => !allowed.Contains(key)) || required.Any(key => !operation.ContainsKey(key))) throw new InvalidDataException("compileOperation fields are incomplete or unknown.");
            if (RequiredCompileInteger(operation, "schemaVersion", 1) != 1L || RequiredCompileInteger(operation, "sequence", 1) <= 0L) throw new InvalidDataException("compileOperation version or sequence is invalid.");
            var runId = TextValue(operation, "runId");
            var phase = TextValue(operation, "phase");
            var phases = new HashSet<string>(new[] { "queued", "waiting_preexisting", "preexisting_stable", "trigger_committed", "dispatching", "awaiting_started", "compiling", "cycle_finished", "waiting_stable", "proven", "terminal" }, StringComparer.Ordinal);
            if (!IsPrefixedLowerHex(runId, "compile-", 32) || !phases.Contains(phase)) throw new InvalidDataException("compileOperation runId or phase is invalid.");
            if (!(operation["refresh"] is bool) || !(operation["forceUpdate"] is bool) || !(operation["importRecursive"] is bool) || !(operation["includeWarnings"] is bool) || !(operation["assemblies"] is System.Collections.IList) || !(operation["diagnostics"] is System.Collections.IList)) throw new InvalidDataException("compileOperation input fields are invalid.");
            if (operation["lease"] != null && !IsPrefixedLowerHex(operation["lease"] as string, "session-", 64)) throw new InvalidDataException("compileOperation lease is invalid.");
            if (operation["callbackError"] != null && (string.IsNullOrWhiteSpace(operation["callbackError"] as string))) throw new InvalidDataException("compileOperation callbackError is invalid.");
            if (operation["path"] != null && !(operation["path"] is string)) throw new InvalidDataException("compileOperation path is invalid.");
            RequiredCompileInteger(operation, "stableFrames", 0);
            RequiredCompileInteger(operation, "stableSequence", -1);
            RequiredCompileInteger(operation, "lastBrokerSequence", 0);
            RequiredCompileInteger(operation, "triggerEventSequence", 0);
            ValidateOptionalCompileInteger(operation, "startedCycle", 1);
            ValidateOptionalCompileInteger(operation, "startedSequence", 1);
            ValidateOptionalCompileInteger(operation, "finishedCycle", 1);
            ValidateOptionalCompileInteger(operation, "finishedSequence", 1);
            ValidateOptionalCompileTime(operation, "stableSince", true);
            ValidateOptionalCompileTime(operation, "startedAt", false);
            ValidateOptionalCompileTime(operation, "finishedAt", false);
            ValidateCompileTuple(operation, "startedCycle", "startedSequence", "startedAt");
            ValidateCompileTuple(operation, "finishedCycle", "finishedSequence", "finishedAt");
            if (operation.ContainsKey("startedCycle"))
            {
                var startedCycle = RequiredCompileInteger(operation, "startedCycle", 1);
                var startedSequence = RequiredCompileInteger(operation, "startedSequence", 1);
                if (startedCycle != startedSequence || startedSequence <= RequiredCompileInteger(operation, "triggerEventSequence", 0) || RequiredCompileInteger(operation, "lastBrokerSequence", 0) < startedSequence) throw new InvalidDataException("compileOperation start attribution is invalid.");
            }
            if (operation.ContainsKey("finishedCycle"))
            {
                if (!operation.ContainsKey("startedCycle") || RequiredCompileInteger(operation, "finishedCycle", 1) != RequiredCompileInteger(operation, "startedCycle", 1) || RequiredCompileInteger(operation, "finishedSequence", 1) <= RequiredCompileInteger(operation, "startedSequence", 1) || RequiredCompileInteger(operation, "lastBrokerSequence", 0) < RequiredCompileInteger(operation, "finishedSequence", 1)) throw new InvalidDataException("compileOperation finish attribution is invalid.");
                DateTimeOffset startedAt; DateTimeOffset finishedAt;
                DateTimeOffset.TryParse(TextValue(operation, "startedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startedAt);
                DateTimeOffset.TryParse(TextValue(operation, "finishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out finishedAt);
                if (finishedAt < startedAt) throw new InvalidDataException("compileOperation finish precedes its start.");
            }
            if (RequiredCompileInteger(operation, "stableSequence", -1) > RequiredCompileInteger(operation, "lastBrokerSequence", 0) || RequiredCompileInteger(operation, "triggerEventSequence", 0) > RequiredCompileInteger(operation, "lastBrokerSequence", 0)) throw new InvalidDataException("compileOperation sequence watermark is invalid.");
            if (operation.ContainsKey("reloadObserved") && !(operation["reloadObserved"] is bool)) throw new InvalidDataException("compileOperation reloadObserved is invalid.");

            var assemblies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in (System.Collections.IList)operation["assemblies"])
                if (!(assembly is string) || string.IsNullOrWhiteSpace((string)assembly) || !assemblies.Add((string)assembly)) throw new InvalidDataException("compileOperation assembly is invalid or duplicated.");
            foreach (var raw in (System.Collections.IList)operation["diagnostics"])
            {
                var diagnostic = raw as Dictionary<string, object>;
                if (diagnostic == null) throw new InvalidDataException("compileOperation diagnostic is invalid.");
                var diagnosticAllowed = new HashSet<string>(new[] { "assembly", "severity", "code", "message", "file", "line", "column" }, StringComparer.Ordinal);
                if (diagnostic.Count != diagnosticAllowed.Count || diagnostic.Keys.Any(key => !diagnosticAllowed.Contains(key)) || string.IsNullOrEmpty(TextValue(diagnostic, "assembly")) || string.IsNullOrEmpty(TextValue(diagnostic, "message")) || !(diagnostic.ContainsKey("file") && diagnostic["file"] is string) || (TextValue(diagnostic, "severity") != "error" && TextValue(diagnostic, "severity") != "warning" && TextValue(diagnostic, "severity") != "info")) throw new InvalidDataException("compileOperation diagnostic schema is invalid.");
                if (diagnostic["code"] != null && !(diagnostic["code"] is string)) throw new InvalidDataException("compileOperation diagnostic code is invalid.");
                if (RequiredCompileInteger(diagnostic, "line", 0) < 0 || RequiredCompileInteger(diagnostic, "column", 0) < 0) throw new InvalidDataException("compileOperation diagnostic location is invalid.");
            }
        }

        private static string TextValue(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) ? value as string : null; }
        private static long RequiredCompileInteger(Dictionary<string, object> data, string key, long minimum) { object value; if (!data.TryGetValue(key, out value) || !(value is long) || (long)value < minimum) throw new InvalidDataException("compileOperation " + key + " is invalid."); return (long)value; }
        private static void ValidateOptionalCompileInteger(Dictionary<string, object> data, string key, long minimum) { if (data.ContainsKey(key)) RequiredCompileInteger(data, key, minimum); }
        private static void ValidateOptionalCompileTime(Dictionary<string, object> data, string key, bool allowNull) { object value; if (!data.TryGetValue(key, out value)) return; if (allowNull && value == null) return; DateTimeOffset parsed; if (!(value is string) || !DateTimeOffset.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)) throw new InvalidDataException("compileOperation " + key + " is invalid."); }
        private static void ValidateCompileTuple(Dictionary<string, object> data, string first, string second, string third) { var count = (data.ContainsKey(first) ? 1 : 0) + (data.ContainsKey(second) ? 1 : 0) + (data.ContainsKey(third) ? 1 : 0); if (count != 0 && count != 3) throw new InvalidDataException("compileOperation callback tuple is incomplete."); }
        private static bool IsPrefixedLowerHex(string value, string prefix, int digits) { return value != null && value.Length == prefix.Length + digits && value.StartsWith(prefix, StringComparison.Ordinal) && value.Skip(prefix.Length).All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')); }

        private static string RequiredString(Dictionary<string, object> data, string name) { var value = OptionalString(data, name); if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing " + name + "."); return value; }
        private static string OptionalString(Dictionary<string, object> data, string name) { object value; if (!data.TryGetValue(name, out value)) return null; var text = value as string; if (text == null) throw new InvalidDataException("Invalid " + name + "."); return text; }
        private static long RequiredLong(Dictionary<string, object> data, string name) { object value; if (!data.TryGetValue(name, out value)) throw new InvalidDataException("Missing " + name + "."); return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        private static DateTimeOffset RequiredTime(Dictionary<string, object> data, string name) { var value = OptionalTime(data, name); if (!value.HasValue) throw new InvalidDataException("Invalid " + name + "."); return value.Value; }
        private static DateTimeOffset? OptionalTime(Dictionary<string, object> data, string name) { object raw; if (!data.TryGetValue(name, out raw)) return null; var value = raw as string; DateTimeOffset time; if (value == null || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time)) throw new InvalidDataException("Invalid " + name + "."); return time; }
        private static object NormalizeTerminalEnvelope(object envelope)
        {
            if (envelope is Dictionary<string, object>) return envelope;
            var normalized = UnityCliSerializer.Parse(UnityCliSerializer.Serialize(envelope));
            if (normalized == null) throw new InvalidDataException("Journal terminal envelope is invalid.");
            return normalized;
        }

        private static void ValidateTerminalEnvelope(object envelope, string requestId)
        {
            var data = envelope as Dictionary<string, object>;
            object ok;
            object terminalRequestId;
            if (data == null || !data.TryGetValue("ok", out ok) || !(ok is bool) || !data.TryGetValue("requestId", out terminalRequestId) || !string.Equals(terminalRequestId as string, requestId, StringComparison.Ordinal)) throw new InvalidDataException("Journal terminal envelope is invalid.");
        }
        private static bool ProvenEnvelopeSucceeded(object envelope) { var data = envelope as Dictionary<string, object>; object ok; return data != null && data.TryGetValue("ok", out ok) && ok is bool && (bool)ok; }
        private static bool IsSha256(string value) { return value != null && value.Length == 64 && value.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')); }
        private static string Sha256Hex(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        private static string Hex(byte[] bytes) { var builder = new StringBuilder(bytes.Length * 2); foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture)); return builder.ToString(); }
        private static BridgeCommandException JournalCorrupt(string message) { return new BridgeCommandException("E_REQUEST_JOURNAL_CORRUPT", message, "Use a new requestId after inspecting the Unity CLI request journal."); }

        private const uint MoveFileReplaceExisting = 0x1;
        private const uint MoveFileWriteThrough = 0x8;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

        /// <summary>临时文件落盘后使用 MoveFileExW write-through 替换；失败时保留旧完整文件。</summary>
        private static void DurableWriteText(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(content); writer.Flush(); stream.Flush(true); }
                if (!MoveFileExW(temporary, path, MoveFileReplaceExisting | MoveFileWriteThrough)) throw new IOException("MoveFileExW failed: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
#endif
