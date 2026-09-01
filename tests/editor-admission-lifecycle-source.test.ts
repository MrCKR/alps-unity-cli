import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const read = (path: string) => readFile(path, "utf8");

test("Admission pool has explicit owner states and no dead expiry outcome", async () => {
  const pool = await read("unitypkg/Shared/UnityCliAdmissionPool.cs");
  assert.ok(pool.includes("enum AdmissionOwnerState { Granted, WorkItem, Coordinator, Continuation, Recovery, TerminalCleanup }"));
  assert.ok(pool.includes("PrepareOwnerTransfer(ConsumedIdentity identity, AdmissionOwnerState intendedOwner)"));
  assert.ok(pool.includes("CommitOwnerTransfer(ConsumedIdentity identity, AdmissionOwnerState intendedOwner, out ConsumedIdentity successor)"));
  assert.ok(pool.includes("CanComplete"));
  assert.ok(!pool.includes("AuthorizeOutcome.Expired"));
  assert.ok(!pool.includes("Expired, IdentityMismatch"));
});

test("Queue settlement is outside QueueLock and has a finally signal path", async () => {
  const queue = await read("unitypkg/Editor/Bridge/BridgeWorkQueue.cs");
  const complete = queue.indexOf("private static void CompleteWorkItem");
  assert.ok(complete >= 0);
  assert.ok(queue.indexOf("UnityCliQueueSettlement.Complete", complete) > complete);
  assert.ok(queue.indexOf("item.Completed.Set()", complete) > complete);
  assert.ok(queue.indexOf("UnityCliTransactionLog.Append", complete) > queue.indexOf("item.Completed.Set()", complete));
  assert.ok(!queue.includes("QueuePublicationTestHook"));
});

test("Queue publication uses the shared rollback transaction and snapshots its barrier outside the lock", async () => {
  const [queue, admission] = await Promise.all([
    read("unitypkg/Editor/Bridge/BridgeWorkQueue.cs"),
    read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs"),
  ]);
  assert.ok(queue.includes("var durableMutationBarrier = CurrentDurableMutationBarrier()"));
  assert.ok(queue.includes("UnityCliQueuePublication.Publish"));
  assert.ok(queue.includes("if (!CompensateAdmissionBeforeQueuePublication(reservation))"));
  assert.ok(admission.includes("ReconcileOrphanAdmissionOwnerMarkers"));
  assert.ok(admission.includes("PersistAdmissionPoolState();"));
});

test("Accepted durable failure retains owner and direct mutations have a recoverable root", async () => {
  const admission = await read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs");
  assert.ok(admission.includes("enum AdmissionTransferResult"));
  assert.ok(admission.includes("TransferPending"));
  assert.ok(admission.includes("command == \"project-tool.run\" || command == \"code.exec\""));
  assert.ok(admission.includes("if (transfer == AdmissionTransferResult.TransferPending) return;"));
  assert.ok(admission.includes("AdmissionAcceptedTopDigest"));
});

test("Reload reconciliation accepts source committed and source pending transfer snapshots", async () => {
  const admission = await read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs");
  assert.ok(admission.includes("var sourceCommitted = record.TransferCommitted"));
  assert.ok(admission.includes("var sourcePending = !record.TransferCommitted"));
  assert.ok(admission.includes("var intendedCommitted = record.TransferCommitted"));
  assert.ok(admission.includes("current.OwnerState != intended"));
  assert.ok(admission.includes("CommitOwnerTransfer"));
});

test("Cancel ownership is bound to the parent digest and Continuation", async () => {
  const cancel = await read("unitypkg/Editor/Infrastructure/UnityCliTestRunCoordinator.cs");
  assert.ok(cancel.includes("cancelOwner.TopRequestDigest, root.RequestDigest"));
  assert.ok(cancel.includes("admissionRequestDigest\"), cancelDigest"));
  assert.ok(cancel.includes("AdmissionOwnerState.Continuation"));
  assert.ok(cancel.includes("CompleteAdmissionRoot(cancelOwner)"));
});

test("Owner marker cleanup uses immutable identity and fixed scopes", async () => {
  const [admission, active] = await Promise.all([
    read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs"),
    read("unitypkg/Editor/Infrastructure/UnityCliActiveOperation.cs"),
  ]);
  assert.ok(admission.includes("AdmissionMarkerHasImmutableIdentity"));
  assert.ok(admission.includes("ClearExactAdmissionOwnerMarkers"));
  assert.ok(admission.includes("scope + \"-admission-owner\""));
  assert.ok(active.includes("CompleteAdmissionRoot(admissionOwner)"));
  assert.ok(active.includes("Clear(cleanupScope)"));
});
