import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const runtimePath = "unitypkg/Runtime/Infrastructure/UnityCliRuntimeBridge.cs";

test("Runtime admission precedes the existing bounded transport queue", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.match(source, /private const int MaxQueueItems = 128;/);
  assert.match(source, /private const int MaxRequestsPerFrame = 16;/);
  assert.match(source, /AuthorizeRuntimeAdmission\(context\.Request, envelope, out admissionOwner\)[\s\S]*ReserveRequestCacheSlot\(\)[\s\S]*pending\.Count >= MaxQueueItems/);
});

test("Runtime binds admission root headers to the strict Bridge body requestId", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.match(source, /var requestId = ReadString\(request, "requestId"\)/);
  assert.match(source, /!string\.Equals\(rootId, requestId, StringComparison\.Ordinal\)/);
  assert.match(source, /Runtime admission root requestId does not match the Bridge request body/);
  assert.match(source, /TryGetConsumedIdentity\(rootId, out admissionOwner\)/);
  assert.match(source, /PendingRequest[\s\S]*ConsumedIdentity AdmissionOwner/);
  assert.match(source, /CompleteRuntimeAdmissionRoot\(pendingRequest\.AdmissionOwner\)/);
});

test("Runtime Player state is process-local and sidecar configuration is strict version 2", async () => {
  const source = await readFile(runtimePath, "utf8");
  assert.match(source, /new UnityCliAdmissionPool\(config\.InstanceId, sessionId, config\.TaskPoolMaxActive, config\.TaskPoolReopenAt\)/);
  assert.match(source, /ReadLong\(value, "version"\) != 2L/);
  assert.match(source, /"taskPoolMaxActive", "taskPoolReopenAt"/);
  assert.match(source, /admissionPool\.Reset\(\); admissionPool = null/);
  for (const event of ["admission_granted", "admission_attached", "admission_revoked", "admission_terminal"]) assert.match(source, new RegExp(event));
  assert.doesNotMatch(source, /Debug\.Log\([^\n]*(?:Token|rootFingerprint|toolInput)/);
});
