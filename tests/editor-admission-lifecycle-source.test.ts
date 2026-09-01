import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const read = (path: string) => readFile(path, "utf8");

test("Admission pool has one owner-state machine and an exact transition table", async () => {
  const pool = await read("unitypkg/Shared/UnityCliAdmissionPool.cs");
  assert.match(pool, /enum AdmissionOwnerState \{ Granted, WorkItem, Coordinator, Continuation, Recovery, TerminalCleanup \}/);
  assert.doesNotMatch(pool, /enum AdmissionState|AdmissionOwnerKind/);
  assert.match(pool, /AuthorizeAndCapture[\s\S]*Granted→WorkItem/);
  assert.match(pool, /PrepareOwnerTransfer\(ConsumedIdentity identity, AdmissionOwnerState intendedOwner\)/);
  assert.match(pool, /CommitOwnerTransfer\(ConsumedIdentity identity, AdmissionOwnerState intendedOwner, out ConsumedIdentity successor\)/);
  assert.match(pool, /Matches\(entry, identity\)[\s\S]*LegalTransfer/);
  assert.match(pool, /value\.RequestDigest, identity\.RequestDigest/);
  assert.match(pool, /source == AdmissionOwnerState\.WorkItem[\s\S]*AdmissionOwnerState\.Coordinator[\s\S]*AdmissionOwnerState\.Continuation[\s\S]*AdmissionOwnerState\.Recovery[\s\S]*AdmissionOwnerState\.TerminalCleanup/);
  assert.match(pool, /source == AdmissionOwnerState\.Coordinator \|\| source == AdmissionOwnerState\.Recovery[\s\S]*target == AdmissionOwnerState\.TerminalCleanup/);
  assert.match(pool, /CanComplete[\s\S]*WorkItem[\s\S]*Continuation[\s\S]*TerminalCleanup/);
  assert.match(pool, /CompensateAuthorization[\s\S]*Matches\(entry, identity\)[\s\S]*OwnerState != AdmissionOwnerState\.WorkItem/);
});

test("every Editor transient gate precedes token consumption", async () => {
  const server = await read("unitypkg/Editor/Bridge/BridgeHttpServer.cs");
  const authorize = server.indexOf("AuthorizeAdmissionForQueue(context.Request, request, rawBody)");
  assert.ok(authorize > 0);
  for (const gate of ["if (domainReloading)", "if (cachedIsCompiling", "if (cachedIsUpdating", "if (cachedIsPlayModeChanging)"])
    assert.ok(server.indexOf(gate) > 0 && server.indexOf(gate) < authorize, `${gate} must precede consumption`);
  for (const code of ["E_UNITY_DOMAIN_RELOAD", "E_UNITY_COMPILING", "E_UNITY_IMPORTING", "E_UNITY_PLAY_MODE"])
    assert.ok(server.indexOf(code) < authorize, `${code} must have zero admission effect`);
});

