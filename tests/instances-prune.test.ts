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
  reduceObservationRegistryRecord,
  reduceRegistryRecord,
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
} from "./helpers/instance-registry-v2.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

/** 创建严格 Unity project identity 夹具。 */
async function project(prefix: string): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), prefix));
  await createUnityProject(root);
  clearProjectIdentityCache();
  return root;
}

test("registry writer remains one v2 file per canonical project with durable replacement", async () => {
  const source = await readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8");
  assert.ok(source.includes('WithCurrentUserMutex("Local\\\\UnityCli.Registry." + InstanceId()'));
  assert.match(source, /stream\.Flush\(true\)/);
  assert.match(source, /File\.Replace\(temporaryPath, path, null\)/);
  assert.match(source, /"version", 2L/);
  assert.match(source, /"sessions", sessions\.Cast<object>\(\)\.ToList\(\)/);
  assert.doesNotMatch(source, /instances\.json|projectHash.*processId/);
});

test("instances prune only reports C# writer-owned cleanup and never rewrites registry", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-prune-"));
  const projectPath = await project("unity-cli-prune-project-");
  const identity = registryIdentity(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:47991", {
    lastHeartbeatAt: new Date(Date.now() - 60_000).toISOString(),
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

test("observation reducer fails closed for unverifiable claims", async () => {
  const projectPath = await project("unity-cli-observation-");
  const identity = registryIdentity(projectPath);
  const verified = await makeRegistrySession("http://127.0.0.1:47991");
  const unverifiable = { ...verified, claimVerification: "unverifiable" as const };
  assert.equal(reduceObservationRegistryRecord(makeRegistry(identity, [verified])).status, "ready");
  assert.equal(reduceObservationRegistryRecord(makeRegistry(identity, [unverifiable])).status, "conflict");
});

test("transitioning Editor claim accepts only the strict listener-zero placeholder", async () => {
  const projectPath = await project("unity-cli-transitioning-");
  const identity = registryIdentity(projectPath);
  const base = await makeRegistrySession("http://127.0.0.1:47991");
  const transitioning = {
    ...base,
    state: "transitioning" as const,
    listener: {
      ...base.listener,
      endpoint: {
        ...base.listener.endpoint,
        actualPort: 0,
        fallback: true,
        url: "http://127.0.0.1:0",
      },
    },
  };
  const observations = new Map([[process.pid, {
    processId: process.pid,
    state: "alive" as const,
    processStartedAt: transitioning.processStartedAt,
    executablePath: process.execPath,
  }]]);

  const record = makeRegistry(identity, [transitioning]);
  const reduced = await reduceRegistryRecord(record, observations);
  assert.equal(reduced.status, "transitioning");
  assert.equal(reduced.activeSession?.listener.endpoint.actualPort, 0);

  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-transitioning-list-"));
  await writeRegistry(dataDir, record);
  const listed = await execute(["instances", "list"], { dataDir } as never);
  assert.equal(listed.ok, true);
  const instances = (listed.data as { instances: Array<{ instanceId: string; status: string }> }).instances;
  assert.deepEqual(instances.map((instance) => ({ instanceId: instance.instanceId, status: instance.status })), [
    { instanceId: identity.instanceId, status: "transitioning" },
  ]);

  await assert.rejects(
    () => reduceRegistryRecord(makeRegistry(identity, [{ ...transitioning, state: "ready" as const }]), observations),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
  await assert.rejects(
    () => reduceRegistryRecord(makeRegistry(identity, [{
      ...transitioning,
      listener: { ...transitioning.listener, endpoint: { ...transitioning.listener.endpoint, fallback: false } },
    }]), observations),
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
  assert.equal(reduced.status, "ready");
  assert.deepEqual(reduced.sessions.map((session) => session.editorSessionId), [live.editorSessionId]);
});

test("editor and runtime sessions share one project identity without cross-target conflict", async () => {
  const projectPath = await project("unity-cli-cross-target-");
  const identity = registryIdentity(projectPath);
  let editorSession = `session-${"e".repeat(64)}`;
  let runtimeSession = `session-${"f".repeat(64)}`;
  const editorHealth = await startStrictHealthServer((request, port) => {
    assert.equal(request.headers["x-unity-cli-expected-session-id"], editorSession);
    return { version: 2, target: "editor", instanceId: identity.instanceId, canonicalProjectPath: identity.canonicalProjectPath, canonicalizationVersion: 1, sessionId: editorSession, listenerId: `listener-${"2".repeat(32)}`, actualPort: port, buildStamp: "test-build" };
  });
  const runtimeHealth = await startStrictHealthServer((request, port) => {
    assert.equal(request.headers["x-unity-cli-expected-session-id"], runtimeSession);
    return { version: 2, target: "runtime", instanceId: identity.instanceId, canonicalProjectPath: identity.canonicalProjectPath, canonicalizationVersion: 1, sessionId: runtimeSession, listenerId: `listener-${"3".repeat(32)}`, actualPort: port, buildStamp: "test-build", buildId: "a".repeat(32) };
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
    assert.deepEqual(instanceListSummary(allView).sessions, [
      { target: "editor", sessionId: editorSession, editorSessionId: editorSession, processId: process.pid, processStartedAt: editor.processStartedAt, lastHeartbeatAt: editor.lastHeartbeatAt, state: "ready", claimVerification: "verified" },
      { target: "runtime", sessionId: runtimeSession, runtimeSessionId: runtimeSession, processId: process.pid, processStartedAt: runtime.processStartedAt, lastHeartbeatAt: runtime.lastHeartbeatAt, state: "ready", claimVerification: "verified" },
    ]);
  } finally {
    await Promise.all([editorHealth.close(), runtimeHealth.close()]);
  }
});

test("duplicate runtime claims do not invalidate the editor target", async () => {
  const projectPath = await project("unity-cli-runtime-conflict-");
  const identity = registryIdentity(projectPath);
  const editor = await makeRegistrySession("http://127.0.0.1:47991", { sessionId: `session-${"a".repeat(64)}` });
  const firstRuntime = await makeRegistrySession("http://127.0.0.1:47992", { target: "runtime", sessionId: `session-${"b".repeat(64)}`, listener: { ...editor.listener, listenerId: `listener-${"3".repeat(32)}`, endpoint: { ...editor.listener.endpoint, preferredPort: 47992, actualPort: 47992, url: "http://127.0.0.1:47992" } } });
  const secondRuntime = await makeRegistrySession("http://127.0.0.1:47993", { target: "runtime", sessionId: `session-${"c".repeat(64)}`, listener: { ...editor.listener, listenerId: `listener-${"4".repeat(32)}`, endpoint: { ...editor.listener.endpoint, preferredPort: 47993, actualPort: 47993, url: "http://127.0.0.1:47993" } } });
  const record = makeRegistry(identity, [editor, firstRuntime, secondRuntime]);
  assert.equal(reduceObservationRegistryRecord(record, undefined, "editor").status, "ready");
  assert.equal(reduceObservationRegistryRecord(record, undefined, "runtime").status, "conflict");
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

test("duplicate registry or process claims reduce to conflict", async () => {
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
  const registryConflict = await reduceRegistryRecord(makeRegistry(identity, [first, second]), observations);
  assert.equal(registryConflict.status, "conflict");

  const processConflict = applyProjectProcessClaimInspection(
    reduceObservationRegistryRecord(makeRegistry(identity, [first])),
    { verification: "verified", processIds: [first.processId, first.processId + 1] },
  );
  assert.equal(processConflict.status, "conflict");
  assert.equal(processConflict.blockers.at(-1)?.kind, "duplicate-project-process");
});

test("public instance summary omits transport and listener identity", async () => {
  const projectPath = await project("unity-cli-instance-summary-");
  const identity = registryIdentity(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:47991");
  const summary = instanceListSummary(reduceObservationRegistryRecord(makeRegistry(identity, [session])));
  const serialized = JSON.stringify(summary);
  assert.doesNotMatch(serialized, /endpoint|actualPort|preferredPort|listenerId|listenerGeneration|sourceListenerGeneration/);
});

test("v2 registry accepts BOM and rejects filename or canonical identity mismatch", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-registry-v2-"));
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
  await writeFile(join(directory, `${wrongId}.json`), JSON.stringify(record), "utf8");
  await assert.rejects(
    () => readInstances(dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
  await rm(join(directory, `${wrongId}.json`));

  await writeFile(path, JSON.stringify({
    ...record,
    identity: { ...record.identity, canonicalProjectPath: `${record.identity.canonicalProjectPath}\\different` },
  }), "utf8");
  await assert.rejects(
    () => readInstances(dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
});
