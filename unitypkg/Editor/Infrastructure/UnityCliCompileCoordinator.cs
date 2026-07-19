#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>editor.compile 的持久化单通道协调器。</summary>
        internal static class UnityCliCompileCoordinator
        {
            private const string StartCommand = "internal.editor.compile.start";
            private const string StatusCommand = "internal.editor.compile.status";
            private const int StableFrames = 3;
            private static readonly TimeSpan StableDuration = TimeSpan.FromSeconds(1);
            private static readonly TimeSpan TickCadence = TimeSpan.FromMilliseconds(100);
            private static DateTime nextTickUtc;
            private static string leasedRequestId;
            private static string activeDispatchRequestId;

            internal static bool IsCommand(string command) { return command == StartCommand || command == StatusCommand; }
            internal static void Register(BridgeCommandRegistry registry)
            {
                registry.Register<BridgeRequest>(StartCommand, "internal", true, 30000, (r, c) => { throw new InvalidOperationException("Compile is coordinator-owned."); });
                registry.Register<BridgeRequest>(StatusCommand, "internal", false, 30000, (r, c) => { throw new InvalidOperationException("Compile is coordinator-owned."); });
            }

            internal static async Task<string> Execute(BridgeRequest request, byte[] rawBytes, Func<bool> tryAcceptJournal, CancellationToken cancellationToken)
            {
                if (request.Command == StatusCommand) return UnityCliSerializer.Serialize(Status(request));
                var args = request.DeserializeArgs<StartArgs>();
                ValidateStartShape(args);
                if (request.Execution != null && request.Execution.DryRun == true)
                {
                    ValidateNewStart(args);
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, Planned(args)));
                }
                var decision = AcceptOrRecover(
                    request,
                    rawBytes,
                    UnityCliToolCompletionRegistry.EditorCompile,
                    () => { if (tryAcceptJournal == null || !tryAcceptJournal()) throw new OperationCanceledException("HTTP timeout cancelled compile before journal acceptance."); },
                    () => ValidateNewStart(args),
                    record => InitializeAcceptedOperation(record, args),
                    (record, recovered) =>
                    {
                        if (!recovered.Execute || record.CompileOperation != null) return false;
                        ValidateNewStart(args);
                        InitializeAcceptedOperation(record, args);
                        return true;
                    });
                if (decision.TerminalEnvelope != null) return TerminalStartResponse(request.RequestId, decision.TerminalEnvelope);
                await Task.FromResult(0);
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", false }, { "accepted", true } }));
            }

            internal static void Initialize() { }
            internal static void Shutdown() { leasedRequestId = null; activeDispatchRequestId = null; }
            internal static void BeforeReload()
            {
                if (string.IsNullOrEmpty(leasedRequestId)) AcquireLease();
                if (!string.IsNullOrEmpty(leasedRequestId)) Update(leasedRequestId, r => { if (r.CompileOperation != null) r.CompileOperation["reloadObserved"] = true; });
            }

            /// <summary>broker 在新 domain 恢复同一 owned cycle，避免把 reload 后的 finish 回调归到 cycle 0。</summary>
            internal static long CurrentOwnedCycle()
            {
                var record = AcquireLease();
                return record == null || record.CompileOperation == null ? 0L : Long(record.CompileOperation, "startedCycle");
            }

            // 普通 mutation 接收前调用；只读观察和状态查询不受此限制。
            internal static bool IsActive()
            {
                var active = false;
                WithRequestJournalMutex(() =>
                {
                    if (!Directory.Exists(RequestJournalDirectory())) return;
                    foreach (var path in Directory.GetFiles(RequestJournalDirectory(), "*.json"))
                    {
                        var record = ReadRequestJournalRecord(path, Path.GetFileNameWithoutExtension(path));
                        if (record.CompileOperation != null && !IsTerminal(record)) { active = true; return; }
                    }
                });
                return active;
            }

            internal static void Tick()
            {
                if (DateTime.UtcNow < nextTickUtc) return;
                nextTickUtc = DateTime.UtcNow + TickCadence;
                var current = AcquireLease();
                if (current == null) return;
                var op = current.CompileOperation;
                var phase = Text(op, "phase");
                var snapshot = UnityCliCompilationEventBroker.ReadSnapshot();
                if (!string.IsNullOrEmpty(Text(op, "callbackError"))) { Unattributed(current, Text(op, "callbackError")); return; }
                if (phase == "queued" || phase == "waiting_preexisting" || phase == "preexisting_stable" || (phase == "trigger_committed" && current.Phase == "accepted_before_effect"))
                {
                    if (EditorApplication.isPlaying || IsPlayModeChanging()) { FailBeforeEffect(current, "E_UNITY_COMPILE_REQUIRES_EDIT_MODE", "Script compilation requires stable Edit Mode."); return; }
                }
                if (Bool(op, "reloadObserved") && phase == "dispatching") { Unknown(current, "Domain reload interrupted dispatch before compilation attribution.", "E_COMPILE_DISPATCH_AMBIGUOUS"); return; }
                if (DeadlineExpired(current)) { Timeout(current, phase); return; }
                if (phase == "compiling" && Bool(op, "reloadObserved") && ReadyAfterCompile() && Stable(current.RequestId, snapshot.Sequence)) { Unattributed(current, "Compilation finished callback was missing after domain reload."); return; }
                if (phase == "queued") { SetPhase(current.RequestId, "waiting_preexisting", snapshot); return; }
                if (phase == "waiting_preexisting")
                {
                    if (!PreexistingStable()) { ResetStable(current.RequestId); return; }
                    if (!Stable(current.RequestId, snapshot.Sequence)) return;
                    SetPhase(current.RequestId, "preexisting_stable", snapshot); return;
                }
                if (phase == "preexisting_stable")
                {
                    if (EditorApplication.isPlaying || IsPlayModeChanging()) { FailBeforeEffect(current, "E_UNITY_COMPILE_REQUIRES_EDIT_MODE", "Script compilation requires stable Edit Mode."); return; }
                    if (!PreexistingStable() || snapshot.Sequence != Long(op, "lastBrokerSequence") || OrdinaryMutationRunning()) { ResetStable(current.RequestId); SetPhase(current.RequestId, "waiting_preexisting", snapshot); return; }
                    // 同一 main-thread Tick 内完成 watermark、effect barrier、dispatch marker 与 Unity API 调用。
                    Update(current.RequestId, r => { r.CompileOperation["triggerEventSequence"] = snapshot.Sequence; r.CompileOperation["phase"] = "trigger_committed"; r.CompileOperation["lastBrokerSequence"] = snapshot.Sequence; });
                    CommitBarrierAndDispatch(current.RequestId, snapshot, false);
                    return;
                }
                if (phase == "trigger_committed")
                {
                    var needsFreshStability = Bool(op, "reloadObserved") || snapshot.Sequence != Long(op, "triggerEventSequence");
                    if (!PreexistingStable() || OrdinaryMutationRunning()) { ResetStable(current.RequestId); return; }
                    if (needsFreshStability && !Stable(current.RequestId, snapshot.Sequence)) return;
                    Update(current.RequestId, r => { r.CompileOperation["reloadObserved"] = false; r.CompileOperation["triggerEventSequence"] = snapshot.Sequence; r.CompileOperation["lastBrokerSequence"] = snapshot.Sequence; });
                    CommitBarrierAndDispatch(current.RequestId, snapshot, current.Phase == "effect_started");
                    return;
                }
                if (phase == "dispatching") { if (string.Equals(activeDispatchRequestId, current.RequestId, StringComparison.Ordinal)) return; Unknown(current, "Compile dispatch did not return an attributable callback before coordinator recovery.", "E_COMPILE_DISPATCH_AMBIGUOUS"); return; }
                if (phase == "awaiting_started" || phase == "compiling") return;
                if (phase == "cycle_finished") { SetPhase(current.RequestId, "waiting_stable", snapshot); return; }
                if (phase == "waiting_stable")
                {
                    if (!ReadyAfterCompile()) { ResetStable(current.RequestId); return; }
                    if (!Stable(current.RequestId, snapshot.Sequence)) return;
                    Finish(current); return;
                }
            }

            internal static void OnCompilationStarted(long cycle, long sequence, DateTime startedAt)
            {
                ApplyCallback(sequence, record =>
                {
                    var op = record.CompileOperation; var phase = Text(op, "phase");
                    if (BeforeOwnedDispatch(record, phase)) { ObservePreexistingCallback(op, sequence); return; }
                    if ((phase != "dispatching" && phase != "awaiting_started") || sequence <= Long(op, "triggerEventSequence") || Long(op, "startedCycle") != 0) { CallbackUnattributed(op, "Compilation started outside the owned dispatch window."); return; }
                    op["startedCycle"] = cycle; op["startedSequence"] = sequence; op["startedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture); op["phase"] = "compiling"; op["lastBrokerSequence"] = sequence;
                });
            }

            internal static void OnAssemblyCompilationFinished(long cycle, long sequence, string assembly, List<Dictionary<string, object>> diagnostics)
            {
                ApplyCallback(sequence, record =>
                {
                    var op = record.CompileOperation;
                    if (BeforeOwnedDispatch(record, Text(op, "phase"))) { ObservePreexistingCallback(op, sequence); return; }
                    if (Text(op, "phase") != "compiling" || Long(op, "startedCycle") != cycle) { CallbackUnattributed(op, "Assembly compilation callback could not be attributed to the owned cycle."); return; }
                    if (string.IsNullOrWhiteSpace(assembly) || diagnostics == null || diagnostics.Any(diagnostic => !ValidDiagnostic(assembly, diagnostic))) { CallbackUnattributed(op, "Assembly compilation callback contained an invalid diagnostic payload."); return; }
                    var assemblies = op["assemblies"] as List<object> ?? new List<object>();
                    if (!assemblies.Cast<object>().Any(item => string.Equals(item as string, assembly, StringComparison.Ordinal))) assemblies.Add(assembly ?? string.Empty);
                    op["assemblies"] = assemblies;
                    var all = op["diagnostics"] as List<object> ?? new List<object>();
                    foreach (var diagnostic in diagnostics)
                        if (!all.Cast<Dictionary<string, object>>().Any(item => DiagnosticKey(item) == DiagnosticKey(diagnostic))) all.Add(diagnostic);
                    op["diagnostics"] = all; op["lastBrokerSequence"] = sequence;
                });
            }

            internal static void OnCompilationFinished(long cycle, long sequence, DateTime finishedAt)
            {
                ApplyCallback(sequence, record =>
                {
                    var op = record.CompileOperation;
                    if (BeforeOwnedDispatch(record, Text(op, "phase"))) { ObservePreexistingCallback(op, sequence); return; }
                    if (Text(op, "phase") != "compiling" || Long(op, "startedCycle") != cycle || Long(op, "finishedCycle") != 0) { CallbackUnattributed(op, "Compilation finished callback could not be attributed to the owned cycle."); return; }
                    op["finishedCycle"] = cycle; op["finishedSequence"] = sequence; op["finishedAt"] = finishedAt.ToString("O", CultureInfo.InvariantCulture); op["phase"] = "cycle_finished"; op["lastBrokerSequence"] = sequence;
                });
            }

            /// <summary>barrier 后不让出主线程；dispatching 是 API 是否已调用的唯一歧义边界。</summary>
            private static void CommitBarrierAndDispatch(string requestId, UnityCliCompilationEventBroker.Snapshot snapshot, bool barrierAlreadyCommitted)
            {
                if (!barrierAlreadyCommitted)
                {
                    var continuation = CopyOperation(ReadCompileOperation(requestId));
                    MarkEffectStarted(requestId, "compile-operation-v1", continuation);
                }
                if (!string.IsNullOrEmpty(activeDispatchRequestId)) throw JournalCorrupt("Concurrent compile dispatch is not permitted.");
                activeDispatchRequestId = requestId;
                try
                {
                    SetPhase(requestId, "dispatching", snapshot);
                    RequestJournalRecord record = null;
                    WithRequestJournalMutex(() => { record = ReadRequestJournalRecord(RequestJournalPath(requestId), requestId); });
                    Dispatch(record);
                }
                finally { if (string.Equals(activeDispatchRequestId, requestId, StringComparison.Ordinal)) activeDispatchRequestId = null; }
            }

            private static void Dispatch(RequestJournalRecord record)
            {
                try
                {
                    var op = record.CompileOperation; var path = Text(op, "path");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var options = ImportAssetOptions.Default;
                        if (Bool(op, "forceUpdate")) options |= ImportAssetOptions.ForceUpdate;
                        if (Bool(op, "importRecursive")) options |= ImportAssetOptions.ImportRecursive;
                        AssetDatabase.ImportAsset(path, options);
                    }
                    else if (Bool(op, "refresh")) AssetDatabase.Refresh(Bool(op, "forceUpdate") ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
                    // Import 可能同步触发回调；必须 fresh-read durable phase，不能用 dispatch 前的旧对象再请求第二个 cycle。
                    var afterImport = ReadCompileOperation(record.RequestId);
                    if (Long(afterImport, "startedCycle") == 0 && !EditorApplication.isCompiling) CompilationPipeline.RequestScriptCompilation();
                    Update(record.RequestId, r => { var live = r.CompileOperation; if (Text(live, "phase") == "dispatching" && Long(live, "startedCycle") == 0) live["phase"] = "awaiting_started"; });
                }
                catch (Exception exception)
                {
                    WriteLog("compile_dispatch_failed requestId=" + record.RequestId + " type=" + exception.GetType().Name + " message=" + exception.Message);
                    Unknown(record, "Compile dispatch did not produce an attributable callback sequence.", "E_COMPILE_DISPATCH_AMBIGUOUS");
                }
            }

            private static void Finish(RequestJournalRecord record)
            {
                var op = record.CompileOperation; var diagnostics = (op["diagnostics"] as List<object> ?? new List<object>()).Cast<Dictionary<string, object>>().ToList();
                var errors = diagnostics.Count(x => Text(x, "severity") == "error"); var warnings = diagnostics.Count(x => Text(x, "severity") == "warning");
                if (errors > 0) { KnownFailure(record, CompileFailed(record.RequestId, op, diagnostics, errors, warnings)); return; }
                var envelope = ProtocolEnvelopeFactory.Success(record.RequestId, CompileData(op, diagnostics, errors, warnings));
                ProvenTerminal(record, envelope, "completed");
            }

            private static void Timeout(RequestJournalRecord record, string phase)
            {
                var knownCycle = phase == "cycle_finished" || phase == "waiting_stable";
                var effectStarted = record.Phase == "effect_started" || record.Phase == "effect_proven";
                if (knownCycle) Unknown(record, "The compilation cycle finished but Unity did not become stable before the deadline.", "E_UNITY_TRANSITION_TIMEOUT");
                else if (!effectStarted && (phase == "queued" || phase == "waiting_preexisting" || phase == "preexisting_stable" || phase == "trigger_committed")) FailBeforeEffect(record, "E_EXECUTION_TIMEOUT", "Compilation did not start before the immutable deadline.");
                else if (phase == "awaiting_started" || phase == "compiling") Unattributed(record, phase == "awaiting_started" ? "Compilation started callback was missing before the deadline." : "Compilation finished callback was missing before the deadline.");
                else Unknown(record, "Compilation did not produce an attributable terminal cycle before the deadline.");
            }

            private static void Unattributed(RequestJournalRecord record, string message) { Unknown(record, message, "E_COMPILE_CYCLE_UNATTRIBUTED"); }
            private static void CallbackUnattributed(Dictionary<string, object> operation, string message) { operation["callbackError"] = message; }
            private static void KnownFailure(RequestJournalRecord record, object envelope) { ProvenTerminal(record, envelope, "failed"); }
            private static void ProvenTerminal(RequestJournalRecord record, object envelope, string state)
            {
                if (IsTerminal(record)) return;
                var op = record.CompileOperation;
                var proofText = "compile-proof-v1\0" + record.RequestDigest + "\0" + Text(op, "runId") + "\0" + Text(op, "startedAt") + "\0" + Text(op, "finishedAt") + "\0" + Sha256Hex(Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(op["diagnostics"])));
                MarkEffectProven(record.RequestId, "compilation-pipeline-v1", Encoding.UTF8.GetBytes(proofText), envelope);
                PersistTerminal(record.RequestId, envelope, state);
                SetPhase(record.RequestId, "terminal", UnityCliCompilationEventBroker.ReadSnapshot());
            }
            private static void FailBeforeEffect(RequestJournalRecord record, string code, string message) { PersistTerminal(record.RequestId, CompileError(record.RequestId, code, message), "failed"); }
            private static void Unknown(RequestJournalRecord record, string message, string reasonCode = "E_COMPILE_CYCLE_UNATTRIBUTED") { PersistTerminal(record.RequestId, CompileOutcomeUnknown(record.RequestId, message, reasonCode), "outcome_unknown"); }

            // 首次 durable record 必须同时包含 FIFO intent 与 accepted phase，不能留下 received-only compile root。
            private static void InitializeAcceptedOperation(RequestJournalRecord record, StartArgs args)
            {
                if (record == null || IsTerminal(record) || record.CompileOperation != null) return;
                if (!string.Equals(record.ToolName, "editor.compile", StringComparison.Ordinal)) throw JournalCorrupt("compileOperation is only valid for editor.compile.");
                record.CompileOperation = NewOperation(record.RequestDigest, args, NextSequenceLocked());
                record.State = "accepted";
                record.Phase = "accepted_before_effect";
                record.AcceptedAt = DateTimeOffset.UtcNow;
            }
            private static long NextSequenceLocked()
            {
                var sidecar = Path.Combine(RequestJournalDirectory(), "compile-sequence.txt"); long max = 0;
                if (File.Exists(sidecar))
                {
                    var text = File.ReadAllText(sidecar).Trim(); long current;
                    if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out current) || current < 0 || text != current.ToString(CultureInfo.InvariantCulture)) throw JournalCorrupt("Compile sequence sidecar is corrupt.");
                    max = current;
                }
                else if (Directory.Exists(RequestJournalDirectory())) foreach (var file in Directory.GetFiles(RequestJournalDirectory(), "*.json")) { var r = ReadRequestJournalRecord(file, Path.GetFileNameWithoutExtension(file)); if (r.CompileOperation != null) max = Math.Max(max, Long(r.CompileOperation, "sequence")); }
                var next = checked(max + 1); DurableWriteText(sidecar, next.ToString(CultureInfo.InvariantCulture)); return next;
            }

            private static RequestJournalRecord AcquireLease()
            {
                RequestJournalRecord winner = null;
                WithRequestJournalMutex(() =>
                {
                    // 已有 lease 时只读该 root；queue sequence 单调，后来的请求不可能排到它之前。
                    if (!string.IsNullOrEmpty(leasedRequestId))
                    {
                        var leasedPath = RequestJournalPath(leasedRequestId);
                        if (File.Exists(leasedPath))
                        {
                            var leased = ReadRequestJournalRecord(leasedPath, leasedRequestId);
                            if (leased.CompileOperation != null && !IsTerminal(leased) && string.Equals(leased.EditorSessionId, EditorSessionId(), StringComparison.Ordinal)) winner = leased;
                        }
                    }
                    if (winner != null || !Directory.Exists(RequestJournalDirectory())) return;
                    // 新 EditorSession 绝不接管旧 session 的 effect 或继续触发它。
                    foreach (var path in Directory.GetFiles(RequestJournalDirectory(), "*.json"))
                    {
                        var record = ReadRequestJournalRecord(path, Path.GetFileNameWithoutExtension(path));
                        if (record.CompileOperation == null || IsTerminal(record)) continue;
                        if (!string.Equals(record.EditorSessionId, EditorSessionId(), StringComparison.Ordinal)) { TerminalizeOldSessionLocked(record); WriteRequestJournalRecord(path, record); continue; }
                        if (winner == null || Long(record.CompileOperation, "sequence") < Long(winner.CompileOperation, "sequence")) winner = record;
                    }
                    if (winner != null && Text(winner.CompileOperation, "lease") != EditorSessionId()) { winner.CompileOperation["lease"] = EditorSessionId(); WriteRequestJournalRecord(RequestJournalPath(winner.RequestId), winner); }
                });
                leasedRequestId = winner == null ? null : winner.RequestId; return winner;
            }
            // mutex 内仅修改 record，终态 envelope 不经 PersistTerminal 以免递归获取 journal mutex。
            private static void TerminalizeOldSessionLocked(RequestJournalRecord record)
            {
                var phase = record.Phase;
                if (phase == "effect_proven" && record.ProvenEnvelope != null)
                {
                    record.State = ProvenEnvelopeSucceeded(record.ProvenEnvelope) ? "completed" : "failed";
                    record.TerminalEnvelope = record.ProvenEnvelope;
                    record.ProvenEnvelope = null;
                }
                else if (phase == "effect_started") { record.State = "outcome_unknown"; record.TerminalEnvelope = OutcomeUnknownEnvelope(record.RequestId); }
                else { record.State = "cancelled"; record.TerminalEnvelope = CancelledEnvelope(record.RequestId); }
                record.Phase = "result_persisted";
                record.EndedAt = DateTimeOffset.UtcNow;
            }
            private static bool ProvenEnvelopeSucceeded(object envelope) { var data = envelope as Dictionary<string, object>; object ok; return data != null && data.TryGetValue("ok", out ok) && ok is bool && (bool)ok; }
            private static void ApplyCallback(long sequence, Action<RequestJournalRecord> change)
            {
                // 空 lease 也必须先取得 durable owner，避免 reload 后首个回调被丢弃。
                if (string.IsNullOrEmpty(leasedRequestId)) AcquireLease();
                if (string.IsNullOrEmpty(leasedRequestId)) return;
                WithRequestJournalMutex(() =>
                {
                    var path = RequestJournalPath(leasedRequestId);
                    if (!File.Exists(path)) return;
                    var record = ReadRequestJournalRecord(path, leasedRequestId);
                    if (record.CompileOperation == null || IsTerminal(record)) return;
                    if (sequence <= Long(record.CompileOperation, "lastBrokerSequence"))
                    {
                        // owned trigger 前的外部 callback 不属于本次 operation，重复或乱序也只重置静默点。
                        if (BeforeOwnedDispatch(record, Text(record.CompileOperation, "phase"))) ObservePreexistingCallback(record.CompileOperation, Long(record.CompileOperation, "lastBrokerSequence"));
                        else CallbackUnattributed(record.CompileOperation, "Duplicate or out-of-order compilation callback.");
                        WriteRequestJournalRecord(path, record);
                        return;
                    }
                    change(record);
                    WriteRequestJournalRecord(path, record);
                });
            }
            private static bool PreEffectPhase(string phase) { return phase == "queued" || phase == "waiting_preexisting" || phase == "preexisting_stable"; }
            private static bool BeforeOwnedDispatch(RequestJournalRecord record, string phase) { return PreEffectPhase(phase) || phase == "trigger_committed"; }
            private static void ObservePreexistingCallback(Dictionary<string, object> operation, long sequence) { operation["lastBrokerSequence"] = sequence; operation["stableFrames"] = 0L; operation["stableSince"] = null; }
            private static void Update(string requestId, Action<RequestJournalRecord> action) { AdvanceRequestJournal(requestId, action); }
            private static Dictionary<string, object> ReadCompileOperation(string requestId) { Dictionary<string, object> operation = null; WithRequestJournalMutex(() => { var record = ReadRequestJournalRecord(RequestJournalPath(requestId), requestId); operation = CopyOperation(record.CompileOperation); }); return operation; }
            private static void SetPhase(string requestId, string phase, UnityCliCompilationEventBroker.Snapshot snapshot) { Update(requestId, r => { if (r.CompileOperation != null) { r.CompileOperation["phase"] = phase; r.CompileOperation["lastBrokerSequence"] = snapshot.Sequence; } }); }
            private static bool OrdinaryMutationRunning() { lock (QueueLock) return RunningWork.Any(work => work.IsMutation && !IsCommand(work.Request.Command)); }
            // effect 前只接受稳定 Edit Mode；effect 后允许稳定 Edit/Play Mode，以返回实际终态而不自动 stop。
            private static bool PlayModeStable() { return !IsPlayModeChanging(); }
            private static bool PreexistingStable() { return !EditorApplication.isCompiling && !EditorApplication.isUpdating && !BuildPipeline.isBuildingPlayer && !IsDomainReloading() && !EditorApplication.isPlaying && !IsPlayModeChanging(); }
            private static bool ReadyAfterCompile() { return !EditorApplication.isCompiling && !EditorApplication.isUpdating && !BuildPipeline.isBuildingPlayer && !IsDomainReloading() && PlayModeStable(); }
            private static bool DeadlineExpired(RequestJournalRecord record) { return DateTimeOffset.UtcNow >= record.AbsoluteDeadlineUtc; }
            private static bool Stable(string requestId, long sequence) { var stable = false; Update(requestId, r => { var op = r.CompileOperation; if (Long(op, "stableSequence") != sequence) { op["stableSequence"] = sequence; op["stableFrames"] = 1L; op["stableSince"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture); return; } var frames = Long(op, "stableFrames"); if (frames < StableFrames) op["stableFrames"] = frames + 1; DateTimeOffset since; stable = Long(op, "stableFrames") >= StableFrames && DateTimeOffset.TryParse(Text(op, "stableSince"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out since) && DateTimeOffset.UtcNow - since >= StableDuration; }); return stable; }
            private static void ResetStable(string requestId) { Update(requestId, r => { var op = r.CompileOperation; if (Long(op, "stableFrames") != 0 || !string.IsNullOrEmpty(Text(op, "stableSince"))) { op["stableFrames"] = 0L; op["stableSince"] = null; } }); }
            private static Dictionary<string, object> NewOperation(string digest, StartArgs args, long sequence) { return new Dictionary<string, object> { { "schemaVersion", 1L }, { "sequence", sequence }, { "runId", "compile-" + digest.Substring(0, 32) }, { "phase", "queued" }, { "lease", null }, { "refresh", args.Refresh ?? true }, { "path", args.Path }, { "forceUpdate", args.ForceUpdate ?? false }, { "importRecursive", args.ImportRecursive ?? false }, { "includeWarnings", args.IncludeWarnings ?? true }, { "assemblies", new List<object>() }, { "diagnostics", new List<object>() }, { "stableFrames", 0L }, { "stableSequence", -1L }, { "lastBrokerSequence", 0L }, { "triggerEventSequence", 0L }, { "callbackError", null } }; }
            private static Dictionary<string, object> CopyOperation(Dictionary<string, object> source) { return UnityCliSerializer.Parse(UnityCliSerializer.Serialize(source)) as Dictionary<string, object>; }
            private static string TerminalStartResponse(string requestId, object rootEnvelope) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(requestId, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", rootEnvelope } })); }
            private static object Status(BridgeRequest request) { var args = request.DeserializeArgs<StatusArgs>(); if (args == null || !IsSafeId(args.RootRequestId)) throw new BridgeCommandException("E_SCHEMA_INVALID", "rootRequestId is required."); RequestJournalRecord record = null; WithRequestJournalMutex(() => { var p = RequestJournalPath(args.RootRequestId); if (File.Exists(p)) record = ReadRequestJournalRecord(p, args.RootRequestId); }); if (record == null || record.CompileOperation == null) throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "Compile request was not found."); return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", IsTerminal(record) }, { "rootEnvelope", IsTerminal(record) ? record.TerminalEnvelope : null }, { "phase", IsTerminal(record) ? null : Text(record.CompileOperation, "phase") } }); }
            private static Dictionary<string, object> Planned(StartArgs args) { return new Dictionary<string, object> { { "plannedEffects", PlannedEffects(args.Path, args.Refresh ?? true) }, { "refresh", args.Refresh ?? true }, { "path", args.Path } }; }
            // 成功与编译失败共用同一摘要，避免两个终态逐渐漂移。
            private static Dictionary<string, object> CommonCompileSummary(Dictionary<string, object> op, int errors, int warnings, string status) { DateTime start; DateTime finish; var started = Text(op, "startedAt"); var finished = Text(op, "finishedAt"); var duration = DateTime.TryParse(started, out start) && DateTime.TryParse(finished, out finish) ? (long)(finish - start).TotalMilliseconds : 0L; var assemblies = op["assemblies"] as List<object> ?? new List<object>(); return new Dictionary<string, object> { { "completed", true }, { "refresh", Bool(op, "refresh") }, { "path", string.IsNullOrEmpty(Text(op, "path")) ? null : Text(op, "path") }, { "startedAt", started }, { "finishedAt", finished }, { "durationMs", duration }, { "assemblyCount", assemblies.Count }, { "errorCount", errors }, { "warningCount", warnings }, { "status", status } }; }
            private static List<object> FilterDiagnostics(Dictionary<string, object> op, List<Dictionary<string, object>> diagnostics) { return Bool(op, "includeWarnings") ? diagnostics.Cast<object>().ToList() : diagnostics.Where(x => Text(x, "severity") != "warning").Cast<object>().ToList(); }
            private static object CompileData(Dictionary<string, object> op, List<Dictionary<string, object>> diagnostics, int errors, int warnings) { return new Dictionary<string, object> { { "compile", CommonCompileSummary(op, errors, warnings, warnings > 0 ? "warnings" : "clean") }, { "diagnostics", FilterDiagnostics(op, diagnostics) }, { "editorState", PublicEditorState() }, { "plannedEffects", PlannedEffects(op) } }; }
            private static object CompileFailed(string requestId, Dictionary<string, object> op, List<Dictionary<string, object>> diagnostics, int errors, int warnings) { return ProtocolEnvelopeFactory.Error(requestId, "E_UNITY_COMPILE_FAILED", "Unity reported compiler errors.", new Dictionary<string, object> { { "compile", CommonCompileSummary(op, errors, warnings, "errors") }, { "diagnostics", FilterDiagnostics(op, diagnostics) }, { "editorState", PublicEditorState() } }, new[] { "Fix the listed compiler diagnostics, then submit a new compile request." }); }
            private static object CompileError(string requestId, string code, string message) { return ProtocolEnvelopeFactory.Error(requestId, code, message, new Dictionary<string, object> { { "editorState", PublicEditorState() } }, code == "E_UNITY_COMPILE_REQUIRES_EDIT_MODE" ? new[] { "Run unity_cli tools run editor.stop, then retry editor.compile." } : new[] { "Inspect editor.state before submitting a new request." }); }
            private static object CompileOutcomeUnknown(string requestId, string message, string reasonCode) { return ProtocolEnvelopeFactory.Error(requestId, "E_REQUEST_OUTCOME_UNKNOWN", message, new Dictionary<string, object> { { "reasonCode", reasonCode }, { "editorState", PublicEditorState() } }, new[] { "Inspect editor.state and the compiler state before deciding whether to submit a new request." }); }
            private static Dictionary<string, object> PublicEditorState() { var modeStable = PlayModeStable(); var transition = EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || IsDomainReloading() || !modeStable; var mode = !modeStable ? "transitioning" : (EditorApplication.isPlaying ? "play" : "edit"); return new Dictionary<string, object> { { "status", transition ? "transitioning" : "ready" }, { "compile", new Dictionary<string, object> { { "known", true }, { "value", EditorApplication.isCompiling } } }, { "import", new Dictionary<string, object> { { "known", true }, { "value", EditorApplication.isUpdating } } }, { "playMode", new Dictionary<string, object> { { "known", true }, { "value", mode } } } }; }
            private static List<object> PlannedEffects(Dictionary<string, object> op) { return PlannedEffects(Text(op, "path"), Bool(op, "refresh")); }
            private static List<object> PlannedEffects(string path, bool refresh) { var effects = new List<object>(); if (!string.IsNullOrEmpty(path)) effects.Add(new Dictionary<string, object> { { "type", "asset-import" }, { "path", path } }); else if (refresh) effects.Add(new Dictionary<string, object> { { "type", "asset-refresh" } }); effects.Add(new Dictionary<string, object> { { "type", "script-compile" } }); effects.Add(new Dictionary<string, object> { { "type", "domain-reload" } }); return effects; }
            private static bool ValidDiagnostic(string assembly, Dictionary<string, object> diagnostic) { object code; object file; object line; object column; if (diagnostic == null || diagnostic.Count != 7 || !string.Equals(Text(diagnostic, "assembly"), assembly, StringComparison.Ordinal) || string.IsNullOrEmpty(Text(diagnostic, "message")) || !diagnostic.TryGetValue("code", out code) || (code != null && !(code is string)) || !diagnostic.TryGetValue("file", out file) || !(file is string) || !diagnostic.TryGetValue("line", out line) || !(line is long) || (long)line < 0 || !diagnostic.TryGetValue("column", out column) || !(column is long) || (long)column < 0) return false; var severity = Text(diagnostic, "severity"); return severity == "error" || severity == "warning" || severity == "info"; }
            private static string DiagnosticKey(Dictionary<string, object> x) { return string.Join("|", new[] { Text(x, "assembly"), Text(x, "severity"), Text(x, "code"), Text(x, "message"), Text(x, "file").Replace('\\', '/').ToLowerInvariant(), Text(x, "line"), Text(x, "column") }); }
            private static string Text(IDictionary<string, object> d, string n) { object v; return d != null && d.TryGetValue(n, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : string.Empty; }
            private static long Long(IDictionary<string, object> d, string n) { long v; return long.TryParse(Text(d, n), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }
            private static bool Bool(IDictionary<string, object> d, string n) { object v; return d != null && d.TryGetValue(n, out v) && v is bool && (bool)v; }
            private static void ValidateStartShape(StartArgs args) { if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Compile input and expectedEditorSessionId are required."); if (args.Path != null && args.Path.Length == 0) throw new BridgeCommandException("E_SCHEMA_INVALID", "Compile path must be non-empty when provided."); }
            // 仅新 root 执行 session/path 前置校验；exact-digest redelivery 必须先恢复已有 durable terminal。
            private static void ValidateNewStart(StartArgs args) { if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed after compile preflight.", "Read editor.state and submit a new root request to the current session."); if (!string.IsNullOrEmpty(args.Path)) args.Path = AssetToolSupport.RequireExistingAssetPath(args.Path); }
            private sealed class StartArgs { public bool? Refresh { get; set; } public string Path { get; set; } public bool? ForceUpdate { get; set; } public bool? ImportRecursive { get; set; } public bool? IncludeWarnings { get; set; } public string ExpectedEditorSessionId { get; set; } }
            private sealed class StatusArgs { public string RootRequestId { get; set; } }
        }
    }
}
#endif
