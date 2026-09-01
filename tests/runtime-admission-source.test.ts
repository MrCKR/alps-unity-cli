import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const runtimePath = "unitypkg/Runtime/Infrastructure/UnityCliRuntimeBridge.cs";

test("Runtime reserves independently reachable cache and queue capacity before consumption", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.match(source, /private const int MaxQueueItems = 128;/);
  assert.match(source, /private const int MaxRequestCacheItems = 160;/);
  assert.match(source, /private const int MaxRequestsPerFrame = 16;/);
  const cache = source.indexOf("if (!ReserveRequestCacheSlot())");
  const queue = source.indexOf("else if (pending.Count >= MaxQueueItems)");
  const authorize = source.indexOf("AuthorizeRuntimeAdmission(context.Request, envelope, digest, out admissionOwner)");
  assert.ok(cache >= 0 && cache < queue && queue < authorize);
  assert.match(source, /requestCache\.Count < MaxRequestCacheItems/);
  assert.doesNotMatch(source, /requestCache\.Count < MaxQueueItems/);
});

test("Runtime construction and publication failures complete the frozen WorkItem identity", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.match(source, /AuthorizeRuntimeAdmission[\s\S]*new PendingRequest[\s\S]*requestCache\.Add[\s\S]*pending\.Enqueue/);
  assert.match(source, /catch[\s\S]*requestCache\.Remove\(requestId\)[\s\S]*CompleteRuntimeAdmissionRoot\(admissionOwner\)[\s\S]*throw/);
  assert.match(source, /CompareExchange\(ref work\.ExecutionState, 2, 0\)[\s\S]*CompleteRuntimeAdmissionRoot\(work\.AdmissionOwner\)/);
  assert.match(source, /finally \{ if \(pendingRequest\.AdmissionOwner != null\) CompleteRuntimeAdmissionRoot/);
  assert.match(source, /CompleteTrusted\(identity\)/);
});

test("Runtime binds admission root to strict request identity and consumes atomically", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.match(source, /var requestId = ReadString\(request, "requestId"\)/);
  assert.match(source, /!string\.Equals\(rootId, requestId, StringComparison\.Ordinal\)/);
  assert.match(source, /AuthorizeAndCapture\(rootId, fingerprint, requestDigest, token, listenerId, 1L/);
  assert.match(source, /result\.Outcome != UnityCliAdmissionPool\.AuthorizeOutcome\.Authorized \|\| result\.Identity == null/);
  assert.match(source, /admissionOwner = result\.Identity/);
  assert.match(source, /PendingRequest[\s\S]*ConsumedIdentity AdmissionOwner/);
});

test("Runtime session shutdown invalidates bounded mechanism state", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.match(source, /new UnityCliAdmissionPool\(config\.InstanceId, sessionId, config\.TaskPoolMaxActive, config\.TaskPoolReopenAt\)/);
  assert.match(source, /stopping\.Cancel\(\)[\s\S]*requestCache\.Clear\(\)[\s\S]*admissionPool\.Reset\(\); admissionPool = null/);
  assert.match(source, /ReadLong\(value, "version"\) != 2L/);
  assert.match(source, /"taskPoolMaxActive", "taskPoolReopenAt"/);
  for (const event of ["admission_granted", "admission_attached", "admission_revoked", "admission_terminal"]) assert.match(source, new RegExp(event));
  assert.doesNotMatch(source, /Debug\.Log\([^\n]*(?:Token|rootFingerprint|toolInput)/);
});
