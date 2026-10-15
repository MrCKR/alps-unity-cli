import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../src/cli.js";
import "../src/tools/.generated-registry.js";
import editorStateTool, { observeVerifiedRegistrySession, readEditorState, rejectUnavailableEditorState } from "../src/tools/editor/state.js";
import { defineToolMetadata } from "../src/tools/define.js";
import { EditorBlockerObserver } from "../src/editor-blocker-observer.js";
import { reduceObservationRegistryRecord, type ProjectProcessClaimInspection, type SessionProcessVerification } from "../src/instances.js";
import { registryIdentity, createUnityProject, makeRegistry, makeRegistrySession, writeRegistry } from "./helpers/instance-registry-v3.js";

const sourcePath = "src/tools/editor/state.ts";

test("editor.state is the sole hybrid observation owner with bounded fallback", async () => {
  const source = await readFile(sourcePath, "utf8");
  assert.match(source, /selectObservationInstanceForFlags/);
  assert.match(source, /verifyRegistrySessionProcess/);
  assert.match(source, /OBSERVATION_BUDGET_MS = 4_000/);
  assert.match(source, /BLOCKER_OBSERVATION_RESERVE_MS = 2_000/);
  assert.match(source, /observationDeadlineMs = Math\.min\(startedAt \+ OBSERVATION_BUDGET_MS, context\.absoluteDeadlineMs/);
  assert.match(source, /bridgeDeadlineMs = Math\.max\(Date\.now\(\), observationDeadlineMs - BLOCKER_OBSERVATION_RESERVE_MS\)/);
  assert.match(source, /if \(instance\.status === "conflict"\) return makeResult\(instance, undefined, "conflict"/);
  assert.doesNotMatch(source, /instance\.status === "unverifiable"\) return makeResult/);
  assert.match(source, /const selectObservation = dependencies\.selectObservation \?\? selectObservationInstanceForFlags/);
  assert.match(source, /selectObservation\(context\.flags \?\? \{\}, context\.dataDir, remaining\(observationDeadlineMs\)\)/);
  assert.match(source, /probeBridge\(context, bridgeDeadlineMs, session, selection\)/);
  assert.match(source, /withObservationSelection/);
  assert.match(source, /isLiveSnapshot/);
  assert.doesNotMatch(source, /observationBudgetMs|BRIDGE_PROBE_BUDGET_MS/);
  assert.match(source, /\{ instance: selection\.instance\.instanceId \}/);
  assert.doesNotMatch(source, /Promise\.race/);
  assert.match(source, /parseSnapshot\(data\.snapshot \?\? data, session\)/);
  assert.match(source, /verificationPromise = dependencies\.verifySession[\s\S]*context\.bridgeExecute[\s\S]*verifyRegistrySessionProcess/);
  assert.match(source, /processClaimsPromise = dependencies\.inspectClaims[\s\S]*context\.bridgeExecute[\s\S]*inspectProjectProcessClaims[\s\S]*probeBridge/);
  assert.match(source, /if \(bridge && isLiveSnapshot\(bridge\.snapshot\)\)[\s\S]*processClaims = await processClaimsPromise/);
  assert.match(source, /Promise\.all\(\[verificationPromise, processClaimsPromise\]\)[\s\S]*observeVerifiedRegistrySession/);
  assert.doesNotMatch(source, /probeWindowsEditorState/);
  assert.match(source, /verification !== "verified" \|\| processClaims\.verification !== "verified"[\s\S]*processClaims\.processIds\.length !== 1[\s\S]*session-process-mismatch[\s\S]*observer\.observe/);
  assert.match(source, /applyProjectProcessClaimInspection[\s\S]*inspected\.status === "conflict"/);
  assert.match(source, /verification === "dead"/);
  assert.match(source, /verification !== "verified"/);
  assert.match(source, /ProjectIdentityError/);
  assert.match(source, /Math\.max\(0, observationDeadlineMs - Date\.now\(\)\)/);
  assert.doesNotMatch(source, /local-request-journal|BM_CLICK|reloading\.json|deadlineUtc/);
});

test("editor.state distinguishes current idle observation from proof of a prior mutation effect", () => {
  assert.match(editorStateTool.description, /selected Unity project's active Editor session/i);
  assert.match(editorStateTool.description, /After a started mutation timeout/i);
  assert.match(editorStateTool.description, /affected business state before a new request/i);
  assert.doesNotMatch(editorStateTool.description, /safe to treat .*not-happened|directly resubmit/i);
});

test("editor state terminal availability statuses map to distinct stable errors", () => {
  const base = {
    identity: { instanceId: `unity-${"a".repeat(64)}`, canonicalProjectPath: "c:\\repo", displayProjectPath: "C:\\repo" },
    session: { known: false, editorSessionId: null },
    state: {}, progress: [], blockers: [], observation: { freshness: "unknown" },
  };
  const cases = [
    ["conflict", "E_INSTANCE_CONFLICT"],
    ["unverifiable", "E_INSTANCE_UNVERIFIABLE"],
    ["unresponsive", "E_UNITY_UNRESPONSIVE"],
    ["offline", "E_INSTANCE_NOT_RUNNING"],
  ] as const;
  for (const [status, code] of cases) {
    assert.throws(
      () => rejectUnavailableEditorState({ ...base, status } as never, "fixture.operation"),
      (error: unknown) => error instanceof Error && (error as { code?: string }).code === code,
    );
  }
  assert.doesNotThrow(() => rejectUnavailableEditorState({ ...base, status: "ready" } as never, "fixture.operation"));
  assert.doesNotThrow(() => rejectUnavailableEditorState({ ...base, status: "transitioning" } as never, "fixture.operation"));
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
  assert.match(source, /observedAtMs <= now/);
  assert.match(source, /field\.known \? Object\.prototype\.hasOwnProperty\.call\(field, "value"\)/);
  assert.match(source, /typeof record\.kind !== "string" \|\| record\.kind\.length === 0/);
  // 实时观察失败后不得用旧 Registry 快照回填当前状态字段或历史 progress。
  assert.match(source, /const blockers = sanitizeEditorBlockers\(observed\.blockers\)/);
  assert.match(source, /const status = blockers\.length > 0 \? "blocked" : "unresponsive"/);
  assert.match(source, /return makeResult\(inspected, session, status, undefined, \[\], blockers, false\)/);
  assert.doesNotMatch(source, /lastUnitySnapshot/);
  assert.doesNotMatch(source, /function fallbackStatus\(session: RegistrySession/);
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
  const cases: Array<{ name: string; verification: SessionProcessVerification; claims: ProjectProcessClaimInspection; outcome: "conflict" | "unproven" }> = [
    { name: "unverifiable", verification: "unverifiable", claims: { verification: "verified", processIds: [fixture.session.processId], processClaims: [correctClaim] }, outcome: "unproven" },
    { name: "duplicate", verification: "verified", claims: { verification: "verified", processIds: [fixture.session.processId, fixture.session.processId + 1], processClaims: [correctClaim, { processId: fixture.session.processId + 1, processStartedAt: fixture.session.processStartedAt }] }, outcome: "conflict" },
    { name: "reused", verification: "verified", claims: { verification: "verified", processIds: [fixture.session.processId], processClaims: [{ processId: fixture.session.processId, processStartedAt: "2026-01-01T00:00:00.000Z" }] }, outcome: "unproven" },
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

    const noProjectPathClaims: ProjectProcessClaimInspection = { verification: "verified", processIds: [], processClaims: [] };
    let registryProbes = 0;
    const registryObserver = new EditorBlockerObserver({
      inspect: async () => noProjectPathClaims,
      verifySession: async () => "verified",
      probe: async (processId) => {
        registryProbes += 1;
        assert.equal(processId, fixture.session.processId);
        return { supported: true, outcome: "observed-empty", windows: [] };
      },
    });
    const noProjectPath = await observeVerifiedRegistrySession(fixture.identity, fixture.session, Date.now() + 1_000, "verified", noProjectPathClaims, registryObserver);
    assert.equal(noProjectPath.outcome, "observed-empty");
    assert.equal(registryProbes, 1);

    const instance = reduceObservationRegistryRecord(makeRegistry(fixture.identity, [{ ...fixture.session, lifecycle: "ready" }]), new Map([[fixture.session.processId, {
      processId: fixture.session.processId, state: "alive" as const,
      processStartedAt: fixture.session.processStartedAt, executablePath: process.execPath,
    }]]));
    const result = await readEditorState({
      requestId: "registry-without-projectpath-catalog-failed",
      flags: { instance: fixture.project }, dataDir: fixture.dataDir,
      bridgeExecute: async (payload) => ({ ok: false, requestId: payload.requestId,
        error: { code: "E_TOOL_REGISTRY_UNHEALTHY", message: "Duplicate tool registration", nextActions: [], transient: false, retryAfterMs: 0 } }),
    }, {
      selectObservation: async () => ({ instance, matchedBy: "projectPath" }),
      verifySession: async () => "verified",
      inspectClaims: async () => noProjectPathClaims,
      observer: registryObserver,
    });
    assert.equal(result.status, "unresponsive");
    assert.equal(result.session.known, true);
    assert.equal(result.session.editorSessionId, fixture.session.editorSessionId);
    assert.equal(registryProbes, 2);

    const recovered = await readEditorState({
      requestId: "registry-without-projectpath-recovered",
      flags: { instance: fixture.project }, dataDir: fixture.dataDir,
      bridgeExecute: async (payload) => ({ ok: true, requestId: payload.requestId,
        data: { snapshot: snapshot(fixture.session), progress: [], blockers: [] } }),
    }, {
      selectObservation: async () => ({ instance, matchedBy: "projectPath" }),
      verifySession: async () => "verified",
      inspectClaims: async () => noProjectPathClaims,
      observer: registryObserver,
    });
    assert.equal(recovered.status, "ready");
    assert.equal(recovered.observation.freshness, "live");
    assert.equal(registryProbes, 2);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("readEditorState lets an exact modal override raw uncertainty but does not trust unverified active claims", async () => {
  const fixture = await stateFixture();
  const rawUnverifiable = reduceObservationRegistryRecord(makeRegistry(fixture.identity, [fixture.session]), new Map([[fixture.session.processId, {
    processId: fixture.session.processId,
    state: "unverifiable" as const,
  }]]));
  assert.equal(rawUnverifiable.status, "unverifiable");
  assert.equal(rawUnverifiable.activeSession, undefined);
  const context = {
    requestId: "state-raw-unverifiable",
    flags: { instance: fixture.project },
    dataDir: fixture.dataDir,
    bridgeExecute: async () => { throw new Error("must not post"); },
  };
  let observerCalls = 0;
  try {
    const blocked = await readEditorState(context, {
      selectObservation: async () => ({ instance: rawUnverifiable, matchedBy: "instanceId" as const }),
      observer: { observe: async () => {
        observerCalls += 1;
        return { outcome: "observed-blocked" as const, blockers: [{
          type: "dialog" as const,
          dialogId: "dialog_exact",
          title: "Exact Modal",
          message: "Exact Modal",
          buttons: ["Close"],
          requiresUserAction: true as const,
          source: "windows-ui" as const,
          confidence: "exact" as const,
        }] };
      } },
    });
    assert.equal(blocked.status, "blocked");
    assert.deepEqual(blocked.blockers.map((blocker) => blocker.title), ["Exact Modal"]);
    assert.equal(observerCalls, 1);

    const stillUnverifiable = await readEditorState({ ...context, requestId: "state-still-unverifiable" }, {
      selectObservation: async () => ({ instance: rawUnverifiable, matchedBy: "instanceId" as const }),
      observer: { observe: async () => ({ outcome: "unproven" as const, blockers: [], reason: "process-claims-unverifiable" }) },
    });
    assert.equal(stillUnverifiable.status, "unverifiable");

    let conflictObserverCalls = 0;
    const conflict = await readEditorState({ ...context, requestId: "state-conflict-priority" }, {
      selectObservation: async () => ({ instance: { ...rawUnverifiable, status: "conflict" as const }, matchedBy: "instanceId" as const }),
      observer: { observe: async () => { conflictObserverCalls += 1; return { outcome: "observed-empty" as const, blockers: [] }; } },
    });
    assert.equal(conflict.status, "conflict");
    assert.equal(conflictObserverCalls, 0);

    const active = reduceObservationRegistryRecord(makeRegistry(fixture.identity, [fixture.session]), new Map([[fixture.session.processId, {
      processId: fixture.session.processId,
      state: "alive" as const,
      processStartedAt: fixture.session.processStartedAt,
      executablePath: process.execPath,
    }]]));
    assert.ok(active.activeSession);
    const unverifiedClaims: ProjectProcessClaimInspection = { verification: "unverifiable", processIds: [] };
    let fallbackObserverCalls = 0;
    const fallbackUnverifiable = await readEditorState({ ...context, requestId: "state-active-fallback-unverifiable" }, {
      selectObservation: async () => ({ instance: active, matchedBy: "instanceId" as const }),
      verifySession: async () => "verified",
      inspectClaims: async () => unverifiedClaims,
      observer: { observe: async () => {
        fallbackObserverCalls += 1;
        return { outcome: "observed-blocked" as const, blockers: [] };
      } },
    });
    assert.equal(fallbackUnverifiable.status, "unverifiable");
    assert.deepEqual(fallbackUnverifiable.blockers, []);
    assert.equal(fallbackObserverCalls, 0);

    const fallbackEmpty = await readEditorState({ ...context, requestId: "state-active-fallback-empty" }, {
      selectObservation: async () => ({ instance: active, matchedBy: "instanceId" as const }),
      verifySession: async () => "verified",
      inspectClaims: async () => unverifiedClaims,
      observer: { observe: async () => ({ outcome: "observed-empty" as const, blockers: [] }) },
    });
    assert.equal(fallbackEmpty.status, "unverifiable");
    assert.equal(fallbackEmpty.session.known, false);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("readEditorState uses one live bounded bridge observation and sanitizes sensitive progress", async () => {
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
    const processClaims: ProjectProcessClaimInspection = {
      verification: "verified",
      processIds: [fixture.session.processId],
      processClaims: [{ processId: fixture.session.processId, processStartedAt: fixture.session.processStartedAt }],
    };
    const instance = reduceObservationRegistryRecord(makeRegistry(fixture.identity, [fixture.session]), new Map([[fixture.session.processId, {
      processId: fixture.session.processId,
      state: "alive" as const,
      processStartedAt: fixture.session.processStartedAt,
      executablePath: process.execPath,
    }]]));
    const direct = await readEditorState({ requestId: "state-direct", flags: { instance: fixture.project }, dataDir: fixture.dataDir, bridgeExecute }, {
      selectObservation: async () => ({ instance, matchedBy: "projectPath" }),
      verifySession: async () => "verified",
      inspectClaims: async () => processClaims,
    });
    assert.equal(direct.observation.freshness, "live");
    assert.equal(direct.status, "blocked");
    assert.ok(Date.now() - startedAt <= 4_100);
    assert.equal(observationDeadlines.length, 1);
    assert.ok(observationDeadlines[0] >= startedAt);
    assert.ok(observationDeadlines[0] <= startedAt + 2_050);
    assert.doesNotMatch(JSON.stringify(direct), /secret|bridgeEndpoint/i);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("an admitted hybrid Tool posts one admission-independent snapshot then continues its business path", async () => {
  const fixture = await stateFixture();
  const requests: Array<{ command: string; input: unknown }> = [];
  const snapshotValue = snapshot(fixture.session);
  snapshotValue.fields.playMode = { known: true, value: "edit" };
  const bridgeExecute = async (payload: { requestId: string; command: string; input: unknown }) => {
    requests.push({ command: payload.command, input: payload.input });
    if (payload.command === "internal.editor.state.snapshot") {
      return { ok: true as const, requestId: payload.requestId, data: { snapshot: snapshotValue, progress: [], blockers: [] } };
    }
    if (payload.command === "internal.asset.refresh.start") {
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          terminal: true,
          rootEnvelope: {
            requestId: payload.requestId,
            ok: true,
            data: { path: "Assets/Test.asset", options: "Default", refreshed: true, plannedEffects: [{ type: "asset.import", path: "Assets/Test.asset" }] },
          },
        },
      };
    }
    throw new Error(`unexpected ${payload.command}`);
  };
  try {
    const result = await execute(["tools", "run", "asset.refresh", "--input", '{"path":"Assets/Test.asset"}', "--instance", fixture.project], {
      dataDir: fixture.dataDir,
      bridgeExecute,
    } as never);
    assert.equal(result.ok, true, JSON.stringify(result));
    assert.deepEqual(requests.map((request) => request.command), ["internal.editor.state.snapshot", "internal.asset.refresh.start"]);
    assert.deepEqual(requests[0]?.input, {});
    assert.equal((result.data as { refreshed: boolean }).refreshed, true);
    const source = await readFile("src/tools/editor/state.ts", "utf8");
    assert.doesNotMatch(source, /rootAdmission:\s*context\.rootAdmission/);
    const businessSource = await readFile("src/tools/asset/refresh.ts", "utf8");
    assert.match(businessSource, /rootAdmission:\s*context\.rootAdmission/);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("readEditorState rejects future snapshots and reports duplicate-session uncertainty or offline without POST", async () => {
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
    assert.equal(result.observation.freshness, "unknown");
  } finally {
    Date.now = realNow;
    await rm(future.root, { recursive: true, force: true });
  }

  const conflict = await stateFixture(2);
  try {
    const result = await readEditorState({ requestId: "state-conflict", flags: { instance: conflict.project }, dataDir: conflict.dataDir, bridgeExecute: async () => { throw new Error("must not post"); } });
    assert.equal(result.status, "offline");
    assert.equal(result.observation.freshness, "unknown");
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
    assert.equal(result.observation.freshness, "unknown");
  } finally {
    await rm(offline, { recursive: true, force: true });
  }
});

test("editor.state ignores an obsolete live reloading marker", async () => {
  const fixture = await stateFixture();
  try {
    const markerDir = join(fixture.project, "Library", "UnityCli");
    await mkdir(markerDir, { recursive: true });
    await writeFile(join(markerDir, "reloading.json"), JSON.stringify({ deadlineUtc: new Date(Date.now() + 60_000).toISOString() }));

    const result = await readEditorState({
      requestId: "state-reloading",
      flags: { instance: fixture.project },
      dataDir: fixture.dataDir,
      bridgeExecute: async () => { throw new Error("bridge broken during reload"); },
    });
    assert.notEqual(result.status, "transitioning");
    assert.equal(result.observation.freshness, "unknown");
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});

test("editor.state ignores an obsolete expired reloading marker", async () => {
  const expired = await stateFixture();
  try {
    const markerDir = join(expired.project, "Library", "UnityCli");
    await mkdir(markerDir, { recursive: true });
    await writeFile(join(markerDir, "reloading.json"), JSON.stringify({ deadlineUtc: new Date(Date.now() - 1_000).toISOString() }));

    // reloading.json 已无产品owner；无论时间戳为何值都不得改变七态归约。
    const result = await readEditorState({
      requestId: "state-reloading-expired",
      flags: { instance: expired.project },
      dataDir: expired.dataDir,
      bridgeExecute: async () => { throw new Error("bridge broken"); },
    });
    assert.notEqual(result.status, "transitioning");
  } finally {
    await rm(expired.root, { recursive: true, force: true });
  }
});

test("generated editor.state descriptor is exactly the stable public output schema and validates actual output", async () => {
  const generated = defineToolMetadata().find((tool) => tool.name === "editor.state")?.outputSchema as Record<string, unknown>;
  const stable = JSON.parse(await readFile("contracts/stable-session/editor-state.schema.json", "utf8")) as Record<string, unknown>;
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

/** 捕获 rejectUnavailableEditorState 的稳定错误投影。 */
function captureUnverifiableError(state: Parameters<typeof rejectUnavailableEditorState>[0]): { code: string; details?: Record<string, unknown>; nextActions: string[] } {
  try {
    rejectUnavailableEditorState(state, "fixture.operation");
  } catch (error) {
    return error as { code: string; details?: Record<string, unknown>; nextActions: string[] };
  }
  throw new Error("expected E_INSTANCE_UNVERIFIABLE");
}

test("editor.state attributes an unverifiable read to its failing evidence source", async () => {
  const fixture = await stateFixture();
  try {
    const denied = reduceObservationRegistryRecord(makeRegistry(fixture.identity, [fixture.session]), new Map([[fixture.session.processId, {
      processId: fixture.session.processId,
      state: "unverifiable" as const,
      reason: "access-denied" as const,
    }]]));
    assert.equal(denied.status, "unverifiable");
    assert.equal(denied.activeSession, undefined);
    assert.equal(denied.claimReason, "access-denied");

    const deniedResult = await readEditorState({
      requestId: "state-evidence-denied", flags: { instance: fixture.project }, dataDir: fixture.dataDir,
      bridgeExecute: async () => { throw new Error("must not post"); },
    }, {
      selectObservation: async () => ({ instance: denied, matchedBy: "projectPath" as const }),
      observer: { observe: async () => ({ outcome: "unproven" as const, blockers: [], reason: "process-claims-unverifiable" }) },
    });
    assert.equal(deniedResult.status, "unverifiable");
    assert.equal(deniedResult.observation.reason, "access-denied");
    assert.equal(deniedResult.observation.freshness, "unknown");

    // observer 自己的归因优先于 instance 级归因，两者都只描述本次观察。
    const observedResult = await readEditorState({
      requestId: "state-evidence-timeout", flags: { instance: fixture.project }, dataDir: fixture.dataDir,
      bridgeExecute: async () => { throw new Error("must not post"); },
    }, {
      selectObservation: async () => ({ instance: { ...denied, claimReason: undefined }, matchedBy: "projectPath" as const }),
      observer: { observe: async () => ({ outcome: "unproven" as const, blockers: [], reason: "deadline", evidenceReason: "timeout" as const }) },
    });
    assert.equal(observedResult.observation.reason, "timeout");

    const timeoutError = captureUnverifiableError(observedResult);
    assert.deepEqual(timeoutError.details, { status: "unverifiable", reason: "timeout" });
    assert.match(timeoutError.nextActions.join("\n"), /observe the current identity again/);

    const permissionError = captureUnverifiableError(deniedResult);
    assert.deepEqual(permissionError.details, { status: "unverifiable", reason: "access-denied" });
    assert.match(permissionError.nextActions.join("\n"), /host denied OS process inspection/);
    assert.doesNotMatch(permissionError.nextActions.join("\n"), /observe the current identity again/);

    // 已证明 identity 的读取不披露证据来源。
    const active = reduceObservationRegistryRecord(makeRegistry(fixture.identity, [{ ...fixture.session, lifecycle: "ready" }]), new Map([[fixture.session.processId, {
      processId: fixture.session.processId, state: "alive" as const,
      processStartedAt: fixture.session.processStartedAt, executablePath: process.execPath,
    }]]));
    const ready = await readEditorState({
      requestId: "state-evidence-ready", flags: { instance: fixture.project }, dataDir: fixture.dataDir,
      bridgeExecute: async (payload) => ({ ok: true, requestId: payload.requestId, data: { snapshot: snapshot(fixture.session), progress: [], blockers: [] } }),
    }, {
      selectObservation: async () => ({ instance: active, matchedBy: "projectPath" as const }),
      verifySession: async () => "verified",
      inspectClaims: async () => ({ verification: "verified" as const, processIds: [fixture.session.processId], processClaims: [{ processId: fixture.session.processId, processStartedAt: fixture.session.processStartedAt }] }),
    });
    assert.equal(ready.status, "ready");
    assert.equal(ready.observation.reason, null);
  } finally {
    await rm(fixture.root, { recursive: true, force: true });
  }
});
