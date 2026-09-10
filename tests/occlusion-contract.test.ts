import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { cancelOcclusionBake } from "../src/tools/occlusion/live.js";

const read = (path: string): string => readFileSync(path, "utf8");
const coordinator = read("unitypkg/Editor/Infrastructure/UnityCliOcclusionBakeCoordinator.cs");
const reloadOperation = read("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs");
const mutationLease = read("unitypkg/Editor/Infrastructure/UnityCliSessionMutationLease.cs");
const support = read("unitypkg/Editor/Tools/occlusion/OcclusionToolSupport.cs");
const clear = read("unitypkg/Editor/Tools/occlusion/bake-clear.cs");
const bake = read("src/tools/occlusion/bake.ts");
const cancel = read("src/tools/occlusion/bake-cancel.ts");
const status = read("src/tools/occlusion/bake-status.ts");
const live = read("src/tools/occlusion/live.ts");
const sessionSupervisor = read("src/bridge/session-supervisor.ts");
const dispatcher = read("src/tools/dispatcher.ts");
const completion = JSON.parse(read("contracts/tool-completion-contract.json")) as { tools: Array<{ toolName: string }> };

const names = ["occlusion.bake", "occlusion.bake-cancel", "occlusion.bake-clear", "occlusion.bake-status"];

test("Occlusion只公开四个批准的canonical Tool", () => {
  const source = bake + cancel + clear + status;
  for (const name of names) {
    assert.match(source, new RegExp(`(?:name:|Name =) "${name.replaceAll(".", "\\.")}"`));
    assert.ok(completion.tools.some((entry) => entry.toolName === name), `completion contract missing ${name}`);
  }
  assert.match(bake, /supportsDryRun: true/);
  assert.match(clear, /SupportsDryRun = true/);
  assert.match(status, /destructiveHint: false/);
  assert.doesNotMatch(source, /bake_occlusion_culling|cancel_occlusion_bake|clear_occlusion_culling|occlusion_bake_status/);
});

test("Occlusion clear通过唯一project-tool bridge spec路由", () => {
  const spec = bridgeCommandRegistry.lookup("occlusion", "bake-clear");
  assert.ok(spec);
  assert.equal(spec?.bridgeCommand, "catalog.editor.run");
  assert.equal(spec?.toolName, "occlusion.bake-clear");
  assert.equal(spec?.supportsDryRun, true);
});

test("Occlusion bake使用独立session-scoped durable root", () => {
  assert.match(coordinator, /ActiveScope = "occlusion-bake"/);
  assert.match(coordinator, /StatusScope = "occlusion-bake-status"/);
  assert.match(coordinator, /internal\.occlusion\.bake\.start/);
  assert.match(coordinator, /internal\.occlusion\.bake\.status/);
  assert.match(coordinator, /internal\.occlusion\.bake\.snapshot/);
  assert.match(coordinator, /internal\.occlusion\.bake\.cancel/);
  assert.doesNotMatch(coordinator, /navmesh-bake|UnityCliNavMeshBakeCoordinator/);
  assert.match(coordinator, /UnityCliSessionMutationLease\.TryAcquire/);
  assert.match(coordinator, /editorSessionId/);
  assert.match(coordinator, /dispatchNotBefore/);
});

test("Occlusion effect冻结settings和clean scene后只dispatch一次", () => {
  assert.match(coordinator, /PrepareSettings\(args\.SmallestOccluder, args\.SmallestHole, args\.BackfaceThreshold\)/);
  assert.match(coordinator, /PrepareLoadedSceneFiles\("occlusion\.bake"\)/);
  assert.equal((coordinator.match(/StaticOcclusionCulling\.GenerateInBackground\(\)/g) ?? []).length, 1);
  assert.match(coordinator, /\["phase"\] = "dispatching"/);
  assert.match(coordinator, /dispatchNonce/);
  assert.match(coordinator, /dispatch proof不足|dispatch crossed a reload without enough proof/);
  assert.match(coordinator, /RecoverReload\(root, phase\)/);
  assert.match(coordinator, /crossed a reload without enough business proof/);
  assert.doesNotMatch(coordinator + support, /AnySceneDirty/);
  assert.match(coordinator, /EditorApplication\.isCompiling \|\| EditorApplication\.isUpdating/);
  assert.match(coordinator, /OcclusionToolSupport\.EnsureEditorReady\("occlusion\.bake", true\)/);
  assert.match(coordinator, /ValidateFrozenSceneFiles\(SceneFiles\(root\), "occlusion\.bake"\)/);
  assert.match(support, /left\.Guid, right\.Guid/);
  assert.match(support, /left\.Sha256, right\.Sha256/);
  assert.match(coordinator, /E_OCCLUSION_TARGET_CHANGED/);
  assert.match(coordinator, /SaveAndProveSceneFiles\(SceneFiles\(root\), SceneContentProofs\(root\), false\)/);
  assert.match(coordinator, /ValidateFrozenSceneFiles\(SceneFiles\(root, "scenesResult"\), "occlusion\.bake"\)/);
  assert.match(coordinator, /Occlusion effective settings changed during terminal proof/);
  assert.match(coordinator, /RecoveredStoredTerminal/);
  assert.match(coordinator, /rootRequestId/);
  assert.match(coordinator, /rootRequestDigest/);
  assert.match(coordinator, /E_REQUEST_ID_REUSED/);
  assert.match(coordinator, /rootEnvelope/);
  assert.match(coordinator, /result\.Remove\("rootEnvelope"\)/);
  assert.match(support, /\(double\)\(float\)result\.SmallestOccluder/);
  assert.match(coordinator, /SettingsEqual/);
  assert.match(dispatcher, /toolName === "occlusion\.bake"/);
  assert.match(dispatcher, /validOcclusionSnapshot/);
  assert.match(dispatcher, /sameOcclusionSettings/);
});

