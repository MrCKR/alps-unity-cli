import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, writeFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { readInstances, writeInstances, selectInstanceForFlags, type UnityInstance } from "../src/instances.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

let healthEndpoint = "http://127.0.0.1:47392";
const staleEndpoint = "http://127.0.0.1:1";
let healthServer: Server | undefined;

test.before(async () => {
  healthEndpoint = await startHealthServer();
});

test.after(async () => {
  await new Promise<void>((resolve) => healthServer?.close(() => resolve()) ?? resolve());
});

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-instances-test-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function startHealthServer(): Promise<string> {
  healthServer = createServer((request, response) => {
    if (request.url === "/health") {
      const expectedTargetId = request.headers["x-unity-cli-expected-target-id"];
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, requestId: "", data: { bridge: "unity_cli", target: { id: expectedTargetId } } }));
      return;
    }
    response.writeHead(404);
    response.end();
  });
  await new Promise<void>((resolve) => healthServer?.listen(0, "127.0.0.1", resolve));
  const address = healthServer.address();
  assert.ok(address && typeof address === "object");
  return `http://127.0.0.1:${address.port}`;
}

function makeInstance(overrides: Partial<UnityInstance> = {}): UnityInstance {
  const now = new Date().toISOString();
  return {
    instanceId: overrides.instanceId ?? "inst_test001",
    projectPath: overrides.projectPath ?? "/tmp/test-project",
    projectHash: overrides.projectHash ?? "abc123",
    bridgeEndpoint: overrides.bridgeEndpoint ?? healthEndpoint,
    status: overrides.status ?? "healthy",
    registeredAt: overrides.registeredAt ?? now,
    lastHeartbeatAt: overrides.lastHeartbeatAt ?? now,
    ...overrides
  };
}

test("Unity instance heartbeat publishes registry files atomically", async () => {
  const source = await readFile("unitypkg/Editor/Infrastructure/UnityCliInstanceRegistry.cs", "utf8");
  assert.match(source, /private static void WriteRegistryTextAtomically\(/);
  assert.match(source, /File\.Replace\(temporaryPath, path, null\)/);
  assert.match(source, /File\.Move\(temporaryPath, path\)/);
  assert.match(source, /WriteRegistryTextAtomically\(InstancePath\(\), UnityCliSerializer\.Serialize\(instanceData\)\)/);
  assert.match(source, /WriteRegistryTextAtomically\(RegistryPath\(\), UnityCliSerializer\.Serialize\(registryData\)\)/);
  assert.doesNotMatch(source, /File\.WriteAllText\(InstancePath\(\)/);
  assert.doesNotMatch(source, /File\.WriteAllText\(RegistryPath\(\)/);
});

test("instance registry readers retry Windows sharing conflicts", async () => {
  const source = await readFile("src/instances.ts", "utf8");
  assert.match(source, /REGISTRY_READ_RETRY_DELAYS_MS = \[5, 10, 20, 40\]/);
  assert.match(source, /\(retryMissing && code === "ENOENT"\) \|\| code === "EBUSY" \|\| code === "EPERM" \|\| code === "EACCES"/);
  assert.match(source, /readRegistryText\(join\(dir, name\), true\)/);
  assert.match(source, /readRegistryText\(registryPath\(dataDir\)\)/);
  assert.match(source, /instances = await readInstances\(dataDir, Date\.now\(\), \{ useCache: true \}\)/);
  assert.match(source, /error instanceof SyntaxError \|\| isRegistryTransientReadError\(error\)/);
});

test("instances prune requires explicit confirmation", async () => {
  const runtime = await makeRuntime();
  await writeInstances(runtime.dataDir, [makeInstance({ instanceId: "inst_stale", bridgeEndpoint: staleEndpoint, lastHeartbeatAt: new Date(Date.now() - 60000).toISOString() })]);

  const result = await execute(["instances", "prune"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_DESTRUCTIVE_CONFIRMATION_REQUIRED");
});

test("instances prune removes stale registry records without terminating Editor processes", async () => {
  const runtime = await makeRuntime();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_healthy", lastHeartbeatAt: new Date().toISOString() }),
    makeInstance({ instanceId: "inst_stale1", bridgeEndpoint: staleEndpoint, processId: process.pid, lastHeartbeatAt: staleTime }),
    makeInstance({ instanceId: "inst_stale2", bridgeEndpoint: staleEndpoint, lastHeartbeatAt: staleTime })
  ]);

  const result = await execute(["instances", "prune", "--yes"], runtime);
  assert.ok(result.ok);
  const data = result.data as { removed: number; remaining: number; staleCleaned: Array<{ instanceId: string; pid: number | null; registryDeleted: boolean }> };
  assert.equal(data.removed, 2);
  assert.equal(data.remaining, 1);

  assert.equal(data.staleCleaned.length, 2);
  assert.deepEqual(data.staleCleaned[0], { instanceId: "inst_stale1", pid: process.pid, registryDeleted: true });
  assert.deepEqual(data.staleCleaned[1], { instanceId: "inst_stale2", pid: null, registryDeleted: true });
  assert.doesNotThrow(() => process.kill(process.pid, 0));
});

test("instances prune reports 0 removed when all instances are healthy", async () => {
  const runtime = await makeRuntime();
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_healthy1" }),
    makeInstance({ instanceId: "inst_healthy2" })
  ]);

  const result = await execute(["instances", "prune", "--yes"], runtime);
  assert.ok(result.ok);
  const data = result.data as { removed: number; remaining: number };
  assert.equal(data.removed, 0);
  assert.equal(data.remaining, 2);
});

test("instances prune reports 0 remaining when all instances are stale", async () => {
  const runtime = await makeRuntime();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_stale", bridgeEndpoint: staleEndpoint, lastHeartbeatAt: staleTime })
  ]);

  const result = await execute(["instances", "prune", "--yes"], runtime);
  assert.ok(result.ok);
  const data = result.data as { removed: number; remaining: number };
  assert.equal(data.removed, 1);
  assert.equal(data.remaining, 0);
});

