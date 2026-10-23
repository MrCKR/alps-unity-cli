import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";

const root = process.cwd();
const read = (relative: string): string => readFileSync(path.join(root, relative), "utf8");
const coordinator = read("unitypkg/Editor/Infrastructure/UnityCliNavMeshBakeCoordinator.cs");
const reloadOperation = read("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs");
const mutationLease = read("unitypkg/Editor/Infrastructure/UnityCliSessionMutationLease.cs");
const support = read("unitypkg/Editor/Tools/navmesh/NavMeshToolSupport.cs");
const settings = read("unitypkg/Editor/Tools/navmesh/settings.cs");
const settingsSet = read("unitypkg/Editor/Tools/navmesh/settings-set.cs");
const clear = read("unitypkg/Editor/Tools/navmesh/bake-clear.cs");
const live = read("src/tools/navmesh/live.ts");
const bake = read("src/tools/navmesh/bake.ts");
const surfaceBake = read("src/tools/navmesh/surface-bake.ts");
const cancel = read("src/tools/navmesh/bake-cancel.ts");
const status = read("src/tools/navmesh/bake-status.ts");
const bridge = read("src/bridge/domains/navmesh.ts");
const completion = JSON.parse(read("contracts/tool-completion-contract.json")) as { tools: Array<Record<string, unknown>> };

const names = ["navmesh.bake", "navmesh.surface-bake", "navmesh.bake-cancel", "navmesh.bake-clear", "navmesh.bake-status", "navmesh.settings", "navmesh.settings-set"];