test("Occlusion损坏active journal只生成durable outcome-unknown且禁止同request重建root", () => {
  assert.match(reloadOperation, /LastLoadWasCorrupt/);
  assert.match(reloadOperation, /OnActiveStateCorrupt\(Exception exception\)/);
  assert.match(reloadOperation, /OnActiveStateCorrupt\(exception\)[\s\S]*terminalStored && ownerReadable[\s\S]*BeginTerminalCleanup\(ownerRequestId, ownerRequestDigest\)/);
  assert.match(mutationLease, /TryGetCurrentOwner\(out string requestId, out string requestDigest\)/);
  assert.doesNotMatch(mutationLease, /CurrentOwnerRequest(?:Id|Digest)/);
  assert.match(coordinator, /protected override bool OnActiveStateCorrupt\(Exception exception\)/);
  assert.match(coordinator, /Occlusion bake active state is corrupt; outcome is unknown/);
  const corruptRecovery = coordinator.match(/private bool RecoverUnavailableActiveState[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.match(corruptRecovery, /if \(StaticOcclusionCulling\.isRunning\) return false/);
  assert.ok(corruptRecovery.indexOf("if (StaticOcclusionCulling.isRunning) return false") < corruptRecovery.indexOf("existingRootEnvelope"));
  assert.doesNotMatch(corruptRecovery, /CancelOwnedNativeBake/);
  assert.doesNotMatch(coordinator.match(/protected override bool OnActiveStateCorrupt[\s\S]*?\n                \}/)?.[0] ?? "", /UnityCliSessionMutationLease\.Release/);
  assert.match(reloadOperation, /TerminalCleanupScope/);
  assert.match(reloadOperation, /BeginTerminalCleanup/);
  assert.match(reloadOperation, /RetryTerminalCleanup/);
  assert.match(coordinator, /rootRequestId.*Text\(root, "requestId"\)/s);
  assert.match(coordinator, /rootRequestDigest.*Text\(root, "requestDigest"\)/s);
  assert.match(coordinator, /if \(LastLoadWasCorrupt \|\| LastLoadWasUnreadable \|\| LastLoadRecoveredTerminal\)[\s\S]*RecoveredStoredTerminal\(request\.RequestId, digest\)[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.ok(coordinator.indexOf("if (LastLoadWasCorrupt || LastLoadRecoveredTerminal)") < coordinator.indexOf("var candidate = CreateRoot"));
  assert.match(coordinator, /Operation\.LoadActiveForStatus\(\)/);
});

test("Occlusion terminal status损坏或缺失在当前Editor session内fail closed", () => {
  assert.match(coordinator, /StatusSessionInitializedKey/);
  assert.match(coordinator, /SessionState\.GetBool\(StatusSessionInitializedKey/);
  assert.match(coordinator, /UnityCliActiveOperation\.Clear\(StatusScope\)[\s\S]*UnityCliActiveOperation\.Clear\(ActiveScope\)[\s\S]*UnityCliActiveOperation\.Clear\(ActiveScope \+ "-terminal-cleanup"\)[\s\S]*SessionState\.SetBool\(StatusSessionInitializedKey, true\)/);
  assert.match(coordinator, /StatusJournalCorrupt = true/);
  assert.match(coordinator, /LoadStatus\(\)[\s\S]*ValidateStatus\(status\)[\s\S]*StatusJournalCorrupt = true/);
  assert.match(coordinator, /ValidateStatus[\s\S]*TryNonEmptyString\(status, "rootRequestDigest"[\s\S]*IsSha256Text\(rootRequestDigest\)[\s\S]*ValidateStatusNumbers[\s\S]*ValidateStatusSettings[\s\S]*ValidateTimestamp[\s\S]*rootEnvelope[\s\S]*envelopeRequestId[\s\S]*envelopeOk/);
  assert.match(coordinator, /ValidateStatusNumbers[\s\S]*TryNonNegativeLong[\s\S]*durationMs[\s\S]*targetCount[\s\S]*umbraDataSizeBytes/);
  assert.match(coordinator, /ValidateStatusSettings[\s\S]*smallestOccluder[\s\S]*smallestHole[\s\S]*backfaceThreshold/);
  assert.match(coordinator, /\(bool\)envelopeOk[\s\S]*state == "completed"[\s\S]*!rootEnvelope\.ContainsKey\("error"\)[\s\S]*ValidateTerminalData\(status, data\)/);
  assert.match(coordinator, /ValidateTerminalData[\s\S]*operationId[\s\S]*startedAt[\s\S]*finishedAt[\s\S]*plannedEffects/);
  assert.match(coordinator, /ValidateTerminalData[\s\S]*ValidateSnapshotValues[\s\S]*SnapshotMatchesStatus[\s\S]*effects\.Count != 1/);
  assert.match(coordinator, /TerminalDataMatchesRoot[\s\S]*SameSnapshotValues\(before, rootBefore\)[\s\S]*SameSettingsValues\(settings, rootSettings\)[\s\S]*SameSceneLists\(scenes, RootList\(root, "scenesResult"\)\)[\s\S]*DurationMs/);
  assert.match(coordinator, /terminalState == "completed"[\s\S]*phase != "verifying"[\s\S]*status == null\) return false[\s\S]*Text\(status, "phase"\) != PublicPhase\(phase\)/);
  assert.match(coordinator, /StatusProofSha256[\s\S]*HMACSHA256/);
  assert.match(coordinator, /statusProofSha256[\s\S]*string\.Equals\(statusProofSha256, StatusProofSha256\(status\)/);
  assert.match(coordinator, /operationId != "occlusion-" \+ rootRequestDigest\.Substring\(0, 32\)/);
  assert.match(coordinator, /Long\(status, "durationMs"\) != DurationMs\(startedAt, finishedAtText\)/);
  assert.match(coordinator, /PublicStatusFromStored[\s\S]*result\.Remove\("statusProofSha256"\)/);
  assert.match(coordinator, /state == "completed" \|\| rootEnvelope\.ContainsKey\("data"\)[\s\S]*nextActionsValue as IList<object>[\s\S]*nextActions\.All\(value => value is string\)/);
  assert.doesNotMatch(coordinator, /RecoverUnavailableActiveState[\s\S]*status == null && StatusHistoryUnavailable\(\)[\s\S]*return false/);
  assert.match(coordinator, /RecoverUnavailableActiveState[\s\S]*if \(!ValidateTimestamp\(startedAt\)\) startedAt = finishedAt[\s\S]*"operationId", "occlusion-" \+ requestDigest\.Substring\(0, 32\)/);
  assert.match(coordinator, /WriteRunningStatus\(candidate\)[\s\S]*BindPreEffectAbort\(candidate\)[\s\S]*BeginPreEffectCleanup\(candidate\)/);
  assert.doesNotMatch(coordinator, /WriteRunningStatus\(candidate\)[\s\S]{0,500}Fail\(candidate, "E_EXECUTION_TIMEOUT", "Occlusion bake status could not be stored before dispatch\."\)/);
  assert.match(reloadOperation, /PreEffectCleanupScope[\s\S]*Scope \+ "-pre-effect-cleanup"/);
  assert.match(reloadOperation, /BeginPreEffectCleanup\(Dictionary<string, object> root\)[\s\S]*RetryPreEffectCleanup/);
  assert.match(coordinator, /CanCleanupAbandonedPreEffectRoot[\s\S]*IsBoundPreEffectAbort[\s\S]*phase == "queued"[\s\S]*phase == "dispatching"[\s\S]*dispatchNonce/);
  assert.match(coordinator, /FinalizeAbandonedPreEffectState[\s\S]*StatusLoadOutcome\.Missing[\s\S]*!StatusJournalCorrupt[\s\S]*!SessionState\.GetBool\(StatusWrittenThisSessionKey, false\)[\s\S]*rootRequestId[\s\S]*rootRequestDigest[\s\S]*dispatchConfirmed[\s\S]*UnityCliActiveOperation\.Clear\(StatusScope\)/);
  assert.match(reloadOperation, /FinalizeAbandonedPreEffectState\(requestId, requestDigest\)[\s\S]*Release\(requestId, requestDigest\)/);
  assert.match(coordinator, /PreEffectAbortRequestIdSessionKey[\s\S]*PreEffectAbortRequestDigestSessionKey/);
  assert.match(coordinator, /InitializeState[\s\S]*ResumeBoundPreEffectAbort\(\)[\s\S]*var active = Load\(\)/);
  assert.doesNotMatch(coordinator, /catch\s*\{\s*UnityCliActiveOperation\.Clear\(StatusScope\);\s*return null;\s*\}/);
  assert.match(coordinator, /StatusWrittenThisSessionKey/);
  assert.match(coordinator, /StatusHistoryUnavailable\(\)[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(coordinator, /SaveStatus\([\s\S]*StatusJournalCorrupt = false/);
  assert.match(reloadOperation, /lastTerminalRequestDigest/);
  assert.match(reloadOperation, /RecoveredTerminal\(string requestId, string requestDigest\)/);
  assert.match(reloadOperation, /E_REQUEST_ID_REUSED/);
  assert.ok(reloadOperation.indexOf("UnityCliRequestDigest.Compute") < reloadOperation.indexOf("RecoveredTerminal(request.RequestId, digest)"));
  assert.match(coordinator, /RecoveredTerminal\(request\.RequestId, digest\)/);
});

test("Occlusion deadline、reload和cancel只在native停止且durable status写入后发布终态", () => {
  assert.match(reloadOperation, /OnTerminal\(root, envelope\)[\s\S]*unityCliTerminalEnvelope[\s\S]*StoreTerminalFirst\(Scope, requestId, requestDigest, envelope\)[\s\S]*BeginTerminalCleanup\(requestId, requestDigest\)/);
  const recoverDispatch = coordinator.match(/private void RecoverDispatch[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.doesNotMatch(recoverDispatch, /dispatchConfirmed|dispatchRecovered|WriteRunningStatus/);
  const onTerminal = coordinator.match(/protected override void OnTerminal[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.match(onTerminal, /!succeeded && HasPossibleNativeEffect\(root\) && StaticOcclusionCulling\.isRunning[\s\S]*throw new InvalidOperationException/);
  assert.doesNotMatch(onTerminal, /CancelOwnedNativeBake/);
  assert.match(coordinator, /occlusion_bake_status_write_failed[\s\S]*throw;/);
  assert.doesNotMatch(coordinator, /occlusion_bake_status_write_failed[^\n]*\}\s*$/m);
});

test("Occlusion root reload失败不使用stale对象且terminal cleanup不可改写已发布结果", () => {
  assert.doesNotMatch(coordinator, /Load\(\) \?\? root/);
  assert.match(reloadOperation, /ValidateLoadedRoot\(root\)[\s\S]*return root/);
  assert.match(coordinator, /ValidateLoadedRoot\(Dictionary<string, object> root\)[\s\S]*ValidateOcclusionRoot\(root\)/);
  assert.match(coordinator, /ValidateOcclusionRoot[\s\S]*dispatchNonce[\s\S]*dispatchConfirmed[\s\S]*phase == "queued"[\s\S]*phase == "dispatching"[\s\S]*phase == "running" \|\| phase == "verifying"/);
  assert.match(coordinator, /ValidateOcclusionRoot[\s\S]*RootList\(root, "scenes"\)[\s\S]*sceneContentProofs[\s\S]*snapshotBefore[\s\S]*rootRequestDigest/);
  assert.match(reloadOperation, /var root = Load\(\)[\s\S]*!string\.Equals\(Text\(root, "requestId"\), request\.RequestId, StringComparison\.Ordinal\)\) return Busy[\s\S]*TryRecoverExact\(request\.RequestId, digest/);
  assert.match(coordinator, /var root = Load\(\)[\s\S]*!string\.Equals\(Text\(root, "requestId"\), request\.RequestId, StringComparison\.Ordinal\)\) return Busy[\s\S]*TryRecoverExact\(request\.RequestId, digest/);
  assert.match(reloadOperation, /internal bool Update\(Action<Dictionary<string, object>> action\)/);
  assert.match(coordinator, /TryUpdateAndReload[\s\S]*if \(!Update\(action\)\) return false[\s\S]*UnityCliActiveOperation\.Load\(ActiveScope\)[\s\S]*IsOwnedBy[\s\S]*WriteRunningStatus\(updated\)[\s\S]*root = Load\(\)[\s\S]*return root != null/);
  assert.match(coordinator, /TryUpdateAndReload[\s\S]*AbortBeforeNativeEffect\(preEffectRoot[\s\S]*var nativeDispatched[\s\S]*var nativeAccepted = StaticOcclusionCulling\.GenerateInBackground\(\)[\s\S]*if \(!nativeAccepted\)[\s\S]*E_EXECUTION_TIMEOUT[\s\S]*nativeDispatched = true[\s\S]*if \(ConfirmDispatch\(\)\)/);
  assert.match(coordinator, /private bool ConfirmDispatch\(\)[\s\S]*root\["phase"\] = "running"[\s\S]*root\["dispatchConfirmed"\] = true[\s\S]*Save\(root\)[\s\S]*WriteRunningStatus\(root\)/);
  assert.match(coordinator, /HasDispatchProof/);
  assert.match(coordinator, /dispatchNonce/);
  const dispatchProof = coordinator.match(/private static bool HasDispatchProof[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.match(dispatchProof, /Bool\(root, "dispatchConfirmed"\)/);
  assert.doesNotMatch(dispatchProof, /dispatchNonce|phase|isRunning/);
  const dispatch = coordinator.match(/private void Dispatch[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.ok(dispatch.indexOf("if (StaticOcclusionCulling.isRunning)") < dispatch.indexOf('value["phase"] = "dispatching"'));
  assert.match(coordinator, /"dispatchConfirmed"/);
  assert.doesNotMatch(coordinator, /"dispatchOwned"/);
  assert.match(coordinator, /RecoverUnavailableActiveState[\s\S]*!Bool\(status, "active"\)[\s\S]*status\.TryGetValue\("rootEnvelope"[\s\S]*return true[\s\S]*WriteTerminalStatus/);
  const unavailableRecovery = coordinator.match(/private bool RecoverUnavailableActiveState[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.match(unavailableRecovery, /if \(StaticOcclusionCulling\.isRunning\) return false/);
  assert.doesNotMatch(unavailableRecovery, /CancelOwnedNativeBake/);
  assert.match(reloadOperation, /TerminalCleanupScope[\s\S]*Scope \+ "-terminal-cleanup"/);
  assert.match(reloadOperation, /RetryTerminalCleanup\(\)[\s\S]*EvaluateTerminalCleanupRoot\(active, requestId, requestDigest\)[\s\S]*TerminalCleanupDecision\.Retry[\s\S]*UnityCliActiveOperation\.Clear\(Scope\)[\s\S]*UnityCliSessionMutationLease\.Release\(requestId, requestDigest\)/);
  assert.match(coordinator, /EvaluateTerminalCleanupRoot[\s\S]*terminalState[\s\S]*TryRestoreTerminalStatus[\s\S]*LoadStatus[\s\S]*TerminalStatusMatchesIdentity/);
  assert.match(coordinator, /EvaluateUnreadableTerminalCleanup[\s\S]*status == null[\s\S]*TerminalCleanupDecision\.Retry/);
  assert.match(reloadOperation, /terminal active root read failed[\s\S]*EvaluateUnreadableTerminalCleanup\(requestId, requestDigest\)/);
  assert.match(coordinator, /EvaluateTerminalCleanupRoot[\s\S]*TryRestoreTerminalStatus[\s\S]*EvaluateUnreadableTerminalCleanup[\s\S]*TerminalStatusMatchesIdentity/);
  assert.match(reloadOperation, /internal virtual void Tick\(\)[\s\S]*if \(RetryPreEffectCleanup\(\)\) return;[\s\S]*if \(RetryTerminalCleanup\(\)\) return;[\s\S]*TryResumePublishedTerminal\(root\)[\s\S]*deadline/);
  const retryCleanup = reloadOperation.slice(reloadOperation.indexOf("private bool RetryTerminalCleanup"), reloadOperation.indexOf("internal bool Update"));
  assert.match(retryCleanup, /TerminalCleanupDecision\.Retry\) return true/);
  assert.match(coordinator, /ThrowIfActiveStateUnresolved\(\)[\s\S]*unresolvedMissing[\s\S]*!string\.IsNullOrEmpty\(ownerRequestId\)[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(coordinator, /LoadActiveForStatus\(\)[\s\S]*ThrowIfActiveStateUnresolved\(\)/);
  assert.match(coordinator, /protected override object Status[\s\S]*ThrowIfActiveStateUnresolved\(\)/);
  assert.match(coordinator, /protected override bool OnActiveStateMissing\(\)[\s\S]*IsBoundSessionOwner[\s\S]*RecoverUnavailableActiveState/);
  assert.match(coordinator, /if \(accept == null \|\| !accept\(\)\)[\s\S]*BindSessionOwner\(request\.RequestId, digest\)[\s\S]*Save\(candidate\)/);
  assert.ok(coordinator.indexOf("var root = Load();") < coordinator.indexOf("BindSessionOwner(request.RequestId, digest);"));
  assert.match(coordinator, /IsBoundSessionOwner[\s\S]*OwnerRequestIdSessionKey[\s\S]*OwnerRequestDigestSessionKey[\s\S]*UnityCliSessionMutationLease\.IsOwnedBy/);
  assert.match(coordinator, /internal static void Tick\(\)[\s\S]*Operation\.Tick\(\)[\s\S]*ClearBoundSessionOwnerIfReleased/);
  assert.match(coordinator, /RecoveredStoredTerminal[\s\S]*ResumeTerminalCleanup\(new Dictionary<string, object>/);
  assert.match(coordinator, /statusRequestId[\s\S]*statusRequestDigest[\s\S]*return false[\s\S]*StaticOcclusionCulling\.isRunning/);
  const initializeState = coordinator.match(/internal void InitializeState[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.ok(initializeState.indexOf("TryResumePublishedTerminal(active)") < initializeState.indexOf("RecoveredStoredTerminal"));
  assert.ok(initializeState.indexOf("RecoveredStoredTerminal") < initializeState.indexOf("WriteRunningStatus(active)"));
  assert.match(initializeState, /terminal != null[\s\S]*ResumeTerminalCleanup\(active\)/);
  assert.doesNotMatch(initializeState, /terminal != null[\s\S]*Terminal\(active/);
  assert.match(coordinator, /TryGetCurrentOwner\(out currentOwnerRequestId, out currentOwnerRequestDigest\)[\s\S]*E_REQUEST_ID_REUSED[\s\S]*TryAcquire\(request\.RequestId, digest/);
  const recoverDispatchSource = coordinator.match(/private void RecoverDispatch[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.doesNotMatch(recoverDispatchSource, /\bUpdate\(|TryUpdateAndReload/);
  const rootStatus = coordinator.match(/protected override object Status[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.ok(rootStatus.indexOf("RecoveredStoredTerminal") < rootStatus.indexOf('{ "terminal", false }'));
  const publicStatus = coordinator.match(/internal static Dictionary<string, object> PublicStatus[\s\S]*?\n            \}/)?.[0] ?? "";
  assert.ok(publicStatus.indexOf("RecoveredStoredTerminal") < publicStatus.indexOf("WriteRunningStatus(active)"));
  assert.match(coordinator, /protected override void OnTick[\s\S]*RecoveredStoredTerminal\(Text\(root, "requestId"\), Text\(root, "requestDigest"\)\)[\s\S]*ResumeTerminalCleanup\(root\)/);
  assert.doesNotMatch(coordinator, /storedTerminal != null[\s\S]*Terminal\(root, storedTerminal\["rootEnvelope"\]\)/);
  assert.match(coordinator, /StoreTerminalEnvelope[\s\S]*root\["terminalState"\] = state[\s\S]*root\["terminalEnvelope"\] = parsed[\s\S]*root\["terminalProofSha256"\][\s\S]*Save\(root\)/);
  assert.match(coordinator, /TerminalAlreadyPublished\(root, envelope\)[\s\S]*return;[\s\S]*WriteTerminalStatus/);
  assert.match(coordinator, /StoreTerminalEnvelope\(root, envelope\)[\s\S]*TerminalAlreadyPublished\(root, envelope\)/);
  assert.match(coordinator, /TerminalAlreadyPublished[\s\S]*status == null[\s\S]*StatusLoadOutcome\.TransientFailure[\s\S]*cannot be verified[\s\S]*different request identity[\s\S]*rootEnvelope[\s\S]*UnityCliSerializer\.Serialize[\s\S]*cannot be overwritten/);
  assert.match(coordinator, /TryRestoreTerminalStatus[\s\S]*ValidateStoredTerminalEnvelope[\s\S]*rootRequestId[\s\S]*return false[\s\S]*HasPossibleNativeEffect[\s\S]*WriteTerminalStatus[\s\S]*TerminalStatusMatchesRoot/);
  assert.match(coordinator, /RootTerminalProofSha256[\s\S]*requestId[\s\S]*requestDigest[\s\S]*targetCount[\s\S]*snapshotAfter[\s\S]*effectiveSettings[\s\S]*dispatchConfirmed[\s\S]*terminalState[\s\S]*terminalEnvelope[\s\S]*HMACSHA256/);
  assert.match(coordinator, /ValidateStoredTerminalEnvelope[\s\S]*RootTerminalProofMatches/);
  assert.match(coordinator, /RecoverUnavailableActiveState[\s\S]*observedRejectedRoot[\s\S]*terminalState[\s\S]*TryRestoreTerminalStatus/);
  assert.match(coordinator, /status != null && \(!string\.Equals\(statusRequestId, requestId[\s\S]*return false/);
  assert.match(coordinator, /terminalState"\) == "completed"[\s\S]*occlusion_terminal_publish_retry[\s\S]*return/);
  assert.match(coordinator, /RecoveredStoredTerminal[\s\S]*TerminalStatusMatchesRoot[\s\S]*Occlusion terminal status does not match the active root/);
});

test("Occlusion running cancel无法证明native identity时返回false且不停止全局operation", () => {
  assert.match(coordinator, /E_OCCLUSION_OPERATION_MISMATCH/);
  assert.ok(live.indexOf("_occlusion_cancel_snapshot") < live.indexOf("commitEffect"));
  assert.match(coordinator, /operationId"\), args\.OperationId[\s\S]*UnityCliSessionMutationLease\.IsOwnedBy\(Text\(root, "requestId"\), Text\(root, "requestDigest"\)\)/);
  const cancelHandler = coordinator.slice(coordinator.indexOf("private object Cancel(CancelArgs args)"), coordinator.indexOf("private static bool HasDispatchProof"));
  assert.match(cancelHandler, /phase == "queued"[\s\S]*"cancelled", true[\s\S]*"cancelled", false/);
  assert.doesNotMatch(cancelHandler, /HasDispatchProof|cancelEffectState[\s\S]*cooperative|CancelOwnedNativeBake|StaticOcclusionCulling\.Cancel/);
  assert.match(cancel, /Running native generation cannot be attributed[\s\S]*cancelled=false/i);
  assert.equal((coordinator.match(/StaticOcclusionCulling\.Cancel\(\)/g) ?? []).length, 1);
  const terminalHandler = coordinator.match(/protected override void OnTerminal[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.doesNotMatch(terminalHandler, /HasDispatchProof\(root\)/);
  assert.match(coordinator, /phase != "running"[\s\S]*if \(!HasDispatchProof\(root\)\)[\s\S]*WriteRunningStatus\(root\)[\s\S]*StaticOcclusionCulling\.isRunning[\s\S]*Fail\(root, "E_EXECUTION_TIMEOUT"/);
  assert.doesNotMatch(coordinator, /cancelEffectState[\s\S]*cooperative/);
  assert.match(coordinator, /StaticOcclusionCulling\.isRunning\) return;[\s\S]*Complete\(root\)/);
  assert.match(coordinator, /全局idle只触发业务readback/);
  assert.match(coordinator, /E_OCCLUSION_BAKE_CANCELLED/);
  assert.match(coordinator, /PublicStatusFromStored/);
  assert.match(coordinator, /var fresh = OcclusionToolSupport\.ReadSnapshot\(\)/);
  assert.match(coordinator, /result\["umbraDataSizeBytes"\] = fresh\.UmbraDataSizeBytes/);
  assert.match(live, /normalizeStatus[\s\S]*normalizeObservedSettings[\s\S]*finiteRange\(value\.smallestOccluder, 0, 1_000_000\)[\s\S]*finiteRange\(value\.backfaceThreshold, 0, 100\)/);
  assert.match(live, /normalizeSettings[\s\S]*finiteRange\(value\.smallestOccluder, 0\.000001, 1_000_000\)[\s\S]*finiteRange\(value\.backfaceThreshold, 1, 100\)/);
  assert.match(coordinator, /umbraDataSizeBytes/);
  assert.match(coordinator, /doesSceneHaveManualPortals/);
});

test("Occlusion cancel前置status接受Unity可观察的零值settings", async () => {
  const commands: string[] = [];
  let commits = 0;
  const running = {
    state: "running", active: true, operationId: "occlusion-0123456789abcdef0123456789abcdef", phase: "baking",
    startedAt: "2026-01-01T00:00:00.000Z", finishedAt: null, durationMs: 1, progress: 0.5, targetCount: 1,
    umbraDataSizeBytes: 0, doesSceneHaveManualPortals: false,
    settings: { smallestOccluder: 0, smallestHole: 0, backfaceThreshold: 0 },
  };
  const result = await cancelOcclusionBake(undefined, {
    requestId: "zero-settings-cancel", flags: {}, dataDir: process.cwd(), absoluteDeadlineMs: Date.now() + 1_000,
    execution: { dryRun: false, timeoutMs: 1_000 }, commitEffect: async () => { commits += 1; },
    bridgeExecute: async (payload: any) => {
      commands.push(payload.command);
      if (payload.command === "internal.occlusion.bake.snapshot") return { ok: true, requestId: payload.requestId, data: running };
      return { ok: true, requestId: payload.requestId, data: { cancelled: false, status: running } };
    },
  } as any);
  assert.equal(result.cancelled, false);
  assert.equal(result.operationId, running.operationId);
  assert.equal(commits, 1);
  assert.deepEqual(commands, ["internal.occlusion.bake.snapshot", "internal.occlusion.bake.cancel"]);
});

test("Occlusion长effect冻结scene object graph且在保存前拒绝外部修改", () => {
  assert.match(support, /internal sealed class OcclusionSceneContentProofData/);
  const publicSceneDto = support.match(/public sealed class OcclusionSceneFileData\s*\{[\s\S]*?\n    \}/)?.[0] ?? "";
  assert.doesNotMatch(publicSceneDto, /ContentSha256|contentSha256/);
  assert.match(support, /GlobalObjectId\.GetGlobalObjectIdSlow/);
  assert.match(support, /new SerializedObject\(/);
  assert.doesNotMatch(support, /EditorJsonUtility\.ToJson/);
  assert.match(support, /ValidateFrozenSceneContentProofs\(contentProofs\)/);
  assert.ok(support.indexOf("ValidateFrozenSceneContentProofs(contentProofs)") < support.indexOf("EditorSceneManager.MarkSceneDirty(scene)"));
  assert.match(coordinator, /"sceneContentProofs"/);
  assert.match(coordinator, /ValidateFrozenSceneContentProofs\(SceneContentProofs\(root\)\)[\s\S]*GenerateInBackground/);
  assert.match(coordinator, /"sceneContentProofsResult"/);
  assert.match(coordinator, /ValidateFrozenSceneContentProofs\(SceneContentProofs\(root, "sceneContentProofsResult"\)\)/);
  assert.match(clear, /CaptureSceneContentProofs\(scenes\)/);
  assert.match(clear, /SaveAndProveSceneFiles\(scenes, contentProofs, true\)/);
  const publicBakeResult = coordinator.match(/protected override object BuildSuccessData[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.doesNotMatch(publicBakeResult, /contentSha256/);
});

test("Occlusion scene fingerprint覆盖Curve、Gradient和ExposedReference值", () => {
  assert.match(support, /case SerializedPropertyType\.AnimationCurve: return AnimationCurveValue/);
  assert.match(support, /case SerializedPropertyType\.Gradient: return GradientValue/);
  assert.match(support, /case SerializedPropertyType\.ExposedReference: return ObjectIdentity/);
  assert.match(support, /curve\.preWrapMode/);
  assert.match(support, /key\.inTangent/);
  assert.match(support, /key\.outTangent/);
  assert.match(support, /key\.inWeight/);
  assert.match(support, /key\.outWeight/);
  assert.match(support, /key\.weightedMode/);
  assert.match(support, /gradient\.colorKeys/);
  assert.match(support, /gradient\.alphaKeys/);
  assert.match(support, /gradient\.mode/);
});

test("Occlusion clear无需额外confirm且以zero Umbra和scene hash完成", () => {
  assert.doesNotMatch(clear, /confirm/i);
  assert.equal((clear.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.equal((clear.match(/StaticOcclusionCulling\.Clear\(\)/g) ?? []).length, 1);
  assert.match(clear, /UmbraDataSizeBytes == 0/);
  assert.match(clear, /UmbraDataSizeBytes != 0/);
  assert.match(clear, /SaveAndProveSceneFiles\(scenes, contentProofs, true\)/);
  assert.match(support, /E_OCCLUSION_SCENE_DIRTY/);
});

test("Occlusion coordinator接入唯一registry、operation router和wait tick", () => {
  const registry = read("unitypkg/Editor/Bridge/BridgeCommandRegistry.cs");
  const router = read("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs");
  const waiter = read("unitypkg/Editor/Infrastructure/UnityCliWaiter.cs");
  const bridge = read("unitypkg/Editor/UnityCliBridge.cs");
  const http = read("unitypkg/Editor/Bridge/BridgeHttpServer.cs");
  assert.match(registry, /UnityCliOcclusionBakeCoordinator\.Register/);
  assert.match(router, /UnityCliOcclusionBakeCoordinator\.Execute/);
  assert.match(waiter, /TickCoordinator\("occlusion\.bake", UnityCliBridge\.UnityCliOcclusionBakeCoordinator\.Tick\)/);
  assert.match(bridge, /UnityCliOcclusionBakeCoordinator\.Initialize/);
  assert.match(bridge, /UnityCliOcclusionBakeCoordinator\.BeforeReload/);
  assert.match(http, /UnityCliOcclusionBakeCoordinator\.IsCommand/);
  for (const command of ["start", "status", "plan", "snapshot", "cancel"]) {
    assert.match(sessionSupervisor, new RegExp(`internal\\.occlusion\\.bake\\.${command}`));
  }
});
