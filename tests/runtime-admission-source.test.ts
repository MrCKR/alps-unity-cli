import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const runtimePath = "unitypkg/Runtime/Infrastructure/UnityCliRuntimeBridge.cs";

test("Runtime uses the shared transactional request publication state", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.ok(source.includes("UnityCliRuntimeRequestPublication<PendingRequest>"));
  assert.ok(source.includes("requestCache.Plan(work.RequestId, work.RequestDigest"));
  assert.ok(source.indexOf("AuthorizeRuntimeAdmission(pool") < source.indexOf("TryPublishPreparedRequest(work"));
  assert.ok(source.indexOf("TryPublishPreparedRequest(work") < source.indexOf("requestCache.Publish(reservation"));
  assert.ok(source.includes("RuntimeTestBoundary"));
});

test("Runtime publication failure compensates the exact newly consumed owner", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.ok(source.includes("if (newlyConsumed && admissionOwner != null && pool != null) pool.CompensateAuthorization(admissionOwner)"));
  assert.ok(source.includes("requestCache.Publish(reservation, work"));
  assert.ok(source.includes("TryPublishPreparedRequest(PendingRequest work"));
  assert.ok(source.includes("pending.Enqueue(value)"));
  assert.ok(source.includes("pool.AuthorizeAndCapture(rootId, fingerprint, requestDigest"));
  assert.ok(source.includes("var pool = admissionPool"));
});

test("Runtime shutdown and completion capture one admission pool reference", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.ok(source.includes("stopping.Cancel()"));
  assert.ok(source.includes("requestCache.Clear()"));
  assert.ok(source.includes("admissionPool.Reset(); admissionPool = null"));
  assert.ok(source.includes("StopRuntimeRequestsLocked()"));
  const completion = source.indexOf("private void CompleteRuntimeAdmissionRoot");
  assert.ok(completion >= 0);
  assert.ok(source.indexOf("var pool = admissionPool", completion) >= completion);
  assert.ok(source.indexOf("pool.CompleteTrusted(identity)", completion) > completion);
});

test("Runtime admission remains bound to strict request identity", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.ok(source.includes("var requestId = ReadString(request, \"requestId\")"));
  assert.ok(source.includes("!string.Equals(rootId, requestId, StringComparison.Ordinal)"));
  assert.ok(source.includes("admissionOwner = result.Identity"));
  assert.ok(source.includes("ConsumedIdentity AdmissionOwner"));
});