test("NavMesh exposes exactly the seven approved canonical capabilities", () => {
  for (const name of names) {
    assert.match(bake + surfaceBake + cancel + status + settings + settingsSet + clear, new RegExp(`(?:name:|Name =) "${name.replaceAll(".", "\\.")}"`));
    assert.ok(completion.tools.some((entry) => entry.toolName === name), `completion contract missing ${name}`);
  }
  assert.equal(bridge.match(/toolName: "navmesh\./g)?.length, 3);
  assert.doesNotMatch(bake + surfaceBake + cancel + status + settings + settingsSet + clear + bridge, /bake_navmesh|cancel_navmesh|clear_navmesh|get_navmesh|set_navmesh/);
});

test("legacy and surface bakes share one durable session-scoped root", () => {
  assert.match(coordinator, /ActiveScope = "navmesh-bake"/);
  assert.match(coordinator, /StatusScope = "navmesh-bake-status"/);
  assert.match(coordinator, /class Coordinator : UnityCliReloadOperation/);
  assert.match(coordinator, /requestDigest/);
  assert.match(coordinator, /editorSessionId/);
  assert.match(coordinator, /dispatchNotBefore.*AddMilliseconds\(3000\)/s);
  assert.match(coordinator, /DateTimeOffset\.UtcNow < Time\(root, "dispatchNotBefore"\)/);
  assert.match(coordinator, /dispatchNonce/);
  assert.match(coordinator, /"phase"\] = "verifying"/);
  assert.match(coordinator, /verifyNotBefore/);
  assert.ok(coordinator.indexOf("root[\"topologyAfter\"] = NavMeshToolSupport.TopologyDictionary()") < coordinator.indexOf("SaveAndProveSceneFiles(SceneFiles(root), false)"));
  assert.match(coordinator, /crossed a domain reload without request-specific completion proof; outcome is unknown/);
  assert.match(coordinator, /UnityCliSessionMutationLease\.TryAcquire/);
  assert.match(coordinator, /inspectedRoot = Load\(\)[\s\S]*RecoveredStoredTerminal[\s\S]*inspectedRoot != null[\s\S]*CreateFirst\(request, digest, args, BuildPlan\(args\), accept\)[\s\S]*accepted", true/);
  assert.match(reloadOperation, /RecoveredTerminal\(request\.RequestId, digest\)/);
  assert.match(coordinator, /RecoveredTerminal\(request\.RequestId, digest\)/);
  assert.match(reloadOperation, /OnTerminal\(root, envelope\)/);
});

test("NavMesh terminal uses exact durable replay and first-envelope-wins", () => {
  assert.match(coordinator, /StatusProofKeySessionKey/);
  assert.match(coordinator, /statusProofSha256[\s\S]*HMACSHA256/);
  assert.match(coordinator, /StoreTerminalEnvelope[\s\S]*terminalProofSha256[\s\S]*Save\(root\)/);
  assert.match(coordinator, /TerminalAlreadyPublished[\s\S]*different request identity[\s\S]*cannot be overwritten/);
  assert.match(coordinator, /TryRestoreTerminalStatus[\s\S]*rootEnvelope[\s\S]*WriteTerminalStatus[\s\S]*TerminalStatusMatchesRoot/);
  assert.match(coordinator, /RecoveredStoredTerminal\(request\.RequestId, digest\)[\s\S]*RecoveredTerminal\(request\.RequestId, digest\)/);
  assert.match(coordinator, /TryResumePublishedTerminal[\s\S]*ResumeTerminalCleanup/);
  assert.match(coordinator, /TerminalLedgerScope = "navmesh-bake-terminal-ledger"[\s\S]*TerminalLedgerCapacity = 128/);
  assert.match(coordinator, /RecoveredLedgerTerminal\(request\.RequestId, digest\)[\s\S]*EnsureTerminalLedgerCapacity[\s\S]*CreateFirst/);
  assert.match(coordinator, /StoreTerminalLedger[\s\S]*TerminalLedgerProofSha256[\s\S]*HmacSha256/);
  // 容量只由未到期的重投窗口决定：条目按 retainUntilUtcMs 裁剪，满额时在 effect 前返回 busy。
  assert.match(coordinator, /PruneTerminalLedger\(ledger\["entries"\] as IList\)/);
  assert.match(coordinator, /Long\(entry, "retainUntilUtcMs"\) > now/);
  assert.match(coordinator, /"E_UNITY_BUSY"[\s\S]*navmesh-terminal-ledger-capacity[\s\S]*ownEffectStarted/);
  assert.doesNotMatch(coordinator, /restart the Editor before starting another bake/);
  assert.match(coordinator, /EvaluateTerminalCleanupRoot[\s\S]*TerminalCleanupDecision\.Retry/);
  assert.match(coordinator, /LoadActiveForStatus[\s\S]*UnityCliActiveOperation\.Load\(ActiveScope\)[\s\S]*snapshot cannot be verified/);
  const publicSnapshot = coordinator.slice(coordinator.indexOf("internal static Dictionary<string, object> PublicStatus()"), coordinator.indexOf("private static Dictionary<string, object> PublicStatusFromStored"));
  assert.doesNotMatch(publicSnapshot, /SaveStatus|WriteRunningStatus|Operation\.Tick/);
  assert.match(publicSnapshot, /StatusHistoryUnavailable[\s\S]*E_EXECUTION_TIMEOUT/);
});

test("NavMesh effect boundary never reuses stale roots or repeats dispatch", () => {
  assert.match(coordinator, /WriteRunningStatus\(candidate\)[\s\S]*AbortBeforeNativeEffect\(candidate, "initial running status could not be stored/);
  assert.doesNotMatch(coordinator, /WriteRunningStatus\(candidate\)[\s\S]{0,500}BeginPreEffectCleanup\(candidate\)/);
  assert.match(coordinator, /TryUpdateAndReload[\s\S]*AbortBeforeNativeEffect[\s\S]*BuildNavMeshAsync/);
  assert.match(coordinator, /TryUpdateAndReload[\s\S]*AbortBeforeNativeEffect[\s\S]*SurfaceApi\.Start/);
  assert.match(coordinator, /AbortBeforeNativeEffect[\s\S]*BindPreEffectAbort\(root\)[\s\S]*PublishPreEffectAbortTerminal\(root\)/);
  assert.match(coordinator, /OnTick\(Dictionary<string, object> root\)[\s\S]*IsBoundPreEffectAbort[\s\S]*PublishPreEffectAbortTerminal\(root\)/);
  assert.match(coordinator, /ResumeBoundPreEffectAbort[\s\S]*RecoverUnavailableActiveState[\s\S]*PublishPreEffectAbortTerminal\(root\)/);
  assert.doesNotMatch(coordinator, /AbortBeforeNativeEffect\([\s\S]{0,700}BeginPreEffectCleanup/);
  assert.match(coordinator, /nativeDispatched[\s\S]*ConfirmDispatch/);
  assert.doesNotMatch(coordinator, /Load\(\) \?\? root/);
  assert.doesNotMatch(coordinator, /phase == "dispatching"[\s\S]*dispatchRecovered/);
  assert.match(support, /ValidateFrozenSceneFiles/);
  assert.match(support, /HasActiveNativeBake/);
  assert.match(coordinator, /HasActiveNativeBake[\s\S]*NavMeshBuilder\.isRunning[\s\S]*SurfaceApi\.ActiveOperationCount\(\) > 0/);
});

test("NavMesh Surface observation is domain-local and reload never claims operation identity", () => {
  assert.match(coordinator, /SurfaceDispatchObservation/);
  assert.match(coordinator, /AsyncOperation[\s\S]*ReferenceEquals/);
  assert.match(coordinator, /byId\.ContainsKey\(globalId\)[\s\S]*byId\.Count != wanted\.Count[\s\S]*wanted\.Any/);
  assert.match(coordinator, /IsPresent\(SurfaceDispatchObservation observation\)/);
  assert.match(coordinator, /HasForeignOperations\(SurfaceDispatchObservation observation\)[\s\S]*!observation\.Entries\.Any/);
  assert.match(coordinator, /PublishedDataMatches\(SurfaceDispatchObservation observation\)[\s\S]*ReferenceEquals\(property\.GetValue\(entry\.Surface, null\), entry\.Data\)/);
  assert.match(coordinator, /AnyNativeOperationRunning\(\)[\s\S]*unowned native NavMesh bake started before Surface dispatch[\s\S]*SurfaceApi\.Start/);
  assert.match(coordinator, /foreignNativeOperationObserved[\s\S]*PublishedDataMatches[\s\S]*Another native NavMesh bake replaced or overlapped/);
  assert.doesNotMatch(coordinator, /foreignSurfaceOperationObserved|MarkForeignSurfaceOperation/);
  assert.match(coordinator, /DispatchLegacy[\s\S]*AnyNativeOperationRunning\(\)[\s\S]*BuildNavMeshAsync/);
  assert.match(coordinator, /DispatchSurfaces[\s\S]*AnyNativeOperationRunning\(\)[\s\S]*SurfaceApi\.Start/);
  assert.match(coordinator, /TickLegacy[\s\S]*SurfaceApi\.ActiveOperationCount\(\) > 0[\s\S]*MarkForeignNativeOperation/);
  assert.match(coordinator, /TickSurfaces[\s\S]*NavMeshBuilder\.isRunning[\s\S]*HasForeignOperations\(observation\)[\s\S]*MarkForeignNativeOperation/);
  assert.match(coordinator, /CompleteSurfaces[\s\S]*AnyNativeOperationRunning\(\)[\s\S]*foreignNativeOperationObserved/);
  assert.match(coordinator, /reloadObserved[\s\S]*RecoverReload/);
  const reload = coordinator.match(/private void RecoverReload[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.doesNotMatch(reload, /SurfaceApi\.Start|BuildNavMeshAsync|dispatchRecovered|dispatchConfirmed.*true/);
  assert.doesNotMatch(reload, /TryProve|SameTopology/);
  assert.match(reload, /phase == "verifying"[\s\S]*CompleteLegacy[\s\S]*without request-specific completion proof; outcome is unknown/);
  assert.match(coordinator, /InitializeState[\s\S]*activePhase == "dispatching" \|\| activePhase == "running"[\s\S]*BindReloadUncertain/);
  assert.match(coordinator, /reloadObserved"\) \|\| IsReloadUncertain\(root\)[\s\S]*RecoverReload/);
  const dispatchRecovery = coordinator.match(/private void RecoverDispatch[\s\S]*?\n                \}/)?.[0] ?? "";
  assert.doesNotMatch(dispatchRecovery, /TryProve|SameTopology|CompleteLegacy|CompleteSurfaces/);
});

test("NavMesh running cancel is cooperative and corrupt recovery is exact", () => {
  const cancelHandler = coordinator.slice(coordinator.indexOf("private object Cancel(CancelArgs args)"), coordinator.indexOf("private void FinishCancelled"));
  assert.doesNotMatch(cancelHandler, /NavMeshBuilder\.Cancel|SurfaceApi\.Cancel/);
  assert.match(cancelHandler, /root == null[\s\S]*storedStatus = PublicStatus\(\)[\s\S]*exactCancelled[\s\S]*operationId[\s\S]*storedStatus/);
  assert.match(cancelHandler, /terminalState[\s\S]*PublicStatus\(\)[\s\S]*phase"\) == "queued"[\s\S]*cancelRequested[\s\S]*FinishCancelled[\s\S]*cancelled", true/);
  assert.match(coordinator, /WriteRunningStatus[\s\S]*terminal root cannot be projected as running/);
  assert.match(coordinator, /PublicStatus\(\)[\s\S]*TerminalStatusMatchesRoot[\s\S]*terminal snapshot cannot be verified/);
  assert.match(cancel, /Running[\s\S]*cooperative[\s\S]*does not call/i);
  assert.match(coordinator, /OnActiveStateCorrupt[\s\S]*IsBoundSessionOwner/);
  assert.match(coordinator, /observed == null && status == null[\s\S]*OwnerNativeRunning[\s\S]*WriteTerminalStatus/);
  assert.match(coordinator, /StatusSessionInitializedKey[\s\S]*UnityCliActiveOperation\.Clear\(ActiveScope\)/);
  assert.match(coordinator, /OwnerRequestIdSessionKey[\s\S]*OwnerRequestDigestSessionKey/);
  assert.match(coordinator, /PreEffectAbortRequestIdSessionKey[\s\S]*PreEffectAbortRequestDigestSessionKey/);
  assert.match(coordinator, /RecoverActiveLease\(\)[\s\S]*TryRecoverActiveLease\(active\)[\s\S]*Operation\.Tick\(\)/);
  assert.match(coordinator, /TryRecoverActiveLease\(Dictionary<string, object> active\)[\s\S]*SessionOwnerIdentityMatches[\s\S]*TryRecoverExact/);
  assert.match(mutationLease, /TryRecoverExact\(string requestId, string requestDigest[\s\S]*IsSha256\(current\.RequestDigest\) \|\| !QuarantineObservedOwner\(current\)[\s\S]*TryAcquire/);
  assert.match(coordinator, /ClearSessionBindingsIfReleased[\s\S]*UnityCliActiveOperation\.Load\(ActiveScope\)[\s\S]*SessionOwnerIdentityMatches/);
  const recoveredDispatch = coordinator.slice(coordinator.indexOf("private void RecoverDispatch"), coordinator.indexOf("private void RecoverReload"));
  const recoveredReload = coordinator.slice(coordinator.indexOf("private void RecoverReload"), coordinator.indexOf("private void BindReloadUncertain"));
  assert.doesNotMatch(recoveredDispatch, /FinishCancelled/);
  assert.doesNotMatch(recoveredReload, /NativeGateRunning\(root\)[\s\S]*FinishCancelled/);
  assert.match(coordinator, /TickLegacy[\s\S]*phase == "verifying"[\s\S]*cancelRequested[\s\S]*FinishCancelled[\s\S]*CompleteLegacy/);
  assert.match(coordinator, /TickSurfaces[\s\S]*phase == "verifying"[\s\S]*cancelRequested[\s\S]*FinishCancelled[\s\S]*CompleteSurfaces/);
  assert.match(recoveredReload, /phase == "verifying" && beforeReload == "verifying"[\s\S]*cancelRequested[\s\S]*NativeGateRunning\(root\)[\s\S]*crossed a domain reload[\s\S]*TryUpdateAndReload/);
  assert.match(coordinator, /FinishCancelled\(Dictionary<string, object> root\)[\s\S]*phase"\) != "queued"[\s\S]*NativeGateRunning/);
  assert.match(coordinator, /terminalState"\) != "completed" && Text\(root, "phase"\) != "queued" && NativeGateRunning/);
  assert.match(reloadOperation, /Release\(requestId, requestDigest\)/);
});

test("bake runner returns only a strict root terminal", () => {
  assert.match(live, /bind\(context, kind === "legacy" \? "navmesh\.bake" : "navmesh\.surface-bake", false\)/);
  assert.match(coordinator, /ValidateStartShape\(args\)[\s\S]*RecoveredLedgerTerminal[\s\S]*inspectedRoot != null[\s\S]*ValidateStart\(args\)[\s\S]*CreateFirst/);
  assert.match(live, /internal\.navmesh\.bake\.start/);
  assert.match(live, /internal\.navmesh\.bake\.status/);
  assert.match(live, /while \(root\.terminal !== true\)/);
  assert.match(live, /prefixLength = 200 - 1 - digest\.length - suffix\.length/);
  assert.match(live, /root\.accepted !== true/);
  assert.match(live, /E_TOOL_COMPLETION_PROOF_INVALID/);
  assert.match(live, /E_EXECUTION_TIMEOUT/);
  assert.match(live, /Run navmesh\.bake-status/);
  assert.match(bake, /supportsDryRun: true/);
  assert.match(surfaceBake, /supportsDryRun: true/);
  assert.match(surfaceBake, /requiresPackages: \["com\.unity\.ai\.navigation"\]/);
  assert.match(live, /NavMeshSceneProof/);
  assert.match(live, /item\.dirty !== false/);
  assert.match(support, /PrepareLoadedSceneFiles/);
  assert.match(support, /E_NAVMESH_SCENE_DIRTY/);
  assert.match(support, /SaveAndProveSceneFiles/);
  assert.match(support, /EditorSceneManager\.SaveScene/);
  assert.match(coordinator, /"scenesResult"/);
});

test("AI Navigation remains package-optional and uses only registered editor APIs", () => {
  assert.doesNotMatch(coordinator, /using Unity\.AI\.Navigation/);
  assert.match(coordinator, /Type\.GetType\("Unity\.AI\.Navigation\.NavMeshSurface, Unity\.AI\.Navigation"/);
  assert.match(coordinator, /Type\.GetType\("Unity\.AI\.Navigation\.Editor\.NavMeshAssetManager, Unity\.AI\.Navigation\.Editor"/);
  assert.match(coordinator, /StartBakingSurfaces/);
  assert.match(coordinator, /IsSurfaceBaking/);
  assert.match(coordinator, /GetBakeOperations/);
  assert.match(coordinator, /GlobalObjectId\.GetGlobalObjectIdSlow/);
  assert.match(coordinator, /dataLocalId.*ToString\(CultureInfo\.InvariantCulture\)/s);
  assert.match(coordinator, /beforeDataHash/);
  assert.match(coordinator, /string\.Equals\(Text\(proof, "dataHash"\), Text\(expected, "beforeDataHash"\)/);
  assert.match(coordinator, /dataHash/);
  assert.match(coordinator, /dataSizeBytes/);
});

test("cancel freezes one exact operation and proves cancelled terminal", () => {
  assert.match(cancel, /operationId\?: string/);
  assert.match(live, /if \(operationId === undefined\)/);
  assert.match(coordinator, /\{ "operationId", Text\(acceptedRoot, "operationId"\) \}/);
  assert.match(live, /await context\.commitEffect\?\.\(\)/);
  assert.match(live, /internal\.navmesh\.bake\.cancel/);
  assert.equal(live.match(/await bind\(/g)?.length, 1, "only bake start needs a separate editor.state session freeze");
  assert.match(live, /status\.operationId === operationId && status\.state === "cancelled"/);
  assert.match(coordinator, /E_NAVMESH_OPERATION_MISMATCH/);
  assert.match(coordinator, /E_NAVMESH_BAKE_CANCELLED/);
});

test("status is read-only, bounded, and restart-scoped", () => {
  assert.match(status, /destructiveHint: false/);
  assert.match(status, /sideEffects: \[\]/);
  assert.match(status, /playModeSupport: "supported"/);
  assert.match(coordinator, /result\.Remove\("editorSessionId"\)/);
  assert.match(coordinator, /UnityCliActiveOperation\.Clear\(StatusScope\)/);
  assert.match(coordinator, /"state", "idle"/);
  assert.doesNotMatch(status + live, /File\.Write|AssetDatabase\.SaveAssets/);
});

test("settings read fails closed and settings set uses one barrier with persistence proof", () => {
  assert.match(settings, /Name = "navmesh\.settings"/);
  assert.match(settings, /SideEffects = System\.Array\.Empty<string>\(\)/);
  assert.doesNotMatch(settings, /CommitEffect|SaveAssets|SetDirty/);
  assert.match(support, /E_NAVMESH_SETTINGS_SCHEMA_UNSUPPORTED/);
  assert.match(support, /GetSerializedAssetInterfaceSingleton/);
  assert.match(support, /NavMeshProjectSettings/);
  assert.match(support, /DefaultAgentPrefix/);
  assert.match(support, /SettingsHash = Sha256/);
  assert.match(settingsSet, /Name = "navmesh\.settings-set"/);
  assert.equal(settingsSet.match(/ctx\.CommitEffect\(\);/g)?.length, 1);
  assert.ok(settingsSet.indexOf("PrepareSettings") < settingsSet.indexOf("if (ctx.DryRun || !plan.Changed)"));
  assert.ok(settingsSet.indexOf("if (ctx.DryRun || !plan.Changed)") < settingsSet.indexOf("ctx.CommitEffect();"));
  assert.match(support, /AssetDatabase\.SaveAssets\(\)/);
  assert.match(support, /plan\.Changed && string\.Equals\(after\.SettingsHash, plan\.Before\.SettingsHash/);
  assert.match(support, /ProveSettings/);
});

test("clear does not touch surface assets and proves empty topology", () => {
  assert.match(clear, /Name = "navmesh\.bake-clear"/);
  assert.equal(clear.match(/ctx\.CommitEffect\(\);/g)?.length, 1);
  assert.match(clear, /HasActiveSurfaceData\(\)/);
  assert.match(clear, /E_NAVMESH_SURFACE_DATA_ACTIVE/);
  assert.ok(clear.indexOf("HasActiveSurfaceData()") < clear.indexOf("ctx.CommitEffect();"));
  assert.match(clear, /PrepareLoadedSceneFiles\("navmesh\.bake-clear"\)/);
  assert.ok(clear.indexOf("PrepareLoadedSceneFiles") < clear.indexOf("ctx.CommitEffect();"));
  assert.match(clear, /SaveAndProveSceneFiles\(scenes, true\)/);
  assert.match(clear, /NavMeshBuilder\.ClearAllNavMeshes\(\)/);
  assert.match(clear, /TopologyEmpty\(after\)/);
  assert.doesNotMatch(clear, /ClearSurfaces|DeleteAsset/);
});

test("coordinator state uses fixed Library UnityCli files and central scheduler wiring", () => {
  const active = read("unitypkg/Editor/Infrastructure/UnityCliActiveOperation.cs");
  const waiter = read("unitypkg/Editor/Infrastructure/UnityCliWaiter.cs");
  const operationRouter = read("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs");
  const registry = read("unitypkg/Editor/Bridge/BridgeCommandRegistry.cs");
  assert.match(active, /Library", "UnityCli", "active-" \+ scope \+ "\.json"/);
  assert.match(waiter, /TickCoordinator\("navmesh\.bake", UnityCliBridge\.UnityCliNavMeshBakeCoordinator\.Tick\)/);
  assert.match(operationRouter, /UnityCliNavMeshBakeCoordinator\.Execute/);
  assert.match(registry, /UnityCliNavMeshBakeCoordinator\.Register/);
  assert.doesNotMatch(coordinator, /Temp\/pipeline|lighting/i);
});
