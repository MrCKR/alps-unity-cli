import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { executeWithLocalRequestJournal } from "../src/local-request-journal.js";
import { success } from "../src/output.js";
import { computeRequestDigestV1 } from "../src/bridge/session-supervisor.js";
import { StructuredToolError } from "../src/tools/errors.js";
import { handleInit } from "../src/cli-commands/package.js";
import { resolveProjectIdentity } from "../src/project-identity.js";
import type { Runtime } from "../src/cli-commands/types.js";

const identity = { instanceId: "unity-local-journal-test", canonicalProjectPath: "C:/unity-local-journal-test" };

async function execution(dataDir: string, requestId = "request-1", input: Record<string, unknown> = { value: 1 }) {
  return { identity, requestId, toolName: "package.registry.set", input, projectPath: dataDir, dataDir, absoluteDeadlineMs: Date.now() + 5_000, executionClass: "atomic" as const, completionDriverId: "manifest-readback" };
}

test("local request journal deduplicates concurrent exact requests using real files", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  let effects = 0;
  const run = async (commitEffect: () => Promise<void>) => {
    await commitEffect();
    effects += 1;
    return success("request-1", { manifestPath: "manifest.json", registry: "https://registry.test" });
  };
  const request = await execution(dataDir);
  const [first, second] = await Promise.all([
    executeWithLocalRequestJournal(request, run),
    executeWithLocalRequestJournal(request, run),
  ]);
  assert.equal(effects, 1);
  assert.deepEqual(first, second);
});

test("local request journal rejects a concurrent requestId digest race before replaying a terminal result", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  let releaseFirst!: () => void;
  const firstReady = new Promise<void>((resolve) => { releaseFirst = resolve; });
  const first = executeWithLocalRequestJournal(await execution(dataDir, "digest-race", { value: 1 }), async (commitEffect) => {
    await commitEffect();
    await firstReady;
    return success("digest-race", { value: 1 });
  });
  await new Promise((resolve) => setTimeout(resolve, 20));
  const second = executeWithLocalRequestJournal(await execution(dataDir, "digest-race", { value: 2 }), async () => success("digest-race", { unexpected: true }));
  releaseFirst();
  const result = await second;
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_REQUEST_ID_REUSED");
  await first;
});

test("local request journal serializes different requestIds for one canonical instance", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  let active = 0;
  let maximum = 0;
  const run = async (requestId: string) => executeWithLocalRequestJournal(await execution(dataDir, requestId), async (commitEffect) => {
    await commitEffect();
    active += 1;
    maximum = Math.max(maximum, active);
    await new Promise((resolve) => setTimeout(resolve, 30));
    active -= 1;
    return success(requestId, { done: true });
  });
  await Promise.all([run("resource-a"), run("resource-b")]);
  assert.equal(maximum, 1);
});

test("local request journal atomically quarantines a dead resource lock for concurrent recoverers", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const requestDir = join(dataDir, "requests", identity.instanceId);
  await mkdir(requestDir, { recursive: true });
  await writeFile(join(requestDir, ".instance-resource.lock"), JSON.stringify({ pid: 999_999_999, token: "dead-owner" }));
  let active = 0;
  let maximum = 0;
  const run = async (requestId: string) => executeWithLocalRequestJournal(await execution(dataDir, requestId), async (commitEffect) => {
    await commitEffect();
    active += 1;
    maximum = Math.max(maximum, active);
    await new Promise((resolve) => setTimeout(resolve, 30));
    active -= 1;
    return success(requestId, { done: true });
  });
  await Promise.all([run("recoverer-a"), run("recoverer-b")]);
  assert.equal(maximum, 1);
});

