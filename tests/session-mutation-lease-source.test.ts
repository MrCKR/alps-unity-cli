import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import test from "node:test";

const root = new URL("../", import.meta.url);
async function source(path: string): Promise<string> { return readFile(new URL(path, root), "utf8"); }

function assertBefore(text: string, first: string, second: string): void {
  const firstIndex = text.indexOf(first);
  assert.ok(firstIndex >= 0, `missing source anchor: ${first}`);
  const secondIndex = text.indexOf(second);
  assert.ok(secondIndex >= 0, `missing source anchor: ${second}`);
  assert.ok(firstIndex < secondIndex, `${first} must precede ${second}`);
}

async function sourceTree(directory: URL): Promise<Array<{ path: string; text: string }>> {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async (entry) => {
    const url = new URL(entry.name + (entry.isDirectory() ? "/" : ""), directory);
    if (entry.isDirectory()) return sourceTree(url);
    if (!entry.name.endsWith(".cs")) return [];
    return [{ path: decodeURIComponent(url.pathname), text: await readFile(url, "utf8") }];
  }));
  return nested.flat();
}

/** 统计指定C#调用的顶层参数，忽略嵌套调用和字符串中的逗号。 */
function invocationArgumentCounts(text: string, marker: string): number[] {
  const counts: number[] = [];
  let offset = 0;
  while ((offset = text.indexOf(marker + "(", offset)) >= 0) {
    let depth = 0;
    let commas = 0;
    let content = false;
    let quote = "";
    let escaped = false;
    let index = offset + marker.length + 1;
    for (; index < text.length; index += 1) {
      const char = text[index];
      if (quote) {
        if (escaped) escaped = false;
        else if (char === "\\") escaped = true;
        else if (char === quote) quote = "";
        continue;
      }
      if (char === '"' || char === "'") { quote = char; content = true; continue; }
      if (char === "(") { depth += 1; content = true; continue; }
      if (char === ")") {
        if (depth === 0) break;
        depth -= 1;
        content = true;
        continue;
      }
      if (char === "," && depth === 0) commas += 1;
      else if (!/\s/.test(char)) content = true;
    }
    assert.ok(index < text.length, `unterminated ${marker} invocation`);
    counts.push(content ? commas + 1 : 0);
    offset = index + 1;
  }
  return counts;
}