test("instances prune on empty registry returns 0 removed", async () => {
  const runtime = await makeRuntime();
  await writeInstances(runtime.dataDir, []);

  const result = await execute(["instances", "prune", "--yes"], runtime);
  assert.ok(result.ok);
  const data = result.data as { removed: number; remaining: number };
  assert.equal(data.removed, 0);
  assert.equal(data.remaining, 0);
});

test("instances list returns all instances with status", async () => {
  const runtime = await makeRuntime();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_healthy" }),
    makeInstance({ instanceId: "inst_stale", bridgeEndpoint: staleEndpoint, lastHeartbeatAt: staleTime })
  ]);

  const result = await execute(["instances", "list"], runtime);
  assert.ok(result.ok);
  const data = result.data as { instances: UnityInstance[] };
  assert.equal(data.instances.length, 2);

  const healthy = data.instances.find((i) => i.instanceId === "inst_healthy");
  const stale = data.instances.find((i) => i.instanceId === "inst_stale");
  assert.equal(healthy?.status, "healthy");
  assert.equal(stale?.status, "stale");
});

test("doctor command is routed correctly via CLI execute", async () => {
  const runtime = await makeRuntime();
  await writeInstances(runtime.dataDir, []);

  const result = await execute(["doctor"], runtime);
  assert.ok(result.ok);
  const data = result.data as { command: string; summary: string; checks: Array<{ name: string; status: string }> };
  assert.equal(data.command, "doctor");
  assert.ok(data.checks.length >= 1);
});

test("job status returns error when job ID is missing", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["jobs", "status"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "JOB_ID_REQUIRED");
});

test("job logs returns error when job ID is missing", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["jobs", "logs"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "JOB_ID_REQUIRED");
});

test("job cancel returns error when job ID is missing", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["jobs", "cancel", "--yes"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "JOB_ID_REQUIRED");
});

test("job status returns not found for unknown job ID", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["jobs", "status", "job_nonexistent"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "JOB_NOT_FOUND");
});