test("Editor exposes queue-linearization compensation without changing BridgeWorkQueue", async () => {
  const [server, admission, queue] = await Promise.all([
    read("unitypkg/Editor/Bridge/BridgeHttpServer.cs"),
    read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs"),
    read("unitypkg/Editor/Bridge/BridgeWorkQueue.cs"),
  ]);
  assert.match(admission, /QueueLock是外层锁，admission pool锁是内层锁/);
  assert.match(admission, /AuthorizeAdmissionForQueue/);
  assert.match(admission, /CompensateAdmissionBeforeQueuePublication/);
  assert.match(admission, /MarkAdmissionQueuePublished/);
  assert.match(server, /AuthorizeAdmissionForQueue[\s\S]*EnqueueOrAttach\(request, rawBody, ListenerId, CurrentListenerGeneration\(\), admissionReservation\.Owner[\s\S]*MarkAdmissionQueuePublished/);
  assert.match(admission, /AuthorizeAndCapture\(rootId, fingerprint, requestDigest, token, ListenerId, CurrentListenerGeneration\(\), deadlineMs\)/);
  assert.match(server, /catch \(BridgeCommandException exception\)[\s\S]*CompensateAdmissionBeforeQueuePublication/);
  assert.match(server, /catch[\s\S]*CompensateAdmissionBeforeQueuePublication\(admissionReservation\)[\s\S]*throw/);
  assert.doesNotMatch(queue, /AuthorizeAndCapture|CompensateAuthorization|MarkAdmissionQueuePublished/);
});

test("durable transfer persists intent, transfers exact pool owner, then commits marker", async () => {
  const admission = await read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs");
  const transfer = admission.slice(admission.indexOf("internal static AdmissionOwnerIdentity PersistAdmissionOwnerTransfer"), admission.indexOf("internal static AdmissionOwnerIdentity ReconcileAdmissionTransferForLiveMarker"));
  const intentSave = transfer.indexOf("UnityCliActiveOperation.Save(markerScope");
  const prepare = transfer.indexOf("PrepareOwnerTransfer");
  const commit = transfer.indexOf("CommitOwnerTransfer");
  const committedFields = transfer.indexOf('marker["admissionTransferCommitted"] = true');
  const committedSave = transfer.lastIndexOf("UnityCliActiveOperation.Save(markerScope");
  assert.ok(intentSave >= 0 && intentSave < prepare && prepare < commit && commit < committedFields && committedFields < committedSave);
  for (const field of ["admissionRootFingerprint", "admissionToken", "admissionSessionId", "admissionRequestDigest", "admissionOwnerState", "admissionIntendedOwnerState", "admissionTransferCommitted"])
    assert.match(admission, new RegExp(`state\\["${field}"\\]`));
  assert.match(admission, /ReconcileAdmissionTransferForLiveMarker[\s\S]*current\.OwnerState != intended[\s\S]*PersistAdmissionOwnerTransfer/);
  assert.match(admission, /readable && durable \? target : UnityCliAdmissionPool\.AdmissionOwnerState\.Recovery/);
  assert.match(admission, /TryFindExactDurableBusinessRoot[\s\S]*record\.RequestId[\s\S]*record\.RequestDigest/);
  assert.match(admission, /ReconcileAdmissionMarker[\s\S]*current\.OwnerState != intended[\s\S]*CommitOwnerTransfer/);
  assert.match(admission, /AdmissionMarkerMatchesIdentity\(existing, identity\)[\s\S]*Admission transfer marker is owned by another exact request/);
  assert.doesNotMatch(admission, /AdmissionStateOwnsRequest|TryHasDurableAdmissionRoot\(string requestId/);
});

test("terminal cleanup retains explicit owner until lease and admission complete", async () => {
  const [reload, active] = await Promise.all([
    read("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs"),
    read("unitypkg/Editor/Infrastructure/UnityCliActiveOperation.cs"),
  ]);
  assert.match(reload, /PersistAdmissionOwnerTransfer\(PreEffectCleanupScope[\s\S]*AdmissionOwnerState\.TerminalCleanup/);
  assert.match(reload, /PersistAdmissionOwnerTransfer\(TerminalCleanupScope[\s\S]*AdmissionOwnerState\.TerminalCleanup/);
  assert.match(reload, /LeaseDefinitelyNotOwned\(requestId, requestDigest\)[\s\S]*CompleteAdmissionRoot\(admissionOwner\)[\s\S]*Clear\(TerminalCleanupScope\)/);
  assert.match(active, /PersistAdmissionOwnerTransfer\(cleanupScope[\s\S]*AdmissionOwnerState\.TerminalCleanup/);
  assert.match(active, /TryIsOwnedBy\(requestId, requestDigest[\s\S]*completeAdmission[\s\S]*CompleteAdmissionRoot\(admissionOwner\)[\s\S]*Clear\(cleanupScope\)/);
});

test("same-session reload state is one bounded startup-scoped snapshot", async () => {
  const [admission, pool] = await Promise.all([
    read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs"),
    read("unitypkg/Shared/UnityCliAdmissionPool.cs"),
  ]);
  assert.match(admission, /private const string AdmissionSessionStateKey/);
  assert.match(admission, /AdmissionText\(state, "editorSessionId"\) != EditorSessionId\(\)[\s\S]*EraseString/);
  assert.match(admission, /CaptureRecovery\(\)/);
  assert.match(pool, /CaptureRecovery[\s\S]*Where\(value => value\.Consumed\)/);
  assert.match(admission, /SessionState\.SetString\(AdmissionSessionStateKey/);
  assert.doesNotMatch(admission, /requestId.*\.json|Directory\.GetFiles/);
});
