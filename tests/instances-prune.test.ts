import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../src/cli.js";
import {
  InstanceRegistryError,
  applyProjectProcessClaimInspection,
  instanceListSummary,
  probeProcessObservations,
  readInstances,
  readInstancesWithRegistryDiagnostics,
  reduceObservationRegistryRecord,
  reduceRegistryRecord,
  selectInstanceForFlags,
} from "../src/instances.js";
import { clearProjectIdentityCache } from "../src/project-identity.js";
import {
  createUnityProject,
  currentProcessStartedAt,
  makeRegistry,
  makeRegistrySession,
  registryIdentity,
  startStrictHealthServer,
  writeRegistry,
} from "./helpers/instance-registry-v3.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

/** 创建严格 Unity project identity 夹具。 */
async function project(prefix: string): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), prefix));
  await createUnityProject(root);
  clearProjectIdentityCache();
  return root;
}

/** 写入一个名称合法但内容不属于Registry v3的单文件夹具。 */
async function writeInvalidRegistry(dataDir: string, instanceId: string, value: unknown = { version: 2 }): Promise<string> {
  const directory = join(dataDir, "instances");
  await mkdir(directory, { recursive: true });
  const path = join(directory, `${instanceId}.json`);
  await writeFile(path, `${JSON.stringify(value)}\n`, "utf8");
  return path;
}

test("registry writer remains one v3 file per canonical project with durable replacement", async () => {
  const source = await readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8");
  assert.ok(source.includes('WithCurrentUserMutex("Local\\\\UnityCli.Registry." + InstanceId()'));
  assert.match(source, /stream\.Flush\(true\)/);
  assert.match(source, /File\.Replace\(temporaryPath, path, null\)/);
  assert.match(source, /"version", 3L/);
  assert.match(source, /"sessions", sessions\.Cast<object>\(\)\.ToList\(\)/);
  assert.match(source, /"publishedAt", DateTime\.UtcNow\.ToString\("O", CultureInfo\.InvariantCulture\)/);
  assert.match(source, /"publicationSequence", sequence/);
  assert.doesNotMatch(source, /instances\.json|projectHash.*processId/);
});

test("Editor and Runtime publish lifecycle changes immediately with a thirty-second fallback refresh", async () => {
  const [bridge, workQueue, runtime] = await Promise.all([
    readFile("unitypkg/Editor/UnityCliBridge.cs", "utf8"),
    readFile("unitypkg/Editor/Bridge/BridgeWorkQueue.cs", "utf8"),
    readFile("unitypkg/Runtime/Infrastructure/UnityCliRuntimeBridge.cs", "utf8"),
  ]);
  assert.match(bridge, /private const double RegistryRefreshSeconds = 30\.0/);
  assert.match(bridge, /RegisterInstance\(playModeChanging \? "transitioning" : "ready"\)/);
  assert.match(bridge, /nextRegistryRefreshAt = EditorApplication\.timeSinceStartup \+ RegistryRefreshSeconds/);
  assert.match(workQueue, /EditorApplication\.timeSinceStartup >= nextRegistryRefreshAt/);
  assert.match(workQueue, /nextRegistryRefreshAt = EditorApplication\.timeSinceStartup \+ RegistryRefreshSeconds/);
  assert.match(runtime, /private const int RegistryRefreshSeconds = 30/);
  assert.match(runtime, /nextRegistryRefreshAt = Time\.realtimeSinceStartup \+ RegistryRefreshSeconds/);
  assert.doesNotMatch(`${bridge}\n${workQueue}\n${runtime}`, /nextHeartbeatAt|HeartbeatSeconds|\+ 2\.0/);
});

test("instances prune only reports C# writer-owned cleanup and never rewrites registry", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-prune-"));
  const projectPath = await project("unity-cli-prune-project-");
  const identity = registryIdentity(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:47991", {
    publishedAt: new Date(Date.now() - 60_000).toISOString(),
  });
  await writeRegistry(dataDir, makeRegistry(identity, [session]));
  const path = join(dataDir, "instances", `${identity.instanceId}.json`);
  const before = await readFile(path, "utf8");
  const result = await execute(["instances", "prune"], { dataDir } as never);
  const after = await readFile(path, "utf8");

  assert.ok(result.ok);
  assert.equal((result.data as { removed: number; writer: string }).removed, 0);
  assert.equal((result.data as { writer: string }).writer, "unity-bridge");
  assert.equal(Object.hasOwn(result.data as object, "registryPath"), false);
  assert.equal(after, before);
});