test("C# mutation execution uses the shared instance lease owner protocol", async () => {
  const [lease, bridge, operation, reloadOperation, workQueue, activeOperation, queueState, admission, testRun] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliSessionMutationLease.cs"),
    source("unitypkg/Editor/UnityCliBridge.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs"),
    source("unitypkg/Editor/Bridge/BridgeWorkQueue.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliActiveOperation.cs"),
    source("unitypkg/Editor/Bridge/BridgeQueueState.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliTestRunCoordinator.cs"),
  ]);
  for (const field of ["pid", "token", "processStartIdentity", "requestId"]) assert.match(lease, new RegExp(`\\b${field}\\b`, "i"));
  assert.match(lease, /TryAcquire\(string requestId, string requestDigest, out bool newlyAcquired\)/);
  assert.match(lease, /Release\(string requestId, string requestDigest\)[\s\S]*owner\.RequestDigest, requestDigest/);
  assert.match(lease, /ReleaseMatchingOwner\(Func<Owner, bool> matches\)[\s\S]*TryReadOwnerSnapshot\(out observed, out observedRaw\)[\s\S]*QuarantineObservedOwner\(observed, observedRaw\)[\s\S]*observedRaw\.SequenceEqual\(currentRaw\)/);
  assert.match(lease, /TryAcquireRecoveryClaim\(string claim, string fingerprint, out FileStream claimHandle\)[\s\S]*FileMode\.OpenOrCreate[\s\S]*FileShare\.None[\s\S]*claimHandle\.Flush\(true\)/);
  assert.match(lease, /finally[\s\S]*claimHandle\.Dispose\(\);[\s\S]*File\.Delete\(claim\)/);
  assert.doesNotMatch(lease, /TryRecoverStaleRecoveryClaim|TryDeleteExactFile\(claim/);
  assert.match(lease, /WriteDurableNew\(path, ownerText\);[\s\S]*newlyAcquired = true;[\s\S]*return true;/);
  assert.match(lease, /TryReadOwnerSnapshot[\s\S]*catch \(FileNotFoundException\) \{ return true; \}[\s\S]*catch \(DirectoryNotFoundException\) \{ return true; \}[\s\S]*owner = ParseOwner\(raw\);[\s\S]*return owner != null/);
  assert.match(lease, /ParseOwner[\s\S]*!IsSafeId\(requestId\) \|\| !IsSha256\(requestDigest\)[\s\S]*return null/);
  assert.match(lease, /RestoreValidatedActiveRootLease[\s\S]*asset-refresh[\s\S]*package-embed[\s\S]*navmesh-bake[\s\S]*occlusion-bake[\s\S]*catch \{ return false; \}/);
  assert.match(lease, /var claim = path \+ "\.recovery"[\s\S]*var quarantine = path \+ "\.quarantine"/);
  assert.doesNotMatch(lease, /\.create-/);
  assert.match(lease, /TryIsOwnedBy\(string requestId, string requestDigest, out bool isOwned\)[\s\S]*owner\.RequestDigest, requestDigest[\s\S]*return true/);
  assert.match(lease, /IsOwnedBy\(string requestId, string requestDigest\)[\s\S]*TryIsOwnedBy\(requestId, requestDigest, out isOwned\)[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(lease, /ReadOwner\(\)[\s\S]*!TryReadOwnerSnapshot[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(lease, /WriteDurableNew\(path, ownerText\)[\s\S]*catch \(Exception\)[\s\S]*QuarantineObservedOwner\(expected, raw\)[\s\S]*!OwnerInvalid\(observed\)[\s\S]*return false[\s\S]*E_EXECUTION_TIMEOUT/);
  assert.match(lease, /TryGetCurrentOwner\(out string requestId, out string requestDigest\)[\s\S]*TryReadOwnerSnapshot[\s\S]*requestId = owner\.RequestId[\s\S]*requestDigest = owner\.RequestDigest/);
  assert.doesNotMatch(lease, /CurrentOwnerRequest(?:Id|Digest)/);
  assert.match(lease, /state\.OperationExists\(scope\)[\s\S]*state\.LoadOperation\(scope\)/);
  assertBefore(lease, "ReconcileAcceptedDirectMutationBeforeLeaseRestore(ProductionQueueState)", "RestoreValidatedActiveRootLease(ProductionQueueState)");
  assert.match(lease, /InitializeRecoveryOrderForTests[\s\S]*ReconcileAcceptedDirectMutationBeforeLeaseRestore\(state\)[\s\S]*RestoreValidatedActiveRootLease\(state\)/);
  assert.match(bridge, /UnityCliSessionMutationLease\.Initialize\(\);\s*UnityCliCompilationEventBroker\.Initialize\(\);/s);
  assert.match(workQueue, /UnityCliSessionMutationLease\.TryFinalizeOwnedPlayModeRoot\(\);/);
  assert.match(operation, /var requestDigest = UnityCliRequestDigest\.Compute\(rawRequestBytes, request\.InstanceId\)/);
  assert.match(operation, /TryAcquire\(request\.RequestId, requestDigest, out newlyAcquiredLease\) \|\| !newlyAcquiredLease/);
  assert.match(operation, /BindDirectMutationLease\(request\.RequestId, requestDigest\)/);
  assert.match(operation, /finally[\s\S]*FinalizeDirectMutationExecution\(request\.RequestId, requestDigest\)/);
  assert.match(operation, /SaveDirectMutationAcceptedRoot[\s\S]*accepted_before_effect/);
  assert.match(operation, /MarkDirectMutationEffectStarted[\s\S]*effect_started/);
  assert.match(operation, /terminal_pending[\s\S]*StoreTerminalFirst\(DirectMutationTerminalScope/);
  assert.match(operation, /PublishDirectMutationTerminal[\s\S]*StoreTerminalFirst[\s\S]*CleanupPublishedDirectMutation/);
  assert.match(operation, /CleanupPublishedDirectMutation[\s\S]*TryGetTerminal\(DirectMutationTerminalScope[\s\S]*BeginExactTerminalCleanup\(DirectMutationTerminalScope/);
  assert.match(operation, /CommitToolEffect[\s\S]*DirectMutationBindingMatches\(request\.RequestId, requestDigest\)[\s\S]*IsOwnedBy\(request\.RequestId, requestDigest\)/);
  assert.match(operation, /TryFinalizeRecoveredPlayModeRoot[\s\S]*DirectMutationCleanupPendingSessionKey[\s\S]*DirectMutationBindingMatches\(requestId, requestDigest\)[\s\S]*ReleaseDirectMutationLease\(requestId, requestDigest\)/);
  assert.match(operation, /ReleaseDirectMutationLease[\s\S]*SetBool\(DirectMutationCleanupPendingSessionKey, true\)[\s\S]*TryIsOwnedBy\(requestId, requestDigest, out isOwned\)[\s\S]*SetBool\(DirectMutationCleanupPendingSessionKey, false\)/);
  assert.match(bridge, /OnBeforeAssemblyReload\(\)[\s\S]*ReleaseActiveDirectMutationLease\(\)/);
  assert.match(workQueue, /RetryDirectMutationTerminalCleanup\(\);[\s\S]*RetryPendingWorkItemSettlements\(\);/);
  assert.match(queueState, /BridgeQueueSinks[\s\S]*BridgeQueueTestHooks[\s\S]*AssertDrained/);
  assert.match(workQueue, /PendingSettlementWork\.Remove\(item\)[\s\S]*RemoveActiveWorkItem\(state, item\)[\s\S]*CompleteAndLogWorkItem\(item\)/);
  assert.match(admission, /TryConfirmDurableTransferredOwner[\s\S]*ReconcileAdmissionTransferForLiveMarker/);
  assert.match(admission, /test-cancel-admission-owner[\s\S]*rootRequestDigest|TryConfirmCancelTransferredOwner[\s\S]*AdmissionMarkerMatchesIdentity/);
  assert.match(testRun, /Continuation本身是legal terminal owner[\s\S]*CompleteAdmissionRoot/);
  assert.match(admission, /ReconcileAdmissionTransferForLiveMarker\(BridgeQueueState state[\s\S]*PersistAdmissionPoolState\(state\)/);
  assert.match(operation, /RetryDirectMutationTerminalCleanup[\s\S]*RetryExactTerminalCleanup\(DirectMutationTerminalScope\)/);
  assertBefore(operation, "if (!mutation)", "TryAcquire(request.RequestId, requestDigest");
  const acquireIndex = operation.indexOf("TryAcquire(request.RequestId, requestDigest");
  assert.ok(acquireIndex >= 0, "missing direct mutation lease acquisition");
  const executeIndex = operation.indexOf("var response = await Execute(request, cancellationToken)", acquireIndex);
  assert.ok(executeIndex >= 0, "missing direct mutation execution");
  assert.ok(acquireIndex < executeIndex, "lease acquisition must precede direct mutation execution");
  assert.match(reloadOperation, /TryGetCurrentOwner\(out currentOwnerRequestId, out currentOwnerRequestDigest\)[\s\S]*E_EXECUTION_TIMEOUT[\s\S]*E_REQUEST_ID_REUSED[\s\S]*var root = Load\(\)[\s\S]*TryRecoverExact\(request\.RequestId, digest[\s\S]*TryAcquire\(request\.RequestId, digest/);
  assert.match(reloadOperation, /catch \(IOException exception\)[\s\S]*active state cannot be read yet[\s\S]*catch \(Exception exception\)[\s\S]*active state corrupt/);
  assert.match(reloadOperation, /TryGetCurrentOwner\(out missingOwnerRequestId, out missingOwnerRequestDigest\)/);
  assert.doesNotMatch(reloadOperation, /CurrentOwnerRequest(?:Id|Digest)/);
  assert.match(reloadOperation, /UnityCliSessionMutationLease\.TryAcquire\([\s\S]*UnityCliSessionMutationLease\.Release\(request\.RequestId, digest\)/s);
  assert.match(reloadOperation, /if \(LastLoadWasCorrupt \|\| LastLoadWasUnreadable \|\| LastLoadRecoveredTerminal[\s\S]*LastLoadWasMissing && !string\.IsNullOrEmpty\(currentOwnerRequestId\)[\s\S]*E_EXECUTION_TIMEOUT[\s\S]*TryAcquire\(request\.RequestId, digest, out acquired\) \|\| !acquired[\s\S]*CreateRoot/);
  assert.match(reloadOperation, /OnActiveStateCorrupt\(Exception exception\)[\s\S]*TryGetOnlyActiveScopeOwner\(Scope[\s\S]*StoreTerminalFirst\(Scope, ownerRequestId, ownerRequestDigest, envelope\)[\s\S]*BeginTerminalCleanup/);
  assert.match(reloadOperation, /Text\(root, "requestDigest"\), digest[\s\S]*E_REQUEST_ID_REUSED/s);
  assert.match(reloadOperation, /Terminal\([\s\S]*unityCliTerminalEnvelope[\s\S]*StoreTerminalFirst\(Scope, requestId, requestDigest, envelope\)[\s\S]*BeginTerminalCleanup\(requestId, requestDigest\)/s);
  assert.match(reloadOperation, /ResumeTerminalCleanup\(Dictionary<string, object> root\)[\s\S]*BeginTerminalCleanup\(Text\(root, "requestId"\), Text\(root, "requestDigest"\)\)/s);
  assert.match(reloadOperation, /RetryTerminalCleanup\([\s\S]*activeRequestId[\s\S]*activeRequestDigest[\s\S]*markerMatchesActive[\s\S]*UnityCliSessionMutationLease\.Release\(requestId, requestDigest\)[\s\S]*LeaseDefinitelyNotOwned\(requestId, requestDigest\)/s);
  assert.match(reloadOperation, /LeaseDefinitelyNotOwned[\s\S]*TryIsOwnedBy\(requestId, requestDigest, out isOwned\) && !isOwned/);
  assert.match(reloadOperation, /terminal active root read failed[\s\S]*IsOwnedBy\(requestId, requestDigest\)[\s\S]*UnityCliActiveOperation\.Clear\(Scope\)/s);
  const retryCleanupStart = reloadOperation.indexOf("private bool RetryTerminalCleanup");
  assert.ok(retryCleanupStart >= 0, "missing terminal cleanup retry");
  const retryCleanupEnd = reloadOperation.indexOf("internal bool Update", retryCleanupStart);
  assert.ok(retryCleanupEnd > retryCleanupStart, "missing terminal cleanup retry boundary");
  const retryCleanup = reloadOperation.slice(retryCleanupStart, retryCleanupEnd);
  const activeCleanupStart = retryCleanup.indexOf("if (active != null)");
  assert.ok(activeCleanupStart >= 0, "missing active cleanup branch");
  const activeCleanupEnd = retryCleanup.indexOf("UnityCliSessionMutationLease.Release(requestId, requestDigest)", activeCleanupStart);
  assert.ok(activeCleanupEnd > activeCleanupStart, "missing active cleanup release boundary");
  const normalActiveCleanup = retryCleanup.slice(activeCleanupStart, activeCleanupEnd);
  assertBefore(normalActiveCleanup, "if (!markerMatchesActive)", "UnityCliActiveOperation.Clear(Scope)");
  assert.match(reloadOperation, /pendingTerminalCleanupRequestId = requestId;[\s\S]*PersistAdmissionOwnerTransfer\(TerminalCleanupScope, marker[\s\S]*UnityCliActiveOperation\.Save\(TerminalCleanupScope, cleanup\)/s);
  assert.match(retryCleanup, /if \(!UnityCliActiveOperation\.Exists\(TerminalCleanupScope\)\)[\s\S]*UnityCliActiveOperation\.Save\(TerminalCleanupScope, cleanup\)[\s\S]*return true[\s\S]*UnityCliActiveOperation\.Clear\(Scope\)/s);
  assert.match(reloadOperation, /enum TerminalCleanupDecision[\s\S]*Cleanup[\s\S]*Retry[\s\S]*Reject/);
  assert.match(retryCleanup, /TerminalCleanupDecision\.Retry\) return true/);
  assert.match(reloadOperation, /PreEffectCleanupScope[\s\S]*BeginPreEffectCleanup[\s\S]*RetryPreEffectCleanup[\s\S]*CanCleanupAbandonedPreEffectRoot/);
  assert.match(reloadOperation, /RetryPreEffectCleanup[\s\S]*markerMatchesActive[\s\S]*UnityCliActiveOperation\.Clear\(Scope\)[\s\S]*FinalizeAbandonedPreEffectState\(requestId, requestDigest\)[\s\S]*Release\(requestId, requestDigest\)/);
  assert.match(reloadOperation, /UnityCliReloadTickSchedule[\s\S]*Stopwatch\.GetTimestamp\(\)[\s\S]*Stopwatch\.Frequency/);
  assert.match(reloadOperation, /utcNow \?\? \(\(\) => DateTimeOffset\.UtcNow\)/);
  assert.match(reloadOperation, /internal virtual void Tick\(\)[\s\S]*tickSchedule\.TryEnter\(\)[\s\S]*RetryPreEffectCleanup\(\)[\s\S]*RetryTerminalCleanup\(\)[\s\S]*TryResumePublishedTerminal\(root\)[\s\S]*tickSchedule\.UtcNow >= Time\(root, "deadline"\)/);
  assert.doesNotMatch(reloadOperation, /nextTickUtc|DateTime\.UtcNow < nextTick/);
  assert.match(activeOperation, /TryGetTerminal\(string scope, string requestId, string requestDigest, out object envelope\)/);
  assert.match(activeOperation, /StoreTerminalFirst[\s\S]*MaxTerminalEntryBytes[\s\S]*while \(cache\.Entries\.Count > 100 \|\| Encoding\.UTF8\.GetByteCount\(serialized\) > MaxTerminalCacheBytes\)[\s\S]*RetiredIdentities\.Add\(TerminalIdentityHash[\s\S]*DurableWriteText\(TerminalCachePath\(\), serialized\)/);
  assert.match(activeOperation, /retiredIdentities[\s\S]*RejectRetiredTerminal[\s\S]*TerminalIdentityHash[\s\S]*SHA256\.Create/);
  assert.match(lease, /!allowValidatedActiveRoot\) UnityCliActiveOperation\.EnsureTerminalAdmission\(\)/);
  assert.match(activeOperation, /HashSet<string>[\s\S]*duplicate or retired request identity[\s\S]*Durable terminal cache envelope is invalid/);
  assert.match(activeOperation, /Active operation state exceeds its fixed size limit/);
  assert.match(operation, /DirectMutationEffectCommittedSessionKey[\s\S]*TryGetTerminal\(DirectMutationTerminalScope[\s\S]*PersistDirectMutationTerminal[\s\S]*TryIsOwnedBy\(requestId, requestDigest/);
  assert.match(activeOperation, /editorSessionId[\s\S]*UnityCliBridge\.EditorSessionId\(\)/);
  assert.match(activeOperation, /E_REQUEST_ID_REUSED/);
  assert.doesNotMatch(operation, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
});

test("every Editor lease ownership and cleanup call binds requestId plus digest", async () => {
  const files = await sourceTree(new URL("unitypkg/Editor/", root));
  for (const file of files) {
    for (const method of ["UnityCliSessionMutationLease.Release", "UnityCliSessionMutationLease.IsOwnedBy"]) {
      const counts = invocationArgumentCounts(file.text, method);
      for (const count of counts) assert.equal(count, 2, `${file.path}: ${method} must receive requestId and requestDigest`);
    }
    for (const count of invocationArgumentCounts(file.text, "UnityCliSessionMutationLease.TryIsOwnedBy"))
      assert.equal(count, 3, `${file.path}: TryIsOwnedBy must receive requestId, requestDigest, and an out result`);
  }
  const lease = await source("unitypkg/Editor/Infrastructure/UnityCliSessionMutationLease.cs");
  assert.doesNotMatch(lease, /(?:Release|IsOwnedBy)\(string requestId\)\s*\{/);
});

test("all durable mutation coordinators keep the shared lease until canonical terminal", async () => {
  const paths = [
    "unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs",
    "unitypkg/Editor/Infrastructure/UnityCliLiveRefreshCoordinator.cs",
    "unitypkg/Editor/Infrastructure/UnityCliPackageResolveCoordinator.cs",
    "unitypkg/Editor/Infrastructure/UnityCliTestRunCoordinator.cs",
    "unitypkg/Editor/Infrastructure/UnityCliLiveBuildCoordinator.cs",
  ];
  const [reloadOperation, ...coordinators] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs"),
    ...paths.map(source),
  ]);
  const combined = coordinators.join("\n");
  assert.match(reloadOperation, /UnityCliSessionMutationLease\.TryAcquire\([\s\S]*Terminal\([\s\S]*BeginTerminalCleanup[\s\S]*RetryTerminalCleanup[\s\S]*UnityCliSessionMutationLease\.Release/s);
  for (const path of paths.slice(0, 4)) assert.match(await source(path), /UnityCliReloadOperation/, path);
  assert.match(await source(paths[4]), /UnityCliSessionMutationLease\.(?:TryAcquire|Release)/);
  assert.match(combined, /UnityCliSessionMutationLease\.IsOwnedBy\(/);
  assert.match(reloadOperation, /UnityCliActiveOperation\.(?:Load|Save|Clear)\(Scope\)/);
  assert.doesNotMatch(combined, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN|E_REQUEST_JOURNAL/);
});

test("Build reload retains its unknown root but releases the terminated domain lease", async () => {
  const [build, bridge] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliLiveBuildCoordinator.cs"),
    source("unitypkg/Editor/UnityCliBridge.cs"),
  ]);
  assert.match(build, /Initialize\(\)[\s\S]*MarkReloadOutcomeUnknownAndRelease[\s\S]*reloadOutcomeUnknown[\s\S]*UnityCliSessionMutationLease\.Release\(requestId, requestDigest\)/);
  assert.match(build, /TryGetTerminal\(Scope, request\.RequestId, digest, out recoveredTerminal\)[\s\S]*active != null[\s\S]*requestId[\s\S]*requestDigest[\s\S]*reloadOutcomeUnknown[\s\S]*FinishTerminal/);
  assert.match(build, /FinishTerminal[\s\S]*StoreTerminalFirst\(Scope, id, digest, envelope\)[\s\S]*BeginExactTerminalCleanup\(Scope, id, digest, queueState\)/);
  assert.match(build, /RecoverTerminalCleanup[\s\S]*RetryExactTerminalCleanup\(Scope, state\)/);
  assert.match(build, /FinishBuildTerminalAfterRootFault[\s\S]*PersistAdmissionOwnerTransfer\(queueState, Scope \+ "-admission-owner"[\s\S]*AdmissionOwnerState\.Recovery/);
  assert.match(bridge, /UnityCliCompileCoordinator\.Initialize\(\);\s*UnityCliLiveBuildCoordinator\.Initialize\(\);/);
  assert.match(bridge, /UnityCliCompileCoordinator\.BeforeReload\(\);\s*UnityCliLiveBuildCoordinator\.BeforeReload\(\);/);
  assert.match(bridge, /UnityCliCompileCoordinator\.Shutdown\(\);\s*UnityCliLiveBuildCoordinator\.Shutdown\(\);/);
});

test("Package coordinator uses active-operation state and terminal cleanup", async () => {
  const [coordinator, state] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliPackageResolveCoordinator.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliPackageResolveState.cs"),
  ]);
  assert.match(coordinator, /private sealed class Coordinator : UnityCliReloadOperation/);
  assert.match(coordinator, /internal static async Task<string> Execute\([\s\S]*operation\.Execute/);
  assert.match(coordinator, /internal static void Tick\(\) \{ RecoverTerminalCleanup\(\); operation\.Tick\(\); \}/);
  assert.match(coordinator, /AdditionalCommands[\s\S]*ClaimFilesCommand[\s\S]*CommitFilesCommand[\s\S]*ProveFilesCommand[\s\S]*FailFilesCommand/);
  assert.match(coordinator, /ExecuteAdditionalCommand\([\s\S]*ClaimFiles\(request\)[\s\S]*CommitFiles\(request\)[\s\S]*ProveFiles\(request\)[\s\S]*FailFiles\(request\)/);
  assert.match(coordinator, /TryGetPackageRoot\(args\.Operation, out contract\)[\s\S]*TryAcquire\(request\.RequestId, requestDigest, out newlyAcquiredLease\)[\s\S]*AcceptPackageActiveOperation\(request, raw, contract, newlyAcquiredLease/);
  assert.match(coordinator, /RetainPackageAdmissionRecovery[\s\S]*PersistAdmissionOwnerTransfer\(state, ActiveScope \+ "-admission-owner"[\s\S]*AdmissionOwnerState\.Recovery/);
  assert.match(state, /AcceptPackageActiveOperation[\s\S]*bool allowCreate[\s\S]*if \(!allowCreate\)[\s\S]*outcome is unknown/);
  assert.match(coordinator, /phase == "awaiting_files" \|\| phase == "files_committing"[\s\S]*package-file-revoke[\s\S]*TerminalTimeout/);
  assert.match(coordinator, /RequireLease\(root\)/);
  assert.match(state, /internal string RequestId; internal string RequestDigest;/);
  assert.match(state, /\{ "requestId", root\.RequestId \}, \{ "requestDigest", root\.RequestDigest \}/);
  assert.match(state, /TerminalRoots\.TryGetValue\(request\.RequestId, out terminal\)[\s\S]*terminal\.RequestDigest, requestDigest[\s\S]*E_REQUEST_ID_REUSED/);
  assert.match(state, /MarkPackageFileEffectStartedActive[\s\S]*AdvancePackageOperation\(requestId, root =>[\s\S]*RequireLease\(root\)/);
  assert.match(state, /MarkPackageResolveEffectStarted[\s\S]*RequireLease\(root\)/);
  assert.match(state, /var dispatchRoot = RequiredRoot\(root\.RequestId\);[\s\S]*RequireLease\(dispatchRoot\);[\s\S]*Client\.Resolve\(\)/);
  assert.match(state, /PersistPackageTerminal[\s\S]*StoreTerminalFirst\(ActiveScope, root\.RequestId, root\.RequestDigest, selectorDigest, envelope\)[\s\S]*BeginExactTerminalCleanup\(ActiveScope, root\.RequestId, root\.RequestDigest\)/);
  assert.match(coordinator, /E_EXECUTION_TIMEOUT/);
  assert.doesNotMatch(coordinator + state, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
});

test("TestRunner attribution persists dispatch, returned identity, callback identity, and pending finish", async () => {
  const testCoordinator = await source("unitypkg/Editor/Infrastructure/UnityCliTestRunCoordinator.cs");
  assert.match(testCoordinator, /private sealed class Coordinator : UnityCliReloadOperation/);
  assert.match(testCoordinator, /internal static Task<string> Execute\([\s\S]*operation\.Execute/);
  assert.match(testCoordinator, /internal static void Tick\(\) \{ operation\.Tick\(\); \}/);
  assert.match(testCoordinator, /RetryAdmissionOwnerRecoveryMarker\(\) \|\| RetryCancelAdmissionOwnerMarker\(\)/);
  assert.match(testCoordinator, /CancelAdmissionMarker[\s\S]*rootRequestId[\s\S]*rootRequestDigest[\s\S]*runId/);
  assert.match(testCoordinator, /RetryCancelAdmissionOwnerMarker[\s\S]*TryReadCancelTerminalProof[\s\S]*RepairCancelParentFromMarker[\s\S]*CompleteAdmissionRoot\(state, owner\)/);
  for (const field of ["execute_dispatching", "run_identity_pending", "returnedRunId", "observedRunId", "pendingFinished", "CompletePending"])
    assert.match(testCoordinator, new RegExp(field));
  assert.match(testCoordinator, /TryGetTerminal\(Scope, request\.RequestId, digest, out durableTerminal\)[\s\S]*terminalRequestDigest = digest/);
  assert.match(testCoordinator, /root\.RequestDigest, digest[\s\S]*E_REQUEST_ID_REUSED/);
  assert.match(testCoordinator, /EnsureLegacyApi\(\)[\s\S]*api == null \|\| broker == null[\s\S]*InitializeLegacy\(\)/);
  assertBefore(testCoordinator, "try { root = Load(); }", "TryAcquire(request.RequestId, digest");
  assert.match(testCoordinator, /TryAcquire\(request\.RequestId, digest, out newlyAcquiredLease\) \|\| !newlyAcquiredLease/);
  assert.doesNotMatch(testCoordinator, /LoadSafely/);
  assert.match(testCoordinator, /Test active state cannot be read; outcome is unknown/);
  assert.match(testCoordinator, /failures\.Count >= 100[\s\S]*BoundedText\(node\.StackTrace, 8192\)/);
  assert.match(testCoordinator, /catch \(Exception\) \{ return; \}/);
  assert.match(testCoordinator, /Finish\(Root root, object envelope\)[\s\S]*StoreTerminalFirst\(Scope, root\.RequestId, root\.RequestDigest, envelope\)[\s\S]*BeginExactTerminalCleanup\(Scope, root\.RequestId, root\.RequestDigest\)/);
  const deadlineRecoveryStart = testCoordinator.indexOf("private static void RecoverAfterDeadline");
  assert.ok(deadlineRecoveryStart >= 0, "missing test deadline recovery");
  const deadlineRecoveryEnd = testCoordinator.indexOf("private static void RecoverRunIdentity", deadlineRecoveryStart);
  assert.ok(deadlineRecoveryEnd > deadlineRecoveryStart, "missing test deadline recovery boundary");
  const deadlineRecovery = testCoordinator.slice(deadlineRecoveryStart, deadlineRecoveryEnd);
  assert.match(deadlineRecovery, /ids\.Length == 1 && Same\(ids\[0\], runId\)\) return/);
  assert.match(deadlineRecovery, /Fail\(root, "E_EXECUTION_TIMEOUT"/);
  assert.doesNotMatch(testCoordinator, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
});

test("refresh delegates compile dispatch and callback attribution to compile coordinator", async () => {
  const [refresh, compile] = await Promise.all([
    source("unitypkg/Editor/Infrastructure/UnityCliLiveRefreshCoordinator.cs"),
    source("unitypkg/Editor/Infrastructure/UnityCliCompileCoordinator.cs"),
  ]);
  assert.match(refresh, /UnityCliCompileCoordinator\.RequestRefreshCompilation/);
  assert.doesNotMatch(refresh, /CompilationPipeline\.RequestScriptCompilation|RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
  assert.match(refresh, /compileDiagnosticsTruncated/);
  assert.match(compile, /target\.Count >= 200[\s\S]*512 \* 1024/);
  assert.match(compile, /BoundedDiagnosticText\(Text\(source, "message"\), 4096\)/);
  assert.match(compile, /diagnosticErrorCount[\s\S]*diagnosticWarningCount[\s\S]*diagnosticInfoCount[\s\S]*diagnosticsTruncated/);
  assert.match(compile, /ValidateBoundedDiagnostics[\s\S]*assembly is string[\s\S]*severityValue is string[\s\S]*message is string[\s\S]*file is string[\s\S]*Compilation diagnostic aggregate is inconsistent with its sample/);
  assert.match(compile, /severity == "warning" && !includeWarnings\) continue/);
  assert.match(compile, /BuildCompileFailureDetails[\s\S]*Fix the listed compiler diagnostics/);
  // active 文件读写收编进统一骨架；refresh 只声明 scope 并继承骨架。
  assert.match(refresh, /Scope \{ get \{ return "refresh"; \} \}/);
  assert.match(refresh, /UnityCliReloadOperation/);
  assert.match(refresh, /E_EXECUTION_TIMEOUT/);
  assert.match(compile, /RequestRefreshCompilation/);
  assert.match(compile, /UpdateRefresh\(null, null, x =>/);
  assert.match(compile, /CurrentCompilationEventSequence[\s\S]*compileDispatchSequence[\s\S]*compilationRequested[\s\S]*sequence > Long\(x, "compileDispatchSequence"\)/);
  assert.match(compile, /compileDiagnostics/);
});
