import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import {
  InstanceRegistryError,
  applyProjectProcessClaimInspection,
  obsoleteLocalStatePaths,
  readInstances,
  reduceObservationRegistryRecord,
  reduceRegistryRecord,
  selectInstanceForFlags,
} from "../src/instances.js";
import { ProjectIdentityError, clearProjectIdentityCache } from "../src/project-identity.js";
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

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-instances-v2-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function makeProject(prefix: string): Promise<string> {
  const project = await mkdtemp(join(tmpdir(), prefix));
  await createUnityProject(project);
  clearProjectIdentityCache();
  return project;
}

async function readyFixture(dataDir: string, projectPath: string) {
  const identity = registryIdentity(projectPath);
  let session = await makeRegistrySession("http://127.0.0.1:47991");
  const server = await startStrictHealthServer((_request, port) => ({
    version: 2,
    instanceId: identity.instanceId,
    canonicalProjectPath: identity.canonicalProjectPath,
    canonicalizationVersion: 1,
    editorSessionId: session.editorSessionId,
    listenerId: session.listener.listenerId,
    actualPort: port,
    buildStamp: session.buildStamp,
  }));
  session = await makeRegistrySession(server.endpoint, {
    editorSessionId: session.editorSessionId,
    listener: { ...session.listener, endpoint: { ...session.listener.endpoint, actualPort: server.port, preferredPort: server.port, url: server.endpoint } },
  });
  const record = makeRegistry(identity, [session]);
  await writeRegistry(dataDir, record);
  return { identity, session, record, server };
}