test("instances list hides offline instances by default and reveals compact summaries with --all", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-instances-filter-"));
  const activePath = await project("unity-cli-filter-active-");
  const activeIdentity = registryIdentity(activePath);
  const session = await makeRegistrySession("http://127.0.0.1:47991");
  await writeRegistry(dataDir, makeRegistry(activeIdentity, [session]));

  const offlinePath = await project("unity-cli-filter-offline-");
  const offlineIdentity = registryIdentity(offlinePath);
  await writeRegistry(dataDir, makeRegistry(offlineIdentity, []));

  const list = await execute(["instances", "list"], { dataDir } as never);
  assert.ok(list.ok, JSON.stringify(list));
  const data = list.data as { instances: Array<Record<string, unknown>>; hiddenOfflineCount?: number };
  assert.deepEqual(data.instances.map((instance) => instance.instanceId), [activeIdentity.instanceId]);
  assert.equal(data.hiddenOfflineCount, 1);
  assert.deepEqual(Object.keys(data.instances[0]).sort(), ["canonicalProjectPath", "displayProjectPath", "instanceId", "projectName", "status", "unityVersion"]);

  const all = await execute(["instances", "list", "--all"], { dataDir } as never);
  assert.ok(all.ok, JSON.stringify(all));
  const allData = all.data as { instances: Array<Record<string, unknown>> };
  assert.equal(allData.instances.length, 2);
  assert.deepEqual(
    allData.instances.map((instance) => instance.instanceId).sort(),
    [activeIdentity.instanceId, offlineIdentity.instanceId].sort(),
  );
  assert.equal(Object.hasOwn(all.data as object, "hiddenOfflineCount"), false);
  const offline = allData.instances.find((instance) => instance.instanceId === offlineIdentity.instanceId)!;
  assert.deepEqual(Object.keys(offline).sort(), ["canonicalProjectPath", "displayProjectPath", "instanceId", "projectName", "status"]);
  assert.equal(offline.status, "offline");
});

test("instances list returns an accurate hidden count when every instance is offline", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-instances-all-offline-"));
  const identities = [];
  for (const prefix of ["first", "second"]) {
    const identity = registryIdentity(await project(`unity-cli-${prefix}-offline-`));
    identities.push(identity);
    await writeRegistry(dataDir, makeRegistry(identity, []));
  }

  const list = await execute(["instances", "list"], { dataDir } as never);
  assert.ok(list.ok, JSON.stringify(list));
  assert.deepEqual(list.data, { instances: [], hiddenOfflineCount: 2 });

  const all = await execute(["instances", "list", "--all"], { dataDir } as never);
  assert.ok(all.ok, JSON.stringify(all));
  const allData = all.data as { instances: Array<Record<string, unknown>> };
  assert.equal(allData.instances.length, identities.length);
  assert.equal(Object.hasOwn(all.data as object, "hiddenOfflineCount"), false);
  assert.ok(allData.instances.every((instance) => Object.keys(instance).sort().join(",") === "canonicalProjectPath,displayProjectPath,instanceId,projectName,status"));
});

test("instances list omits hiddenOfflineCount when no offline instance is hidden", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-instances-no-offline-"));
  const identity = registryIdentity(await project("unity-cli-no-offline-"));
  await writeRegistry(dataDir, makeRegistry(identity, [await makeRegistrySession("http://127.0.0.1:47991")]));

  const list = await execute(["instances", "list"], { dataDir } as never);
  assert.ok(list.ok, JSON.stringify(list));
  assert.equal((list.data as { instances: unknown[] }).instances.length, 1);
  assert.equal(Object.hasOwn(list.data as object, "hiddenOfflineCount"), false);
  assert.equal(Object.hasOwn(list.data as object, "invalidRecordCount"), false);
});

