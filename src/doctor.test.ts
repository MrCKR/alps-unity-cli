import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { handleDoctorCommand, type DoctorRuntime, type DoctorCheck } from "./doctor.js";
import { readInstances, writeInstances, type UnityInstance } from "./instances.js";

process.env.ALPS_UNITY_ALLOW_NON_WINDOWS = "1";

async function makeDataDir(): Promise<string> {
  return mkdtemp(join(tmpdir(), "unity_cli-doctor-test-"));
}

function makeRuntime(dataDir: string, bridgeExecute?: DoctorRuntime["bridgeExecute"]): DoctorRuntime {
  return { dataDir, bridgeExecute };
}

function makeInstance(overrides: Partial<UnityInstance> = {}): UnityInstance {
  const now = new Date().toISOString();
  return {
    instanceId: "inst_test001",
    projectPath: "/tmp/test-project",
    projectHash: "abc123",
    bridgeEndpoint: "http://127.0.0.1:47392",
    status: "healthy",
    registeredAt: now,
    lastHeartbeatAt: now,
    ...overrides
  };
}

test("doctor returns all-pass when registry has healthy instance and bridge is reachable", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime = makeRuntime(dataDir, async (payload) => {
    return { ok: true, data: { command: payload.command, status: "ok" } };
  });

  const result = await handleDoctorCommand({}, runtime, "req_doctor_1");
  assert.equal(result.ok, true);
  const data = result.data as { summary: string; checks: DoctorCheck[] };
  assert.equal(data.summary, "pass");
  assert.equal(data.checks.length, 6);

  const registryCheck = data.checks.find((c) => c.name === "instance_registry");
  assert.equal(registryCheck?.status, "pass");

  const healthyCheck = data.checks.find((c) => c.name === "healthy_instances");
  assert.equal(healthyCheck?.status, "pass");

  const bridgeCheck = data.checks.find((c) => c.name === "bridge_connectivity");
  assert.equal(bridgeCheck?.status, "pass");
});

test("doctor returns warn when instance registry is empty", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, []);

  const runtime = makeRuntime(dataDir);

  const result = await handleDoctorCommand({}, runtime, "req_doctor_2");
  assert.equal(result.ok, true);
  const data = result.data as { summary: string; checks: DoctorCheck[]; suggestions: string[] };
  assert.equal(data.summary, "warn");

  const registryCheck = data.checks.find((c) => c.name === "instance_registry");
  assert.equal(registryCheck?.status, "warn");
  assert.ok(data.suggestions.some((s) => s.includes("init")));
});

test("doctor returns fail when all instances are stale", async () => {
  const dataDir = await makeDataDir();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(dataDir, [makeInstance({ lastHeartbeatAt: staleTime })]);

  const runtime = makeRuntime(dataDir);
  const result = await handleDoctorCommand({}, runtime, "req_doctor_3");
  assert.equal(result.ok, true);
  const data = result.data as { summary: string; checks: DoctorCheck[] };
  assert.equal(data.summary, "fail");

  const healthyCheck = data.checks.find((c) => c.name === "healthy_instances");
  assert.equal(healthyCheck?.status, "fail");
});

test("doctor returns fail when bridge is unreachable", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime = makeRuntime(dataDir, async () => {
    return { ok: false, error: { code: "BRIDGE_CONNECTION_FAILED", message: "Connection refused" } };
  });

  const result = await handleDoctorCommand({}, runtime, "req_doctor_4");
  assert.equal(result.ok, true);
  const data = result.data as { summary: string; checks: DoctorCheck[] };
  assert.equal(data.summary, "fail");

  const bridgeCheck = data.checks.find((c) => c.name === "bridge_connectivity");
  assert.equal(bridgeCheck?.status, "fail");
});

test("doctor returns warn when data directory does not exist", async () => {
  const dataDir = join(await mkdtemp(join(tmpdir(), "unity_cli-doctor-")), "nonexistent");
  const runtime = makeRuntime(dataDir);

  const result = await handleDoctorCommand({}, runtime, "req_doctor_5");
  assert.equal(result.ok, true);
  const data = result.data as { summary: string; checks: DoctorCheck[] };

  const dirCheck = data.checks.find((c) => c.name === "data_directory");
  assert.equal(dirCheck?.status, "warn");
});

test("doctor returns pass for job store when no jobs have been run", async () => {
  const dataDir = await makeDataDir();
  const runtime = makeRuntime(dataDir);

  const result = await handleDoctorCommand({}, runtime, "req_doctor_6");
  assert.equal(result.ok, true);
  const data = result.data as { checks: DoctorCheck[] };

  const jobStoreCheck = data.checks.find((c) => c.name === "job_store");
  assert.equal(jobStoreCheck?.status, "pass");
  assert.ok(jobStoreCheck?.message.includes("no jobs run"));
});

test("doctor returns pass for job store when jobs exist", async () => {
  const dataDir = await makeDataDir();
  const { JobStore } = await import("./job-store.js");
  const store = new JobStore(dataDir);
  await store.create({
    id: "job_test1",
    kind: "build",
    status: "succeeded",
    requestId: "req_test",
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    projectPath: "/tmp/test",
    unityPath: "Unity.exe",
    logPath: "/tmp/test.log",
    command: ["Unity.exe"]
  });

  const runtime = makeRuntime(dataDir);
  const result = await handleDoctorCommand({}, runtime, "req_doctor_7");
  assert.equal(result.ok, true);
  const data = result.data as { checks: DoctorCheck[] };

  const jobStoreCheck = data.checks.find((c) => c.name === "job_store");
  assert.equal(jobStoreCheck?.status, "pass");
  assert.ok(jobStoreCheck?.message.includes("1 job"));
});

test("doctor config integrity check warns on missing bridgeEndpoint", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance({ bridgeEndpoint: "" as unknown as string })]);

  const runtime = makeRuntime(dataDir);
  const result = await handleDoctorCommand({}, runtime, "req_doctor_8");
  assert.equal(result.ok, true);
  const data = result.data as { checks: DoctorCheck[] };

  const configCheck = data.checks.find((c) => c.name === "config_integrity");
  assert.equal(configCheck?.status, "warn");
  const details = configCheck?.details as { issues: string[] };
  assert.ok(details.issues.some((i) => i.includes("missing bridgeEndpoint")));
});

test("doctor uses --bridge-endpoint flag when provided", async () => {
  const dataDir = await makeDataDir();
  let capturedEndpoint = "";

  const runtime = makeRuntime(dataDir, async (payload) => {
    return { ok: true, data: { command: payload.command, status: "ok" } };
  });

  const result = await handleDoctorCommand({ "bridge-endpoint": "http://127.0.0.1:9999" }, runtime, "req_doctor_9");
  assert.equal(result.ok, true);
  const data = result.data as { checks: DoctorCheck[] };

  const bridgeCheck = data.checks.find((c) => c.name === "bridge_connectivity");
  assert.equal(bridgeCheck?.status, "pass");
  assert.ok(bridgeCheck?.message.includes("9999"));
});

test("doctor provides actionable suggestions when checks fail", async () => {
  const dataDir = await makeDataDir();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(dataDir, [makeInstance({ lastHeartbeatAt: staleTime })]);

  const runtime = makeRuntime(dataDir);
  const result = await handleDoctorCommand({}, runtime, "req_doctor_10");
  assert.equal(result.ok, true);
  const data = result.data as { suggestions: string[] };

  assert.ok(data.suggestions.some((s) => s.includes("cleanup") || s.includes("Reopen")));
});