test("registry v2 使用 project identity 单文件、named mutex 与 durable atomic replace", async () => {
  const source = await readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8");
  assert.match(source, /Local\\\\UnityCli\.Registry\./);
  assert.match(source, /ConvertStringSecurityDescriptorToSecurityDescriptorW/);
  assert.match(source, /OpenProcessToken\(GetCurrentProcessHandle\(\), TokenQuery/);
  assert.match(source, /GetTokenInformation\(token, TokenUser/);
  assert.match(source, /ConvertSidToStringSidW/);
  assert.match(source, /D:P\(A;;GA;;;/);
  assert.match(source, /CreateMutexExW/);
  assert.match(source, /WithCurrentUserMutex\("Local\\\\UnityCli\.Registry\." \+ InstanceId\(\), RegistryMutexTimeoutMs,/);
  assert.match(source, /WaitForSingleObject\(handle, \(uint\)timeoutMs\)/);
  assert.match(source, /wait != WaitObject0 && wait != WaitAbandoned/);
  assert.match(source, /ReleaseMutex\(handle\)/);
  assert.match(source, /stream\.Flush\(true\)/);
  assert.match(source, /File\.Replace\(temporaryPath, path, null\)/);
  assert.match(source, /"version", 2L/);
  assert.match(source, /"sessions", sessions\.Cast<object>\(\)\.ToList\(\)/);
  assert.match(source, /private static bool RegisterInstance\(string requestedState\)/);
  const bridge = await readFile("unitypkg/Editor/UnityCliBridge.cs", "utf8");
  assert.match(bridge, /if \(!RegisterInstance\("ready"\)\) throw new InvalidOperationException/);
  assert.match(bridge, /catch \(Exception exception\)[\s\S]*StopListener\(\);[\s\S]*listener_start_failed/);
  assert.doesNotMatch(source, /instances\.json|projectHash.*processId|ToLowerInvariant/);
});

test("registry reader 有界重试 Windows sharing conflict 且不读取 aggregate", async () => {
  const source = await readFile("src/instances.ts", "utf8");
  assert.match(source, /REGISTRY_READ_RETRY_DELAYS_MS = \[5, 10, 20, 40\]/);
  assert.match(source, /code === "EBUSY" \|\| code === "EPERM" \|\| code === "EACCES"/);
  assert.match(source, /const instance = \(await readInstances\(dataDir\)\)\.find/);
  assert.match(source, /reduceObservationRegistryRecord\(record\)/);
  assert.match(source, /activeSession!\.claimVerification === "unverifiable"/);
  assert.match(source, /hasExactKeys\(snapshot, \["schemaVersion", "observedAt", "sourceEditorSessionId", "sourceListenerGeneration", "fields"\]\)/);
  assert.doesNotMatch(source, /readRegistryFileInstances|export async function writeInstances|return resolve\(dataDir, "instances\.json"\)/);
});

test("strict health identity 使单个存活 claim 归约为 ready", async () => {
  const runtime = await makeRuntime();
  const project = await makeProject("unity-cli-ready-");
  const fixture = await readyFixture(runtime.dataDir, project);
  try {
    const instances = await readInstances(runtime.dataDir);
    assert.equal(instances.length, 1);
    assert.equal(instances[0].instanceId, fixture.identity.instanceId);
    assert.equal(instances[0].status, "ready");
    assert.equal(instances[0].editorSessionId, fixture.session.editorSessionId);
    const summary = (await execute(["instances", "list"], runtime));
    assert.ok(summary.ok);
    assert.doesNotMatch(JSON.stringify(summary.data), /bridgeEndpoint|listenerId|actualPort|preferredPort/);
  } finally {
    await fixture.server.close();
  }
});

test("错误 listener health 不能证明 ready，失败 health 不缓存", async () => {
  const runtime = await makeRuntime();
  const project = await makeProject("unity-cli-health-mismatch-");
  const identity = registryIdentity(project);
  const server = await startStrictHealthServer((_request, port) => ({
    version: 2,
    instanceId: identity.instanceId,
    canonicalProjectPath: identity.canonicalProjectPath,
    canonicalizationVersion: 1,
    editorSessionId: `session-${"1".repeat(64)}`,
    listenerId: "listener-wrong",
    actualPort: port,
    buildStamp: "test-build",
  }));
  try {
    const session = await makeRegistrySession(server.endpoint);
    await writeRegistry(runtime.dataDir, makeRegistry(identity, [session]));
    assert.equal((await readInstances(runtime.dataDir))[0].status, "transitioning");
    assert.equal((await readInstances(runtime.dataDir))[0].status, "transitioning");
    assert.equal(server.requests(), 2);
  } finally {
    await server.close();
  }
});

test("heartbeat 过期但 PID/start time 存活时保留 claim 且不 prune", async () => {
  const runtime = await makeRuntime();
  const project = await makeProject("unity-cli-stale-heartbeat-");
  const identity = registryIdentity(project);
  const session = await makeRegistrySession("http://127.0.0.1:47991", { lastHeartbeatAt: new Date(Date.now() - 60_000).toISOString() });
  const record = makeRegistry(identity, [session]);
  await writeRegistry(runtime.dataDir, record);
  const before = await readFile(join(runtime.dataDir, "instances", `${identity.instanceId}.json`), "utf8");
  const result = await execute(["instances", "prune", "--yes"], runtime);
  const after = await readFile(join(runtime.dataDir, "instances", `${identity.instanceId}.json`), "utf8");
  assert.ok(result.ok);
  assert.equal((result.data as { removed: number }).removed, 0);
  assert.equal(before, after);
  assert.equal((await readInstances(runtime.dataDir))[0].status, "transitioning");
});

test("observation reducer does not health-check or scan process claims", async () => {
  const project = await makeProject("unity-cli-observation-raw-");
  const identity = registryIdentity(project);
  const session = await makeRegistrySession("http://127.0.0.1:47991", { processId: 2_000_000_000 });
  const instance = reduceObservationRegistryRecord(makeRegistry(identity, [session]));
  assert.equal(instance.status, "ready");
  assert.equal(instance.activeSession?.editorSessionId, session.editorSessionId);
});

test("raw observation rejects an unverifiable single-session claim", async () => {
  const project = await makeProject("unity-cli-observation-unverifiable-");
  const identity = registryIdentity(project);
  const session = await makeRegistrySession("http://127.0.0.1:47991", { claimVerification: "unverifiable" });
  assert.equal(reduceObservationRegistryRecord(makeRegistry(identity, [session])).status, "conflict");
});

test("strict selector rejects dead and PID-start-time-stale claims", async () => {
  const runtime = await makeRuntime();
  const project = await makeProject("unity-cli-strict-selector-");
  const identity = registryIdentity(project);
  const dead = await makeRegistrySession("http://127.0.0.1:47991", { processId: 2_000_000_000, processStartedAt: new Date(0).toISOString() });
  await writeRegistry(runtime.dataDir, makeRegistry(identity, [dead]));
  await assert.rejects(
    () => selectInstanceForFlags({ instance: identity.instanceId }, runtime.dataDir, project),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_NOT_RUNNING",
  );

  const stale = await makeRegistrySession("http://127.0.0.1:47991", { processId: process.pid, processStartedAt: new Date(0).toISOString() });
  await writeRegistry(runtime.dataDir, makeRegistry(identity, [stale]));
  await assert.rejects(
    () => selectInstanceForFlags({ instance: identity.instanceId }, runtime.dataDir, project),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_NOT_RUNNING",
  );
});

test("PID lifecycle 失效归约为 offline", async () => {
  const project = await makeProject("unity-cli-dead-pid-");
  const identity = registryIdentity(project);
  const session = await makeRegistrySession("http://127.0.0.1:47991", { processId: 2_000_000_000, processStartedAt: new Date(0).toISOString() });
  const record = makeRegistry(identity, [session]);
  const instance = await reduceRegistryRecord(record, new Map([[session.processId, { processId: session.processId, state: "dead" as const }]]));
  assert.equal(instance.status, "offline");
  assert.equal(instance.sessions.length, 0);
});

test("OS 中同 canonical identity 双 Unity.exe claim 覆盖单 session registry 为 conflict", async () => {
  const project = await makeProject("unity-cli-process-conflict-");
  const identity = registryIdentity(project);
  const session = await makeRegistrySession("http://127.0.0.1:47997");
  const instance = reduceObservationRegistryRecord(makeRegistry(identity, [session]));
  const conflicted = applyProjectProcessClaimInspection(instance, { verification: "verified", processIds: [session.processId, session.processId + 1] });
  assert.equal(conflicted.status, "conflict");
  assert.equal(conflicted.activeSession, undefined);
  assert.deepEqual(conflicted.processClaimIds, [session.processId, session.processId + 1]);
  assert.equal(conflicted.blockers.at(-1)?.kind, "duplicate-project-process");
  assert.equal(conflicted.blockers.at(-1)?.confidence, "exact");
  await rm(project, { recursive: true, force: true });
});

test("补充 process claim 枚举不可用时不覆盖已验证 registry session", async () => {
  const project = await makeProject("unity-cli-process-unverifiable-");
  const identity = registryIdentity(project);
  const session = await makeRegistrySession("http://127.0.0.1:47998");
  const instance = reduceObservationRegistryRecord(makeRegistry(identity, [session]));
  const observed = applyProjectProcessClaimInspection(instance, { verification: "unverifiable", processIds: [] });
  assert.equal(observed, instance);
  assert.equal(observed.status, "ready");
  assert.equal(observed.claimVerification, "verified");
  await rm(project, { recursive: true, force: true });
});

test("同 canonical identity 双活 claim 归约为 conflict", async () => {
  const project = await makeProject("unity-cli-conflict-");
  const identity = registryIdentity(project);
  const processStartedAt = await currentProcessStartedAt();
  const first = await makeRegistrySession("http://127.0.0.1:47991", { editorSessionId: `session-${"a".repeat(64)}`, processStartedAt });
  const second = await makeRegistrySession("http://127.0.0.1:47992", { editorSessionId: `session-${"b".repeat(64)}`, processStartedAt });
  second.listener.listenerId = `listener-${"3".repeat(32)}`;
  const observations = new Map([[process.pid, { processId: process.pid, state: "alive" as const, processStartedAt }]]);
  const instance = await reduceRegistryRecord(makeRegistry(identity, [first, second]), observations);
  assert.equal(instance.status, "conflict");
  assert.equal(instance.sessions.length, 2);
});

test("unverifiable target claim fail closed 为 conflict 且不发送 mutation/health POST", async () => {
  const project = await makeProject("unity-cli-unverifiable-");
  const identity = registryIdentity(project);
  const session = await makeRegistrySession("http://127.0.0.1:47991", { claimVerification: "unverifiable" });
  const instance = await reduceRegistryRecord(makeRegistry(identity, [session]), new Map([[process.pid, { processId: process.pid, state: "unverifiable" as const }]]));
  assert.equal(instance.status, "conflict");
});

test("selector 支持 full id、project path、alias 与 CWD，且无 single-ready fallback", async () => {
  const runtime = await makeRuntime();
  const project = await makeProject("unity-cli-selector-");
  const fixture = await readyFixture(runtime.dataDir, project);
  try {
    const byId = await selectInstanceForFlags({ instance: fixture.identity.instanceId }, runtime.dataDir, project);
    const byPath = await selectInstanceForFlags({ instance: join(project, "Assets") }, runtime.dataDir, project);
    const byCwd = await selectInstanceForFlags({}, runtime.dataDir, join(project, "Assets"));
    assert.equal(byId.matchedBy, "instanceId");
    assert.equal(byPath.matchedBy, "projectPath");
    assert.equal(byCwd.matchedBy, "cwd");

    const aliasResult = await execute(["target", "alias", "set", project, "game"], runtime);
    assert.ok(aliasResult.ok);
    const byAlias = await selectInstanceForFlags({ instance: "game" }, runtime.dataDir, project);
    assert.equal(byAlias.instance.instanceId, fixture.identity.instanceId);
    assert.equal(byAlias.matchedBy, "alias");

    const nonProject = await mkdtemp(join(tmpdir(), "unity-cli-selector-none-"));
    await assert.rejects(
      () => selectInstanceForFlags({}, runtime.dataDir, nonProject),
      (error: unknown) => error instanceof ProjectIdentityError && error.code === "E_INSTANCE_REQUIRED",
    );
  } finally {
    await fixture.server.close();
  }
});

test("offline project 与 project constraint mismatch 使用稳定错误", async () => {
  const runtime = await makeRuntime();
  const first = await makeProject("unity-cli-offline-a-");
  const second = await makeProject("unity-cli-offline-b-");
  const firstIdentity = registryIdentity(first);
  await writeRegistry(runtime.dataDir, makeRegistry(firstIdentity, []));
  await assert.rejects(
    () => selectInstanceForFlags({ instance: firstIdentity.instanceId }, runtime.dataDir, first),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_NOT_RUNNING",
  );

  const fixture = await readyFixture(runtime.dataDir, first);
  try {
    await assert.rejects(
      () => selectInstanceForFlags({ instance: firstIdentity.instanceId, project: second }, runtime.dataDir, first),
      (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_PROJECT_MISMATCH",
    );
  } finally {
    await fixture.server.close();
  }
});

test("旧 active target 命令与 endpoint bypass 被明确拒绝", async () => {
  const runtime = await makeRuntime();
  for (const argv of [["target", "current"], ["target", "set", "x"], ["target", "clear"]]) {
    const result = await execute(argv, runtime);
    assert.ok(!result.ok);
    assert.equal(result.error.code, "UNKNOWN_TARGET_COMMAND");
  }
  const endpoint = await execute(["context", "read", "editor.state", "--bridge-endpoint", "http://127.0.0.1:47991"], runtime);
  assert.ok(!endpoint.ok);
  assert.equal(endpoint.error.code, "E_OBSOLETE_OPTION");
  const target = await execute(["context", "read", "editor.state", "--target", "anything"], runtime);
  assert.ok(!target.ok);
  assert.equal(target.error.code, "E_OBSOLETE_OPTION");
});

test("旧 aliases/config/aggregate 只由 doctor 发现，不参与解析", async () => {
  const runtime = await makeRuntime();
  await Promise.all([
    writeFile(join(runtime.dataDir, "config.json"), JSON.stringify({ activeTarget: "legacy" }), "utf8"),
    writeFile(join(runtime.dataDir, "aliases.json"), JSON.stringify({ legacy: "old-id" }), "utf8"),
    writeFile(join(runtime.dataDir, "instances.json"), JSON.stringify({ version: 1, instances: [] }), "utf8"),
  ]);
  assert.equal((await obsoleteLocalStatePaths(runtime.dataDir)).length, 3);
  await assert.rejects(
    () => selectInstanceForFlags({ instance: "legacy" }, runtime.dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_NOT_FOUND",
  );
});

test("v2 registry 支持 UTF-8 BOM，filename/identity mismatch fail closed", async () => {
  const runtime = await makeRuntime();
  const project = await makeProject("unity-cli-bom-v2-");
  const identity = registryIdentity(project);
  const session = await makeRegistrySession("http://127.0.0.1:47991");
  const record = makeRegistry(identity, [session]);
  const directory = join(runtime.dataDir, "instances");
  await mkdir(directory, { recursive: true });
  await writeFile(join(directory, `${identity.instanceId}.json`), `\uFEFF${JSON.stringify(record)}`, "utf8");
  assert.equal((await readInstances(runtime.dataDir))[0].instanceId, identity.instanceId);

  const wrongId = `unity-${"f".repeat(64)}`;
  await writeFile(join(directory, `${wrongId}.json`), JSON.stringify(record), "utf8");
  await assert.rejects(
    () => readInstances(runtime.dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );

  await rm(join(directory, `${wrongId}.json`));
  await writeFile(join(directory, `${identity.instanceId}.json`), JSON.stringify({
    ...record,
    identity: { ...record.identity, canonicalProjectPath: `${record.identity.canonicalProjectPath}\\different` },
  }), "utf8");
  await assert.rejects(
    () => readInstances(runtime.dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );

  await writeFile(join(directory, `${identity.instanceId}.json`), JSON.stringify({
    ...record,
    sessions: [{ ...session, lastUnitySnapshot: { ...session.lastUnitySnapshot, sourceListenerGeneration: session.listener.generation + 1 } }],
  }), "utf8");
  await assert.rejects(
    () => readInstances(runtime.dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );

  await writeFile(join(directory, `${identity.instanceId}.json`), JSON.stringify({
    ...record,
    sessions: [{ ...session, lastUnitySnapshot: { ...session.lastUnitySnapshot, unexpected: true } }],
  }), "utf8");
  await assert.rejects(
    () => readInstances(runtime.dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );

  await writeFile(join(directory, `${identity.instanceId}.json`), JSON.stringify({
    ...record,
    sessions: [{ ...session, lastUnitySnapshot: { ...session.lastUnitySnapshot, fields: { ...(session.lastUnitySnapshot.fields as Record<string, unknown>), compile: { known: false, value: false } } } }],
  }), "utf8");
  await assert.rejects(
    () => readInstances(runtime.dataDir),
    (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
  );
});

test("doctor command remains routed through CLI", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["doctor"], runtime);
  assert.ok(result.ok);
  assert.equal((result.data as { command: string }).command, "doctor");
});

test("job control errors remain unchanged", async () => {
  const runtime = await makeRuntime();
  for (const [argv, code] of [
    [["jobs", "status"], "JOB_ID_REQUIRED"],
    [["jobs", "logs"], "JOB_ID_REQUIRED"],
    [["jobs", "cancel", "--yes"], "JOB_ID_REQUIRED"],
    [["jobs", "status", "job_nonexistent"], "JOB_NOT_FOUND"],
    [["jobs", "logs", "job_nonexistent"], "JOB_NOT_FOUND"],
  ] as const) {
    const result = await execute([...argv], runtime);
    assert.ok(!result.ok);
    assert.equal(result.error.code, code);
  }
});

test("control-plane duplicate tools and contexts remain unavailable", async () => {
  const runtime = await makeRuntime();
  for (const argv of [
    ["tools", "run", "target.set", "anything"],
    ["tools", "run", "instances.prune"],
    ["tools", "run", "job.status", "--id", "anything"],
    ["tools", "run", "batch.schema"],
    ["tools", "run", "diagnose.doctor"],
    ["context", "read", "instances.list"],
    ["context", "read", "target.current"],
  ]) {
    assert.equal((await execute(argv, runtime)).ok, false, argv.join(" "));
  }
});