test("full instance views isolate invalid records while exact selection still fails closed", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-invalid-registry-mixed-"));
  const validIdentity = registryIdentity(await project("unity-cli-invalid-registry-valid-"));
  await writeRegistry(dataDir, makeRegistry(validIdentity, []));
  const invalidIdentity = registryIdentity(await project("unity-cli-invalid-registry-bad-"));
  const invalidPath = await writeInvalidRegistry(dataDir, invalidIdentity.instanceId);
  const invalidBefore = await readFile(invalidPath, "utf8");

  const diagnostics = await readInstancesWithRegistryDiagnostics(dataDir);
  assert.equal(diagnostics.instances.length, 1);
  assert.equal(diagnostics.instances[0].instanceId, validIdentity.instanceId);
  assert.equal(diagnostics.invalidRecordCount, 1);

  const list = await execute(["instances", "list"], { dataDir } as never);
  assert.ok(list.ok, JSON.stringify(list));
  assert.deepEqual(list.data, { instances: [], hiddenOfflineCount: 1, invalidRecordCount: 1 });

  const all = await execute(["instances", "list", "--all"], { dataDir } as never);
  assert.ok(all.ok, JSON.stringify(all));
  const allData = all.data as { instances: Array<{ instanceId: string }>; invalidRecordCount: number };
  assert.deepEqual(allData.instances.map((instance) => instance.instanceId), [validIdentity.instanceId]);
  assert.equal(allData.invalidRecordCount, 1);
  assert.equal(Object.hasOwn(all.data as object, "hiddenOfflineCount"), false);
  assert.doesNotMatch(JSON.stringify(all), new RegExp(invalidPath.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i"));

  const prune = await execute(["instances", "prune"], { dataDir } as never);
  assert.ok(prune.ok, JSON.stringify(prune));
  assert.equal((prune.data as { remaining: number }).remaining, 1);
  assert.equal((prune.data as { invalidRecordCount: number }).invalidRecordCount, 1);
  assert.equal(await readFile(invalidPath, "utf8"), invalidBefore);

  await assert.rejects(
    selectInstanceForFlags({ instance: invalidIdentity.instanceId }, dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH"
      && !JSON.stringify(error).includes(invalidPath),
  );
});

test("instances list reports all invalid records without inventing offline instances", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-invalid-registry-all-"));
  for (const suffix of ["first", "second"]) {
    const identity = registryIdentity(await project(`unity-cli-invalid-registry-${suffix}-`));
    await writeInvalidRegistry(dataDir, identity.instanceId, suffix === "first" ? { version: 2 } : "not-a-registry");
  }

  const list = await execute(["instances", "list"], { dataDir } as never);
  assert.ok(list.ok, JSON.stringify(list));
  assert.deepEqual(list.data, { instances: [], invalidRecordCount: 2 });

  const all = await execute(["instances", "list", "--all"], { dataDir } as never);
  assert.ok(all.ok, JSON.stringify(all));
  assert.deepEqual(all.data, { instances: [], invalidRecordCount: 2 });
});

test("Windows process observation proves the current PID under the production startup budget", { skip: process.platform !== "win32" }, async () => {
  const observation = (await probeProcessObservations([process.pid])).get(process.pid);
  assert.equal(observation?.state, "alive");
  assert.ok(observation?.processStartedAt);
  assert.match(observation?.executablePath ?? "", /node(?:\.exe)?$/i);
});

test("normal selector retries only an unverifiable read-only process claim", async () => {
  const source = await readFile("src/instances.ts", "utf8");
  assert.match(source, /inspectProjectProcessClaimsWithConvergence\(selection\.identity\)/);
  assert.match(source, /if \(first\.verification !== "unverifiable"\) return first;[\s\S]*setTimeout\(resolveDelay, 50\)[\s\S]*inspectProjectProcessClaims\(identity, PROJECT_PROCESS_CLAIM_TIMEOUT_MS\)/);
});

test("exact instance selector survives one transient missing-file window and trusts strict health over an old timestamp", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-exact-selector-race-"));
  const projectPath = await project("unity-cli-exact-selector-project-");
  const identity = registryIdentity(projectPath);
  const sessionId = `session-${"9".repeat(64)}`;
  const listenerId = `listener-${"8".repeat(32)}`;
  const health = await startStrictHealthServer((_request, port) => ({
    version: 3,
    target: "runtime",
    instanceId: identity.instanceId,
    canonicalProjectPath: identity.canonicalProjectPath,
    canonicalizationVersion: 1,
    sessionId,
    listenerId,
    listenerGeneration: 1,
    actualPort: port,
    lifecycle: "ready",
    publishedAt: new Date(Date.now() - 60_000).toISOString(),
    mainThread: { observed: true, sequence: 10, ageMs: 0, listenerGeneration: 1, lifecycle: "ready" },
    buildStamp: "test-build",
    buildId: "a".repeat(32),
  }));
  const base = await makeRegistrySession(health.endpoint, {
    target: "runtime",
    sessionId,
    runtimeSessionId: sessionId,
    publishedAt: new Date(Date.now() - 60_000).toISOString(),
  });
  const session = { ...base, listener: { ...base.listener, listenerId } };
  const record = makeRegistry(identity, [session]);
  const filePath = join(dataDir, "instances", `${identity.instanceId}.json`);
  await writeRegistry(dataDir, record);
  await rm(filePath);
  const restore = new Promise<void>((resolve, reject) => {
    setTimeout(() => { writeRegistry(dataDir, record).then(resolve, reject); }, 20);
  });
  try {
    const selected = await selectInstanceForFlags({ instance: identity.instanceId }, dataDir, projectPath, "runtime");
    assert.equal(selected.instance.status, "ready");
    assert.equal(selected.instance.instanceId, identity.instanceId);
    assert.equal(health.requests(), 1);
  } finally {
    await restore;
    await health.close();
  }
});