test("local request journal claim prevents an interleaved recoverer from moving a new resource owner", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const requestDir = join(dataDir, "requests", identity.instanceId);
  await mkdir(requestDir, { recursive: true });
  const resourceLockPath = join(requestDir, ".instance-resource.lock");
  const deadRaw = "malformed dead resource lock";
  const fingerprint = createHash("sha256").update(Buffer.from(deadRaw, "utf8").toString("base64"), "utf8").digest("hex");
  const claimPath = `${resourceLockPath}.recovery-${fingerprint}`;
  await writeFile(resourceLockPath, deadRaw, "utf8");
  await writeFile(claimPath, JSON.stringify({ pid: process.pid, observedFingerprint: fingerprint }), { flag: "wx" });
  const request = { ...await execution(dataDir, "interleaved-recoverer"), absoluteDeadlineMs: Date.now() + 100 };
  const secondRecoverer = executeWithLocalRequestJournal(request, async () => success(request.requestId, { unexpected: true }));
  await new Promise((resolve) => setTimeout(resolve, 25));
  const newOwnerRaw = JSON.stringify({ pid: process.pid, token: "new-owner" });
  await rm(resourceLockPath, { force: true });
  await writeFile(resourceLockPath, newOwnerRaw, "utf8");
  await assert.rejects(
    () => secondRecoverer,
    (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT",
  );
  assert.equal(await readFile(resourceLockPath, "utf8"), newOwnerRaw);
  await rm(claimPath, { force: true });
  await rm(resourceLockPath, { force: true });
});

test("local request journal reports resource lock deadline as a structured timeout", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const requestDir = join(dataDir, "requests", identity.instanceId);
  await mkdir(requestDir, { recursive: true });
  const resourceLockPath = join(requestDir, ".instance-resource.lock");
  await writeFile(resourceLockPath, JSON.stringify({ pid: process.pid, token: "live-owner" }));
  const request = { ...await execution(dataDir, "resource-timeout"), absoluteDeadlineMs: Date.now() + 50 };
  await assert.rejects(
    () => executeWithLocalRequestJournal(request, async () => success(request.requestId, { unexpected: true })),
    (error: unknown) => error instanceof StructuredToolError && error.code === "E_EXECUTION_TIMEOUT",
  );
  await rm(resourceLockPath, { force: true });
});

test("local request journal does not report success after the absolute deadline", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const request = { ...await execution(dataDir, "late-success"), absoluteDeadlineMs: Date.now() + 200 };
  const result = await executeWithLocalRequestJournal(request, async (commitEffect) => {
    await commitEffect();
    await new Promise((resolve) => setTimeout(resolve, 225));
    return success(request.requestId, { done: true });
  });
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_REQUEST_OUTCOME_UNKNOWN");
});

test("local request journal binds CWD project A work to selected instance B identity", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const cwdProjectA = await mkdtemp(join(tmpdir(), "unity-cli-project-a-"));
  const selectedInstanceB = { instanceId: "unity-selected-instance-b", canonicalProjectPath: "C:\\Projects\\B" };
  const request = { ...(await execution(dataDir, "instance-b-request")), projectPath: cwdProjectA, identity: selectedInstanceB };
  const result = await executeWithLocalRequestJournal(request, async (commitEffect) => {
    await commitEffect();
    return success(request.requestId, { done: true });
  });
  assert.ok(result.ok);
  const journal = JSON.parse(await readFile(join(dataDir, "requests", selectedInstanceB.instanceId, "instance-b-request.json"), "utf8")) as { instanceId: string; phase: string; provenEnvelope?: unknown };
  assert.equal(journal.instanceId, selectedInstanceB.instanceId);
  assert.equal(journal.phase, "result_persisted");
  assert.equal(journal.provenEnvelope, undefined);
});

