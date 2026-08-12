#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>Occlusion bake独占的session-scoped durable root，不与其他bake共享业务状态。</summary>
        internal static class UnityCliOcclusionBakeCoordinator
        {
            internal const string StartCommand = "internal.occlusion.bake.start";
            internal const string StatusCommand = "internal.occlusion.bake.status";
            internal const string PlanCommand = "internal.occlusion.bake.plan";
            internal const string SnapshotCommand = "internal.occlusion.bake.snapshot";
            internal const string CancelCommand = "internal.occlusion.bake.cancel";
            private const string ActiveScope = "occlusion-bake";
            private const string StatusScope = "occlusion-bake-status";
            private const string StatusSessionInitializedKey = "UnityCli.OcclusionBake.StatusSessionInitialized";
            private const string StatusWrittenThisSessionKey = "UnityCli.OcclusionBake.StatusWrittenThisSession";
            private const string StatusProofKeySessionKey = "UnityCli.OcclusionBake.StatusProofKey";
            private const string OwnerRequestIdSessionKey = "UnityCli.OcclusionBake.OwnerRequestId";
            private const string OwnerRequestDigestSessionKey = "UnityCli.OcclusionBake.OwnerRequestDigest";
            private const string PreEffectAbortRequestIdSessionKey = "UnityCli.OcclusionBake.PreEffectAbortRequestId";
            private const string PreEffectAbortRequestDigestSessionKey = "UnityCli.OcclusionBake.PreEffectAbortRequestDigest";
            private static readonly OcclusionCoordinator Operation = new OcclusionCoordinator();
            private enum StatusLoadOutcome { Missing, Loaded, TransientFailure, Corrupt }
            private static bool StatusJournalCorrupt;
            private static StatusLoadOutcome LastStatusLoadOutcome;

            private sealed class StartArgs
            {
                public string ExpectedEditorSessionId { get; set; }
                public double? SmallestOccluder { get; set; }
                public double? SmallestHole { get; set; }
                public double? BackfaceThreshold { get; set; }
            }

            private sealed class CancelArgs { public string OperationId { get; set; } }
            private sealed class RootStatusArgs { public string RootRequestId { get; set; } public string RootRequestDigest { get; set; } }

            private sealed class OcclusionCoordinator : UnityCliReloadOperation
            {
                private Dictionary<string, object> rejectedRoot;

                protected override string Scope { get { return ActiveScope; } }
                protected override string StartCommand { get { return UnityCliOcclusionBakeCoordinator.StartCommand; } }
                protected override string StatusCommand { get { return UnityCliOcclusionBakeCoordinator.StatusCommand; } }
                protected override string OperationDisplayName { get { return "Occlusion bake"; } }
                protected override int CommandTimeoutMs { get { return 600000; } }
                protected override bool IncludeAcceptedInStatus { get { return true; } }
                protected override IEnumerable<(string name, bool destructive)> AdditionalCommands
                {
                    get
                    {
                        yield return (PlanCommand, false);
                        yield return (SnapshotCommand, false);
                        yield return (CancelCommand, true);
                    }
                }

                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<StartArgs>(); }

                protected override void ValidateStart(object value)
                {
                    var args = value as StartArgs;
                    if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId))
                        throw new BridgeCommandException("E_SCHEMA_INVALID", "Occlusion bake requires expectedEditorSessionId.");
                    if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal))
                        throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed after Occlusion bake preflight.");
                    OcclusionToolSupport.EnsureEditorReady("occlusion.bake", true);
                }

                /// <summary>active root必须满足完整阶段与effect证据约束，语义损坏不得回退到可再次dispatch的phase。</summary>
                protected override void ValidateLoadedRoot(Dictionary<string, object> root)
                {
                    if (!ValidateOcclusionRoot(root))
                    {
                        rejectedRoot = root;
                        throw new InvalidOperationException("Occlusion active root semantic validation failed.");
                    }
                    rejectedRoot = null;
                }

                protected override TerminalCleanupDecision EvaluateTerminalCleanupRoot(Dictionary<string, object> root, string requestId, string requestDigest)
                {
                    if (StaticOcclusionCulling.isRunning) return TerminalCleanupDecision.Retry;
                    var hasTerminalState = !string.IsNullOrEmpty(Text(root, "terminalState"));
                    if (hasTerminalState)
                    {
                        try { if (TryRestoreTerminalStatus(root)) return TerminalCleanupDecision.Cleanup; }
                        catch (Exception exception) { WriteLog("occlusion_terminal_restore_retry " + exception.Message); }
                    }
                    var status = LoadStatus();
                    if (status == null) return hasTerminalState || LastStatusLoadOutcome == StatusLoadOutcome.TransientFailure
                        ? TerminalCleanupDecision.Retry : TerminalCleanupDecision.Reject;
                    return TerminalStatusMatchesIdentity(status, requestId, requestDigest)
                        ? TerminalCleanupDecision.Cleanup : TerminalCleanupDecision.Reject;
                }

                protected override TerminalCleanupDecision EvaluateUnreadableTerminalCleanup(string requestId, string requestDigest)
                {
                    if (StaticOcclusionCulling.isRunning) return TerminalCleanupDecision.Retry;
                    var status = LoadStatus();
                    if (status == null) return TerminalCleanupDecision.Retry;
                    return TerminalStatusMatchesIdentity(status, requestId, requestDigest)
                        ? TerminalCleanupDecision.Cleanup : TerminalCleanupDecision.Retry;
                }

                protected override bool CanCleanupAbandonedPreEffectRoot(Dictionary<string, object> root, string requestId, string requestDigest)
                {
                    if (root == null || !IsBoundPreEffectAbort(requestId, requestDigest)
                        || !string.Equals(Text(root, "requestId"), requestId, StringComparison.Ordinal)
                        || !string.Equals(Text(root, "requestDigest"), requestDigest, StringComparison.Ordinal)
                        || Bool(root, "dispatchConfirmed") || !string.IsNullOrEmpty(Text(root, "terminalState"))) return false;
                    var phase = Text(root, "phase");
                    var dispatchNonce = Text(root, "dispatchNonce");
                    return (phase == "queued" && string.IsNullOrEmpty(dispatchNonce))
                        || (phase == "dispatching" && !string.IsNullOrEmpty(dispatchNonce));
                }

                /// <summary>effect前root删除后同步清除同identity未dispatch status，避免accepted状态永久悬挂。</summary>
                protected override bool FinalizeAbandonedPreEffectState(string requestId, string requestDigest)
                {
                    var status = LoadStatus();
                    if (status == null)
                    {
                        return LastStatusLoadOutcome == StatusLoadOutcome.Missing
                            && !StatusJournalCorrupt
                            && !SessionState.GetBool(StatusWrittenThisSessionKey, false)
                            && !UnityCliActiveOperation.Exists(StatusScope);
                    }
                    else
                    {
                        object dispatchConfirmed;
                        if (!string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal)
                            || !string.Equals(Text(status, "rootRequestDigest"), requestDigest, StringComparison.Ordinal)
                            || !Bool(status, "active")
                            || !status.TryGetValue("dispatchConfirmed", out dispatchConfirmed) || !(dispatchConfirmed is bool)
                            || (bool)dispatchConfirmed) return false;
                    }
                    try { UnityCliActiveOperation.Clear(StatusScope); }
                    catch (Exception exception) { WriteLog("occlusion_pre_effect_status_cleanup_failed " + exception.Message); return false; }
                    if (UnityCliActiveOperation.Exists(StatusScope)) return false;
                    StatusJournalCorrupt = false;
                    LastStatusLoadOutcome = StatusLoadOutcome.Missing;
                    SessionState.SetBool(StatusWrittenThisSessionKey, false);
                    return true;
                }

                protected override bool TryResumePublishedTerminal(Dictionary<string, object> root)
                {
                    if (!string.IsNullOrEmpty(Text(root, "terminalState")))
                    {
                        if (TryRestoreTerminalStatus(root)) ResumeTerminalCleanup(root);
                        return true;
                    }
                    var terminal = RecoveredStoredTerminal(Text(root, "requestId"), Text(root, "requestDigest"));
                    if (terminal == null) return false;
                    ResumeTerminalCleanup(root);
                    return true;
                }

                private static bool TerminalStatusMatchesIdentity(IDictionary<string, object> status, string requestId, string requestDigest)
                {
                    return status != null && !Bool(status, "active") && status.ContainsKey("rootEnvelope")
                        && string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal)
                        && string.Equals(Text(status, "rootRequestDigest"), requestDigest, StringComparison.Ordinal);
                }

                private static bool TerminalStatusMatchesRoot(Dictionary<string, object> root, IDictionary<string, object> status, string requestId, string requestDigest)
                {
                    if (root == null || !TerminalStatusMatchesIdentity(status, requestId, requestDigest)
                        || !string.Equals(Text(root, "requestId"), requestId, StringComparison.Ordinal)
                        || !string.Equals(Text(root, "requestDigest"), requestDigest, StringComparison.Ordinal)
                        || !string.Equals(Text(root, "operationId"), Text(status, "operationId"), StringComparison.Ordinal)
                        || !string.Equals(Text(root, "startedAt"), Text(status, "startedAt"), StringComparison.Ordinal)
                        || Long(root, "targetCount") != Long(status, "targetCount")) return false;
                    var state = Text(status, "state");
                    var phase = Text(root, "phase");
                    var terminalState = Text(root, "terminalState");
                    if (state == "completed")
                    {
                        object envelopeValue;
                        var envelope = status.TryGetValue("rootEnvelope", out envelopeValue) ? envelopeValue as IDictionary<string, object> : null;
                        object dataValue;
                        var data = envelope != null && envelope.TryGetValue("data", out dataValue) ? dataValue as IDictionary<string, object> : null;
                        return phase == "verifying" && Bool(root, "dispatchConfirmed") && terminalState == "completed" && data != null
                            && Text(data, "status") == "completed"
                            && Text(data, "operationId") == Text(root, "operationId")
                            && Text(data, "startedAt") == Text(root, "startedAt")
                            && Text(data, "finishedAt") == Text(root, "finishedAt")
                            && Text(status, "finishedAt") == Text(root, "finishedAt")
                            && TerminalDataMatchesRoot(root, status, data);
                    }
                    if (state == "cancelled")
                        return terminalState == "cancelled" && phase == "queued"
                            && Bool(root, "cancelRequested") && Text(root, "cancelEffectState") == "not-required";
                    return state == "failed" && (string.IsNullOrEmpty(terminalState) || terminalState == "failed");
                }

                private static bool TerminalDataMatchesRoot(Dictionary<string, object> root, IDictionary<string, object> status, IDictionary<string, object> data)
                {
                    object beforeValue;
                    object afterValue;
                    object settingsValue;
                    object scenesValue;
                    var before = data.TryGetValue("before", out beforeValue) ? beforeValue as IDictionary<string, object> : null;
                    var after = data.TryGetValue("after", out afterValue) ? afterValue as IDictionary<string, object> : null;
                    var settings = data.TryGetValue("effectiveSettings", out settingsValue) ? settingsValue as IDictionary<string, object> : null;
                    var scenes = data.TryGetValue("scenes", out scenesValue) ? scenesValue as IList<object> : null;
                    object rootBeforeValue;
                    object rootAfterValue;
                    object rootSettingsValue;
                    var rootBefore = root.TryGetValue("snapshotBefore", out rootBeforeValue) ? rootBeforeValue as IDictionary<string, object> : null;
                    var rootAfter = root.TryGetValue("snapshotAfter", out rootAfterValue) ? rootAfterValue as IDictionary<string, object> : null;
                    var rootSettings = root.TryGetValue("effectiveSettings", out rootSettingsValue) ? rootSettingsValue as IDictionary<string, object> : null;
                    return SameSnapshotValues(before, rootBefore) && SameSnapshotValues(after, rootAfter)
                        && SameSettingsValues(settings, rootSettings)
                        && SameSceneLists(scenes, RootList(root, "scenesResult"))
                        && Long(data, "durationMs") == Long(status, "durationMs")
                        && Long(data, "durationMs") == DurationMs(Text(root, "startedAt"), Text(root, "finishedAt"));
                }

                private static bool SameSceneLists(IList<object> left, IList<object> right)
                {
                    if (left == null || right == null || left.Count != right.Count) return false;
                    for (var index = 0; index < left.Count; index++)
                    {
                        var leftScene = left[index] as IDictionary<string, object>;
                        var rightScene = right[index] as IDictionary<string, object>;
                        object leftDirty;
                        object rightDirty;
                        if (leftScene == null || rightScene == null
                            || Text(leftScene, "path") != Text(rightScene, "path")
                            || Text(leftScene, "guid") != Text(rightScene, "guid")
                            || Text(leftScene, "sha256") != Text(rightScene, "sha256")
                            || Long(leftScene, "sizeBytes") != Long(rightScene, "sizeBytes")
                            || !leftScene.TryGetValue("dirty", out leftDirty) || !(leftDirty is bool)
                            || !rightScene.TryGetValue("dirty", out rightDirty) || !(rightDirty is bool)
                            || (bool)leftDirty != (bool)rightDirty) return false;
                    }
                    return true;
                }

                private static bool ValidateOcclusionRoot(Dictionary<string, object> root)
                {
                    if (root == null || string.IsNullOrEmpty(Text(root, "requestId"))
                        || !IsSha256Text(Text(root, "requestDigest"))
                        || string.IsNullOrEmpty(Text(root, "editorSessionId"))
                        || !ValidateTimestamp(Text(root, "deadline"))
                        || !ValidateTimestamp(Text(root, "startedAt"))
                        || !ValidateTimestamp(Text(root, "dispatchNotBefore"))
                        || !TryNonNegativeLong(root, "targetCount")
                        || !ValidateRootSettings(root)
                        || !ValidateRootSnapshot(root, "snapshotBefore")) return false;
                    var digest = Text(root, "requestDigest");
                    if (!string.Equals(Text(root, "operationId"), "occlusion-" + digest.Substring(0, 32), StringComparison.Ordinal)) return false;
                    object reloadValue;
                    object cancelValue;
                    if (!root.TryGetValue("reloadObserved", out reloadValue) || !(reloadValue is bool)
                        || !root.TryGetValue("cancelRequested", out cancelValue) || !(cancelValue is bool)) return false;
                    var scenes = RootList(root, "scenes");
                    var proofs = RootList(root, "sceneContentProofs");
                    if (scenes == null || proofs == null || scenes.Count != Long(root, "targetCount") || proofs.Count != scenes.Count) return false;
                    for (var index = 0; index < scenes.Count; index++)
                    {
                        var scene = scenes[index] as IDictionary<string, object>;
                        var proof = proofs[index] as IDictionary<string, object>;
                        object dirty;
                        if (scene == null || proof == null || string.IsNullOrEmpty(Text(scene, "path"))
                            || !IsHexText(Text(scene, "guid"), 32) || !IsSha256Text(Text(scene, "sha256"))
                            || !TryNonNegativeLong(scene, "sizeBytes")
                            || !scene.TryGetValue("dirty", out dirty) || !(dirty is bool) || (bool)dirty
                            || !string.Equals(Text(scene, "path"), Text(proof, "path"), StringComparison.Ordinal)
                            || !IsSha256Text(Text(proof, "contentSha256"))) return false;
                    }
                    var phase = Text(root, "phase");
                    if (phase != "queued" && phase != "dispatching" && phase != "running" && phase != "verifying") return false;
                    var dispatchNonce = Text(root, "dispatchNonce");
                    object dispatchConfirmedValue;
                    var hasDispatchConfirmed = root.TryGetValue("dispatchConfirmed", out dispatchConfirmedValue);
                    if (hasDispatchConfirmed && !(dispatchConfirmedValue is bool)) return false;
                    var dispatchConfirmed = hasDispatchConfirmed && (bool)dispatchConfirmedValue;
                    if (phase == "queued" && (!string.IsNullOrEmpty(dispatchNonce) || dispatchConfirmed)) return false;
                    if (phase == "dispatching" && (string.IsNullOrEmpty(dispatchNonce) || dispatchConfirmed)) return false;
                    if ((phase == "running" || phase == "verifying") && (string.IsNullOrEmpty(dispatchNonce) || !dispatchConfirmed)) return false;
                    if ((bool)reloadValue)
                    {
                        var beforeReload = Text(root, "phaseBeforeReload");
                        if (beforeReload != "queued" && beforeReload != "dispatching" && beforeReload != "running" && beforeReload != "verifying") return false;
                    }
                    if (phase == "verifying" && (!ValidateTimestamp(Text(root, "verifyNotBefore"))
                        || !ValidateRootSnapshot(root, "snapshotAfter")
                        || RootList(root, "scenesResult") == null
                        || RootList(root, "sceneContentProofsResult") == null)) return false;
                    var terminalState = Text(root, "terminalState");
                    if (!string.IsNullOrEmpty(terminalState))
                    {
                        if ((terminalState != "completed" && terminalState != "cancelled" && terminalState != "failed")
                            || !ValidateTimestamp(Text(root, "finishedAt")) || !ValidateRootSnapshot(root, "snapshotAfter")
                            || !RootTerminalProofMatches(root)) return false;
                        if (terminalState == "completed" && (phase != "verifying" || !dispatchConfirmed)) return false;
                        if (terminalState == "cancelled"
                            && !(phase == "queued" && Bool(root, "cancelRequested") && Text(root, "cancelEffectState") == "not-required")) return false;
                    }
                    var status = LoadStatus();
                    if (status == null) return false;
                    if (!string.Equals(Text(status, "rootRequestId"), Text(root, "requestId"), StringComparison.Ordinal)
                        || !string.Equals(Text(status, "rootRequestDigest"), digest, StringComparison.Ordinal)) return false;
                    if (Bool(status, "active"))
                    {
                        object statusDispatchConfirmed;
                        if (!status.TryGetValue("dispatchConfirmed", out statusDispatchConfirmed) || !(statusDispatchConfirmed is bool)
                            || (bool)statusDispatchConfirmed != dispatchConfirmed || Text(status, "phase") != PublicPhase(phase)) return false;
                        return string.IsNullOrEmpty(terminalState) || terminalState == "completed" || terminalState == "cancelled" || terminalState == "failed";
                    }
                    return TerminalStatusMatchesRoot(root, status, Text(root, "requestId"), digest);
                }

                private static bool ValidateRootSettings(IDictionary<string, object> root)
                {
                    object settingsValue;
                    var settings = root.TryGetValue("effectiveSettings", out settingsValue) ? settingsValue as IDictionary<string, object> : null;
                    return ValidateSettingsValues(settings);
                }

                private static bool ValidateRootSnapshot(IDictionary<string, object> root, string key)
                {
                    object snapshotValue;
                    var snapshot = root.TryGetValue(key, out snapshotValue) ? snapshotValue as IDictionary<string, object> : null;
                    return ValidateSnapshotValues(snapshot);
                }

                private static IList<object> RootList(IDictionary<string, object> root, string key)
                {
                    object value;
                    return root.TryGetValue(key, out value) ? value as IList<object> : null;
                }

                protected override object Planned(object value) { return DryRun((StartArgs)value, BuildPlan((StartArgs)value)); }
                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object value) { return CreateRoot(request, digest, (StartArgs)value, BuildPlan((StartArgs)value)); }

                /// <summary>完整plan在HTTP acceptance和durable effect边界前冻结；已accept的root不由transport重放。</summary>
                internal override async Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken cancellationToken)
                {
                    if (request.Command != StartCommand) return await base.Execute(request, raw, accept, cancellationToken);
                    var args = (StartArgs)DeserializeStartArgs(request);
                    ValidateStart(args);
                    if (request.Execution != null && request.Execution.DryRun == true)
                        return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, DryRun(args, BuildPlan(args))));
                    var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId);
                    var recovered = RecoveredStoredTerminal(request.RequestId, digest) ?? RecoveredTerminal(request.RequestId, digest);
                    if (recovered != null) return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, recovered));

                    var plan = BuildPlan(args);
                    string currentOwnerRequestId;
                    string currentOwnerRequestDigest;
                    if (!UnityCliSessionMutationLease.TryGetCurrentOwner(out currentOwnerRequestId, out currentOwnerRequestDigest))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion lease ownership cannot be read; outcome is unknown.");
                    if (string.Equals(currentOwnerRequestId, request.RequestId, StringComparison.Ordinal)
                        && !string.Equals(currentOwnerRequestDigest, digest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Occlusion bake requestId was reused with different input.");
                    var root = Load();
                    bool acquired = false;
                    var saved = false;
                    try
                    {
                        if (root == null)
                        {
                            if (LastLoadWasCorrupt || LastLoadWasUnreadable || LastLoadRecoveredTerminal)
                            {
                                var corruptTerminal = RecoveredStoredTerminal(request.RequestId, digest);
                                if (corruptTerminal != null)
                                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, corruptTerminal));
                                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion bake active state is unavailable; outcome is unknown.");
                            }
                            if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out acquired)) return Busy(request.RequestId);
                            if (!acquired) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion bake active state is missing while the exact lease still exists; outcome is unknown.");
                            var candidate = CreateRoot(request, digest, args, plan);
                            if (accept == null || !accept()) throw new OperationCanceledException("HTTP timeout cancelled Occlusion bake before acceptance.");
                            BindSessionOwner(request.RequestId, digest);
                            Save(candidate);
                            saved = true;
                            try { WriteRunningStatus(candidate); }
                            catch (Exception exception)
                            {
                                BindPreEffectAbort(candidate);
                                BeginPreEffectCleanup(candidate);
                                throw new InvalidOperationException("Occlusion bake running status could not be stored before dispatch.", exception);
                            }
                        }
                        else
                        {
                            if (!string.Equals(Text(root, "requestId"), request.RequestId, StringComparison.Ordinal)) return Busy(request.RequestId);
                            if (!string.Equals(Text(root, "requestDigest"), digest, StringComparison.Ordinal))
                                throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Occlusion bake requestId was reused with different input.");
                            if (!UnityCliSessionMutationLease.TryRecoverExact(request.RequestId, digest, out acquired)) return Busy(request.RequestId);
                            BindSessionOwner(request.RequestId, digest);
                            saved = true;
                        }
                        var acceptedRoot = Load();
                        await Task.FromResult(0);
                        return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object>
                        {
                            { "terminal", false }, { "accepted", true },
                            { "operationId", acceptedRoot == null ? null : Text(acceptedRoot, "operationId") }, { "rootRequestDigest", digest }
                        }));
                    }
                    catch
                    {
                        if (acquired && !saved) UnityCliSessionMutationLease.Release(request.RequestId, digest);
                        throw;
                    }
                }

                /// <summary>active root与同session durable terminal共享一个root status协议，reload后不会重放已完成effect。</summary>
                protected override object Status(BridgeRequest request)
                {
                    var args = request.DeserializeArgs<RootStatusArgs>();
                    if (args == null || string.IsNullOrWhiteSpace(args.RootRequestId) || !IsSha256Text(args.RootRequestDigest))
                        throw new BridgeCommandException("E_SCHEMA_INVALID", "Occlusion bake status requires rootRequestId and rootRequestDigest.");
                    var root = Load();
                    if (root != null && string.Equals(Text(root, "requestId"), args.RootRequestId, StringComparison.Ordinal))
                    {
                        if (!string.Equals(Text(root, "requestDigest"), args.RootRequestDigest, StringComparison.Ordinal))
                            throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Occlusion bake requestId was reused with different input.");
                        var rootedTerminal = RecoveredStoredTerminal(args.RootRequestId, args.RootRequestDigest);
                        if (rootedTerminal != null) return ProtocolEnvelopeFactory.Success(request.RequestId, rootedTerminal);
                        return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", false }, { "accepted", true }, { "phase", Text(root, "phase") }, { "rootRequestDigest", args.RootRequestDigest } });
                    }
                    var terminal = RecoveredStoredTerminal(args.RootRequestId, args.RootRequestDigest) ?? RecoveredTerminal(args.RootRequestId, args.RootRequestDigest);
                    if (terminal != null) return ProtocolEnvelopeFactory.Success(request.RequestId, terminal);
                    ThrowIfActiveStateUnresolved();
                    throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "Occlusion bake request was not found.");
                }

                protected override object ExecuteAdditionalCommand(BridgeRequest request)
                {
                    if (request.Command == PlanCommand)
                    {
                        var args = request.DeserializeArgs<StartArgs>();
                        ValidateStart(args);
                        return ProtocolEnvelopeFactory.Success(request.RequestId, DryRun(args, BuildPlan(args)));
                    }
                    if (request.Command == SnapshotCommand) return ProtocolEnvelopeFactory.Success(request.RequestId, PublicStatus());
                    if (request.Command == CancelCommand) return ProtocolEnvelopeFactory.Success(request.RequestId, Cancel(request.DeserializeArgs<CancelArgs>()));
                    return base.ExecuteAdditionalCommand(request);
                }

                protected override void OnTick(Dictionary<string, object> root)
                {
                    var storedTerminal = RecoveredStoredTerminal(Text(root, "requestId"), Text(root, "requestDigest"));
                    if (storedTerminal != null)
                    {
                        ResumeTerminalCleanup(root);
                        return;
                    }
                    var phase = Text(root, "phase");
                    if (Bool(root, "reloadObserved")) { RecoverReload(root, phase); return; }
                    if (Bool(root, "cancelRequested") && phase == "queued") { FinishCancelled(root); return; }
                    if (phase == "queued" && (EditorApplication.isCompiling || EditorApplication.isUpdating)) { WriteRunningStatus(root); return; }
                    if (phase == "queued" && DateTimeOffset.UtcNow < Time(root, "dispatchNotBefore")) { WriteRunningStatus(root); return; }
                    if (phase == "queued") { Dispatch(root); return; }
                    if (phase == "dispatching") { RecoverDispatch(root); return; }
                    if (phase == "verifying") { Complete(root); return; }
                    if (phase != "running") { Fail(root, "E_EXECUTION_TIMEOUT", "Occlusion bake phase is invalid; outcome is unknown."); return; }
                    if (!HasDispatchProof(root))
                    {
                        WriteRunningStatus(root);
                        if (StaticOcclusionCulling.isRunning) return;
                        Fail(root, "E_EXECUTION_TIMEOUT", "Occlusion running state lacks dispatch proof; outcome is unknown.");
                        return;
                    }
                    WriteRunningStatus(root);
                    if (StaticOcclusionCulling.isRunning) return;
                    // 全局idle只触发业务readback；完成结论仍由settings、scene和Umbra fresh proof决定。
                    Complete(root);
                }

                protected override object BuildSuccessData(Dictionary<string, object> root)
                {
                    var startedAt = Text(root, "startedAt");
                    var finishedAt = Text(root, "finishedAt");
                    return new Dictionary<string, object>
                    {
                        { "operationId", Text(root, "operationId") }, { "status", "completed" },
                        { "startedAt", startedAt }, { "finishedAt", finishedAt }, { "durationMs", DurationMs(startedAt, finishedAt) },
                        { "effectiveSettings", root["effectiveSettings"] },
                        { "before", root["snapshotBefore"] },
                        { "after", root.ContainsKey("snapshotAfter") ? root["snapshotAfter"] : OcclusionToolSupport.SnapshotDictionary(OcclusionToolSupport.ReadSnapshot()) },
                        { "scenes", root.ContainsKey("scenesResult") ? root["scenesResult"] : root["scenes"] },
                        { "plannedEffects", new List<object> { PlannedEffect(Long(root, "targetCount")) } }
                    };
                }

                protected override void OnTerminal(Dictionary<string, object> root, object envelope)
                {
                    try
                    {
                        StoreTerminalEnvelope(root, envelope);
                        if (TerminalAlreadyPublished(root, envelope)) return;
                        var parsed = UnityCliSerializer.Parse(UnityCliSerializer.Serialize(envelope)) as IDictionary<string, object>;
                        object ok;
                        var succeeded = parsed != null && parsed.TryGetValue("ok", out ok) && ok is bool && (bool)ok;
                        if (!succeeded && HasPossibleNativeEffect(root) && StaticOcclusionCulling.isRunning)
                            throw new InvalidOperationException("Occlusion native bake must stop before terminal publication.");
                        WriteTerminalStatus(root, Text(root, "terminalState"), envelope);
                    }
                    catch (Exception exception)
                    {
                        WriteLog("occlusion_bake_status_write_failed " + exception.GetType().Name + ": " + exception.Message);
                        throw;
                    }
                }

                /// <summary>status发布前原子冻结terminal state、exact envelope与session proof，后续Tick不得回到业务调度。</summary>
                private void StoreTerminalEnvelope(Dictionary<string, object> root, object envelope)
                {
                    var serialized = UnityCliSerializer.Serialize(envelope);
                    object existing;
                    if (root.TryGetValue("terminalEnvelope", out existing) && existing != null)
                    {
                        if (!string.Equals(UnityCliSerializer.Serialize(existing), serialized, StringComparison.Ordinal)
                            || !RootTerminalProofMatches(root))
                            throw new InvalidOperationException("Occlusion root terminal cannot be overwritten by a different envelope.");
                        return;
                    }
                    var parsed = UnityCliSerializer.Parse(serialized) as IDictionary<string, object>;
                    object okValue;
                    if (parsed == null || !parsed.TryGetValue("ok", out okValue) || !(okValue is bool))
                        throw new InvalidOperationException("Occlusion terminal envelope is invalid.");
                    var state = (bool)okValue ? "completed"
                        : string.Equals(Text(root, "terminalState"), "cancelled", StringComparison.Ordinal) ? "cancelled" : "failed";
                    var existingState = Text(root, "terminalState");
                    if (!string.IsNullOrEmpty(existingState) && !string.Equals(existingState, state, StringComparison.Ordinal))
                        throw new InvalidOperationException("Occlusion root terminal state conflicts with the proposed envelope.");
                    root["terminalState"] = state;
                    if (string.IsNullOrEmpty(Text(root, "finishedAt")))
                        root["finishedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    if (!root.ContainsKey("snapshotAfter"))
                        root["snapshotAfter"] = OcclusionToolSupport.SnapshotDictionary(OcclusionToolSupport.ReadSnapshot());
                    root["terminalEnvelope"] = parsed;
                    root["terminalProofSha256"] = RootTerminalProofSha256(root);
                    if (!RootTerminalProofMatches(root))
                        throw new InvalidOperationException("Occlusion terminal proof key is unavailable.");
                    Save(root);
                }

                /// <summary>queued且无dispatch证据时可证明尚未调用本请求的native effect。</summary>
                private static bool HasPossibleNativeEffect(Dictionary<string, object> root)
                {
                    return Text(root, "phase") != "queued" || !string.IsNullOrEmpty(Text(root, "dispatchNonce")) || Bool(root, "dispatchConfirmed");
                }

                /// <summary>同identity terminal只能首次发布；cleanup重试不得以新的envelope覆盖既有结果。</summary>
                private static bool TerminalAlreadyPublished(Dictionary<string, object> root, object envelope)
                {
                    var status = LoadStatus();
                    if (status == null)
                    {
                        if (LastStatusLoadOutcome == StatusLoadOutcome.TransientFailure)
                            throw new InvalidOperationException("Occlusion durable terminal cannot be verified while status I/O is unavailable.");
                        return false;
                    }
                    if (!string.Equals(Text(status, "rootRequestId"), Text(root, "requestId"), StringComparison.Ordinal)
                        || !string.Equals(Text(status, "rootRequestDigest"), Text(root, "requestDigest"), StringComparison.Ordinal))
                        throw new InvalidOperationException("Occlusion status belongs to a different request identity.");
                    if (Bool(status, "active")) return false;
                    object rootEnvelope;
                    if (!status.TryGetValue("rootEnvelope", out rootEnvelope) || rootEnvelope == null) return false;
                    var existing = UnityCliSerializer.Serialize(rootEnvelope);
                    var proposed = UnityCliSerializer.Serialize(envelope);
                    if (!string.Equals(existing, proposed, StringComparison.Ordinal))
                        throw new InvalidOperationException("Occlusion durable terminal cannot be overwritten by a different envelope.");
                    return true;
                }

                /// <summary>root中的exact envelope是status损坏后的唯一恢复源；不同identity status绝不覆盖。</summary>
                private bool TryRestoreTerminalStatus(Dictionary<string, object> root)
                {
                    if (!ValidateStoredTerminalEnvelope(root)) return false;
                    var requestId = Text(root, "requestId");
                    var requestDigest = Text(root, "requestDigest");
                    object envelope;
                    root.TryGetValue("terminalEnvelope", out envelope);
                    var status = LoadStatus();
                    if (status != null)
                    {
                        if (!string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal)
                            || !string.Equals(Text(status, "rootRequestDigest"), requestDigest, StringComparison.Ordinal)) return false;
                        if (!Bool(status, "active"))
                        {
                            object existing;
                            if (!status.TryGetValue("rootEnvelope", out existing) || existing == null
                                || !string.Equals(UnityCliSerializer.Serialize(existing), UnityCliSerializer.Serialize(envelope), StringComparison.Ordinal))
                                throw new InvalidOperationException("Occlusion stored terminal conflicts with the root envelope.");
                            return true;
                        }
                    }
                    else if (LastStatusLoadOutcome == StatusLoadOutcome.TransientFailure) return false;

                    var terminalState = Text(root, "terminalState");
                    if (HasPossibleNativeEffect(root) && terminalState != "completed" && StaticOcclusionCulling.isRunning) return false;
                    WriteTerminalStatus(root, terminalState == "completed" ? "completed" : terminalState == "cancelled" ? "cancelled" : "failed", envelope);
                    var restored = LoadStatus();
                    return TerminalStatusMatchesRoot(root, restored, requestId, requestDigest);
                }

                /// <summary>验证root envelope、业务state与session HMAC属于同一不可改写终态。</summary>
                private bool ValidateStoredTerminalEnvelope(Dictionary<string, object> root)
                {
                    var terminalState = Text(root, "terminalState");
                    object envelopeValue;
                    var envelope = root.TryGetValue("terminalEnvelope", out envelopeValue) ? envelopeValue as IDictionary<string, object> : null;
                    object okValue;
                    string envelopeRequestId;
                    if ((terminalState != "completed" && terminalState != "cancelled" && terminalState != "failed")
                        || !RootTerminalProofMatches(root)
                        || envelope == null || !envelope.TryGetValue("ok", out okValue) || !(okValue is bool)
                        || !TryNonEmptyString(envelope, "requestId", out envelopeRequestId)
                        || !string.Equals(envelopeRequestId, Text(root, "requestId"), StringComparison.Ordinal)) return false;
                    if (terminalState == "completed")
                    {
                        object dataValue;
                        var data = envelope.TryGetValue("data", out dataValue) ? dataValue as IDictionary<string, object> : null;
                        return (bool)okValue && data != null
                            && string.Equals(UnityCliSerializer.Serialize(data), UnityCliSerializer.Serialize(BuildSuccessData(root)), StringComparison.Ordinal);
                    }
                    object errorValue;
                    var error = envelope.TryGetValue("error", out errorValue) ? errorValue as IDictionary<string, object> : null;
                    string errorCode;
                    return !(bool)okValue && error != null && TryNonEmptyString(error, "code", out errorCode)
                        && (terminalState != "cancelled" || errorCode == "E_OCCLUSION_BAKE_CANCELLED");
                }

                /// <summary>损坏root与无故缺失root共用durable status/lease recovery，绝不从旧内存root发布终态。</summary>
                protected override bool OnActiveStateCorrupt(Exception exception) { return RecoverUnavailableActiveState(exception); }
                protected override bool OnActiveStateMissing()
                {
                    string requestId;
                    string requestDigest;
                    return UnityCliSessionMutationLease.TryGetCurrentOwner(out requestId, out requestDigest)
                        && IsBoundSessionOwner(requestId, requestDigest)
                        && RecoverUnavailableActiveState(new InvalidOperationException("Occlusion bake active state is missing."));
                }

                private bool RecoverUnavailableActiveState(Exception exception)
                {
                    string requestId;
                    string requestDigest;
                    if (!UnityCliSessionMutationLease.TryGetCurrentOwner(out requestId, out requestDigest)
                        || !IsBoundSessionOwner(requestId, requestDigest)) return false;
                    var observedRejectedRoot = rejectedRoot;
                    rejectedRoot = null;
                    if (observedRejectedRoot != null && !string.IsNullOrEmpty(Text(observedRejectedRoot, "terminalState")))
                    {
                        try
                        {
                            if (TryRestoreTerminalStatus(observedRejectedRoot)) return true;
                            var storedStatus = LoadStatus();
                            return TerminalStatusMatchesIdentity(storedStatus, requestId, requestDigest);
                        }
                        catch (Exception restoreException) { WriteLog("occlusion_terminal_recovery_retry " + restoreException.Message); return false; }
                    }
                    var status = LoadStatus();
                    if (observedRejectedRoot == null && status == null
                        && (LastStatusLoadOutcome == StatusLoadOutcome.TransientFailure || LastStatusLoadOutcome == StatusLoadOutcome.Corrupt)) return false;
                    if (status != null && !string.Equals(Text(status, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)) return false;
                    var statusRequestId = Text(status, "rootRequestId");
                    var statusRequestDigest = Text(status, "rootRequestDigest");
                    if (status != null && (!string.Equals(statusRequestId, requestId, StringComparison.Ordinal)
                        || !string.Equals(statusRequestDigest, requestDigest, StringComparison.Ordinal))) return false;
                    if (StaticOcclusionCulling.isRunning) return false;
                    object existingRootEnvelope;
                    if (status != null && !Bool(status, "active") && status.TryGetValue("rootEnvelope", out existingRootEnvelope) && existingRootEnvelope != null)
                        return true;
                    WriteLog("occlusion_bake_active_state_corrupt requestId=" + requestId + " " + exception.GetType().Name + ": " + exception.Message);
                    var finishedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    var startedAt = Text(status, "startedAt");
                    if (!ValidateTimestamp(startedAt)) startedAt = finishedAt;
                    var terminalRoot = new Dictionary<string, object>
                    {
                        { "requestId", requestId }, { "requestDigest", requestDigest }, { "operationId", "occlusion-" + requestDigest.Substring(0, 32) },
                        { "startedAt", startedAt }, { "finishedAt", finishedAt },
                        { "targetCount", Long(status, "targetCount") }, { "terminalState", "failed" },
                        { "snapshotAfter", OcclusionToolSupport.SnapshotDictionary(OcclusionToolSupport.ReadSnapshot()) }
                    };
                    var envelope = ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "Occlusion bake active state is corrupt; outcome is unknown.", null,
                        new[] { "Inspect occlusion.bake-status and the open scene state before submitting a new request." });
                    WriteTerminalStatus(terminalRoot, "failed", envelope);
                    return true;
                }

                internal Dictionary<string, object> RecoveredStoredTerminal(string requestId, string requestDigest)
                {
                    var status = LoadStatus();
                    if (status == null)
                    {
                        if (StatusHistoryUnavailable())
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion bake terminal status is unavailable; outcome is unknown.");
                        return null;
                    }
                    if (!string.Equals(Text(status, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal)
                        || !string.Equals(Text(status, "rootRequestId"), requestId, StringComparison.Ordinal)
                        || !status.ContainsKey("rootEnvelope")) return null;
                    if (!string.IsNullOrEmpty(requestDigest) && !string.Equals(Text(status, "rootRequestDigest"), requestDigest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", "Occlusion bake requestId was reused with different input.");
                    if (UnityCliActiveOperation.Exists(ActiveScope))
                    {
                        if (StaticOcclusionCulling.isRunning)
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion terminal cannot be recovered while a native bake is running.");
                        try
                        {
                            var active = UnityCliActiveOperation.Load(ActiveScope);
                            ValidateLoadedRoot(active);
                            if (!TerminalStatusMatchesRoot(active, status, requestId, Text(status, "rootRequestDigest")))
                                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion terminal status does not match the active root.");
                        }
                        catch (BridgeCommandException) { throw; }
                        catch (Exception) { throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion active root cannot be verified against terminal status."); }
                    }
                    else if (!StaticOcclusionCulling.isRunning && IsBoundSessionOwner(requestId, Text(status, "rootRequestDigest"))
                        && UnityCliSessionMutationLease.IsOwnedBy(requestId, Text(status, "rootRequestDigest")))
                    {
                        ResumeTerminalCleanup(new Dictionary<string, object>
                        {
                            { "requestId", requestId }, { "requestDigest", Text(status, "rootRequestDigest") }
                        });
                    }
                    return new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", status["rootEnvelope"] } };
                }

                internal void InitializeState()
                {
                    if (!SessionState.GetBool(StatusSessionInitializedKey, false))
                    {
                        UnityCliActiveOperation.Clear(StatusScope);
                        UnityCliActiveOperation.Clear(ActiveScope);
                        UnityCliActiveOperation.Clear(ActiveScope + "-terminal-cleanup");
                        UnityCliActiveOperation.Clear(ActiveScope + "-pre-effect-cleanup");
                        SessionState.SetBool(StatusSessionInitializedKey, true);
                        SessionState.SetBool(StatusWrittenThisSessionKey, false);
                        SessionState.SetString(StatusProofKeySessionKey, NewStatusProofKey());
                        SessionState.SetString(OwnerRequestIdSessionKey, string.Empty);
                        SessionState.SetString(OwnerRequestDigestSessionKey, string.Empty);
                        SessionState.SetString(PreEffectAbortRequestIdSessionKey, string.Empty);
                        SessionState.SetString(PreEffectAbortRequestDigestSessionKey, string.Empty);
                        StatusJournalCorrupt = false;
                        LastStatusLoadOutcome = StatusLoadOutcome.Missing;
                    }
                    else if (string.IsNullOrEmpty(SessionState.GetString(StatusProofKeySessionKey, string.Empty)))
                    {
                        SessionState.SetString(StatusProofKeySessionKey, NewStatusProofKey());
                        StatusJournalCorrupt = UnityCliActiveOperation.Exists(StatusScope);
                    }
                    if (ResumeBoundPreEffectAbort()) return;
                    if (ResumePreEffectCleanup()) return;
                    var active = Load();
                    if (active != null && !string.Equals(Text(active, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                    {
                        UnityCliActiveOperation.Clear(ActiveScope);
                        UnityCliSessionMutationLease.Release(Text(active, "requestId"), Text(active, "requestDigest"));
                        active = null;
                    }
                    var status = LoadStatus();
                    if (status != null && !string.Equals(Text(status, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                        UnityCliActiveOperation.Clear(StatusScope);
                    if (active != null)
                    {
                        if (TryResumePublishedTerminal(active)) return;
                        var terminal = RecoveredStoredTerminal(Text(active, "requestId"), Text(active, "requestDigest"));
                        if (terminal != null)
                        {
                            ResumeTerminalCleanup(active);
                            return;
                        }
                        WriteRunningStatus(active);
                    }
                }

                /// <summary>marker写失败后的domain reload以SessionState exact binding重建effect前cleanup，禁止queued root继续dispatch。</summary>
                private bool ResumeBoundPreEffectAbort()
                {
                    var requestId = SessionState.GetString(PreEffectAbortRequestIdSessionKey, string.Empty);
                    var requestDigest = SessionState.GetString(PreEffectAbortRequestDigestSessionKey, string.Empty);
                    if (!IsBoundPreEffectAbort(requestId, requestDigest)) return false;
                    if (!UnityCliSessionMutationLease.IsOwnedBy(requestId, requestDigest))
                    {
                        ClearPreEffectAbortBinding();
                        return false;
                    }
                    var cleanupRoot = new Dictionary<string, object>
                    {
                        { "requestId", requestId }, { "requestDigest", requestDigest }
                    };
                    if (UnityCliActiveOperation.Exists(ActiveScope))
                    {
                        try { cleanupRoot = UnityCliActiveOperation.Load(ActiveScope); }
                        catch (Exception exception) { WriteLog("occlusion_bake_pre_effect_root_read_failed " + exception.Message); return true; }
                        if (!string.Equals(Text(cleanupRoot, "requestId"), requestId, StringComparison.Ordinal)
                            || !string.Equals(Text(cleanupRoot, "requestDigest"), requestDigest, StringComparison.Ordinal)) return true;
                    }
                    BeginPreEffectCleanup(cleanupRoot);
                    return true;
                }

                internal void BeforeReload() { Update(root => { root["reloadObserved"] = true; root["phaseBeforeReload"] = Text(root, "phase"); }); }

                private Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, StartArgs args, Dictionary<string, object> plan)
                {
                    return new Dictionary<string, object>
                    {
                        { "requestId", request.RequestId }, { "requestDigest", digest }, { "editorSessionId", EditorSessionId() },
                        { "deadline", request.Execution.AbsoluteDeadlineUtc }, { "operationId", "occlusion-" + digest.Substring(0, 32) },
                        { "phase", "queued" }, { "startedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                        { "dispatchNotBefore", DateTime.UtcNow.AddMilliseconds(3000).ToString("O", CultureInfo.InvariantCulture) },
                        { "targetCount", plan["targetCount"] }, { "scenes", plan["scenes"] },
                        { "sceneContentProofs", plan["sceneContentProofs"] },
                        { "effectiveSettings", plan["effectiveSettings"] }, { "snapshotBefore", plan["before"] },
                        { "reloadObserved", false }, { "cancelRequested", false }
                    };
                }

                private Dictionary<string, object> BuildPlan(StartArgs args)
                {
                    if (StaticOcclusionCulling.isRunning)
                        throw new BridgeCommandException("E_OCCLUSION_BAKE_ACTIVE", "An unowned Occlusion bake is already running.", "Wait for it to finish before starting a tracked bake.");
                    var scenes = OcclusionToolSupport.PrepareLoadedSceneFiles("occlusion.bake");
                    var sceneContentProofs = OcclusionToolSupport.CaptureSceneContentProofs(scenes);
                    var settings = OcclusionToolSupport.PrepareSettings(args.SmallestOccluder, args.SmallestHole, args.BackfaceThreshold);
                    var before = OcclusionToolSupport.ReadSnapshot();
                    return new Dictionary<string, object>
                    {
                        { "targetCount", (long)scenes.Count }, { "scenes", SceneObjects(scenes) },
                        { "sceneContentProofs", SceneContentProofObjects(sceneContentProofs) },
                        { "effectiveSettings", OcclusionToolSupport.SettingsDictionary(settings) },
                        { "before", OcclusionToolSupport.SnapshotDictionary(before) },
                        { "plannedEffects", new List<object> { PlannedEffect(scenes.Count) } }
                    };
                }

                private object DryRun(StartArgs args, Dictionary<string, object> plan)
                {
                    return new Dictionary<string, object>
                    {
                        { "dryRun", true }, { "targetCount", plan["targetCount"] }, { "scenes", plan["scenes"] },
                        { "effectiveSettings", plan["effectiveSettings"] }, { "before", plan["before"] }, { "plannedEffects", plan["plannedEffects"] }
                    };
                }

                /// <summary>执行fresh root更新并重新读取；任一读取失败都中止当前调用，禁止使用旧内存对象继续。</summary>
                private bool TryUpdateAndReload(Action<Dictionary<string, object>> action, out Dictionary<string, object> root)
                {
                    root = null;
                    if (!Update(action)) return false;
                    Dictionary<string, object> updated;
                    try { updated = UnityCliActiveOperation.Load(ActiveScope); }
                    catch (Exception exception)
                    {
                        WriteLog("occlusion_bake_updated_root_reload_failed " + exception.GetType().Name + ": " + exception.Message);
                        return false;
                    }
                    if (!UnityCliSessionMutationLease.IsOwnedBy(Text(updated, "requestId"), Text(updated, "requestDigest"))) return false;
                    WriteRunningStatus(updated);
                    root = Load();
                    return root != null;
                }

                /// <summary>root先进入dispatching并持久化nonce，随后仅调用一次Unity bake effect。</summary>
                private void Dispatch(Dictionary<string, object> root)
                {
                    if (StaticOcclusionCulling.isRunning)
                    {
                        Fail(root, "E_OCCLUSION_BAKE_ACTIVE", "An unowned Occlusion bake started before the tracked effect was dispatched.");
                        return;
                    }
                    var preEffectRoot = root;
                    try
                    {
                        if (!TryUpdateAndReload(value => { value["phase"] = "dispatching"; value["dispatchNonce"] = Guid.NewGuid().ToString("N"); }, out root))
                        {
                            AbortBeforeNativeEffect(preEffectRoot, "dispatch transition could not be confirmed");
                            return;
                        }
                    }
                    catch (Exception exception)
                    {
                        AbortBeforeNativeEffect(preEffectRoot, "dispatch transition failed: " + exception.GetType().Name);
                        return;
                    }
                    var nativeDispatched = false;
                    try
                    {
                        OcclusionToolSupport.EnsureEditorReady("occlusion.bake", true);
                        if (StaticOcclusionCulling.isRunning)
                            throw new BridgeCommandException("E_OCCLUSION_BAKE_ACTIVE", "An unowned Occlusion bake started before the tracked effect was dispatched.");
                        OcclusionToolSupport.ValidateFrozenSceneFiles(SceneFiles(root), "occlusion.bake");
                        OcclusionToolSupport.ValidateFrozenSceneContentProofs(SceneContentProofs(root));
                        var expected = Snapshot(root, "snapshotBefore");
                        var observed = OcclusionToolSupport.ReadSnapshot();
                        if (expected.UmbraDataSizeBytes != observed.UmbraDataSizeBytes || expected.DoesSceneHaveManualPortals != observed.DoesSceneHaveManualPortals)
                            throw new BridgeCommandException("E_OCCLUSION_TARGET_CHANGED", "Occlusion bake refused to dispatch because the frozen Umbra state changed.", "Inspect occlusion.bake-status and the loaded scenes, then submit a fresh request.");
                        OcclusionToolSupport.ApplySettings(Settings(root));
                        var nativeAccepted = StaticOcclusionCulling.GenerateInBackground();
                        if (!nativeAccepted)
                        {
                            Fail(root, "E_EXECUTION_TIMEOUT", "Occlusion bake dispatch was not accepted by Unity; outcome is unknown.");
                            return;
                        }
                        nativeDispatched = true;
                        if (ConfirmDispatch()) return;
                        if (StaticOcclusionCulling.isRunning)
                        {
                            CancelOwnedNativeBake();
                            if (StaticOcclusionCulling.isRunning) return;
                        }
                        Fail(root, "E_EXECUTION_TIMEOUT", "Occlusion bake dispatch journal disappeared after the native effect; outcome is unknown.");
                    }
                    catch (BridgeCommandException exception)
                    {
                        if (nativeDispatched && StaticOcclusionCulling.isRunning)
                        {
                            CancelOwnedNativeBake();
                            if (StaticOcclusionCulling.isRunning) return;
                        }
                        Fail(root, exception.Code, exception.Message);
                    }
                    catch (Exception exception)
                    {
                        if (nativeDispatched && StaticOcclusionCulling.isRunning)
                        {
                            CancelOwnedNativeBake();
                            if (StaticOcclusionCulling.isRunning) return;
                        }
                        Fail(root, "E_EXECUTION_TIMEOUT", "Occlusion bake dispatch could not be confirmed: " + exception.GetType().Name + ".");
                    }
                }

                /// <summary>Unity尚未调用时以SessionState exact binding冻结abort；root/status只能cleanup，不能再dispatch。</summary>
                private void AbortBeforeNativeEffect(Dictionary<string, object> root, string reason)
                {
                    BindPreEffectAbort(root);
                    WriteLog("occlusion_pre_effect_abort requestId=" + Text(root, "requestId") + " reason=" + reason);
                    BeginPreEffectCleanup(root);
                }

                /// <summary>native effect返回后同步发布root与running status；任一写失败由调用栈取消owned native并fail closed。</summary>
                private bool ConfirmDispatch()
                {
                    var root = Load();
                    if (root == null) return false;
                    root["phase"] = "running";
                    root["dispatchConfirmed"] = true;
                    Save(root);
                    WriteRunningStatus(root);
                    return true;
                }

                private void RecoverReload(Dictionary<string, object> root, string phase)
                {
                    var beforeReload = Text(root, "phaseBeforeReload");
                    if (phase == "queued" && beforeReload == "queued")
                    {
                        Update(value => value["reloadObserved"] = false);
                        return;
                    }
                    if (phase == "dispatching" || beforeReload == "dispatching") { RecoverDispatch(root); return; }
                    if (phase == "verifying" && beforeReload == "verifying")
                    {
                        Dictionary<string, object> fresh;
                        if (TryUpdateAndReload(value => value["reloadObserved"] = false, out fresh)) Complete(fresh);
                        return;
                    }
                    if (phase == "running" && beforeReload == "running")
                    {
                        if (StaticOcclusionCulling.isRunning)
                        {
                            WriteRunningStatus(root);
                            return;
                        }
                        var before = Snapshot(root, "snapshotBefore");
                        var after = OcclusionToolSupport.ReadSnapshot();
                        if (after.UmbraDataSizeBytes != before.UmbraDataSizeBytes)
                        {
                            Dictionary<string, object> fresh;
                            if (TryUpdateAndReload(value => value["reloadObserved"] = false, out fresh)) Complete(fresh);
                            return;
                        }
                    }
                    Fail(root, "E_EXECUTION_TIMEOUT", "Occlusion bake crossed a reload without enough business proof; outcome is unknown.");
                }

                /// <summary>dispatching恢复只从native running或业务readback继续；证据不足时绝不重放effect。</summary>
                private void RecoverDispatch(Dictionary<string, object> root)
                {
                    if (StaticOcclusionCulling.isRunning) return;
                    Fail(root, "E_EXECUTION_TIMEOUT", "Occlusion dispatch crossed a reload without enough proof; outcome is unknown.");
                }

                /// <summary>scene save和fresh readback跨至少一次Editor update；verifying恢复只继续proof。</summary>
                private void Complete(Dictionary<string, object> root)
                {
                    var fresh = Load();
                    if (fresh == null) return;
                    root = fresh;
                    try
                    {
                        if (Text(root, "phase") != "verifying")
                        {
                            var after = OcclusionToolSupport.ReadSnapshot();
                            if (!OcclusionToolSupport.SettingsEqual(after.Settings, Settings(root)))
                                throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion effective settings do not match terminal readback.");
                            root["snapshotAfter"] = OcclusionToolSupport.SnapshotDictionary(after);
                            var observedScenes = OcclusionToolSupport.SaveAndProveSceneFiles(SceneFiles(root), SceneContentProofs(root), false);
                            root["scenesResult"] = SceneObjects(observedScenes);
                            root["sceneContentProofsResult"] = SceneContentProofObjects(OcclusionToolSupport.CaptureSceneContentProofs(observedScenes));
                            root["phase"] = "verifying";
                            root["verifyNotBefore"] = DateTime.UtcNow.AddMilliseconds(250).ToString("O", CultureInfo.InvariantCulture);
                            Save(root);
                            return;
                        }
                        if (DateTimeOffset.UtcNow < Time(root, "verifyNotBefore")) return;
                        if (StaticOcclusionCulling.isRunning) throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion bake became active during terminal proof.");
                        var terminalSnapshot = OcclusionToolSupport.ReadSnapshot();
                        if (!OcclusionToolSupport.SettingsEqual(terminalSnapshot.Settings, Settings(root)))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion effective settings changed during terminal proof.");
                        try
                        {
                            OcclusionToolSupport.ValidateFrozenSceneFiles(SceneFiles(root, "scenesResult"), "occlusion.bake");
                            OcclusionToolSupport.ValidateFrozenSceneContentProofs(SceneContentProofs(root, "sceneContentProofsResult"));
                        }
                        catch (BridgeCommandException)
                        {
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion scene identity changed during terminal proof.");
                        }
                        root["snapshotAfter"] = OcclusionToolSupport.SnapshotDictionary(terminalSnapshot);
                        root["finishedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                        root["terminalState"] = "completed";
                        Succeed(root);
                    }
                    catch (BridgeCommandException exception)
                    {
                        if (Text(root, "terminalState") == "completed") { WriteLog("occlusion_terminal_publish_retry " + exception.Code); return; }
                        Fail(root, exception.Code, exception.Message);
                    }
                    catch (Exception exception)
                    {
                        if (Text(root, "terminalState") == "completed") { WriteLog("occlusion_terminal_publish_retry " + exception.GetType().Name); return; }
                        Fail(root, "E_EXECUTION_TIMEOUT", "Occlusion scene persistence proof failed: " + exception.GetType().Name + ".");
                    }
                }

                private object Cancel(CancelArgs args)
                {
                    if (args == null || string.IsNullOrWhiteSpace(args.OperationId))
                        throw new BridgeCommandException("E_SCHEMA_INVALID", "occlusion.bake-cancel requires the frozen operationId.");
                    var root = Load();
                    if (root == null) return new Dictionary<string, object> { { "cancelled", false }, { "status", PublicStatus() } };
                    if (!string.Equals(Text(root, "operationId"), args.OperationId, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_OCCLUSION_OPERATION_MISMATCH", "The active Occlusion bake does not match operationId.", "Read occlusion.bake-status and retry only for that exact operation.");
                    if (!UnityCliSessionMutationLease.IsOwnedBy(Text(root, "requestId"), Text(root, "requestDigest")))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion cancel cannot prove current root ownership; outcome is unknown.");
                    var phase = Text(root, "phase");
                    Dictionary<string, object> fresh;
                    if (phase == "queued")
                    {
                        if (!TryUpdateAndReload(value => { value["cancelRequested"] = true; value["cancelEffectState"] = "not-required"; }, out fresh))
                            throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion cancel could not confirm its queued root; outcome is unknown.");
                        FinishCancelled(fresh);
                        return new Dictionary<string, object> { { "cancelled", true }, { "operationId", args.OperationId }, { "status", PublicStatus() } };
                    }
                    return new Dictionary<string, object> { { "cancelled", false }, { "operationId", args.OperationId }, { "status", PublicStatus() } };
                }

                /// <summary>confirmed只证明本coordinator调用过Unity，不构成跨帧native request identity。</summary>
                private static bool HasDispatchProof(Dictionary<string, object> root)
                {
                    return Bool(root, "dispatchConfirmed");
                }

                /// <summary>只允许Generate返回后的同一主线程同步调用栈立即rollback，跨帧路径禁止调用。</summary>
                private static void CancelOwnedNativeBake() { StaticOcclusionCulling.Cancel(); }

                private void FinishCancelled(Dictionary<string, object> root)
                {
                    var fresh = Load();
                    if (fresh == null) return;
                    root = fresh;
                    root["finishedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    root["terminalState"] = "cancelled";
                    root["snapshotAfter"] = OcclusionToolSupport.SnapshotDictionary(OcclusionToolSupport.ReadSnapshot());
                    Fail(root, "E_OCCLUSION_BAKE_CANCELLED", "Occlusion bake was cancelled for operation " + Text(root, "operationId") + ".");
                }

                internal void WriteRunningStatus(Dictionary<string, object> root)
                {
                    var snapshot = OcclusionToolSupport.ReadSnapshot();
                    SaveStatus(new Dictionary<string, object>
                    {
                        { "editorSessionId", EditorSessionId() }, { "state", "running" }, { "active", true },
                        { "operationId", Text(root, "operationId") }, { "phase", PublicPhase(Text(root, "phase")) },
                        { "startedAt", Text(root, "startedAt") }, { "finishedAt", null },
                        { "durationMs", DurationMs(Text(root, "startedAt"), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)) },
                        { "progress", null }, { "targetCount", Long(root, "targetCount") },
                        { "umbraDataSizeBytes", snapshot.UmbraDataSizeBytes }, { "doesSceneHaveManualPortals", snapshot.DoesSceneHaveManualPortals },
                        { "settings", OcclusionToolSupport.SettingsDictionary(snapshot.Settings) },
                        { "rootRequestId", Text(root, "requestId") }, { "rootRequestDigest", Text(root, "requestDigest") },
                        { "dispatchConfirmed", HasDispatchProof(root) }
                    });
                }

                private void WriteTerminalStatus(Dictionary<string, object> root, string state, object envelope)
                {
                    var finishedAt = string.IsNullOrEmpty(Text(root, "finishedAt")) ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) : Text(root, "finishedAt");
                    var snapshot = root.ContainsKey("snapshotAfter") ? Snapshot(root, "snapshotAfter") : OcclusionToolSupport.ReadSnapshot();
                    SaveStatus(new Dictionary<string, object>
                    {
                        { "editorSessionId", EditorSessionId() }, { "state", state }, { "active", false },
                        { "operationId", Text(root, "operationId") }, { "phase", "terminal" },
                        { "startedAt", Text(root, "startedAt") }, { "finishedAt", finishedAt },
                        { "durationMs", DurationMs(Text(root, "startedAt"), finishedAt) }, { "progress", state == "completed" ? 1d : (object)null },
                        { "targetCount", Long(root, "targetCount") }, { "umbraDataSizeBytes", snapshot.UmbraDataSizeBytes },
                        { "doesSceneHaveManualPortals", snapshot.DoesSceneHaveManualPortals }, { "settings", OcclusionToolSupport.SettingsDictionary(snapshot.Settings) },
                        { "rootRequestId", Text(root, "requestId") }, { "rootRequestDigest", Text(root, "requestDigest") }, { "rootEnvelope", envelope }
                    });
                }

                /// <summary>公开status复用同一corruption recovery路径，不能直接解析并删除损坏root。</summary>
                internal Dictionary<string, object> LoadActiveForStatus()
                {
                    var root = Load();
                    if (root == null) ThrowIfActiveStateUnresolved();
                    return root;
                }

                /// <summary>idle缺少active文件是正常态；只有corrupt文件或仍有owner的missing root必须fail closed。</summary>
                private void ThrowIfActiveStateUnresolved()
                {
                    if (!LastLoadWasCorrupt && !LastLoadWasMissing) return;
                    var status = LoadStatus();
                    string ownerRequestId;
                    string ownerRequestDigest;
                    if (!UnityCliSessionMutationLease.TryGetCurrentOwner(out ownerRequestId, out ownerRequestDigest))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion lease ownership cannot be read; outcome is unknown.");
                    var statusMatchesOwner = !string.IsNullOrEmpty(ownerRequestId)
                        && string.Equals(Text(status, "rootRequestId"), ownerRequestId, StringComparison.Ordinal)
                        && string.Equals(Text(status, "rootRequestDigest"), ownerRequestDigest, StringComparison.Ordinal);
                    var unresolvedCorrupt = LastLoadWasCorrupt
                        && (UnityCliActiveOperation.Exists(ActiveScope) || status == null || Bool(status, "active") || (!string.IsNullOrEmpty(ownerRequestId) && !statusMatchesOwner));
                    var unresolvedMissing = LastLoadWasMissing && !string.IsNullOrEmpty(ownerRequestId)
                        && (status == null || Bool(status, "active") || !statusMatchesOwner);
                    if (unresolvedCorrupt || unresolvedMissing)
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion bake active state is corrupt; outcome is unknown.");
                }
            }

            internal static bool IsCommand(string command) { return Operation.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { Operation.Register(registry); }
            internal static Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return Operation.Execute(request, raw, accept, ct); }
            internal static void Initialize() { Operation.InitializeState(); }
            internal static void Shutdown() { }
            internal static void BeforeReload() { Operation.BeforeReload(); }
            internal static void Tick()
            {
                Operation.Tick();
                ClearBoundSessionOwnerIfReleased();
            }

            /// <summary>公开status只投影当前process session的bounded snapshot。</summary>
            internal static Dictionary<string, object> PublicStatus()
            {
                var active = Operation.LoadActiveForStatus();
                if (active != null && string.Equals(Text(active, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                {
                    var terminal = Operation.RecoveredStoredTerminal(Text(active, "requestId"), Text(active, "requestDigest"));
                    if (terminal != null) return PublicStatusFromStored(LoadStatus());
                    Operation.WriteRunningStatus(active);
                    return PublicStatusFromStored(LoadStatus());
                }
                var stored = LoadStatus();
                if (stored == null && StatusHistoryUnavailable())
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion bake terminal status is unavailable; outcome is unknown.");
                return PublicStatusFromStored(stored);
            }

            private static Dictionary<string, object> PublicStatusFromStored(Dictionary<string, object> stored)
            {
                if (stored == null || !string.Equals(Text(stored, "editorSessionId"), EditorSessionId(), StringComparison.Ordinal))
                {
                    var snapshot = OcclusionToolSupport.ReadSnapshot();
                    return new Dictionary<string, object>
                    {
                        { "state", "idle" }, { "active", false }, { "operationId", null }, { "phase", null },
                        { "startedAt", null }, { "finishedAt", null }, { "durationMs", 0L }, { "progress", null }, { "targetCount", 0L },
                        { "umbraDataSizeBytes", snapshot.UmbraDataSizeBytes }, { "doesSceneHaveManualPortals", snapshot.DoesSceneHaveManualPortals },
                        { "settings", OcclusionToolSupport.SettingsDictionary(snapshot.Settings) }
                    };
                }
                var result = new Dictionary<string, object>(stored, StringComparer.Ordinal);
                var fresh = OcclusionToolSupport.ReadSnapshot();
                result["umbraDataSizeBytes"] = fresh.UmbraDataSizeBytes;
                result["doesSceneHaveManualPortals"] = fresh.DoesSceneHaveManualPortals;
                result["settings"] = OcclusionToolSupport.SettingsDictionary(fresh.Settings);
                result.Remove("editorSessionId");
                result.Remove("rootRequestId");
                result.Remove("rootRequestDigest");
                result.Remove("rootEnvelope");
                result.Remove("dispatchConfirmed");
                result.Remove("statusProofSha256");
                return result;
            }

            private static Dictionary<string, object> LoadStatus()
            {
                try
                {
                    if (!UnityCliActiveOperation.Exists(StatusScope))
                    {
                        LastStatusLoadOutcome = StatusLoadOutcome.Missing;
                        return null;
                    }
                    var status = UnityCliActiveOperation.Load(StatusScope);
                    if (ValidateStatus(status))
                    {
                        LastStatusLoadOutcome = StatusLoadOutcome.Loaded;
                        return status;
                    }
                    LastStatusLoadOutcome = StatusLoadOutcome.Corrupt;
                    StatusJournalCorrupt = true;
                    WriteLog("occlusion_bake_status_corrupt semantic_validation_failed");
                    return null;
                }
                catch (Exception exception)
                {
                    LastStatusLoadOutcome = exception is IOException || exception is UnauthorizedAccessException
                        ? StatusLoadOutcome.TransientFailure : StatusLoadOutcome.Corrupt;
                    StatusJournalCorrupt = true;
                    WriteLog("occlusion_bake_status_corrupt " + exception.GetType().Name + ": " + exception.Message);
                    return null;
                }
            }

            /// <summary>可解析status仍须满足完整running/terminal schema，否则禁止exact replay重放effect。</summary>
            private static bool ValidateStatus(Dictionary<string, object> status)
            {
                string editorSessionId;
                string rootRequestId;
                string rootRequestDigest;
                string operationId;
                string startedAt;
                string stateText;
                string phaseText;
                string statusProofSha256;
                if (status == null || !TryNonEmptyString(status, "editorSessionId", out editorSessionId)
                    || !TryNonEmptyString(status, "rootRequestId", out rootRequestId)
                    || !TryNonEmptyString(status, "rootRequestDigest", out rootRequestDigest) || !IsSha256Text(rootRequestDigest)
                    || !TryNonEmptyString(status, "operationId", out operationId)
                    || !TryNonEmptyString(status, "startedAt", out startedAt) || !ValidateTimestamp(startedAt)
                    || !TryNonEmptyString(status, "state", out stateText)
                    || !TryNonEmptyString(status, "phase", out phaseText)
                    || !TryNonEmptyString(status, "statusProofSha256", out statusProofSha256) || !IsSha256Text(statusProofSha256)
                    || !string.Equals(statusProofSha256, StatusProofSha256(status), StringComparison.Ordinal)
                    || operationId != "occlusion-" + rootRequestDigest.Substring(0, 32)
                    || !ValidateStatusNumbers(status)
                    || !ValidateStatusSettings(status)) return false;
                object activeValue;
                object portalValue;
                if (!status.TryGetValue("active", out activeValue) || !(activeValue is bool)
                    || !status.TryGetValue("doesSceneHaveManualPortals", out portalValue) || !(portalValue is bool)
                    || !status.ContainsKey("progress") || !status.ContainsKey("finishedAt")) return false;
                if ((bool)activeValue)
                {
                    object finishedAt;
                    object progress;
                    object statusDispatchConfirmed;
                    return stateText == "running"
                        && (phaseText == "queued" || phaseText == "starting" || phaseText == "baking")
                        && status.TryGetValue("finishedAt", out finishedAt) && finishedAt == null
                        && status.TryGetValue("progress", out progress) && progress == null
                        && status.TryGetValue("dispatchConfirmed", out statusDispatchConfirmed) && statusDispatchConfirmed is bool
                        && !status.ContainsKey("rootEnvelope");
                }
                var state = stateText;
                string finishedAtText;
                if (phaseText != "terminal"
                    || (state != "completed" && state != "cancelled" && state != "failed")
                    || !TryNonEmptyString(status, "finishedAt", out finishedAtText) || !ValidateTimestamp(finishedAtText)
                    || Long(status, "durationMs") != DurationMs(startedAt, finishedAtText)) return false;
                object progressValue;
                if (!status.TryGetValue("progress", out progressValue)
                    || (state == "completed" ? !IsExactOne(progressValue) : progressValue != null)) return false;
                object rootEnvelopeValue;
                var rootEnvelope = status.TryGetValue("rootEnvelope", out rootEnvelopeValue) ? rootEnvelopeValue as IDictionary<string, object> : null;
                if (rootEnvelope == null) return false;
                object envelopeOk;
                string envelopeRequestId;
                if (!rootEnvelope.TryGetValue("ok", out envelopeOk) || !(envelopeOk is bool)
                    || !TryNonEmptyString(rootEnvelope, "requestId", out envelopeRequestId)
                    || !string.Equals(envelopeRequestId, rootRequestId, StringComparison.Ordinal)) return false;
                if ((bool)envelopeOk)
                {
                    object dataValue;
                    var data = rootEnvelope.TryGetValue("data", out dataValue) ? dataValue as IDictionary<string, object> : null;
                    return state == "completed" && !rootEnvelope.ContainsKey("error")
                        && ValidateTerminalData(status, data);
                }
                if (state == "completed" || rootEnvelope.ContainsKey("data")) return false;
                object errorValue;
                var error = rootEnvelope.TryGetValue("error", out errorValue) ? errorValue as IDictionary<string, object> : null;
                object transientValue;
                object nextActionsValue;
                string errorCode;
                string errorMessage;
                var nextActions = error != null && error.TryGetValue("nextActions", out nextActionsValue) ? nextActionsValue as IList<object> : null;
                return error != null && TryNonEmptyString(error, "code", out errorCode) && TryNonEmptyString(error, "message", out errorMessage)
                    && (state == "cancelled" ? errorCode == "E_OCCLUSION_BAKE_CANCELLED" : errorCode != "E_OCCLUSION_BAKE_CANCELLED")
                    && error.TryGetValue("transient", out transientValue) && transientValue is bool
                    && nextActions != null && nextActions.All(value => value is string)
                    && TryNonNegativeLong(error, "retryAfterMs");
            }

            private static bool ValidateStatusNumbers(IDictionary<string, object> status)
            {
                return TryNonNegativeLong(status, "durationMs")
                    && TryNonNegativeLong(status, "targetCount")
                    && TryNonNegativeLong(status, "umbraDataSizeBytes");
            }

            private static bool ValidateStatusSettings(IDictionary<string, object> status)
            {
                object settingsValue;
                var settings = status.TryGetValue("settings", out settingsValue) ? settingsValue as IDictionary<string, object> : null;
                return ValidateSettingsValues(settings);
            }

            private static bool ValidateTerminalData(IDictionary<string, object> status, IDictionary<string, object> data)
            {
                string dataStatus;
                string operationId;
                string startedAt;
                string finishedAt;
                long dataDuration;
                long statusDuration;
                if (data == null || !TryNonEmptyString(data, "status", out dataStatus) || dataStatus != "completed"
                    || !TryNonEmptyString(data, "operationId", out operationId) || operationId != Text(status, "operationId")
                    || !TryNonEmptyString(data, "startedAt", out startedAt) || startedAt != Text(status, "startedAt")
                    || !TryNonEmptyString(data, "finishedAt", out finishedAt) || finishedAt != Text(status, "finishedAt")
                    || !TryNonNegativeLong(data, "durationMs", out dataDuration)
                    || !TryNonNegativeLong(status, "durationMs", out statusDuration) || dataDuration != statusDuration) return false;
                object beforeValue;
                object afterValue;
                object settingsValue;
                object statusSettingsValue;
                object scenesValue;
                object effectsValue;
                var before = data.TryGetValue("before", out beforeValue) ? beforeValue as IDictionary<string, object> : null;
                var after = data.TryGetValue("after", out afterValue) ? afterValue as IDictionary<string, object> : null;
                var effectiveSettings = data.TryGetValue("effectiveSettings", out settingsValue) ? settingsValue as IDictionary<string, object> : null;
                var statusSettings = status.TryGetValue("settings", out statusSettingsValue) ? statusSettingsValue as IDictionary<string, object> : null;
                var scenes = data.TryGetValue("scenes", out scenesValue) ? scenesValue as IList<object> : null;
                var effects = data.TryGetValue("plannedEffects", out effectsValue) ? effectsValue as IList<object> : null;
                if (!ValidateSnapshotValues(before) || !ValidateSnapshotValues(after)
                    || !ValidateSettingsValues(effectiveSettings) || !SameSettingsValues(effectiveSettings, statusSettings)
                    || !SnapshotMatchesStatus(after, status)
                    || scenes == null || scenes.Count != Long(status, "targetCount")) return false;
                foreach (var sceneValue in scenes)
                {
                    var scene = sceneValue as IDictionary<string, object>;
                    object dirty;
                    if (scene == null || string.IsNullOrEmpty(Text(scene, "path")) || !IsHexText(Text(scene, "guid"), 32)
                        || !IsSha256Text(Text(scene, "sha256")) || !TryNonNegativeLong(scene, "sizeBytes")
                        || !scene.TryGetValue("dirty", out dirty) || !(dirty is bool) || (bool)dirty) return false;
                }
                if (effects == null || effects.Count != 1) return false;
                var effect = effects[0] as IDictionary<string, object>;
                return effect != null && Text(effect, "type") == "occlusion.bake" && Text(effect, "target") == "open-scenes"
                    && TryNonNegativeLong(effect, "targetCount") && Long(effect, "targetCount") == Long(status, "targetCount");
            }

            private static bool SameSnapshotValues(IDictionary<string, object> left, IDictionary<string, object> right)
            {
                object leftPortals;
                object rightPortals;
                object leftSettings;
                object rightSettings;
                return left != null && right != null
                    && Long(left, "umbraDataSizeBytes") == Long(right, "umbraDataSizeBytes")
                    && left.TryGetValue("doesSceneHaveManualPortals", out leftPortals) && leftPortals is bool
                    && right.TryGetValue("doesSceneHaveManualPortals", out rightPortals) && rightPortals is bool
                    && (bool)leftPortals == (bool)rightPortals
                    && left.TryGetValue("settings", out leftSettings) && right.TryGetValue("settings", out rightSettings)
                    && SameSettingsValues(leftSettings as IDictionary<string, object>, rightSettings as IDictionary<string, object>);
            }

            private static bool SnapshotMatchesStatus(IDictionary<string, object> snapshot, IDictionary<string, object> status)
            {
                object snapshotPortals;
                object statusPortals;
                object snapshotSettings;
                object statusSettings;
                return snapshot != null && Long(snapshot, "umbraDataSizeBytes") == Long(status, "umbraDataSizeBytes")
                    && snapshot.TryGetValue("doesSceneHaveManualPortals", out snapshotPortals) && snapshotPortals is bool
                    && status.TryGetValue("doesSceneHaveManualPortals", out statusPortals) && statusPortals is bool
                    && (bool)snapshotPortals == (bool)statusPortals
                    && snapshot.TryGetValue("settings", out snapshotSettings) && status.TryGetValue("settings", out statusSettings)
                    && SameSettingsValues(snapshotSettings as IDictionary<string, object>, statusSettings as IDictionary<string, object>);
            }

            private static bool ValidateSnapshotValues(IDictionary<string, object> snapshot)
            {
                object portals;
                object settingsValue;
                return snapshot != null && TryNonNegativeLong(snapshot, "umbraDataSizeBytes")
                    && snapshot.TryGetValue("doesSceneHaveManualPortals", out portals) && portals is bool
                    && snapshot.TryGetValue("settings", out settingsValue) && ValidateSettingsValues(settingsValue as IDictionary<string, object>);
            }

            private static bool ValidateSettingsValues(IDictionary<string, object> settings)
            {
                double smallestOccluder;
                double smallestHole;
                double backfaceThreshold;
                return settings != null
                    && TryFiniteNumber(settings, "smallestOccluder", out smallestOccluder) && smallestOccluder >= 0d
                    && TryFiniteNumber(settings, "smallestHole", out smallestHole) && smallestHole >= 0d
                    && TryFiniteNumber(settings, "backfaceThreshold", out backfaceThreshold) && backfaceThreshold >= 0d && backfaceThreshold <= 100d;
            }

            private static bool SameSettingsValues(IDictionary<string, object> left, IDictionary<string, object> right)
            {
                double leftOccluder;
                double leftHole;
                double leftBackface;
                double rightOccluder;
                double rightHole;
                double rightBackface;
                return TryFiniteNumber(left, "smallestOccluder", out leftOccluder)
                    && TryFiniteNumber(left, "smallestHole", out leftHole)
                    && TryFiniteNumber(left, "backfaceThreshold", out leftBackface)
                    && TryFiniteNumber(right, "smallestOccluder", out rightOccluder)
                    && TryFiniteNumber(right, "smallestHole", out rightHole)
                    && TryFiniteNumber(right, "backfaceThreshold", out rightBackface)
                    && leftOccluder.Equals(rightOccluder) && leftHole.Equals(rightHole) && leftBackface.Equals(rightBackface);
            }

            private static bool TryNonNegativeLong(IDictionary<string, object> value, string key)
            {
                long result;
                return TryNonNegativeLong(value, key, out result);
            }

            private static bool TryNonNegativeLong(IDictionary<string, object> value, string key, out long result)
            {
                object item;
                if (value != null && value.TryGetValue(key, out item) && item is long && (long)item >= 0L)
                {
                    result = (long)item;
                    return true;
                }
                result = 0L;
                return false;
            }

            private static bool TryFiniteNumber(IDictionary<string, object> value, string key, out double result)
            {
                object item;
                if (value == null || !value.TryGetValue(key, out item) || (item is long) == false && (item is double) == false)
                {
                    result = 0d;
                    return false;
                }
                result = item is long ? (double)(long)item : (double)item;
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }

            private static bool IsExactOne(object value)
            {
                return (value is long && (long)value == 1L) || (value is double && (double)value == 1d);
            }

            private static bool TryNonEmptyString(IDictionary<string, object> value, string key, out string result)
            {
                object item;
                if (value != null && value.TryGetValue(key, out item) && item is string && !string.IsNullOrEmpty((string)item))
                {
                    result = (string)item;
                    return true;
                }
                result = null;
                return false;
            }

            private static bool ValidateTimestamp(string value)
            {
                DateTimeOffset timestamp;
                return !string.IsNullOrEmpty(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
            }

            private static bool IsSha256Text(string value) { return IsHexText(value, 64); }

            private static bool IsHexText(string value, int length)
            {
                if (string.IsNullOrEmpty(value) || value.Length != length) return false;
                foreach (var character in value) if (!Uri.IsHexDigit(character)) return false;
                return true;
            }

            /// <summary>当前session已产生status却无法读取时，禁止把缺失记录解释为从未执行。</summary>
            private static bool StatusHistoryUnavailable()
            {
                return StatusJournalCorrupt
                    || (SessionState.GetBool(StatusWrittenThisSessionKey, false)
                        && !UnityCliActiveOperation.Exists(StatusScope));
            }

            /// <summary>active root的terminal envelope也用同一session key认证，外部文件修改不能被反向签发为status。</summary>
            private static string RootTerminalProofSha256(IDictionary<string, object> root)
            {
                var key = SessionState.GetString(StatusProofKeySessionKey, string.Empty);
                if (string.IsNullOrEmpty(key) || root == null) return string.Empty;
                var material = new Dictionary<string, object>(StringComparer.Ordinal);
                var keys = new[]
                {
                    "editorSessionId", "requestId", "requestDigest", "operationId", "phase", "startedAt", "finishedAt",
                    "targetCount", "snapshotAfter", "effectiveSettings", "dispatchConfirmed", "terminalState", "terminalEnvelope"
                };
                object value;
                foreach (var field in keys) if (root.TryGetValue(field, out value)) material[field] = value;
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
                {
                    var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(material)));
                    var builder = new StringBuilder(hash.Length * 2);
                    foreach (var item in hash) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                    return builder.ToString();
                }
            }

            /// <summary>验证active root terminal proof仍由当前Editor session签发且内容未变。</summary>
            private static bool RootTerminalProofMatches(IDictionary<string, object> root)
            {
                string proof;
                if (!TryNonEmptyString(root, "terminalProofSha256", out proof) || !IsSha256Text(proof)) return false;
                var expected = RootTerminalProofSha256(root);
                return !string.IsNullOrEmpty(expected) && string.Equals(proof, expected, StringComparison.Ordinal);
            }

            /// <summary>session-local HMAC把terminal proof绑定为一个整体；domain reload保留key，Editor重启作废status。</summary>
            private static string StatusProofSha256(IDictionary<string, object> status)
            {
                var key = SessionState.GetString(StatusProofKeySessionKey, string.Empty);
                if (string.IsNullOrEmpty(key) || status == null) return string.Empty;
                var material = new Dictionary<string, object>(StringComparer.Ordinal);
                var keys = new[]
                {
                    "editorSessionId", "state", "active", "operationId", "phase", "startedAt", "finishedAt", "durationMs", "progress",
                    "targetCount", "umbraDataSizeBytes", "doesSceneHaveManualPortals", "settings", "dispatchConfirmed",
                    "rootRequestId", "rootRequestDigest", "rootEnvelope"
                };
                object value;
                foreach (var field in keys) if (status.TryGetValue(field, out value)) material[field] = value;
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key)))
                {
                    var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(material)));
                    var builder = new StringBuilder(hash.Length * 2);
                    foreach (var item in hash) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                    return builder.ToString();
                }
            }

            private static string NewStatusProofKey()
            {
                return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            }

            /// <summary>root丢失时只允许Occlusion自身的exact session owner驱动保守恢复，禁止收割其他coordinator的全局lease。</summary>
            private static void BindSessionOwner(string requestId, string requestDigest)
            {
                if (string.IsNullOrWhiteSpace(requestId) || !IsSha256Text(requestDigest))
                    throw new InvalidOperationException("Occlusion session owner identity is invalid.");
                SessionState.SetString(OwnerRequestIdSessionKey, requestId);
                SessionState.SetString(OwnerRequestDigestSessionKey, requestDigest);
            }

            private static bool IsBoundSessionOwner(string requestId, string requestDigest)
            {
                return !string.IsNullOrWhiteSpace(requestId) && IsSha256Text(requestDigest)
                    && string.Equals(SessionState.GetString(OwnerRequestIdSessionKey, string.Empty), requestId, StringComparison.Ordinal)
                    && string.Equals(SessionState.GetString(OwnerRequestDigestSessionKey, string.Empty), requestDigest, StringComparison.Ordinal)
                    && UnityCliSessionMutationLease.IsOwnedBy(requestId, requestDigest);
            }

            /// <summary>首次status失败先写入domain-reload稳定的exact abort binding，再尝试文件cleanup。</summary>
            private static void BindPreEffectAbort(Dictionary<string, object> root)
            {
                var requestId = Text(root, "requestId");
                var requestDigest = Text(root, "requestDigest");
                if (string.IsNullOrWhiteSpace(requestId) || !IsSha256Text(requestDigest))
                    throw new InvalidOperationException("Occlusion pre-effect abort identity is invalid.");
                SessionState.SetString(PreEffectAbortRequestIdSessionKey, requestId);
                SessionState.SetString(PreEffectAbortRequestDigestSessionKey, requestDigest);
            }

            /// <summary>检查effect前放弃绑定与待清理的exact request identity一致。</summary>
            private static bool IsBoundPreEffectAbort(string requestId, string requestDigest)
            {
                return !string.IsNullOrWhiteSpace(requestId) && IsSha256Text(requestDigest)
                    && string.Equals(SessionState.GetString(PreEffectAbortRequestIdSessionKey, string.Empty), requestId, StringComparison.Ordinal)
                    && string.Equals(SessionState.GetString(PreEffectAbortRequestDigestSessionKey, string.Empty), requestDigest, StringComparison.Ordinal);
            }

            /// <summary>exact lease释放后清除本session的effect前放弃绑定。</summary>
            private static void ClearPreEffectAbortBinding()
            {
                SessionState.SetString(PreEffectAbortRequestIdSessionKey, string.Empty);
                SessionState.SetString(PreEffectAbortRequestDigestSessionKey, string.Empty);
            }

            private static void ClearBoundSessionOwnerIfReleased()
            {
                var requestId = SessionState.GetString(OwnerRequestIdSessionKey, string.Empty);
                var requestDigest = SessionState.GetString(OwnerRequestDigestSessionKey, string.Empty);
                var abortRequestId = SessionState.GetString(PreEffectAbortRequestIdSessionKey, string.Empty);
                var abortRequestDigest = SessionState.GetString(PreEffectAbortRequestDigestSessionKey, string.Empty);
                if ((!string.IsNullOrEmpty(requestId) || !string.IsNullOrEmpty(requestDigest))
                    && UnityCliSessionMutationLease.IsOwnedBy(requestId, requestDigest)) return;
                if ((!string.IsNullOrEmpty(abortRequestId) || !string.IsNullOrEmpty(abortRequestDigest))
                    && UnityCliSessionMutationLease.IsOwnedBy(abortRequestId, abortRequestDigest)) return;
                SessionState.SetString(OwnerRequestIdSessionKey, string.Empty);
                SessionState.SetString(OwnerRequestDigestSessionKey, string.Empty);
                ClearPreEffectAbortBinding();
            }

            private static void SaveStatus(Dictionary<string, object> status)
            {
                status.Remove("statusProofSha256");
                status["statusProofSha256"] = StatusProofSha256(status);
                UnityCliActiveOperation.Save(StatusScope, status);
                LastStatusLoadOutcome = StatusLoadOutcome.Loaded;
                StatusJournalCorrupt = false;
                SessionState.SetBool(StatusWrittenThisSessionKey, true);
            }

            private static List<OcclusionSceneFileData> SceneFiles(Dictionary<string, object> root) { return SceneFiles(root, "scenes"); }

            private static List<OcclusionSceneFileData> SceneFiles(Dictionary<string, object> root, string key)
            {
                var result = new List<OcclusionSceneFileData>();
                object raw;
                var values = root != null && root.TryGetValue(key, out raw) ? raw as IEnumerable : null;
                if (values != null) foreach (var value in values)
                {
                    var item = value as IDictionary<string, object>;
                    if (item == null) continue;
                    result.Add(new OcclusionSceneFileData
                    {
                        Path = Text(item, "path"), Guid = Text(item, "guid"), Sha256 = Text(item, "sha256"),
                        SizeBytes = Long(item, "sizeBytes"), Dirty = Bool(item, "dirty")
                    });
                }
                return result;
            }

            private static List<object> SceneObjects(IEnumerable<OcclusionSceneFileData> scenes) { return scenes.Select(value => (object)OcclusionToolSupport.SceneDictionary(value)).ToList(); }

            private static List<OcclusionSceneContentProofData> SceneContentProofs(Dictionary<string, object> root) { return SceneContentProofs(root, "sceneContentProofs"); }

            private static List<OcclusionSceneContentProofData> SceneContentProofs(Dictionary<string, object> root, string key)
            {
                var result = new List<OcclusionSceneContentProofData>();
                object raw;
                var values = root != null && root.TryGetValue(key, out raw) ? raw as IEnumerable : null;
                if (values != null) foreach (var value in values)
                {
                    var item = value as IDictionary<string, object>;
                    if (item != null) result.Add(new OcclusionSceneContentProofData { Path = Text(item, "path"), ContentSha256 = Text(item, "contentSha256") });
                }
                return result;
            }

            private static List<object> SceneContentProofObjects(IEnumerable<OcclusionSceneContentProofData> proofs)
            {
                return proofs.Select(value => (object)OcclusionToolSupport.SceneContentProofDictionary(value)).ToList();
            }

            private static OcclusionBakeSettingsData Settings(Dictionary<string, object> root)
            {
                var value = root["effectiveSettings"] as IDictionary<string, object>;
                return new OcclusionBakeSettingsData
                {
                    SmallestOccluder = Number(value, "smallestOccluder"),
                    SmallestHole = Number(value, "smallestHole"),
                    BackfaceThreshold = Number(value, "backfaceThreshold")
                };
            }

            private static OcclusionBakeSnapshotData Snapshot(Dictionary<string, object> root, string key)
            {
                var value = root[key] as IDictionary<string, object>;
                var settings = value == null ? null : value["settings"] as IDictionary<string, object>;
                return new OcclusionBakeSnapshotData
                {
                    UmbraDataSizeBytes = Long(value, "umbraDataSizeBytes"),
                    DoesSceneHaveManualPortals = Bool(value, "doesSceneHaveManualPortals"),
                    Settings = new OcclusionBakeSettingsData
                    {
                        SmallestOccluder = Number(settings, "smallestOccluder"), SmallestHole = Number(settings, "smallestHole"),
                        BackfaceThreshold = Number(settings, "backfaceThreshold")
                    }
                };
            }

            private static Dictionary<string, object> PlannedEffect(long targetCount)
            {
                return new Dictionary<string, object> { { "type", "occlusion.bake" }, { "target", "open-scenes" }, { "targetCount", targetCount } };
            }

            private static string PublicPhase(string phase) { return phase == "queued" ? "queued" : phase == "dispatching" ? "starting" : phase == "running" || phase == "verifying" ? "baking" : "unknown"; }
            private static long DurationMs(string start, string finish) { DateTimeOffset a; DateTimeOffset b; return DateTimeOffset.TryParse(start, out a) && DateTimeOffset.TryParse(finish, out b) ? (long)Math.Max(0, (b - a).TotalMilliseconds) : 0L; }
            private static DateTimeOffset Time(IDictionary<string, object> value, string key) { DateTimeOffset result; return DateTimeOffset.TryParse(Text(value, key), out result) ? result : DateTimeOffset.MinValue; }
            private static string Text(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : string.Empty; }
            private static bool Bool(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) && item is bool && (bool)item; }
            private static long Long(IDictionary<string, object> value, string key) { long result; return long.TryParse(Text(value, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0L; }
            private static double Number(IDictionary<string, object> value, string key) { double result; return double.TryParse(Text(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : 0d; }
        }
    }
}
#endif