test("strict health keeps stale main-thread pulse distinct from transport identity", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-stale-pulse-"));
  const projectPath = await project("unity-cli-stale-pulse-project-");
  const identity = registryIdentity(projectPath);
  const sessionId = `session-${"7".repeat(64)}`;
  const listenerId = `listener-${"6".repeat(32)}`;
  const health = await startStrictHealthServer((_request, port) => ({
    version: 3,
    target: "runtime",
    instanceId: identity.instanceId,
    canonicalProjectPath: identity.canonicalProjectPath,
    canonicalizationVersion: 1,
    sessionId,
    listenerId,
    listenerGeneration: 1,
    actualPort: port,
    buildStamp: "test-build",
    buildId: "a".repeat(32),
    mainThread: { observed: true, sequence: 10, ageMs: 3_000, listenerGeneration: 1, lifecycle: "ready" },
  }));
  try {
    const base = await makeRegistrySession(health.endpoint, { target: "runtime", sessionId, runtimeSessionId: sessionId });
    const session = { ...base, listener: { ...base.listener, listenerId } };
    await writeRegistry(dataDir, makeRegistry(identity, [session]));
    const selected = await selectInstanceForFlags({ instance: identity.instanceId }, dataDir, projectPath, "runtime");
    assert.equal(selected.instance.status, "unresponsive");
    assert.notEqual(selected.instance.status, "conflict");
  } finally {
    await health.close();
  }
});