test("init rejects a plan made stale while waiting for the resource lock before commitEffect", { skip: process.platform !== "win32" }, async () => {
  const projectPath = await mkdtemp(join(tmpdir(), "unity-cli-init-stale-"));
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-init-journal-"));
  await mkdir(join(projectPath, "Assets"), { recursive: true });
  await mkdir(join(projectPath, "Packages"), { recursive: true });
  await mkdir(join(projectPath, "ProjectSettings"), { recursive: true });
  await writeFile(join(projectPath, "Packages", "manifest.json"), "{\"dependencies\":{}}\n", "utf8");
  await writeFile(join(projectPath, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.0f1\n", "utf8");
  const projectIdentity = resolveProjectIdentity(projectPath);
  const requestDir = join(dataDir, "requests", projectIdentity.instanceId);
  const resourceLockPath = join(requestDir, ".instance-resource.lock");
  const requestLockPath = join(requestDir, "init-stale.json.lock");
  await mkdir(requestDir, { recursive: true });
  await writeFile(resourceLockPath, JSON.stringify({ pid: process.pid, token: "init-waiter" }));
  const init = handleInit({ project: projectPath, "skill-only": true, agent: "pi" }, { dataDir } as Runtime, "init-stale");
  let requestLockAcquired = false;
  for (let attempt = 0; attempt < 100; attempt += 1) {
    try {
      await readFile(requestLockPath, "utf8");
      requestLockAcquired = true;
      break;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  }
  assert.equal(requestLockAcquired, true);
  await mkdir(join(projectPath, ".pi", "skills", "unity_cli"), { recursive: true });
  await writeFile(join(projectPath, ".pi", "skills", "unity_cli", "SKILL.md"), "stale target\n", "utf8");
  await rm(resourceLockPath, { force: true });
  const result = await init;
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "INIT_PLAN_STALE");
  const journal = JSON.parse(await readFile(join(requestDir, "init-stale.json"), "utf8")) as { phase: string; startedAt?: string };
  assert.equal(journal.phase, "result_persisted");
  assert.equal(journal.startedAt, undefined);
});

test("local request journal rejects a successful operation that misses the effect barrier", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const request = await execution(dataDir, "missing-commit");
  const result = await executeWithLocalRequestJournal(request, async () => success(request.requestId, { unexpected: true }));
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_TOOL_COMPLETION_PROOF_INVALID");
});

test("local request journal classifies an error after the durable barrier as outcome unknown", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const request = await execution(dataDir, "post-effect-error");
  const result = await executeWithLocalRequestJournal(request, async (commitEffect) => {
    await commitEffect();
    throw new Error("filesystem write failed after effect");
  });
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_REQUEST_OUTCOME_UNKNOWN");
});

test("local request journal rejects a requestId bound to different exact bytes", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const run = async (commitEffect: () => Promise<void>) => {
    await commitEffect();
    return success("request-1", { done: true });
  };
  await executeWithLocalRequestJournal(await execution(dataDir), run);
  const conflict = await executeWithLocalRequestJournal(await execution(dataDir, "request-1", { value: 2 }), run);
  assert.equal(conflict.ok, false);
  if (!conflict.ok) assert.equal(conflict.error.code, "E_REQUEST_ID_REUSED");
});

test("local request journal finalizes durable effect proof without replaying the operation", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const request = await execution(dataDir, "proven-request");
  const requestDir = join(dataDir, "requests", identity.instanceId);
  await mkdir(requestDir, { recursive: true });
  const absoluteDeadlineUtc = new Date(request.absoluteDeadlineMs).toISOString();
  const body = JSON.stringify({ protocolVersion: 2, requestId: request.requestId, instanceId: identity.instanceId, command: "local-tool.run", input: { name: request.toolName, input: request.input }, execution: { absoluteDeadlineUtc, completionContractVersion: 1, requestDigestVersion: 1 } });
  const provenEnvelope = success(request.requestId, { manifestPath: "manifest.json", registry: "https://registry.test" });
  await writeFile(join(requestDir, "proven-request.json"), JSON.stringify({
    version: 1, requestId: request.requestId, instanceId: identity.instanceId, editorSessionId: "session-test",
    command: "local-tool.run", toolName: request.toolName, digestVersion: 1, requestDigest: computeRequestDigestV1(identity.instanceId, Buffer.from(body, "utf8")),
    executionClass: "atomic", completionDriverId: request.completionDriverId, absoluteDeadlineUtc,
    state: "running", phase: "effect_proven", acceptedAt: new Date().toISOString(), startedAt: new Date().toISOString(),
    proofKind: "local-manifest-readback", proofSha256: createHash("sha256").update(JSON.stringify(provenEnvelope), "utf8").digest("hex"), provenEnvelope
  }));
  let replays = 0;
  const result = await executeWithLocalRequestJournal(request, async () => { replays += 1; return success(request.requestId, { unexpected: true }); });
  assert.equal(replays, 0);
  assert.deepEqual(result, provenEnvelope);
  const recovered = JSON.parse(await readFile(join(requestDir, "proven-request.json"), "utf8")) as { phase: string; provenEnvelope?: unknown };
  assert.equal(recovered.phase, "result_persisted");
  assert.equal(recovered.provenEnvelope, undefined);
});

