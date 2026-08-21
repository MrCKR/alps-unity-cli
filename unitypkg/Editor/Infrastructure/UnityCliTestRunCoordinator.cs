#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.TestTools;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>TestRunner 的 callback 身份和阶段保存在 active-test-run 文件中，reload 后只续接而不重放 Execute。</summary>
        internal static class UnityCliTestRunCoordinator
        {
            internal const string Command = "internal.test.run.start";
            private const string StatusCommand = "internal.test.run.status";
            private const string CancelPlanCommand = "internal.test.run.cancel.plan";
            private const string CancelCommand = "internal.test.run.cancel";
            private const string CancelStatusCommand = "internal.test.run.cancel.status";
            private const string CancelTerminalScope = "test-cancel";
            private const string Scope = "test-run";
            private static readonly TimeSpan RecoveryGrace = TimeSpan.FromSeconds(5);
            private static TestRunnerApi api;
            private static Broker broker;
            // 终态不落盘；同一 Editor session 内的 status 轮询可取回 callback 结果。
            private static string terminalRequestId;
            private static string terminalRequestDigest;
            private static object terminalEnvelope;
            private static string cancelTerminalRequestId;
            private static object cancelTerminalData;
            private static readonly Coordinator operation = new Coordinator();

            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return "test-run"; } }
                protected override string StartCommand { get { return Command; } }
                protected override string StatusCommand { get { return UnityCliTestRunCoordinator.StatusCommand; } }
                protected override string OperationDisplayName { get { return "Test run"; } }
                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<Args>(); }
                protected override void ValidateStart(object args) { }
                protected override object Planned(object args) { return null; }
                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object args) { return null; }
                protected override void OnTick(Dictionary<string, object> root) { }
                protected override object BuildSuccessData(Dictionary<string, object> root) { return null; }
                protected override IEnumerable<(string name, bool destructive)> AdditionalCommands
                {
                    get { yield return (CancelPlanCommand, false); yield return (CancelCommand, true); yield return (CancelStatusCommand, false); }
                }
                internal void Initialize() { InitializeLegacy(); }
                internal void Shutdown() { ShutdownLegacy(); }
                internal void BeforeReload() { BeforeReloadLegacy(); }
                internal override Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return LegacyExecute(request, raw, accept, ct); }
                internal override void Tick() { LegacyTick(); }
            }

            private sealed class Root { internal string RequestId; internal string RequestDigest; internal string EditorSessionId; internal DateTimeOffset AbsoluteDeadlineUtc; internal Dictionary<string, object> TestOperation; }
            private sealed class CancelDispatchIdentity
            {
                internal string RootRequestId; internal string RootRequestDigest; internal string EditorSessionId; internal DateTimeOffset AbsoluteDeadlineUtc;
                internal string RunId; internal string CancelRequestId; internal string CancelRequestDigest;
            }
            private enum CancelDispatchOutcome { NotInvoked, Returned, OutcomeUnknown }
            internal static bool IsCommand(string command) { return operation.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { operation.Register(registry); }
            internal static void Initialize() { operation.Initialize(); RecoverTerminalCleanup(); }
            internal static void Shutdown() { operation.Shutdown(); }
            internal static void BeforeReload() { operation.BeforeReload(); }
            private static void InitializeLegacy() { api = ScriptableObject.CreateInstance<TestRunnerApi>(); broker = new Broker(); api.RegisterCallbacks(broker, 1000); }
            /// <summary>一次run终态会注销callback；后续新identity必须先重建唯一API与broker。</summary>
            private static void EnsureLegacyApi() { if (api == null || broker == null) InitializeLegacy(); }
            private static void ShutdownLegacy() { if (api != null && broker != null) api.UnregisterCallbacks(broker); api = null; broker = null; terminalRequestId = null; terminalRequestDigest = null; terminalEnvelope = null; cancelTerminalRequestId = null; cancelTerminalData = null; }
            private static void BeforeReloadLegacy() { }

            internal static Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return operation.Execute(request, raw, accept, ct); }

            private static async Task<string> LegacyExecute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct)
            {
                if (request.Command == StatusCommand) return UnityCliSerializer.Serialize(Status(request));
                if (request.Command == CancelPlanCommand) return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, CancelPlan(request)));
                if (request.Command == CancelCommand)
                {
                    var cancelDigest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
                    object cancelTerminal;
                    if (UnityCliActiveOperation.TryGetTerminal(CancelTerminalScope, request.RequestId, cancelDigest, out cancelTerminal))
                        return CancelTerminalStartResponse(request.RequestId, cancelTerminal);
                    if (accept == null || !accept()) throw new OperationCanceledException("HTTP timeout cancelled test cancellation before acceptance.");
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, Cancel(request, cancelDigest, ct)));
                }
                if (request.Command == CancelStatusCommand) return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, CancelStatus(request)));
                var args = request.DeserializeArgs<Args>();
                var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
                object durableTerminal;
                try
                {
                    if (UnityCliActiveOperation.TryGetTerminal(Scope, request.RequestId, digest, out durableTerminal))
                    {
                        terminalRequestId = request.RequestId;
                        terminalRequestDigest = digest;
                        terminalEnvelope = durableTerminal;
                        try { UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, request.RequestId, digest); } catch { }
                        return Terminal(request.RequestId, durableTerminal);
                    }
                }
                catch (BridgeCommandException exception) { return SerializeBridgeExceptionEnvelope(request.RequestId, exception); }

                Root root;
                try { root = Load(); }
                catch (Exception) { return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test active state cannot be read; outcome is unknown.")); }
                if (root != null)
                {
                    if (!string.Equals(root.RequestId, request.RequestId, StringComparison.Ordinal)) return BusyStart(request.RequestId);
                    if (!string.Equals(root.RequestDigest, digest, StringComparison.Ordinal))
                        return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_REQUEST_ID_REUSED", "Test requestId was reused with different input."));
                    if (!string.Equals(root.EditorSessionId, EditorSessionId(), StringComparison.Ordinal))
                    {
                        Fail(root, "E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before tests began.");
                        return Accepted(request.RequestId, digest);
                    }
                    if (!UnityCliSessionMutationLease.IsOwnedBy(root.RequestId, root.RequestDigest))
                        return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test active root no longer owns its exact lease; outcome is unknown."));
                    EnsureLegacyApi();
                    return Accepted(request.RequestId, digest);
                }

                bool newlyAcquiredLease;
                if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out newlyAcquiredLease) || !newlyAcquiredLease) return BusyStart(request.RequestId);
                try
                {
                    if (accept == null || !accept()) throw new OperationCanceledException();
                    Validate(args);
                    EnsureLegacyApi();
                    root = NewRoot(request, digest, args);
                    Save(root);
                    await Task.FromResult(0);
                    return Accepted(request.RequestId, digest);
                }
                catch (OperationCanceledException)
                {
                    var released = TrySchedulePreEffectLeaseCleanup(request.RequestId, digest);
                    var message = released ? "Tests did not begin before the request deadline." : "Tests did not begin and exact lease cleanup remains pending.";
                    return Terminal(request.RequestId, ProtocolEnvelopeFactory.Error(request.RequestId, "E_EXECUTION_TIMEOUT", message, null, new[] { "Inspect editor.state and retry after cleanup completes." }));
                }
                catch (BridgeCommandException ex)
                {
                    return TrySchedulePreEffectLeaseCleanup(request.RequestId, digest)
                        ? SerializeBridgeExceptionEnvelope(request.RequestId, ex)
                        : SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test validation failed and exact lease cleanup remains pending."));
                }
                catch (Exception exception)
                {
                    var released = TrySchedulePreEffectLeaseCleanup(request.RequestId, digest);
                    var message = released
                        ? "Test start failed before its effect: " + exception.GetType().Name + "."
                        : "Test start failed before its effect and exact lease cleanup remains pending.";
                    return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", message));
                }
            }

            /// <summary>effect前失败也必须保留exact cleanup marker，直到可证明lease已不再归属该请求。</summary>
            private static bool TrySchedulePreEffectLeaseCleanup(string requestId, string requestDigest)
            {
                try { UnityCliActiveOperation.Clear(Scope); } catch { }
                try { UnityCliSessionMutationLease.Release(requestId, requestDigest); } catch { }
                bool isOwned;
                if (UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned) && !isOwned) return true;
                try { UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, requestId, requestDigest); } catch { }
                return false;
            }

            internal static void Tick() { operation.Tick(); }

            private static void LegacyTick()
            {
                RecoverTerminalCleanup();
                Root root;
                try { root = Load(); }
                catch (Exception) { return; }
                if (root == null || !UnityCliSessionMutationLease.IsOwnedBy(root.RequestId, root.RequestDigest)) return;
                var cancelRequested = Bool(root.TestOperation, "cancelRequested");
                var cancelEffectState = Text(root.TestOperation, "cancelEffectState");
                if (cancelRequested && Same(cancelEffectState, "dispatching")) { TickCancel(root); return; }
                var acceptedCancel = cancelRequested && (Same(cancelEffectState, "dispatched") || Same(cancelEffectState, "outcome_unknown"));
                if (!string.Equals(root.EditorSessionId, EditorSessionId(), StringComparison.Ordinal))
                {
                    if (acceptedCancel)
                    {
                        if (!TryFreezeCancelOutcomeUnknown(root, "The Editor session changed after test cancellation was dispatched; cancellation outcome is unknown.")) return;
                        Fail(root, "E_EXECUTION_TIMEOUT", "Test cancellation outcome is unknown after the Editor session changed."); return;
                    }
                    Fail(root, "E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before tests began."); return;
                }
                if (DateTimeOffset.UtcNow >= root.AbsoluteDeadlineUtc)
                {
                    if (acceptedCancel)
                    {
                        TickCancel(root);
                        try { root = Load(); } catch { return; }
                        if (root == null) return;
                        if (!TryFreezeCancelOutcomeUnknown(root, "Test cancellation did not reach a proven terminal before the deadline; outcome is unknown.")) return;
                        Fail(root, "E_EXECUTION_TIMEOUT", "Test cancellation did not reach a proven terminal before the deadline."); return;
                    }
                    RecoverAfterDeadline(root); return;
                }
                if (cancelRequested) { TickCancel(root); return; }
                var phase = Text(root.TestOperation, "phase"); if (phase != "queued") { RecoverRunIdentity(root); return; }
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || IsPlayModeChanging()) return;
                string[] activeRunIds; string reason;
                if (!TryReadActiveRunIds(out activeRunIds, out reason)) { Fail(root, "E_TEST_RUN_STATE_UNPROVEN", "Unity Test Framework active-run state could not be verified: " + reason + "."); return; }
                if (activeRunIds.Length > 0) { Fail(root, "E_TEST_RUN_BUSY", "Another Unity Test Framework run is already active."); return; }
                var dispatchNonce = Guid.NewGuid().ToString("N"); Update(root, x => { x.TestOperation["phase"] = "execute_dispatching"; x.TestOperation["dispatchNonce"] = dispatchNonce; });
                try
                {
                    var runId = api.Execute(Settings(root.TestOperation));
                    if (!Guid.TryParse(runId, out _)) { Fail(root, "E_EXECUTION_TIMEOUT", "TestRunnerApi.Execute returned an invalid run identity."); return; }
                    root = Required(); Update(root, x => { x.TestOperation["returnedRunId"] = runId; x.TestOperation["phase"] = "run_identity_pending"; }); ResolveRunIdentity(Required());
                }
                catch (Exception ex) { Fail(root, "E_EXECUTION_TIMEOUT", "TestRunnerApi.Execute failed: " + ex.GetType().Name + "."); }
            }

            private sealed class Broker : ICallbacks
            {
                public void RunStarted(ITestAdaptor testsToRun)
                {
                    var root = Lease(); if (!CanObserveCallback(root, "execute_dispatching", "run_identity_pending", "running")) return;
                    string[] activeRunIds; string reason; var unique = TryReadActiveRunIds(out activeRunIds, out reason) && activeRunIds.Length == 1;
                    Update(root, x => { x.TestOperation["runStarted"] = true; if (unique) x.TestOperation["observedRunId"] = activeRunIds[0]; x.TestOperation["testTreeHash"] = TestTreeHash(testsToRun); x.TestOperation["testCaseCount"] = (long)Math.Max(0, testsToRun.TestCaseCount); });
                }
                public void TestStarted(ITestAdaptor test) { }
                public void TestFinished(ITestResultAdaptor result) { }
                public void RunFinished(ITestResultAdaptor result)
                {
                    var root = Lease(); if (!CanObserveCallback(root, "execute_dispatching", "run_identity_pending", "running") || !Bool(root.TestOperation, "runStarted")) return;
                    var observed = Text(root.TestOperation, "observedRunId"); if (string.IsNullOrEmpty(observed) || !Same(Text(root.TestOperation, "testTreeHash"), TestTreeHash(result.Test))) return;
                    if (Bool(root.TestOperation, "cancelRequested") && Same(Text(root.TestOperation, "cancelRunId"), observed)) { Update(root, value => value.TestOperation["cancelRunFinished"] = true); return; }
                    if (Text(root.TestOperation, "phase") == "running" && Same(Text(root.TestOperation, "runId"), observed)) Complete(root, result);
                    else Update(root, x => { x.TestOperation["pendingFinished"] = CaptureResultData(x, result); x.TestOperation["pendingFinishedRunId"] = observed; });
                }
            }

            /// <summary>plan与actual都只接受本coordinator拥有且native GUID唯一匹配的running test operation。</summary>
            private static object CancelPlan(BridgeRequest request)
            {
                var args = request.DeserializeArgs<CancelArgs>();
                var root = PrepareCancel(args, false);
                return CancelData(root, false, true, ActiveIdsRequired(root));
            }

            /// <summary>journal先冻结cancel identity，随后仅对该TestJobData.editModeRunner调用一次OnRunCancel。</summary>
            private static object Cancel(BridgeRequest request, string cancelDigest, CancellationToken cancellationToken)
            {
                var args = request.DeserializeArgs<CancelArgs>();
                var root = PrepareCancel(args, true);
                var runId = Text(root.TestOperation, "runId");
                var existingCancelId = Text(root.TestOperation, "cancelRequestId");
                if (!string.IsNullOrEmpty(existingCancelId))
                {
                    if (!Same(existingCancelId, request.RequestId) || !Same(Text(root.TestOperation, "cancelRequestDigest"), cancelDigest))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Test cancellation identity changed after acceptance.");
                    return CancelAcceptedData(root, runId);
                }
                var dispatch = new CancelDispatchIdentity { RootRequestId = root.RequestId, RootRequestDigest = root.RequestDigest, EditorSessionId = root.EditorSessionId, AbsoluteDeadlineUtc = root.AbsoluteDeadlineUtc, RunId = runId, CancelRequestId = request.RequestId, CancelRequestDigest = cancelDigest };
                var cancelAdmissionOwner = CaptureAdmissionOwner(request.RequestId);
                Update(root, value => { AddAdmissionOwnerFields(value.TestOperation, cancelAdmissionOwner); value.TestOperation["cancelRequested"] = true; value.TestOperation["cancelRequestId"] = dispatch.CancelRequestId; value.TestOperation["cancelRequestDigest"] = dispatch.CancelRequestDigest; value.TestOperation["cancelRootRequestDigest"] = dispatch.RootRequestDigest; value.TestOperation["cancelRunId"] = dispatch.RunId; value.TestOperation["cancelPhaseBefore"] = Text(value.TestOperation, "phase"); value.TestOperation["cancelEffectState"] = "dispatching"; });
                bool isOwned;
                try
                {
                    root = Required();
                    if (!MatchesCancelDispatch(root, dispatch) || cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= dispatch.AbsoluteDeadlineUtc
                        || !Same(dispatch.EditorSessionId, EditorSessionId()) || !UnityCliSessionMutationLease.TryIsOwnedBy(dispatch.RootRequestId, dispatch.RootRequestDigest, out isOwned) || !isOwned)
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test cancellation dispatch authorization is not exact.");
                }
                catch
                {
                    TryStoreCancelDispatchTimeout(dispatch, "Test cancellation dispatch authorization became uncertain before OnRunCancel; cancellation was not invoked.", "outcome_unknown");
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test cancellation authorization expired before its effect.", "Inspect test.run before submitting another cancellation.");
                }
                string reason;
                var outcome = TryCancelExactRun(dispatch, cancellationToken, out reason);
                if (outcome != CancelDispatchOutcome.Returned)
                {
                    var message = outcome == CancelDispatchOutcome.OutcomeUnknown
                        ? "Exact Unity Test Runner cancellation may have occurred but its return state is unknown."
                        : "Exact Unity Test Runner cancellation was not invoked: " + reason + ".";
                    TryStoreCancelDispatchTimeout(dispatch, message, outcome == CancelDispatchOutcome.OutcomeUnknown ? "outcome_unknown" : "not_invoked");
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", message, "Inspect test.run state before deciding whether to submit another cancellation.");
                }
                try
                {
                    root = Required();
                    if (!MatchesCancelDispatch(root, dispatch)) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test cancellation root changed after OnRunCancel.");
                    Update(root, value => value.TestOperation["cancelEffectState"] = "dispatched");
                }
                catch
                {
                    TryStoreCancelDispatchTimeout(dispatch, "Test cancellation occurred but its dispatched state could not be durably proven.", "outcome_unknown");
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test cancellation outcome is unknown after OnRunCancel.", "Inspect test.run before submitting another cancellation.");
                }
                return CancelAcceptedData(root, runId);
            }

            private static object CancelStatus(BridgeRequest request)
            {
                var args = request.DeserializeArgs<CancelStatusArgs>();
                if (args == null || !IsSafeId(args.CancelRequestId) || !IsSha256(args.RootRequestDigest)) throw new BridgeCommandException("E_SCHEMA_INVALID", "cancelRequestId and rootRequestDigest are required.");
                object durable;
                if (UnityCliActiveOperation.TryGetTerminalBySelector(CancelTerminalScope, args.CancelRequestId, args.RootRequestDigest, out durable))
                    return new Dictionary<string, object> { { "terminal", true }, { "result", CancelTerminalData(durable) } };
                var root = Load();
                if (root != null && Same(Text(root.TestOperation, "cancelRequestId"), args.CancelRequestId)
                    && Same(root.RequestDigest, args.RootRequestDigest))
                    return new Dictionary<string, object> { { "terminal", false }, { "accepted", true }, { "rootRequestId", root.RequestId }, { "runId", Text(root.TestOperation, "cancelRunId") } };
                throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "Test cancellation request was not found.");
            }

            private static Root PrepareCancel(CancelArgs args, bool allowAcceptedReplay)
            {
                if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId)) throw new BridgeCommandException("E_SCHEMA_INVALID", "expectedEditorSessionId is required.");
                var root = Load();
                if (root == null) throw new BridgeCommandException("E_TEST_RUN_NOT_ACTIVE", "No tracked test.run operation is active.", "Start test.run before calling test.cancel.");
                if (!Same(root.EditorSessionId, args.ExpectedEditorSessionId) || !Same(root.EditorSessionId, EditorSessionId())) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before test cancellation.");
                if (!UnityCliSessionMutationLease.IsOwnedBy(root.RequestId, root.RequestDigest)) throw new BridgeCommandException("E_TEST_CANCEL_NOT_OWNED", "The active test run is not owned by the tracked test.run operation.");
                if (Text(root.TestOperation, "phase") != "running" || Bool(root.TestOperation, "cancelRequested") && !allowAcceptedReplay) throw new BridgeCommandException("E_TEST_CANCEL_IDENTITY_UNPROVEN", "No uncancelled running test operation has a proven run GUID.", "Wait until test.run is running or inspect its terminal result.");
                ActiveIdsRequired(root);
                return root;
            }

            private static string[] ActiveIdsRequired(Root root)
            {
                string[] ids; string reason = null; var runId = Text(root.TestOperation, "runId");
                if (!Guid.TryParse(runId, out _) || !TryReadActiveRunIds(out ids, out reason) || ids.Length != 1 || !Same(ids[0], runId))
                    throw new BridgeCommandException("E_TEST_CANCEL_IDENTITY_UNPROVEN", "The active Unity Test Framework run cannot be uniquely matched to the tracked operation: " + (reason ?? "identity-mismatch") + ".");
                return ids;
            }

            private static void TickCancel(Root root)
            {
                var effectState = Text(root.TestOperation, "cancelEffectState");
                if (Same(effectState, "dispatching"))
                {
                    TryFreezeCancelOutcomeUnknown(root, "Test cancellation return state could not be durably confirmed; the cancellation effect may have occurred.");
                    return;
                }
                if (!Same(effectState, "dispatched")) return;
                string[] ids; string reason;
                if (!TryReadActiveRunIds(out ids, out reason)) return;
                if (!ids.Any(id => Same(id, Text(root.TestOperation, "cancelRunId")))) FinishCancelled(root);
            }

            /// <summary>校验dispatch后重读root仍与effect前冻结的完整身份和selector一致。</summary>
            private static bool MatchesCancelDispatch(Root root, CancelDispatchIdentity dispatch)
            {
                return root != null && Same(root.RequestId, dispatch.RootRequestId) && Same(root.RequestDigest, dispatch.RootRequestDigest)
                    && Same(root.EditorSessionId, dispatch.EditorSessionId) && root.AbsoluteDeadlineUtc == dispatch.AbsoluteDeadlineUtc
                    && Same(Text(root.TestOperation, "runId"), dispatch.RunId) && Same(Text(root.TestOperation, "cancelRunId"), dispatch.RunId)
                    && Same(Text(root.TestOperation, "cancelRequestId"), dispatch.CancelRequestId)
                    && Same(Text(root.TestOperation, "cancelRequestDigest"), dispatch.CancelRequestDigest)
                    && Same(Text(root.TestOperation, "cancelRootRequestDigest"), dispatch.RootRequestDigest)
                    && Same(Text(root.TestOperation, "cancelEffectState"), "dispatching");
            }

            /// <summary>I/O或身份不确定时直接以冻结selector发布first terminal；root状态只能best-effort推进，不能阻塞terminal。</summary>
            private static bool TryStoreCancelDispatchTimeout(CancelDispatchIdentity dispatch, string message, string effectState)
            {
                try
                {
                    var terminal = ProtocolEnvelopeFactory.Error(dispatch.CancelRequestId, "E_EXECUTION_TIMEOUT", message, null, new[] { "Inspect the exact test.run business state before starting another run or cancellation." });
                    UnityCliActiveOperation.StoreTerminalFirst(CancelTerminalScope, dispatch.CancelRequestId, dispatch.CancelRequestDigest, dispatch.RootRequestDigest, terminal);
                    try
                    {
                        var current = Load();
                        if (MatchesCancelDispatch(current, dispatch))
                        {
                            current.TestOperation["cancelRequested"] = Same(effectState, "outcome_unknown");
                            current.TestOperation["cancelEffectState"] = effectState;
                            Save(current);
                        }
                    }
                    catch { }
                    return true;
                }
                catch { return false; }
            }

            /// <summary>effect后的取消不确定性先按三元identity写first terminal；写失败保留root供下一Tick重试。</summary>
            private static bool TryFreezeCancelOutcomeUnknown(Root root, string message)
            {
                var cancelRequestId = Text(root.TestOperation, "cancelRequestId");
                var cancelDigest = Text(root.TestOperation, "cancelRequestDigest");
                if (!IsSafeId(cancelRequestId) || !IsSha256(cancelDigest) || !Same(Text(root.TestOperation, "cancelRootRequestDigest"), root.RequestDigest))
                    return false;
                try
                {
                    var terminal = ProtocolEnvelopeFactory.Error(cancelRequestId, "E_EXECUTION_TIMEOUT", message, null, new[] { "Inspect the exact test.run business state before starting another run or cancellation." });
                    UnityCliActiveOperation.StoreTerminalFirst(CancelTerminalScope, cancelRequestId, cancelDigest, root.RequestDigest, terminal);
                    Update(root, value => value.TestOperation["cancelEffectState"] = "outcome_unknown");
                    return true;
                }
                catch { return false; }
            }

            private static void FinishCancelled(Root root)
            {
                string[] ids; string reason;
                if (!TryReadActiveRunIds(out ids, out reason)) ids = new string[0];
                var data = CancelData(root, true, false, ids);
                cancelTerminalRequestId = Text(root.TestOperation, "cancelRequestId");
                var cancelDigest = Text(root.TestOperation, "cancelRequestDigest");
                if (!IsSafeId(cancelTerminalRequestId) || !IsSha256(cancelDigest)
                    || !Same(Text(root.TestOperation, "cancelRootRequestDigest"), root.RequestDigest))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test cancellation terminal identity is incomplete; outcome is unknown.");
                var cancelEnvelope = ProtocolEnvelopeFactory.Success(cancelTerminalRequestId, data);
                UnityCliActiveOperation.StoreTerminalFirst(CancelTerminalScope, cancelTerminalRequestId, cancelDigest, root.RequestDigest, cancelEnvelope);
                CompleteAdmissionRoot(ReadAdmissionOwnerFields(root.TestOperation, cancelTerminalRequestId));
                cancelTerminalData = data;
                Finish(root, ProtocolEnvelopeFactory.Error(root.RequestId, "E_TEST_RUN_CANCELLED", "The exact tracked Unity test run was cancelled.", data, new[] { "Inspect the test.cancel proof before starting another test run." }));
            }

            private static Dictionary<string, object> CancelAcceptedData(Root root, string runId)
            {
                return new Dictionary<string, object> { { "terminal", false }, { "accepted", true }, { "rootRequestId", root.RequestId }, { "rootRequestDigest", root.RequestDigest }, { "runId", runId } };
            }

            private static string CancelTerminalStartResponse(string requestId, object envelope)
            {
                var parsed = UnityCliSerializer.Parse(UnityCliSerializer.Serialize(envelope)) as Dictionary<string, object>;
                object ok;
                if (parsed == null || !parsed.TryGetValue("ok", out ok) || !(ok is bool))
                    return SerializeBridgeExceptionEnvelope(requestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test cancellation terminal cache is invalid; outcome is unknown."));
                if (!(bool)ok) return UnityCliSerializer.Serialize(parsed);
                return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(requestId, new Dictionary<string, object> { { "terminal", true }, { "result", CancelTerminalData(parsed) } }));
            }

            private static object CancelTerminalData(object envelope)
            {
                var parsed = UnityCliSerializer.Parse(UnityCliSerializer.Serialize(envelope)) as Dictionary<string, object>;
                object ok; object data;
                if (parsed == null || !parsed.TryGetValue("ok", out ok) || !(ok is bool) || !(bool)ok
                    || !parsed.TryGetValue("data", out data) || !(data is Dictionary<string, object>))
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test cancellation terminal cache is invalid; outcome is unknown.");
                return data;
            }

            private static Dictionary<string, object> CancelData(Root root, bool cancelled, bool dryRun, string[] activeIds)
            {
                return new Dictionary<string, object> { { "cancelled", cancelled }, { "dryRun", dryRun }, { "rootRequestId", root.RequestId }, { "runId", Text(root.TestOperation, "runId") }, { "platform", Text(root.TestOperation, "platform") }, { "activeRunIds", activeIds }, { "editorSessionId", root.EditorSessionId }, { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", "test-run-cancel" }, { "rootRequestId", root.RequestId }, { "runId", Text(root.TestOperation, "runId") } } } } };
            }

            private static CancelDispatchOutcome TryCancelExactRun(CancelDispatchIdentity dispatch, CancellationToken cancellationToken, out string reason)
            {
                reason = null;
                var invoked = false;
                try
                {
                    var holderType = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder", false);
                    var property = holderType == null ? null : holderType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    var holder = property == null ? null : property.GetValue(null, null);
                    var field = holderType == null ? null : holderType.GetField("TestRuns", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var runs = holder == null || field == null ? null : field.GetValue(holder) as IEnumerable;
                    if (runs == null) { reason = "active-runs-missing"; return CancelDispatchOutcome.NotInvoked; }
                    object match = null;
                    foreach (var run in runs)
                    {
                        var type = run.GetType();
                        var guid = type.GetField("guid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(run) as string;
                        var running = type.GetField("isRunning", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(run);
                        if (Same(guid, dispatch.RunId) && running is bool && (bool)running) { if (match != null) { reason = "duplicate-run-id"; return CancelDispatchOutcome.NotInvoked; } match = run; }
                    }
                    if (match == null) { reason = "exact-run-missing"; return CancelDispatchOutcome.NotInvoked; }
                    var runnerField = match.GetType().GetField("editModeRunner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var runner = runnerField == null ? null : runnerField.GetValue(match);
                    var cancel = runner == null ? null : runner.GetType().GetMethod("OnRunCancel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (cancel == null) { reason = "cancel-owner-missing"; return CancelDispatchOutcome.NotInvoked; }
                    bool isOwned;
                    Root current;
                    try { current = Required(); }
                    catch { reason = "dispatch-root-unreadable"; return CancelDispatchOutcome.NotInvoked; }
                    if (!MatchesCancelDispatch(current, dispatch) || cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= dispatch.AbsoluteDeadlineUtc
                        || !Same(dispatch.EditorSessionId, EditorSessionId()) || !UnityCliSessionMutationLease.TryIsOwnedBy(dispatch.RootRequestId, dispatch.RootRequestDigest, out isOwned) || !isOwned)
                    { reason = "dispatch-authorization-expired"; return CancelDispatchOutcome.NotInvoked; }
                    invoked = true;
                    cancel.Invoke(runner, null);
                    return CancelDispatchOutcome.Returned;
                }
                catch (TargetInvocationException exception) { reason = exception.InnerException == null ? exception.GetType().Name : exception.InnerException.GetType().Name; return invoked ? CancelDispatchOutcome.OutcomeUnknown : CancelDispatchOutcome.NotInvoked; }
                catch (Exception exception) { reason = exception.GetType().Name; return invoked ? CancelDispatchOutcome.OutcomeUnknown : CancelDispatchOutcome.NotInvoked; }
            }

            private static Root Lease() { var root = Load(); return root != null && UnityCliSessionMutationLease.IsOwnedBy(root.RequestId, root.RequestDigest) ? root : null; }
            private static void RecoverAfterDeadline(Root root) { object pending; if (root.TestOperation.TryGetValue("pendingFinished", out pending) && pending is Dictionary<string, object>) { ResolveRunIdentity(root); return; } string[] ids; string reason; var runId = Text(root.TestOperation, "runId") ?? Text(root.TestOperation, "returnedRunId") ?? Text(root.TestOperation, "observedRunId"); if (TryReadActiveRunIds(out ids, out reason) && ids.Length == 1 && Same(ids[0], runId)) return; if (DateTimeOffset.UtcNow >= root.AbsoluteDeadlineUtc + RecoveryGrace) Fail(root, "E_EXECUTION_TIMEOUT", "TestRunner callback terminal is unavailable after recovery grace."); }
            private static void RecoverRunIdentity(Root root) { var phase = Text(root.TestOperation, "phase"); if (phase == "execute_dispatching" || phase == "run_identity_pending") ResolveRunIdentity(root); }
            private static void ResolveRunIdentity(Root root)
            {
                root = Required(); var returned = Text(root.TestOperation, "returnedRunId"); var observed = Text(root.TestOperation, "observedRunId"); if (!string.IsNullOrEmpty(returned) && !string.IsNullOrEmpty(observed) && !Same(returned, observed)) { Fail(root, "E_EXECUTION_TIMEOUT", "TestRunnerApi returned GUID does not match RunStarted attribution."); return; }
                var candidate = returned ?? observed; object pending; if (root.TestOperation.TryGetValue("pendingFinished", out pending) && pending is Dictionary<string, object>) { if (string.IsNullOrEmpty(candidate) || !Same(Text(root.TestOperation, "pendingFinishedRunId"), candidate)) { Fail(root, "E_EXECUTION_TIMEOUT", "Synchronous RunFinished result has no matching durable run identity."); return; } Update(root, x => { x.TestOperation["runId"] = candidate; x.TestOperation["phase"] = "running"; }); CompletePending(Required(), (Dictionary<string, object>)pending); return; }
                string[] ids; string reason; if (!TryReadActiveRunIds(out ids, out reason) || ids.Length != 1 || string.IsNullOrEmpty(candidate) || !Same(ids[0], candidate)) { Fail(root, "E_EXECUTION_TIMEOUT", "TestRunnerApi active run identity could not be matched after Execute: " + (reason ?? "identity-mismatch") + "."); return; }
                Update(root, x => { x.TestOperation["runId"] = candidate; x.TestOperation["observedRunId"] = candidate; x.TestOperation["phase"] = "running"; });
            }
            private static void CompletePending(Root root, Dictionary<string, object> data) { var summary = data["summary"] as Dictionary<string, object>; if (summary == null) { Fail(root, "E_EXECUTION_TIMEOUT", "Pending RunFinished summary is corrupt."); return; } object artifactFailed; if (data.TryGetValue("artifactWriteFailed", out artifactFailed) && artifactFailed is bool && (bool)artifactFailed) { data.Remove("artifactWriteFailed"); Finish(root, ProtocolEnvelopeFactory.Error(root.RequestId, "E_TEST_ARTIFACT_WRITE_FAILED", "RunFinished was proven but its NUnit XML artifact could not be written.", data, new[] { "Inspect the returned test summary and verify the UnityCli data directory is writable." })); return; } var failed = Convert.ToInt32(summary["failed"], CultureInfo.InvariantCulture); var count = Convert.ToInt32(summary["testCount"], CultureInfo.InvariantCulture); object envelope = count == 0 ? (object)ProtocolEnvelopeFactory.Error(root.RequestId, "E_TESTS_NOT_FOUND", "The requested filters matched no Unity tests.", data, new[] { "Inspect the available test names and relax the test filters." }) : failed > 0 ? (object)ProtocolEnvelopeFactory.Error(root.RequestId, "E_TESTS_FAILED", "One or more Unity tests failed.", data, new[] { "Inspect failures and the optional NUnit XML artifact." }) : ProtocolEnvelopeFactory.Success(root.RequestId, data); Finish(root, envelope); }
            private static void Complete(Root root, ITestResultAdaptor result) { Finish(root, ResultEnvelope(root, result)); }
            private static object ResultEnvelope(Root root, ITestResultAdaptor result) { var data = CaptureResultData(root, result); object artifactFailed; if (data.TryGetValue("artifactWriteFailed", out artifactFailed) && artifactFailed is bool && (bool)artifactFailed) { data.Remove("artifactWriteFailed"); return ProtocolEnvelopeFactory.Error(root.RequestId, "E_TEST_ARTIFACT_WRITE_FAILED", "RunFinished was proven but its NUnit XML artifact could not be written.", data, new[] { "Inspect the returned test summary and verify the UnityCli data directory is writable." }); } var failed = result.FailCount; var count = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount; return count == 0 ? (object)ProtocolEnvelopeFactory.Error(root.RequestId, "E_TESTS_NOT_FOUND", "The requested filters matched no Unity tests.", data, new[] { "Inspect the available test names and relax the test filters." }) : failed > 0 ? (object)ProtocolEnvelopeFactory.Error(root.RequestId, "E_TESTS_FAILED", "One or more Unity tests failed.", data, new[] { "Inspect failures and the optional NUnit XML artifact." }) : ProtocolEnvelopeFactory.Success(root.RequestId, data); }
            private static Dictionary<string, object> CaptureResultData(Root root, ITestResultAdaptor result) { var summary = new Dictionary<string, object> { { "platform", Text(root.TestOperation, "platform") }, { "status", result.FailCount == 0 ? "passed" : "failed" }, { "testCount", (long)(result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount) }, { "passed", (long)result.PassCount }, { "failed", (long)result.FailCount }, { "skipped", (long)result.SkipCount }, { "inconclusive", (long)result.InconclusiveCount }, { "duration", result.Duration }, { "startedAt", result.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) }, { "finishedAt", result.EndTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) } }; var data = new Dictionary<string, object> { { "summary", summary }, { "failures", Failures(result).Cast<object>().ToList() } }; if (Bool(root.TestOperation, "xml")) { try { data["xml"] = WriteXml(root, result); } catch { data["artifactWriteFailed"] = true; } } return data; }
            private static Root Required() { var root = Load(); if (root == null) throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "Test active operation was not found."); return root; }
            private static Root Load()
            {
                try { var state = UnityCliActiveOperation.Load(Scope); return state == null ? null : Parse(state); }
                catch (BridgeCommandException) { throw; }
                catch (Exception exception) { throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test active state cannot be read; outcome is unknown: " + exception.GetType().Name + "."); }
            }
            private static void Save(Root root) { UnityCliActiveOperation.Save(Scope, Data(root)); }
            private static void Update(Root root, Action<Root> change)
            {
                var current = Required();
                if (!Same(current.RequestId, root.RequestId) || !Same(current.RequestDigest, root.RequestDigest)
                    || !Same(current.EditorSessionId, root.EditorSessionId) || current.AbsoluteDeadlineUtc != root.AbsoluteDeadlineUtc)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Test active root changed before durable update; outcome is unknown.");
                change(current);
                Save(current);
            }
            private static void Finish(Root root, object envelope)
            {
                bool isOwned;
                if (!UnityCliSessionMutationLease.TryIsOwnedBy(root.RequestId, root.RequestDigest, out isOwned) || !isOwned)
                    envelope = ProtocolEnvelopeFactory.Error(root.RequestId, "E_EXECUTION_TIMEOUT", "Test run lost its exact mutation lease after effect; outcome is unknown.", null, new[] { "Inspect the Unity Test Runner before submitting another mutation." });
                root.TestOperation["phase"] = "terminal_pending";
                root.TestOperation["terminalEnvelope"] = envelope;
                Save(root);
                var first = UnityCliActiveOperation.StoreTerminalFirst(Scope, root.RequestId, root.RequestDigest, envelope);
                terminalRequestId = root.RequestId; terminalRequestDigest = root.RequestDigest; terminalEnvelope = first;
                UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, root.RequestId, root.RequestDigest);
            }
            private static void RecoverTerminalCleanup()
            {
                try
                {
                    if (UnityCliActiveOperation.RetryExactTerminalCleanup(Scope)) return;
                    var root = Load();
                    if (root == null) return;
                    object terminal;
                    if (!UnityCliActiveOperation.TryGetTerminal(Scope, root.RequestId, root.RequestDigest, out terminal))
                    {
                        object pending;
                        if (root.TestOperation.TryGetValue("terminalEnvelope", out pending) && pending != null)
                            terminal = UnityCliActiveOperation.StoreTerminalFirst(Scope, root.RequestId, root.RequestDigest, pending);
                    }
                    if (terminal != null) UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, root.RequestId, root.RequestDigest);
                }
                catch { }
            }
            private static void Fail(Root root, string code, string message) { Finish(root, ProtocolEnvelopeFactory.Error(root.RequestId, code, message, null, new[] { "Inspect editor.state and retry." })); }
            private static object Status(BridgeRequest request) { var input = request.DeserializeArgs<StatusArgs>(); if (input == null || !IsSafeId(input.RootRequestId) || !IsSha256(input.RootRequestDigest)) throw new BridgeCommandException("E_SCHEMA_INVALID", "rootRequestId and rootRequestDigest are required."); object durable; if (UnityCliActiveOperation.TryGetTerminal(Scope, input.RootRequestId, input.RootRequestDigest, out durable)) return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", durable } }); var root = Required(); if (!Same(root.RequestId, input.RootRequestId)) throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "Test request was not found."); if (!Same(root.RequestDigest, input.RootRequestDigest)) throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Test requestId was reused with different input."); return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", false }, { "accepted", true }, { "rootRequestDigest", root.RequestDigest } }); }
            private static Root NewRoot(BridgeRequest request, string digest, Args args) { DateTimeOffset deadline; if (!DateTimeOffset.TryParse(request.Execution.AbsoluteDeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Request deadline is invalid."); return new Root { RequestId = request.RequestId, RequestDigest = digest, EditorSessionId = EditorSessionId(), AbsoluteDeadlineUtc = deadline, TestOperation = new Dictionary<string, object> { { "phase", "queued" }, { "platform", args.Platform ?? "EditMode" }, { "xml", args.Xml }, { "testNames", args.TestNames ?? new string[0] }, { "groupNames", args.GroupNames ?? new string[0] }, { "categoryNames", args.CategoryNames ?? new string[0] }, { "assemblyNames", args.AssemblyNames ?? new string[0] }, { "lease", digest }, { "filterFingerprint", FilterFingerprint(args) } } }; }
            private static Root Parse(Dictionary<string, object> state) { object id; object digest; object session; object deadline; object operation; if (!state.TryGetValue("requestId", out id) || !state.TryGetValue("requestDigest", out digest) || !state.TryGetValue("editorSessionId", out session) || !state.TryGetValue("absoluteDeadlineUtc", out deadline) || !state.TryGetValue("testOperation", out operation) || !(operation is Dictionary<string, object>)) throw new InvalidDataException("Test active state is incomplete."); DateTimeOffset parsed; if (!DateTimeOffset.TryParse(deadline as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)) throw new InvalidDataException("Test active deadline is invalid."); return new Root { RequestId = id as string, RequestDigest = digest as string, EditorSessionId = session as string, AbsoluteDeadlineUtc = parsed, TestOperation = (Dictionary<string, object>)operation }; }
            private static Dictionary<string, object> Data(Root root) { return new Dictionary<string, object> { { "requestId", root.RequestId }, { "requestDigest", root.RequestDigest }, { "editorSessionId", root.EditorSessionId }, { "absoluteDeadlineUtc", root.AbsoluteDeadlineUtc.ToString("O", CultureInfo.InvariantCulture) }, { "testOperation", root.TestOperation } }; }
            private static bool CanObserveCallback(Root root, params string[] phases) { return root != null && root.TestOperation != null && phases.Contains(Text(root.TestOperation, "phase")); }
            private static string BusyStart(string id) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(id, "E_UNITY_BUSY", "Another mutation owns the selected Unity instance.", null, new[] { "Wait for the current mutation to reach its terminal result, then retry." }, true, 250)); }
            private static string Accepted(string id, string digest) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(id, new Dictionary<string, object> { { "terminal", false }, { "accepted", true }, { "rootRequestDigest", digest } })); }
            private static string Terminal(string id, object envelope) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(id, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", envelope } })); }
            private static string TestTreeHash(ITestAdaptor root) { var names = new List<string>(); CollectTestNames(root, names); names.Sort(StringComparer.Ordinal); return Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", names))); }
            private static void CollectTestNames(ITestAdaptor node, List<string> names) { if (node == null) return; if (node.HasChildren && node.Children != null) { foreach (var child in node.Children) CollectTestNames(child, names); return; } names.Add(node.UniqueName ?? node.FullName ?? node.Name ?? string.Empty); }
            private static bool TryReadActiveRunIds(out string[] runIds, out string reason) { runIds = new string[0]; reason = null; try { var holderType = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder", false); var property = holderType == null ? null : holderType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy); var holder = property == null ? null : property.GetValue(null, null); var field = holderType == null ? null : holderType.GetField("TestRuns", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); var runs = holder == null || field == null ? null : field.GetValue(holder) as IEnumerable; if (runs == null) { reason = "active-runs-missing"; return false; } var ids = new List<string>(); foreach (var run in runs) { var type = run.GetType(); var running = type.GetField("isRunning", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); var guid = type.GetField("guid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (running == null || guid == null) { reason = "run-fields-missing"; return false; } if (running.GetValue(run) is bool && (bool)running.GetValue(run)) ids.Add(guid.GetValue(run) as string); } runIds = ids.Where(x => Guid.TryParse(x, out _)).Distinct(StringComparer.Ordinal).ToArray(); return true; } catch (Exception ex) { reason = ex.GetType().Name; return false; } }
            private static IEnumerable<Dictionary<string, object>> Failures(ITestResultAdaptor node)
            {
                var failures = new List<Dictionary<string, object>>();
                CollectFailures(node, failures);
                return failures;
            }
            private static void CollectFailures(ITestResultAdaptor node, List<Dictionary<string, object>> failures)
            {
                if (node == null || failures.Count >= 100) return;
                if (node.HasChildren && node.Children != null) { foreach (var child in node.Children) CollectFailures(child, failures); return; }
                if (node.TestStatus != TestStatus.Failed) return;
                failures.Add(new Dictionary<string, object> { { "fullName", BoundedText(node.FullName, 1024) }, { "message", BoundedText(node.Message, 4096) }, { "stackTrace", BoundedText(node.StackTrace, 8192) }, { "duration", node.Duration } });
            }
            private static string BoundedText(string value, int limit) { value = value ?? string.Empty; return value.Length <= limit ? value : value.Substring(0, limit); }
            private static Dictionary<string, object> WriteXml(Root root, ITestResultAdaptor result) { var path = Path.Combine(DataDir(), "test-results", InstanceId(), root.RequestId + ".xml"); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, result.ToXml().OuterXml, new UTF8Encoding(false)); var bytes = File.ReadAllBytes(path); using (var sha = SHA256.Create()) return new Dictionary<string, object> { { "path", path }, { "kind", "nunit-xml" }, { "hash", Hex(sha.ComputeHash(bytes)) }, { "size", (long)bytes.Length } }; }
            private static ExecutionSettings Settings(Dictionary<string, object> op) { return new ExecutionSettings(new Filter { testMode = Text(op, "platform") == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode, testNames = Strings(op, "testNames"), groupNames = Strings(op, "groupNames"), categoryNames = Strings(op, "categoryNames"), assemblyNames = Strings(op, "assemblyNames") }); }
            private static string Text(Dictionary<string, object> x, string key) { object value; return x != null && x.TryGetValue(key, out value) ? value as string : null; } private static bool Bool(Dictionary<string, object> x, string key) { object value; return x != null && x.TryGetValue(key, out value) && value is bool && (bool)value; } private static bool Same(string left, string right) { return !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal); } private static string[] Strings(Dictionary<string, object> x, string key) { object value; if (x == null || !x.TryGetValue(key, out value)) return new string[0]; var items = value as IEnumerable; return items == null ? new string[0] : items.Cast<object>().OfType<string>().ToArray(); }
            private static string FilterFingerprint(Args a) { return Sha256Hex(Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(new Dictionary<string, object> { { "platform", a.Platform ?? "EditMode" }, { "xml", a.Xml }, { "testNames", a.TestNames ?? new string[0] }, { "groupNames", a.GroupNames ?? new string[0] }, { "categoryNames", a.CategoryNames ?? new string[0] }, { "assemblyNames", a.AssemblyNames ?? new string[0] } }))); }
            private static void Validate(Args a) { if (a == null || string.IsNullOrWhiteSpace(a.ExpectedEditorSessionId) || (a.Platform != null && a.Platform != "EditMode" && a.Platform != "PlayMode")) throw new BridgeCommandException("E_SCHEMA_INVALID", "platform and expectedEditorSessionId are invalid."); if (!string.Equals(a.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before tests began."); }
            private sealed class Args { public string Platform { get; set; } public string[] TestNames { get; set; } public string[] GroupNames { get; set; } public string[] CategoryNames { get; set; } public string[] AssemblyNames { get; set; } public bool Xml { get; set; } public string ExpectedEditorSessionId { get; set; } }
            private sealed class StatusArgs { public string RootRequestId { get; set; } public string RootRequestDigest { get; set; } }
            private sealed class CancelArgs { public string ExpectedEditorSessionId { get; set; } }
            private sealed class CancelStatusArgs { public string CancelRequestId { get; set; } public string RootRequestDigest { get; set; } }
        }
    }
}
#endif