test("observation reducer distinguishes verified transition claims from unverifiable claims", async () => {
  const projectPath = await project("unity-cli-observation-");
  const identity = registryIdentity(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:47991");
  const verified = new Map([[session.processId, { processId: session.processId, state: "alive" as const, processStartedAt: session.processStartedAt, executablePath: process.execPath }]]);
  const unverifiable = new Map([[session.processId, { processId: session.processId, state: "unverifiable" as const }]]);
  assert.equal(reduceObservationRegistryRecord(makeRegistry(identity, [session]), verified).status, "transitioning");
  assert.equal(reduceObservationRegistryRecord(makeRegistry(identity, [session]), unverifiable).status, "unverifiable");
});

test("transitioning Editor claim keeps one exact listener identity and rejects a zero-port placeholder", async () => {
  const projectPath = await project("unity-cli-transitioning-");
  const identity = registryIdentity(projectPath);
  const base = await makeRegistrySession("http://127.0.0.1:47991");
  const transitioning = { ...base, lifecycle: "transitioning" as const };
  const observations = new Map([[process.pid, {
    processId: process.pid,
    state: "alive" as const,
    processStartedAt: transitioning.processStartedAt,
    executablePath: process.execPath,
  }]]);

  const reduced = await reduceRegistryRecord(makeRegistry(identity, [transitioning]), observations);
  assert.equal(reduced.status, "transitioning");
  assert.equal(reduced.activeSession?.listener.endpoint.actualPort, 47991);

  await assert.rejects(
    () => reduceRegistryRecord(makeRegistry(identity, [{
      ...transitioning,
      listener: { ...transitioning.listener, endpoint: { ...transitioning.listener.endpoint, actualPort: 0, fallback: true, url: "http://127.0.0.1:0" } },
    }]), observations),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
  await assert.rejects(
    () => reduceRegistryRecord(makeRegistry(identity, [{ ...transitioning, listener: { ...transitioning.listener, generation: 2 } }]), observations),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
});

test("observation reducer removes only OS-proven dead restart claims", async () => {
  const projectPath = await project("unity-cli-restart-observation-");
  const identity = registryIdentity(projectPath);
  const live = await makeRegistrySession("http://127.0.0.1:47991");
  const dead = await makeRegistrySession("http://127.0.0.1:47992", {
    editorSessionId: `session-${"d".repeat(64)}`,
    processId: 2_000_000_000,
    processStartedAt: new Date(0).toISOString(),
  });
  dead.listener = { ...dead.listener, listenerId: `listener-${"3".repeat(32)}` };
  const reduced = reduceObservationRegistryRecord(makeRegistry(identity, [dead, live]), new Map([
    [dead.processId, { processId: dead.processId, state: "dead" as const }],
    [live.processId, { processId: live.processId, state: "alive" as const, processStartedAt: live.processStartedAt, executablePath: process.execPath }],
  ]));
  assert.equal(reduced.status, "transitioning");
  assert.deepEqual(reduced.sessions.map((session) => session.editorSessionId), [live.editorSessionId]);
});

test("editor and runtime sessions share one project identity without cross-target conflict", async () => {
  const projectPath = await project("unity-cli-cross-target-");
  const identity = registryIdentity(projectPath);
  let editorSession = `session-${"e".repeat(64)}`;
  let runtimeSession = `session-${"f".repeat(64)}`;
  const editorHealth = await startStrictHealthServer((request, port) => {
    assert.equal(request.headers["x-unity-cli-expected-session-id"], editorSession);
    return { version: 3, target: "editor", instanceId: identity.instanceId, canonicalProjectPath: identity.canonicalProjectPath, canonicalizationVersion: 1, sessionId: editorSession, listenerId: `listener-${"2".repeat(32)}`, listenerGeneration: 1, actualPort: port, lifecycle: "ready", publishedAt: new Date().toISOString(), mainThread: { observed: true, sequence: 10, ageMs: 0, listenerGeneration: 1, lifecycle: "ready" }, buildStamp: "test-build" };
  });
  const runtimeHealth = await startStrictHealthServer((request, port) => {
    assert.equal(request.headers["x-unity-cli-expected-session-id"], runtimeSession);
    return { version: 3, target: "runtime", instanceId: identity.instanceId, canonicalProjectPath: identity.canonicalProjectPath, canonicalizationVersion: 1, sessionId: runtimeSession, listenerId: `listener-${"3".repeat(32)}`, listenerGeneration: 1, actualPort: port, lifecycle: "ready", publishedAt: new Date().toISOString(), mainThread: { observed: true, sequence: 10, ageMs: 0, listenerGeneration: 1, lifecycle: "ready" }, buildStamp: "test-build", buildId: "a".repeat(32) };
  });
  try {
    const editor = await makeRegistrySession(editorHealth.endpoint, { sessionId: editorSession, editorSessionId: editorSession });
    const runtime = await makeRegistrySession(runtimeHealth.endpoint, {
      target: "runtime", sessionId: runtimeSession, runtimeSessionId: runtimeSession,
      listener: { ...editor.listener, listenerId: `listener-${"3".repeat(32)}`, endpoint: { ...editor.listener.endpoint, preferredPort: runtimeHealth.port, actualPort: runtimeHealth.port, url: runtimeHealth.endpoint } },
    });
    const observations = new Map([[process.pid, { processId: process.pid, state: "alive" as const, processStartedAt: editor.processStartedAt, executablePath: process.execPath }]]);
    const record = makeRegistry(identity, [editor, runtime]);
    const editorView = await reduceRegistryRecord(record, observations, Date.now(), "editor");
    const runtimeView = await reduceRegistryRecord(record, observations, Date.now(), "runtime");
    const allView = await reduceRegistryRecord(record, observations, Date.now(), "all");
    assert.equal(editorView.status, "ready");
    assert.equal(editorView.activeSession?.sessionId, editorSession);
    assert.equal(runtimeView.status, "ready");
    assert.equal(runtimeView.activeSession?.sessionId, runtimeSession);
    assert.equal(allView.status, "ready");
    assert.equal(allView.activeSession, undefined);
    const summary = instanceListSummary(allView);
    assert.equal(summary.instanceId, identity.instanceId);
    assert.equal(summary.status, "ready");
    assert.equal(summary.sessions, undefined);
    assert.equal(summary.lastUnitySnapshot, undefined);
    assert.equal(summary.claimVerification, undefined);
    assert.equal(summary.processClaimIds, undefined);
    assert.equal(summary.revision, undefined);
    assert.equal(summary.updatedAt, undefined);
    assert.equal(summary.buildStamp, undefined);
    assert.equal(summary.canonicalizationVersion, undefined);
  } finally {
    await Promise.all([editorHealth.close(), runtimeHealth.close()]);
  }
});

test("duplicate runtime claims do not invalidate the editor target", async () => {
  const projectPath = await project("unity-cli-runtime-conflict-");
  const identity = registryIdentity(projectPath);
  const editor = await makeRegistrySession("http://127.0.0.1:47991", { sessionId: `session-${"a".repeat(64)}` });
  const firstRuntime = await makeRegistrySession("http://127.0.0.1:47992", { target: "runtime", sessionId: `session-${"b".repeat(64)}`, listener: { ...editor.listener, listenerId: `listener-${"3".repeat(32)}`, endpoint: { ...editor.listener.endpoint, preferredPort: 47992, actualPort: 47992, url: "http://127.0.0.1:47992" } } });
  const secondRuntime = await makeRegistrySession("http://127.0.0.1:47993", { target: "runtime", sessionId: `session-${"c".repeat(64)}`, processId: editor.processId + 1, processStartedAt: editor.processStartedAt, listener: { ...editor.listener, listenerId: `listener-${"4".repeat(32)}`, endpoint: { ...editor.listener.endpoint, preferredPort: 47993, actualPort: 47993, url: "http://127.0.0.1:47993" } } });
  const record = makeRegistry(identity, [editor, firstRuntime, secondRuntime]);
  const observations = new Map([
    [editor.processId, { processId: editor.processId, state: "alive" as const, processStartedAt: editor.processStartedAt, executablePath: process.execPath }],
    [secondRuntime.processId, { processId: secondRuntime.processId, state: "alive" as const, processStartedAt: secondRuntime.processStartedAt, executablePath: process.execPath }],
  ]);
  assert.equal(reduceObservationRegistryRecord(record, observations, "editor").status, "transitioning");
  assert.equal(reduceObservationRegistryRecord(record, observations, "runtime").status, "conflict");
});

test("dead PID/start identity reduces to offline", async () => {
  const projectPath = await project("unity-cli-dead-pid-");
  const identity = registryIdentity(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:47991", {
    processId: 2_000_000_000,
    processStartedAt: new Date(0).toISOString(),
  });
  const instance = await reduceRegistryRecord(
    makeRegistry(identity, [session]),
    new Map([[session.processId, { processId: session.processId, state: "dead" as const }]]),
  );
  assert.equal(instance.status, "offline");
  assert.equal(instance.sessions.length, 0);
});

test("only distinct verified process claims reduce to conflict", async () => {
  const projectPath = await project("unity-cli-conflict-");
  const identity = registryIdentity(projectPath);
  const processStartedAt = await currentProcessStartedAt();
  const first = await makeRegistrySession("http://127.0.0.1:47991", {
    editorSessionId: `session-${"a".repeat(64)}`,
    processStartedAt,
  });
  const second = await makeRegistrySession("http://127.0.0.1:47992", {
    editorSessionId: `session-${"b".repeat(64)}`,
    processStartedAt,
    listener: {
      ...first.listener,
      listenerId: `listener-${"3".repeat(32)}`,
      endpoint: { ...first.listener.endpoint, preferredPort: 47992, actualPort: 47992, url: "http://127.0.0.1:47992" },
    },
  });
  const observations = new Map([[process.pid, { processId: process.pid, state: "alive" as const, processStartedAt }]]);
  const duplicateSession = await reduceRegistryRecord(makeRegistry(identity, [first, second]), observations);
  assert.equal(duplicateSession.status, "unverifiable");

  const processConflict = applyProjectProcessClaimInspection(
    reduceObservationRegistryRecord(makeRegistry(identity, [first])),
    { verification: "verified", processIds: [first.processId, first.processId + 1] },
  );
  assert.equal(processConflict.status, "conflict");
  assert.equal(processConflict.blockers.at(-1)?.kind, "duplicate-project-process");
});

test("public instance summary has one minimal field set and omits internal identity, snapshot, and diagnostics", async () => {
  const projectPath = await project("unity-cli-instance-summary-");
  const identity = registryIdentity(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:47991");
  const summary = instanceListSummary(reduceObservationRegistryRecord(makeRegistry(identity, [session])));
  assert.deepEqual(Object.keys(summary).sort(), ["canonicalProjectPath", "displayProjectPath", "instanceId", "projectName", "status"]);
  assert.equal(summary.unityVersion, undefined);
  const serialized = JSON.stringify(summary);
  assert.doesNotMatch(serialized, /sessions|lastUnitySnapshot|processClaimIds|progress|blockers|buildStamp|revision|updatedAt|claimVerification|canonicalizationVersion|endpoint|actualPort|preferredPort|listenerId|listenerGeneration|sourceListenerGeneration/);
});

test("v3 registry accepts BOM, isolates full-view identity mismatches, and rejects exact bad targets", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-registry-v3-"));
  const projectPath = await project("unity-cli-registry-project-");
  const identity = registryIdentity(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:47991");
  const record = makeRegistry(identity, [session]);
  const directory = join(dataDir, "instances");
  const path = join(directory, `${identity.instanceId}.json`);
  await mkdir(directory, { recursive: true });
  await writeFile(path, `\uFEFF${JSON.stringify(record)}`, "utf8");
  assert.equal((await readInstances(dataDir))[0].instanceId, identity.instanceId);

  const wrongId = `unity-${"f".repeat(64)}`;
  const wrongPath = join(directory, `${wrongId}.json`);
  await writeFile(wrongPath, JSON.stringify(record), "utf8");
  const filenameMismatch = await readInstancesWithRegistryDiagnostics(dataDir);
  assert.equal(filenameMismatch.instances.length, 1);
  assert.equal(filenameMismatch.invalidRecordCount, 1);
  await assert.rejects(
    selectInstanceForFlags({ instance: wrongId }, dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
  await rm(wrongPath);

  await writeFile(path, JSON.stringify({
    ...record,
    identity: { ...record.identity, canonicalProjectPath: `${record.identity.canonicalProjectPath}\\different` },
  }), "utf8");
  const canonicalMismatch = await readInstancesWithRegistryDiagnostics(dataDir);
  assert.deepEqual(canonicalMismatch.instances, []);
  assert.equal(canonicalMismatch.invalidRecordCount, 1);
  await assert.rejects(
    selectInstanceForFlags({ instance: identity.instanceId }, dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
});