test("local request journal marks a mismatched durable proof as outcome unknown", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const request = await execution(dataDir, "invalid-proven-request");
  const requestDir = join(dataDir, "requests", identity.instanceId);
  await mkdir(requestDir, { recursive: true });
  const absoluteDeadlineUtc = new Date(request.absoluteDeadlineMs).toISOString();
  const body = JSON.stringify({ protocolVersion: 2, requestId: request.requestId, instanceId: identity.instanceId, command: "local-tool.run", input: { name: request.toolName, input: request.input }, execution: { absoluteDeadlineUtc, completionContractVersion: 1, requestDigestVersion: 1 } });
  const journalPath = join(requestDir, "invalid-proven-request.json");
  await writeFile(journalPath, JSON.stringify({
    version: 1, requestId: request.requestId, instanceId: identity.instanceId, editorSessionId: "session-test",
    command: "local-tool.run", toolName: request.toolName, digestVersion: 1, requestDigest: computeRequestDigestV1(identity.instanceId, Buffer.from(body, "utf8")),
    executionClass: "atomic", completionDriverId: request.completionDriverId, absoluteDeadlineUtc,
    state: "running", phase: "effect_proven", acceptedAt: new Date().toISOString(), startedAt: new Date().toISOString(),
    proofKind: "local-manifest-readback", proofSha256: "0".repeat(64), provenEnvelope: success(request.requestId, { done: true })
  }));
  let replays = 0;
  const result = await executeWithLocalRequestJournal(request, async () => { replays += 1; return success(request.requestId, { unexpected: true }); });
  assert.equal(replays, 0);
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_REQUEST_OUTCOME_UNKNOWN");
  const persisted = JSON.parse(await readFile(journalPath, "utf8")) as { state: string; provenEnvelope?: unknown; terminalEnvelope?: { ok: boolean } };
  assert.equal(persisted.state, "outcome_unknown");
  assert.equal(persisted.provenEnvelope, undefined);
  assert.equal(persisted.terminalEnvelope?.ok, false);
});

test("local request journal recovers a crash after effect_started as outcome unknown", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-local-journal-"));
  const request = await execution(dataDir, "crashed-request");
  const requestDir = join(dataDir, "requests", identity.instanceId);
  await mkdir(requestDir, { recursive: true });
  const absoluteDeadlineUtc = new Date(request.absoluteDeadlineMs).toISOString();
  const body = JSON.stringify({ protocolVersion: 2, requestId: request.requestId, instanceId: identity.instanceId, command: "local-tool.run", input: { name: request.toolName, input: request.input }, execution: { absoluteDeadlineUtc, completionContractVersion: 1, requestDigestVersion: 1 } });
  await writeFile(join(requestDir, "crashed-request.json"), JSON.stringify({
    version: 1, requestId: request.requestId, instanceId: identity.instanceId, editorSessionId: "session-test",
    command: "local-tool.run", toolName: request.toolName, digestVersion: 1, requestDigest: computeRequestDigestV1(identity.instanceId, Buffer.from(body, "utf8")),
    executionClass: "atomic", completionDriverId: request.completionDriverId, absoluteDeadlineUtc,
    state: "running", phase: "effect_started", acceptedAt: new Date().toISOString(), startedAt: new Date().toISOString()
  }));
  const result = await executeWithLocalRequestJournal(request, async () => success(request.requestId, { unexpected: true }));
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, "E_REQUEST_OUTCOME_UNKNOWN");
});
