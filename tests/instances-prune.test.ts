import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/types.js";
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
  selectObservationInstanceForFlags,
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

test("registry writers share one project lock and publish one durable v3 file", async () => {
  const [editor, runtime, reader] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8"),
    readFile("unitypkg/Runtime/Infrastructure/UnityCliRuntimeRegistry.cs", "utf8"),
    readFile("src/instances.ts", "utf8"),
  ]);
  assert.ok(editor.includes('WithCurrentUserMutex("Local\\\\UnityCli.Registry." + InstanceId()'));
  assert.ok(runtime.includes('"Local\\\\UnityCli.Registry." + identity.InstanceId'));
  assert.match(editor, /WithRegistryMutex\(\(\) =>[\s\S]*?ReadCurrentRegistryForPublication[\s\S]*?WriteRegistryTextAtomically/);
  assert.match(runtime, /WithRegistryMutex\(delegate[\s\S]*?var record = ReadRecord\(\)[\s\S]*?WriteRecord/);
  assert.match(editor, /stream\.Flush\(true\)/);
  assert.match(editor, /File\.Replace\(temporaryPath, path, null\)/);
  assert.match(runtime, /stream\.Flush\(true\)/);
  assert.match(runtime, /File\.Replace\(temporary, path, null\)/);
  assert.match(editor, /"version", 3L/);
  assert.match(editor, /"sessions", sessions\.Cast<object>\(\)\.ToList\(\)/);
  assert.match(editor, /"publishedAt", DateTime\.UtcNow\.ToString\("O", CultureInfo\.InvariantCulture\)/);
  assert.match(editor, /"publicationSequence", sequence/);
  assert.match(reader, /entry\.endsWith\("\.json"\)[\s\S]*?entry\.slice\(0, -"\.json"\.length\)/);
  assert.doesNotMatch(reader, /readFile\([^\n]*\.tmp|readRegistrySnapshot\([^\n]*\.tmp/);
  assert.doesNotMatch(editor, /instances\.json|projectHash.*processId/);
});

test("Editor publication rebuilds non-inheritable current state without CLI repair files", async () => {
  const source = await readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8");
  assert.match(source, /private const long RegistryMaxBytes = 1024L \* 1024L/);
  assert.match(source, /var registry = ReadCurrentRegistryForPublication\(out recoveryReason\)/);
  assert.match(source, /catch \(FormatException\)[\s\S]*?recoveryReason = "malformed-json"/);
  assert.match(source, /parsed == null[\s\S]*?recoveryReason = "non-object-root"/);
  assert.match(source, /ReadLong\(parsed, "version"\) != 3L[\s\S]*?recoveryReason = "unsupported-version"/);
  assert.match(source, /!RegistryIdentityMatchesCurrentProject[\s\S]*?recoveryReason = "identity-mismatch"/);
  assert.match(source, /new FileInfo\(path\)\.Length > RegistryMaxBytes[\s\S]*?recoveryReason = "oversized"/);
  assert.match(source, /WriteRegistryTextAtomically\(InstancePath\(\), UnityCliSerializer\.Serialize\(data\) \+ "\\n"\)[\s\S]*?instance_registry_recovered reason=/);
  assert.match(source, /private static Dictionary<string, object> ReadCurrentRegistry\(\)[\s\S]*?throw new InvalidDataException\("Instance registry is not a JSON object\."\)[\s\S]*?throw new InvalidDataException\("Instance registry identity does not match CanonicalPath v1\."\)/);
  assert.doesNotMatch(source, /quarantine|registry-recovery|\.bad["']/i);
});

test("Editor and Runtime publish lifecycle changes immediately with a thirty-second fallback refresh", async () => {
  const [bridge, workQueue, runtime] = await Promise.all([
    readFile("unitypkg/Editor/UnityCliBridge.cs", "utf8"),
    readFile("unitypkg/Editor/Bridge/BridgeWorkQueue.cs", "utf8"),
    readFile("unitypkg/Runtime/Infrastructure/UnityCliRuntimeBridge.cs", "utf8"),
  ]);
  assert.match(bridge, /private const double RegistryRefreshSeconds = 30\.0/);
  assert.match(bridge, /private static bool queuePublicationOpen;/);
  assert.match(bridge, /PrepareListenerGenerationForStart\(\);[\s\S]*?StartHttpListener\(listener\)[\s\S]*?RegisterInstance\("transitioning"\)[\s\S]*?queuePublicationOpen = true[\s\S]*?Task\.Run\(ListenLoop\);[\s\S]*?readyPublicationPending = true/);
  assert.match(bridge, /catch \(Exception exception\)[\s\S]*?StopListener\(\);[\s\S]*?listener_start_failed/);
  assert.match(bridge, /RegisterInstance\(playModeChanging \? "transitioning" : "ready"\)/);
  assert.match(bridge, /nextRegistryRefreshAt = EditorApplication\.timeSinceStartup \+ RegistryRefreshSeconds/);
  assert.match(workQueue, /var listenerWasStarted = started/);
  assert.match(workQueue, /cachedIsCompiling != isCompiling[\s\S]*?cachedIsUpdating != isUpdating[\s\S]*?cachedIsBuilding != isBuilding[\s\S]*?cachedIsPlayModeChanging != isPlayModeChanging/);
  assert.match(workQueue, /listenerWasStarted && started[\s\S]*?readyPublicationPending \|\| lifecycleChanged \|\| EditorApplication\.timeSinceStartup >= nextRegistryRefreshAt[\s\S]*?readyPublicationPending = false;[\s\S]*?RegisterInstance\("ready"\)/);
  assert.match(workQueue, /nextRegistryRefreshAt = EditorApplication\.timeSinceStartup \+ RegistryRefreshSeconds/);
  assert.match(runtime, /private const int RegistryRefreshSeconds = 30/);
  assert.match(runtime, /nextRegistryRefreshAt = Time\.realtimeSinceStartup \+ RegistryRefreshSeconds/);
  assert.doesNotMatch(`${bridge}\n${workQueue}\n${runtime}`, /nextHeartbeatAt|HeartbeatSeconds|\+ 2\.0/);
});

test("Editor registry publication uses one bounded same-domain retry chain", async () => {
  const [registry, bridge, workQueue, unityTests] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/UnityCliBridge.cs", "utf8"),
    readFile("unitypkg/Editor/Bridge/BridgeWorkQueue.cs", "utf8"),
    readFile("unitypkg/Tests/Editor/UnityCliInstancePublicationRetryTests.cs", "utf8"),
  ]);
  assert.match(registry, /private const int RegistryPublicationMaxAttempts = 4/);
  assert.match(registry, /if \(registryPublicationRetryExhausted\) return false;[\s\S]*?if \(registryPublicationRetryPending\)[\s\S]*?pendingRegistryRequestedState = requestedState;[\s\S]*?return false/);
  assert.match(registry, /!registryPublicationRetryPending \|\| registryPublicationRetryExhausted \|\| !started \|\| domainReloading/);
  assert.match(registry, /registryPublicationRetryPending = false;[\s\S]*?AttemptInstancePublication\(string\.IsNullOrEmpty\(requestedState\) \? "ready" : requestedState\)/);
  assert.match(registry, /registryPublicationAttemptCount >= RegistryPublicationMaxAttempts[\s\S]*?registryPublicationRetryExhausted = true[\s\S]*?instance_publication_retry_stopped kind=[\s\S]*?nextAction=restore-data-root-write-access-then-reload-domain/);
  assert.match(registry, /failure is IOException[\s\S]*?failure is UnauthorizedAccessException[\s\S]*?failure is TimeoutException[\s\S]*?failure is Win32Exception/);
  assert.match(registry, /failedAttempts <= 1\) return 0\.25;[\s\S]*?failedAttempts == 2\) return 0\.5;[\s\S]*?return 1\.0/);
  assert.match(registry, /instance_publication_retry_succeeded attempts=/);
  assert.match(bridge, /PrepareListenerGenerationForStart\(\);[\s\S]*?if \(!RegisterInstance\("transitioning"\)\) throw[\s\S]*?queuePublicationOpen = true[\s\S]*?Task\.Run\(ListenLoop\);[\s\S]*?readyPublicationPending = true/);
  assert.match(bridge, /started = false;[\s\S]*?readyPublicationPending = false;[\s\S]*?CancelInstancePublicationRetry\(\)/);
  assert.match(workQueue, /RetryPendingWorkItemSettlements\(\);[\s\S]*?TickInstancePublicationRetry\(\);[\s\S]*?CurrentDurableMutationBarrier\(\)[\s\S]*?UnityCliBridgeQueuePolicy\.SelectStarts/);
  assert.match(unityTests, /new FileStream\(paths\.Temporary[\s\S]*?register\.Invoke[\s\S]*?Thread\.Sleep\(300\)[\s\S]*?tick\.Invoke/);
  assert.match(unityTests, /PersistentWriteFailureStopsAtFourAndDoesNotStartAnotherChain[\s\S]*?attemptCount[\s\S]*?Is\.EqualTo\(4\)[\s\S]*?exhausted[\s\S]*?Is\.True/);
  assert.match(unityTests, /CancelClearsExhaustionForTheNextDomainOwner[\s\S]*?register\.Invoke[\s\S]*?Is\.True/);
  assert.doesNotMatch(`${registry}\n${bridge}\n${workQueue}`, /EditorApplication\.update \+=.*Retry|new Timer|Task\.Delay/);
});

test("automatic instance retention is globally locked, oldest-first, and bounded", async () => {
  const [registry, cleanup, cleanupTests] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceCleanup.cs", "utf8"),
    readFile("unitypkg/Tests/Editor/UnityCliInstanceCleanupTests.cs", "utf8"),
  ]);
  assert.match(registry, /TryRunAutomaticInstanceCleanup\(\);[\s\S]*?return true/);
  assert.match(cleanup, /InstanceCleanupMaxScanned = 2000/);
  assert.match(cleanup, /InstanceCleanupMaxDeleted = 20/);
  assert.match(cleanup, /InstanceCleanupRetainedOffline = 100/);
  assert.match(cleanup, /InstanceCleanupRetainedDamaged = 20/);
  assert.match(cleanup, /InstanceCleanupRetentionDays = 30/);
  assert.match(cleanup, /TryWithCurrentUserMutex\("Local\\\\UnityCli\.Registry\.Cleanup", 0/);
  assert.match(cleanup, /summary\.Scanned < InstanceCleanupMaxScanned/);
  assert.match(cleanup, /OrderBy\(value => value\.LastActivity\)[\s\S]*?Take\(InstanceCleanupMaxDeleted\)/);
  assert.match(cleanup, /sessions\.Any\(IsProcessClaimAliveOrUnverifiable\)\) return false/);
  assert.match(cleanup, /FileAttributes\.ReparsePoint/);
  assert.match(cleanup, /TryWithCurrentUserMutex\("Local\\\\UnityCli\.Registry\." \+ candidate\.InstanceId, 0[\s\S]*?TryInspectCleanupCandidate[\s\S]*?File\.Delete\(candidate\.Path\)/);
  assert.match(cleanup, /current\.Damaged != candidate\.Damaged \|\| current\.LastActivity > candidate\.LastActivity/);
  assert.match(cleanup, /instance_cleanup_completed scanned=/);
  assert.match(cleanupTests, /index < 115[\s\S]*?index < 30[\s\S]*?WriteClaim\("active"[\s\S]*?WriteClaim\("unverifiable"[\s\S]*?WriteMixedClaims/);
  assert.match(cleanupTests, /Deleted"\), Is\.EqualTo\(20\)[\s\S]*?Deleted"\), Is\.EqualTo\(5\)[\s\S]*?CountOffline\(\), Is\.EqualTo\(100\)[\s\S]*?CountDamaged\(\), Is\.EqualTo\(20\)/);
  assert.match(cleanupTests, /new Mutex\(false, "Local\\\\UnityCli\.Registry\." \+ instanceId\)[\s\S]*?Deferred"\), Is\.EqualTo\(1\)[\s\S]*?File\.Exists\(candidate\), Is\.False/);
  assert.match(cleanup, /InstanceCleanupCompletion = new UnityCliToolCompletionContract\([\s\S]*?"atomic-typed-result-v1", "required", true\)/);
  assert.match(cleanup, /"internal\.instances\.prune"[\s\S]*?BeginDirectEffect\(request\.RequestId\)[\s\S]*?"removed"[\s\S]*?"remaining"[\s\S]*?"deferred"[\s\S]*?"scanned"[\s\S]*?"truncated"[\s\S]*?"retention"[\s\S]*?RecordDirectCompletionEvidence/);
  assert.doesNotMatch(cleanup, /Delete.*current|quarantine|\.bad["']/i);
});

test("registry recovery and cleanup reuse fixed temp paths and bounded rolling logs", async () => {
  const [log, registry, cleanup] = await Promise.all([
    readFile("unitypkg/Editor/Infrastructure/UnityCliLog.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceCleanup.cs", "utf8"),
  ]);
  assert.match(log, /BridgeLogMaxBytes = 10L \* 1024 \* 1024/);
  assert.match(log, /BridgeLogMaxEntryBytes = 64 \* 1024/);
  assert.match(log, /var oldPath = path \+ "\.old"[\s\S]*?File\.Copy\(path, oldPath, true\)/);
  assert.match(log, /BoundedBridgeLogLine\(line, lineBudget\)/);
  assert.match(registry, /var temporaryPath = path \+ "\.tmp"[\s\S]*?if \(File\.Exists\(temporaryPath\)\) File\.Delete\(temporaryPath\)/);
  assert.match(registry, /instance_registry_recovered reason=/);
  assert.match(registry, /instance_publication_retry_(?:succeeded|stopped)/);
  assert.match(cleanup, /instance_cleanup_(?:completed|deferred|failed)/);
  assert.doesNotMatch(`${registry}\n${cleanup}`, /quarantine|registry-recovery|Guid\.NewGuid\(\).*file|requestId.*File/);
});

test("instances prune invokes only the Unity writer and returns its bounded result", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-prune-"));
  const commands: string[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    commands.push(payload.command);
    assert.equal(payload.command, "internal.instances.prune");
    return {
      ok: true as const,
      requestId: payload.requestId,
      data: {
        removed: 3,
        remaining: 7,
        deferred: 1,
        scanned: 11,
        truncated: false,
        retention: { offlineMax: 100, damagedMax: 20, maxAgeDays: 30, scanLimit: 2000, deleteLimit: 20 },
      },
    };
  };
  const result = await execute(["instances", "prune", "--timeout-ms", "5000"], { dataDir, bridgeExecute } as never);
  assert.ok(result.ok, JSON.stringify(result));
  assert.deepEqual(result.data, {
    removed: 3,
    remaining: 7,
    deferred: 1,
    scanned: 11,
    truncated: false,
    retention: { offlineMax: 100, damagedMax: 20, maxAgeDays: 30, scanLimit: 2000, deleteLimit: 20 },
  });
  assert.deepEqual(commands, ["internal.instances.prune"]);

  const invalid = await execute(["instances", "prune", "--timeout-ms", "0"], { dataDir, bridgeExecute } as never);
  assert.equal(invalid.ok, false);
  if (!invalid.ok) assert.equal(invalid.error.code, "E_SCHEMA_INVALID");
  assert.deepEqual(commands, ["internal.instances.prune"]);
});

test("instances prune preserves busy errors and rejects non-contract Bridge data", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-prune-errors-"));
  const busy = await execute(["instances", "prune"], { dataDir, bridgeExecute: async (payload: BridgeRequest) => ({
    ok: false as const,
    requestId: payload.requestId,
    error: { code: "E_UNITY_BUSY", message: "cleanup busy", nextActions: ["Retry later."], transient: true, retryAfterMs: 100 },
  }) } as never);
  assert.equal(busy.ok, false);
  if (!busy.ok) {
    assert.equal(busy.error.code, "E_UNITY_BUSY");
    assert.equal(busy.error.transient, true);
    assert.equal(busy.error.retryAfterMs, 100);
    assert.deepEqual(busy.error.nextActions, ["Retry later."]);
  }

  const malformed = await execute(["instances", "prune"], { dataDir, bridgeExecute: async (payload: BridgeRequest) => ({
    ok: true as const,
    requestId: payload.requestId,
    data: { removed: 0, remaining: 1, deferred: 0, scanned: 1, truncated: false, retention: { offlineMax: 100, damagedMax: 20, maxAgeDays: 30, scanLimit: 2000, deleteLimit: 20 }, internalPath: "forbidden" },
  }) } as never);
  assert.equal(malformed.ok, false);
  if (!malformed.ok) assert.equal(malformed.error.code, "E_BRIDGE_RESPONSE_INVALID");
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

test("full instance id observation waits for the first fixed-file publication", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-first-instance-publication-"));
  const projectPath = await project("unity-cli-first-instance-project-");
  const identity = registryIdentity(projectPath);
  const delayedWrite = new Promise<void>((resolveWrite, rejectWrite) => {
    setTimeout(() => { writeRegistry(dataDir, makeRegistry(identity, [])).then(() => resolveWrite(), rejectWrite); }, 20);
  });
  const observing = selectObservationInstanceForFlags({ instance: identity.instanceId }, dataDir, 500, projectPath);
  const [observed] = await Promise.all([observing, delayedWrite]);
  assert.equal(observed.instance.instanceId, identity.instanceId);
  assert.equal(observed.instance.canonicalProjectPath, identity.canonicalProjectPath);
  assert.equal(observed.instance.status, "offline");
});

test("observation preserves canonical identity while the running writer replaces invalid state", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-invalid-observation-"));
  const projectPath = await project("unity-cli-invalid-observation-project-");
  const identity = registryIdentity(projectPath);
  await writeInvalidRegistry(dataDir, identity.instanceId, "not-a-registry");
  const observed = await selectObservationInstanceForFlags({ instance: projectPath }, dataDir, 500, projectPath);
  assert.equal(observed.instance.instanceId, identity.instanceId);
  assert.equal(observed.instance.canonicalProjectPath, identity.canonicalProjectPath);
  assert.equal(observed.instance.status, "offline");
  assert.equal(observed.instance.sessions.length, 0);
});

test("recovery-boundary fixtures cover malformed, non-object, old-version, and identity mismatch state", async () => {
  for (const fixture of ["malformed", "non-object", "old-version", "identity-mismatch"] as const) {
    const dataDir = await mkdtemp(join(tmpdir(), `unity-cli-recovery-${fixture}-`));
    const projectPath = await project(`unity-cli-recovery-project-${fixture}-`);
    const identity = registryIdentity(projectPath);
    const directory = join(dataDir, "instances");
    const file = join(directory, `${identity.instanceId}.json`);
    await mkdir(directory, { recursive: true });
    const content = fixture === "malformed" ? '{"version":3,'
      : fixture === "non-object" ? "[]"
        : fixture === "old-version" ? JSON.stringify({ version: 2 })
          : JSON.stringify({ ...makeRegistry(identity, []), identity: { ...identity, canonicalProjectPath: `${identity.canonicalProjectPath}\\wrong` } });
    await writeFile(file, content, "utf8");
    const observed = await selectObservationInstanceForFlags({ instance: projectPath }, dataDir, 200, projectPath);
    assert.equal(observed.instance.instanceId, identity.instanceId, fixture);
    assert.equal(observed.instance.status, "offline", fixture);
    await assert.rejects(
      selectInstanceForFlags({ instance: projectPath }, dataDir, projectPath),
      (error: unknown) => error instanceof InstanceRegistryError && error.code === "E_INSTANCE_IDENTITY_MISMATCH",
      fixture,
    );
  }
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

test("full instance view converges when the formal name appears during an empty enumeration window", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-instance-enumeration-convergence-"));
  const projectPath = await project("unity-cli-instance-enumeration-project-");
  const identity = registryIdentity(projectPath);
  await mkdir(join(dataDir, "instances"), { recursive: true });
  await writeFile(join(dataDir, "instances", `${identity.instanceId}.json.tmp`), "temporary", "utf8");

  const delayedWrite = new Promise<void>((resolveWrite, rejectWrite) => {
    setTimeout(() => { writeRegistry(dataDir, makeRegistry(identity, [])).then(() => resolveWrite(), rejectWrite); }, 8);
  });
  const reading = readInstancesWithRegistryDiagnostics(dataDir);
  const [result] = await Promise.all([reading, delayedWrite]);
  assert.equal(result.invalidRecordCount, 0);
  assert.deepEqual(result.instances.map((instance) => instance.instanceId), [identity.instanceId]);
  assert.equal(result.instances[0].status, "offline");
});

test("missing selected instance errors omit redundant identity details", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-missing-details-"));
  const projectPath = await project("unity-cli-missing-details-project-");
  try {
    await assert.rejects(
      selectInstanceForFlags({ instance: projectPath }, dataDir, projectPath),
      (error: unknown) => error instanceof InstanceRegistryError
        && error.code === "E_INSTANCE_NOT_RUNNING"
        && error.details === undefined,
    );
  } finally {
    await Promise.all([rm(dataDir, { recursive: true, force: true }), rm(projectPath, { recursive: true, force: true })]);
  }
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

test("formal Editor identity does not require a projectPath process argument", async () => {
  const source = await readFile("src/instances.ts", "utf8");
  assert.match(source, /if \(index < 0\) return "verified";/);
  assert.match(source, /const contradictoryClaim = target === "editor"[\s\S]*claims\.processIds\.length > 0/);
  assert.doesNotMatch(source, /index < 0 \|\| !argv\?\.\[index \+ 1\]/);
  assert.doesNotMatch(source, /const exactClaim = target === "runtime"/);
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

  const registryClaim = reduceObservationRegistryRecord(makeRegistry(identity, [first]));
  const noCommandLineClaim = applyProjectProcessClaimInspection(
    registryClaim,
    { verification: "verified", processIds: [] },
  );
  assert.equal(noCommandLineClaim.status, registryClaim.status);
  assert.equal(noCommandLineClaim.activeSession?.sessionId, registryClaim.activeSession?.sessionId);

  const unavailableSupplement = applyProjectProcessClaimInspection(
    registryClaim,
    { verification: "unverifiable", processIds: [] },
  );
  assert.equal(unavailableSupplement.status, registryClaim.status);
  assert.equal(unavailableSupplement.activeSession?.sessionId, registryClaim.activeSession?.sessionId);

  const processConflict = applyProjectProcessClaimInspection(
    registryClaim,
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
