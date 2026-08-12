import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import "../src/tools/.generated-registry.js";
import { dispatchTool } from "../src/tools/dispatcher.js";
import { observeVerifiedRegistrySession, readEditorState } from "../src/tools/editor/state.js";
import { defineToolMetadata } from "../src/tools/define.js";
import { EditorBlockerObserver } from "../src/editor-blocker-observer.js";
import type { ProjectProcessClaimInspection, SessionProcessVerification } from "../src/instances.js";
import { registryIdentity, createUnityProject, makeRegistry, makeRegistrySession, writeRegistry } from "./helpers/instance-registry-v2.js";

const sourcePath = "src/tools/editor/state.ts";

test("editor.state is the sole hybrid observation owner with bounded fallback", async () => {
  const source = await readFile(sourcePath, "utf8");
  assert.match(source, /selectObservationInstanceForFlags/);
  assert.match(source, /verifyRegistrySessionProcess/);
  assert.match(source, /OBSERVATION_BUDGET_MS = 10_000/);
  assert.match(source, /observationDeadlineMs = Math\.min\(startedAt \+ OBSERVATION_BUDGET_MS, context\.absoluteDeadlineMs/);
  assert.match(source, /if \(instance\.status === "conflict"\) return makeResult\(instance, undefined, "conflict"/);
  assert.match(source, /selectObservationInstanceForFlags\(context\.flags \?\? \{\}, context\.dataDir, remaining\(observationDeadlineMs\)\)/);
  assert.match(source, /probeBridge\(context, observationDeadlineMs, session, selection\)/);
  assert.match(source, /withObservationSelection/);
  assert.match(source, /isLiveSnapshot/);
  assert.doesNotMatch(source, /observationBudgetMs|BRIDGE_PROBE_BUDGET_MS/);
  assert.match(source, /\{ instance: selection\.instance\.instanceId \}/);
  assert.doesNotMatch(source, /Promise\.race/);
  assert.match(source, /parseSnapshot\(data\.snapshot \?\? data, session\)/);
  assert.match(source, /verificationPromise = verifyRegistrySessionProcess[\s\S]*processClaimsPromise = inspectProjectProcessClaims[\s\S]*probeBridge/);
  assert.match(source, /if \(bridge && isLiveSnapshot\(bridge\.snapshot\)\)[\s\S]*processClaims = await processClaimsPromise/);
  assert.match(source, /Promise\.all\(\[verificationPromise, processClaimsPromise\]\)[\s\S]*observeVerifiedRegistrySession/);
  assert.doesNotMatch(source, /probeWindowsEditorState/);
  assert.match(source, /verification !== "verified" \|\| processClaims\.verification !== "verified"[\s\S]*processClaims\.processIds\.length !== 1[\s\S]*session-process-mismatch[\s\S]*observer\.observe/);
  assert.match(source, /windowsUi: "not-observed" \| "observed-empty" \| "observed-blocked" \| "unproven" \| "unsupported"/);
  assert.match(source, /applyProjectProcessClaimInspection[\s\S]*inspected\.status === "conflict"/);
  assert.match(source, /verification === "dead"/);
  assert.match(source, /verification !== "verified"/);
  assert.match(source, /ProjectIdentityError/);
  assert.match(source, /Math\.max\(0, observationDeadlineMs - Date\.now\(\)\)/);
  assert.doesNotMatch(source, /local-request-journal|BM_CLICK/);
});

test("editor.state accepts only a complete matching v1 snapshot and strips transport internals", async () => {
  const source = await readFile(sourcePath, "utf8");
  assert.match(source, /Object\.keys\(fields\)\.length !== STATE_FIELDS\.length/);
  assert.match(source, /raw\.sourceEditorSessionId !== session\.editorSessionId/);
  assert.match(source, /raw\.sourceListenerGeneration !== session\.listener\.generation/);
  assert.match(source, /hasExactKeys\(raw, \["schemaVersion", "observedAt", "sourceEditorSessionId", "sourceListenerGeneration", "fields"\]\)/);
  assert.match(source, /SENSITIVE_KEYS = new Set\(\["endpoint", "endpointurl", "bridgeendpoint", "bridgeendpointurl"/);
  assert.match(source, /observedAtMs > Date\.now\(\)/);
  assert.doesNotMatch(source, /FUTURE_SNAPSHOT_TOLERANCE_MS/);
  assert.match(source, /key\.replace\(\/\[\^a-z0-9\]\/gi, ""\)\.toLowerCase\(\)/);
  assert.match(source, /dataObservedAt/);
  assert.match(source, /dataAgeMs/);
  assert.match(source, /observedAtMs <= now/);
  assert.match(source, /field\.known \? Object\.prototype\.hasOwnProperty\.call\(field, "value"\)/);
  assert.match(source, /typeof record\.kind !== "string" \|\| record\.kind\.length === 0/);
  assert.match(source, /function fallbackStatus\(session: RegistrySession/);
});

async function stateFixture(sessionCount = 1) {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-editor-state-"));
  const project = join(root, "Project");
  const dataDir = join(root, "data");
  await createUnityProject(project);
  const identity = registryIdentity(project);
  const sessions = await Promise.all(Array.from({ length: sessionCount }, (_, index) => makeRegistrySession(`http://127.0.0.1:${49000 + index}`, {
    editorSessionId: `session-${String(index + 1).repeat(64)}`,
    listener: {
      listenerId: `listener-${String(index + 2).repeat(32)}`,
      generation: index + 1,
      startedAt: new Date().toISOString(),
      endpoint: { host: "127.0.0.1", preferredPort: 49000 + index, actualPort: 49000 + index, fallback: false, url: `http://127.0.0.1:${49000 + index}` },
    },
  })));
  await writeRegistry(dataDir, makeRegistry(identity, sessions));
  return { root, project, dataDir, identity, session: sessions[0] };
}

function snapshot(session: Awaited<ReturnType<typeof makeRegistrySession>>, observedAt = new Date().toISOString()) {
  return {
    schemaVersion: 1,
    observedAt,
    sourceEditorSessionId: session.editorSessionId,
    sourceListenerGeneration: session.listener.generation,
    fields: {
      compile: { known: true, value: false }, import: { known: false }, build: { known: false },
      playMode: { known: true, value: "stopped" }, prefabStage: { known: false }, activeScene: { known: false }, runtime: { known: false },
    },
  };
}

test("editor.state fallback performs zero UI probes for unverified, duplicate, or reused process claims", async () => {
  const fixture = await stateFixture();
  const correctClaim = { processId: fixture.session.processId, processStartedAt: fixture.session.processStartedAt };
  const cases: Array<{ name: string; verification: SessionProcessVerification; claims: ProjectProcessClaimInspection; outcome: "conflict" }> = [
    { name: "unverifiable", verification: "unverifiable", claims: { verification: "verified", processIds: [fixture.session.processId], processClaims: [correctClaim] }, outcome: "conflict" },
    { name: "duplicate", verification: "verified", claims: { verification: "verified", processIds: [fixture.session.processId, fixture.session.processId + 1], processClaims: [correctClaim, { processId: fixture.session.processId + 1, processStartedAt: fixture.session.processStartedAt }] }, outcome: "conflict" },
    { name: "reused", verification: "verified", claims: { verification: "verified", processIds: [fixture.session.processId], processClaims: [{ processId: fixture.session.processId, processStartedAt: "2026-01-01T00:00:00.000Z" }] }, outcome: "conflict" },
  ];
  try {
    for (const entry of cases) {
      let probes = 0;
      const observer = new EditorBlockerObserver({
        inspect: async () => entry.claims,
        probe: async () => { probes += 1; return { supported: true, outcome: "observed-empty", windows: [] }; },
      });
      const result = await observeVerifiedRegistrySession(fixture.identity, fixture.session, Date.now() + 1_000, entry.verification, entry.claims, observer);
      assert.equal(result.outcome, entry.outcome, entry.name);
      assert.equal(probes, 0, entry.name);
    }

    let verifiedProbes = 0;
    const verifiedClaims: ProjectProcessClaimInspection = { verification: "verified", processIds: [fixture.session.processId], processClaims: [correctClaim] };
    const verified = await observeVerifiedRegistrySession(fixture.identity, fixture.session, Date.now() + 1_000, "verified", verifiedClaims, new EditorBlockerObserver({
      inspect: async () => verifiedClaims,
      probe: async () => { verifiedProbes += 1; return { supported: true, outcome: "observed-empty", windows: [] }; },
    }));
    assert.equal(verified.outcome, "observed-empty");
    assert.equal(verifiedProbes, 1);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("readEditorState and dispatchTool use one live bounded bridge observation and sanitize sensitive progress", async () => {
  const fixture = await stateFixture();
  const startedAt = Date.now();
  const observationDeadlines: number[] = [];
  const bridgeExecute = async (payload: { requestId: string; command: string; execution: { absoluteDeadlineUtc: string } }) => {
    observationDeadlines.push(Date.parse(payload.execution.absoluteDeadlineUtc));
    return {
      ok: true as const,
      requestId: payload.requestId,
    data: {
      snapshot: snapshot(fixture.session),
      progress: [{ kind: "import", title: "Importing", value: 0.5, canWait: true, source: "unity", bridgeEndpointUrl: "http://secret" }],
      blockers: [{ type: "dialog", dialogId: "dialog_fixture", title: "Dialog", message: "Dialog", buttons: ["OK"], requiresUserAction: true, source: "windows-ui", confidence: "exact", bridgeEndpoint: "http://secret" }],
    },
  };
  };
  try {
    const direct = await readEditorState({ requestId: "state-direct", flags: { instance: fixture.project }, dataDir: fixture.dataDir, bridgeExecute });
    assert.equal(direct.observation.live, true);
    assert.equal(direct.status, "blocked");
    assert.ok(direct.observation.elapsedMs <= 10_100);
    assert.ok(Date.now() - startedAt <= 10_100);
    assert.doesNotMatch(JSON.stringify(direct), /secret|bridgeEndpoint/i);

    const dispatchedStartedAt = Date.now();
    const dispatched = await dispatchTool("editor.state", undefined, { instance: fixture.project }, { dataDir: fixture.dataDir, bridgeExecute }, "state-dispatch");
    assert.ok(dispatched.ok);
    assert.equal((dispatched.data as { observation: { live: boolean } }).observation.live, true);
    assert.ok(observationDeadlines[1] >= dispatchedStartedAt);
    assert.ok(observationDeadlines[1] <= Date.now() + 10_000);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("readEditorState rejects future snapshots and reports conflict or offline from the registry without POST", async () => {
  const future = await stateFixture();
  const realNow = Date.now;
  const fixedNow = 1_000_000;
  let posts = 0;
  Date.now = () => fixedNow;
  try {
    const result = await readEditorState({
      requestId: "state-future", flags: { instance: future.project }, dataDir: future.dataDir,
      bridgeExecute: async (payload) => { posts += 1; return { ok: true, requestId: payload.requestId, data: { snapshot: snapshot(future.session, new Date(fixedNow + 1).toISOString()) } }; },
    });
    assert.equal(posts, 1);
    assert.equal(result.observation.live, false);
    assert.notEqual(result.observation.freshness, "live");
  } finally {
    Date.now = realNow;
    await rm(future.root, { recursive: true, force: true });
  }

  const conflict = await stateFixture(2);
  try {
    const result = await readEditorState({ requestId: "state-conflict", flags: { instance: conflict.project }, dataDir: conflict.dataDir, bridgeExecute: async () => { throw new Error("must not post"); } });
    assert.equal(result.status, "conflict");
    assert.equal(result.observation.live, false);
  } finally {
    await rm(conflict.root, { recursive: true, force: true });
  }

  const offline = await mkdtemp(join(tmpdir(), "unity-cli-editor-state-offline-"));
  const project = join(offline, "Project");
  const dataDir = join(offline, "data");
  await createUnityProject(project);
  await writeRegistry(dataDir, makeRegistry(registryIdentity(project), []));
  try {
    const result = await readEditorState({ requestId: "state-offline", flags: { instance: project }, dataDir, bridgeExecute: async () => { throw new Error("must not post"); } });
    assert.equal(result.status, "offline");
    assert.equal(result.observation.live, false);
  } finally {
    await rm(offline, { recursive: true, force: true });
  }
});

test("generated editor.state descriptor is exactly the stable public output schema and validates actual output", async () => {
  const generated = defineToolMetadata().find((tool) => tool.name === "editor.state")?.outputSchema as Record<string, unknown>;
  const stable = JSON.parse(await readFile("contracts/stable-session/editor-state.schema.json", "utf8")) as Record<string, unknown>;
  const observation = (stable.properties as Record<string, { properties: Record<string, { maximum?: number }> }>).observation;
  // 3 秒是内部观察 deadline，不是受事件循环和 schema 校验开销约束的公共耗时上限。
  assert.equal(observation.properties.elapsedMs.maximum, undefined);
  assert.deepEqual(generated, stable);
  const fixture = await stateFixture();
  try {
    const result = await readEditorState({ requestId: "state-schema", flags: { instance: fixture.project }, dataDir: fixture.dataDir, bridgeExecute: async (payload) => ({ ok: true, requestId: payload.requestId, data: { snapshot: snapshot(fixture.session) } }) });
    const { validateSchema } = await import("../src/schema/validator.js");
    assert.deepEqual(validateSchema(generated, result), []);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});
