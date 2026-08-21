import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const read = (path: string) => readFile(path, "utf8");

test("Editor WorkItem releases only direct or non-accepted durable terminals", async () => {
  const [queue, admission] = await Promise.all([
    read("unitypkg/Editor/Bridge/BridgeWorkQueue.cs"),
    read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs"),
  ]);
  assert.match(queue, /AdmissionCoordinatorOwns/);
  assert.match(admission, /AdmissionResponseIsCoordinatorAccepted/);
  assert.match(admission, /TryGetConsumedIdentity/);
  assert.match(admission, /TryHasDurableAdmissionRoot/);
  assert.match(queue, /AdmissionResponseHasTrustedTerminal/);
  assert.match(queue, /rootAdmission[\s\S]*AdmissionOwner[\s\S]*Request\.RequestId/);
  assert.match(admission, /data\.TryGetValue\("terminal"[\s\S]*!\(bool\)terminal[\s\S]*data\.TryGetValue\("accepted"/);
  assert.match(admission, /code != "E_EXECUTION_TIMEOUT"/);
  assert.match(admission, /code != "E_UNITY_DOMAIN_RELOAD"/);
});

test("both durable cleanup owners release admission only after lease is not owned", async () => {
  const [reload, active] = await Promise.all([
    read("unitypkg/Editor/Infrastructure/UnityCliReloadOperation.cs"),
    read("unitypkg/Editor/Infrastructure/UnityCliActiveOperation.cs"),
  ]);
  assert.match(reload, /LeaseDefinitelyNotOwned\(requestId, requestDigest\)[\s\S]*CompleteAdmissionRoot\(admissionOwner\)[\s\S]*Clear\(TerminalCleanupScope\)/);
  assert.match(reload, /AddAdmissionOwnerFields\(marker, pendingTerminalAdmissionOwner\)/);
  assert.match(reload, /AddAdmissionOwnerFields\(marker, pendingPreEffectAdmissionOwner\)/);
  assert.match(active, /ReadAdmissionOwnerFields\(marker, requestId\)[\s\S]*TryIsOwnedBy\(requestId, requestDigest[\s\S]*completeAdmission[\s\S]*CompleteAdmissionRoot\(admissionOwner\)[\s\S]*Clear\(cleanupScope\)/);
  assert.match(active, /existing\["completeAdmission"\] = true/);
});

test("exact terminal replay can bypass a released admission without creating a new slot", async () => {
  const admission = await read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs");
  const server = await read("unitypkg/Editor/Bridge/BridgeHttpServer.cs");
  assert.match(server, /AdmissionRecoveredDirectTerminal\(request, rawBody\)[\s\S]*TryDeadlineRemainingMs/);
  assert.match(server, /AuthorizeAdmission\(context\.Request, request, rawBody\)/);
  assert.match(admission, /AdmissionHasExactTerminal\(request, rawRequestBytes\)/);
  assert.match(admission, /AdmissionRecoveredDirectTerminal/);
  assert.match(admission, /UnityCliRequestDigest\.Compute\(rawRequestBytes, request\.InstanceId\)/);
  assert.match(admission, /case "code\.exec":[\s\S]*"direct-mutation"/);
  assert.match(admission, /TryGetTerminal\(scope, request\.RequestId, digest/);
});

test("reload restores bounded consumed roots and reconciles fixed active owners", async () => {
  const [bridge, admission, pool] = await Promise.all([
    read("unitypkg/Editor/UnityCliBridge.cs"),
    read("unitypkg/Editor/Infrastructure/UnityCliAdmission.cs"),
    read("unitypkg/Shared/UnityCliAdmissionPool.cs"),
  ]);
  assert.match(bridge, /SaveAdmissionPoolBeforeReload\(\)/);
  assert.match(bridge, /ReconcileRestoredAdmissionRoots\(\)/);
  assert.match(admission, /RevokeUnconsumed\(ListenerId, CurrentListenerGeneration\(\)\)/);
  assert.match(admission, /"direct-mutation"/);
  assert.match(admission, /scope \+ "-pre-effect-cleanup"/);
  assert.match(admission, /scope \+ "-terminal-cleanup"/);
  assert.match(admission, /TryGetCurrentOwner/);
  assert.match(admission, /"cancelRequestId"/);
  assert.match(admission, /"activeCount"[\s\S]*"recoveryDigest"[\s\S]*"configuredMaxActive"/);
  assert.match(pool, /CompleteTrusted\(string requestId, string rootFingerprint, string token, string ownerSessionId, AdmissionOwnerKind ownerKind\)/);
  assert.match(pool, /TransferOwner\(/);
  assert.match(pool, /ValidConsumedRecord\(entry\)/);
  assert.match(pool, /expectedRecoveryDigest/);
  assert.match(pool, /recoveryBlocked = true;[\s\S]*gateClosed = true;/);
});
