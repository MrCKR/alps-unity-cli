import assert from "node:assert/strict";
import { mkdtemp } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "./cli.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import { readInstances, writeInstances, type UnityInstance } from "./instances.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-instances-test-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

function makeInstance(overrides: Partial<UnityInstance> = {}): UnityInstance {
  const now = new Date().toISOString();
  return {
    instanceId: overrides.instanceId ?? "inst_test001",
    projectPath: overrides.projectPath ?? "/tmp/test-project",
    projectHash: overrides.projectHash ?? "abc123",
    bridgeEndpoint: overrides.bridgeEndpoint ?? "http://127.0.0.1:47392",
    status: overrides.status ?? "healthy",
    registeredAt: overrides.registeredAt ?? now,
    lastHeartbeatAt: overrides.lastHeartbeatAt ?? now,
    ...overrides
  };
}

test("instances cleanup removes stale instances and reports count", async () => {
  const runtime = await makeRuntime();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_healthy", lastHeartbeatAt: new Date().toISOString() }),
    makeInstance({ instanceId: "inst_stale1", lastHeartbeatAt: staleTime }),
    makeInstance({ instanceId: "inst_stale2", lastHeartbeatAt: staleTime })
  ]);

  const result = await execute(["instances", "cleanup"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as { removed: number; remaining: number; staleCleaned: Array<{ instanceId: string; pid: number | null; killed: boolean }> };
  assert.equal(data.removed, 2);
  assert.equal(data.remaining, 1);

  // staleCleaned should have entries for the stale instances
  assert.equal(data.staleCleaned.length, 2);
  assert.equal(data.staleCleaned[0].instanceId, "inst_stale1");
  assert.equal(data.staleCleaned[1].instanceId, "inst_stale2");
});

test("instances cleanup reports 0 removed when all instances are healthy", async () => {
  const runtime = await makeRuntime();
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_healthy1" }),
    makeInstance({ instanceId: "inst_healthy2" })
  ]);

  const result = await execute(["instances", "cleanup"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as { removed: number; remaining: number };
  assert.equal(data.removed, 0);
  assert.equal(data.remaining, 2);
});

test("instances cleanup reports 0 remaining when all instances are stale", async () => {
  const runtime = await makeRuntime();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_stale", lastHeartbeatAt: staleTime })
  ]);

  const result = await execute(["instances", "cleanup"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as { removed: number; remaining: number };
  assert.equal(data.removed, 1);
  assert.equal(data.remaining, 0);
});

test("instances cleanup on empty registry returns 0 removed", async () => {
  const runtime = await makeRuntime();
  await writeInstances(runtime.dataDir, []);

  const result = await execute(["instances", "cleanup"], runtime);
  assert.equal(result.ok, true);
  const data = result.data as { removed: number; remaining: number };
  assert.equal(data.removed, 0);
  assert.equal(data.remaining, 0);
});

test("instances list returns all instances with status", async () => {
  const runtime = await makeRuntime();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(runtime.dataDir, [
    makeInstance({ instanceId: "inst_healthy" }),
    makeInstance({ instanceId: "inst_stale", lastHeartbeatAt: staleTime })
  ]);

  const result = await execute(["instances", "list"], runtime);
  assert.equal(result.ok, true);
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
  assert.equal(result.ok, true);
  const data = result.data as { command: string; summary: string; checks: Array<{ name: string; status: string }> };
  assert.equal(data.command, "doctor");
  assert.ok(data.checks.length >= 1);
});

test("job status returns error when job ID is missing", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["job", "status"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "JOB_ID_REQUIRED");
});

test("job logs returns error when job ID is missing", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["job", "logs"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "JOB_ID_REQUIRED");
});

test("job cancel returns error when job ID is missing", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["job", "cancel"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "JOB_ID_REQUIRED");
});

test("job status returns not found for unknown job ID", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["job", "status", "--id", "job_nonexistent"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "JOB_NOT_FOUND");
});

test("job logs returns not found for unknown job ID", async () => {
  const runtime = await makeRuntime();

  const result = await execute(["job", "logs", "--id", "job_nonexistent"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "JOB_NOT_FOUND");
});