test("job logs returns not found for unknown job ID", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["jobs", "logs", "job_nonexistent"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "JOB_NOT_FOUND");
});
test("control-plane duplicate tools and contexts are not exposed", async () => {
  const runtime = await makeRuntime();

  for (const argv of [
    ["tools", "run", "target.set", "anything"],
    ["tools", "run", "instances.prune"],
    ["tools", "run", "job.status", "--id", "anything"],
    ["tools", "run", "batch.schema"],
    ["tools", "run", "diagnose.doctor"],
    ["context", "read", "instances.list"],
    ["context", "read", "target.current"]
  ]) {
    const result = await execute(argv, runtime);
    assert.equal(result.ok, false, argv.join(" "));
  }
});

test("instances list reads UTF-8 BOM prefixed registry files", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-bom-"));
  const registryDir = join(dataDir, "instances");
  await mkdir(registryDir, { recursive: true });

  const instanceWithBom = {
    instanceId: "editor-bom",
    projectPath: "/tmp/project-bom",
    projectHash: "hash-bom",
    unityVersion: "2022.3.45f1",
    packageVersion: "com.unity_cli",
    pid: 1234,
    bridgeEndpoint: "http://127.0.0.1:47392",
    capabilitiesHash: "caps",
    status: "ready",
    editorState: { playMode: false, compiling: false, domainReloading: false },
    playModeState: { isPlaying: false, isPaused: false, isPlayingOrWillChangePlaymode: false },
    runtimeSessionId: null,
    toolCatalogFingerprint: null,
    registeredAt: new Date().toISOString(),
    lastHeartbeatAt: new Date().toISOString()
  };

  // Write with UTF-8 BOM prefix (0xEF 0xBB 0xBF), like C# File.WriteAllText(Encoding.UTF8)
  const bom = "\uFEFF";
  await writeFile(join(registryDir, "editor-bom.json"), bom + JSON.stringify(instanceWithBom, null, 2), "utf8");

  const instances = await readInstances(dataDir);
  assert.equal(instances.length, 1);
  assert.equal(instances[0].instanceId, "editor-bom");
  // Health check will fail (unreachable), but instanceId is preserved - the key is BOM does not prevent parsing
  assert.equal(instances[0].instanceId, "editor-bom");
});

// B1: 进程级隔离优先于全局活动目标。当 CWD 落在某个已就绪实例的项目目录下时，
// 应选中该实例，即使存在全局 activeTarget 指向另一个实例。
test("selectInstanceForFlags prefers CWD match over global activeTarget", async () => {
  const runtime = await makeRuntime();
  const projectA = await mkdtemp(join(tmpdir(), "unity_cli-cwd-pinned-A-"));
  const projectB = await mkdtemp(join(tmpdir(), "unity_cli-cwd-pinned-B-"));
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_A", projectPath: projectA, projectHash: "hashA" }),
    makeInstance({ instanceId: "inst_B", projectPath: projectB, projectHash: "hashB" })
  ]);

  // 设置全局 activeTarget 指向 A
  await execute(["target", "set", "inst_A"], runtime);

  // CWD 落在 B 项目目录下 → 应选中 B（目录隔离），而非 activeTarget(A)
  const selection = await selectInstanceForFlags({}, runtime.dataDir, projectB);
  assert.equal(selection?.instance.instanceId, "inst_B");
  assert.equal(selection?.matchedBy, "cwd");

  // CWD 落在任何项目之外 → 回退到全局 activeTarget(A)
  const nowhere = await mkdtemp(join(tmpdir(), "unity_cli-cwd-nowhere-"));
  const fallback = await selectInstanceForFlags({}, runtime.dataDir, nowhere);
  assert.equal(fallback?.instance.instanceId, "inst_A");
  assert.equal(fallback?.matchedBy, "activeTarget");

  // --instance 仍然最优先，覆盖 CWD
  const explicit = await selectInstanceForFlags({ instance: "inst_A" }, runtime.dataDir, projectB);
  assert.equal(explicit?.instance.instanceId, "inst_A");
  assert.equal(explicit?.matchedBy, "instanceId");
});
