import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest, BridgeResponse } from "../src/bridge/index.js";
import { handleDoctorCommand } from "../src/doctor.js";
import { readInstances } from "../src/instances.js";
import {
  createUnityProject,
  makeRegistry,
  makeRegistrySession,
  registryIdentity,
  startStrictHealthServer,
  writeRegistry,
} from "./helpers/instance-registry-v3.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

type MainThreadFixture = {
  observed: boolean;
  sequence: number;
  ageMs: number | null;
  listenerGeneration: number;
  lifecycle: string;
};

/** 创建含严格 v3 claim 与可控 Health pulse 的 doctor 夹具。 */
async function doctorFixture(mainThread: Partial<MainThreadFixture> = {}) {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-doctor-data-"));
  const projectPath = await mkdtemp(join(tmpdir(), "unity-cli-doctor-project-"));
  await createUnityProject(projectPath);
  const identity = registryIdentity(projectPath);
  const sessionId = `session-${"1".repeat(64)}`;
  const listenerId = `listener-${"2".repeat(32)}`;
  const health = await startStrictHealthServer((_request, port) => ({
    version: 3,
    target: "editor",
    instanceId: identity.instanceId,
    canonicalProjectPath: identity.canonicalProjectPath,
    canonicalizationVersion: 1,
    sessionId,
    listenerId,
    listenerGeneration: 1,
    actualPort: port,
    buildStamp: "test-build",
    mainThread: {
      observed: true,
      sequence: 10,
      ageMs: 0,
      listenerGeneration: 1,
      lifecycle: "ready",
      ...mainThread,
    },
  }));
  const session = await makeRegistrySession(health.endpoint, { sessionId, editorSessionId: sessionId });
  await writeRegistry(dataDir, makeRegistry(identity, [session]));
  return { dataDir, projectPath, identity, session, health };
}

function healthyDoctorBridge(fixture: Awaited<ReturnType<typeof doctorFixture>>, requests: BridgeRequest[] = []): (payload: BridgeRequest) => Promise<BridgeResponse> {
  return async (payload) => {
    requests.push(payload);
    if (payload.command === "internal.doctor.business") {
      return { ok: true, requestId: payload.requestId, data: { accepted: true, projectPath: fixture.projectPath, sceneLoaded: true, activeScenePath: "Assets/Main.unity", playMode: false } };
    }
    if (payload.command === "internal.doctor.catalog") {
      return { ok: true, requestId: payload.requestId, data: { algorithm: "unity-cli-project-tool-fingerprint-v2", catalogFingerprint: "a".repeat(64), toolCount: 327, blockedCount: 0, blockedToolsTruncated: false, blockedTools: [] } };
    }
    if (payload.command === "internal.doctor.compile") {
      return { ok: true, requestId: payload.requestId, data: { compiling: false, scanComplete: true, hasCompileErrors: false, compileErrorCount: 0, compileErrorsTruncated: false, compileErrors: [], nextActions: [] } };
    }
    throw new Error(`Unexpected doctor command: ${payload.command}`);
  };
}

async function doctorRuntime(fixture: Awaited<ReturnType<typeof doctorFixture>>, bridgeExecute = healthyDoctorBridge(fixture)) {
  return {
    dataDir: fixture.dataDir,
    selectInstance: async () => ({ instance: (await readInstances(fixture.dataDir))[0]!, matchedBy: "instanceId" as const }),
    bridgeExecute,
  };
}

function checks(result: Awaited<ReturnType<typeof handleDoctorCommand>>) {
  assert.ok(result.ok, JSON.stringify(result));
  return (result.data as { summary: string; checks: Array<{ name: string; status: string; message: string; details?: Record<string, unknown> }> }).checks;
}

test("doctor reports an empty registry through a public success envelope", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-doctor-empty-"));
  const result = await handleDoctorCommand({}, { dataDir }, "req_doctor_empty");
  const resultChecks = checks(result);
  assert.equal(resultChecks.find((check) => check.name === "registry_health_identity")?.details?.code, "E_INSTANCE_REQUIRED");
  assert.equal(resultChecks.find((check) => check.name === "main_thread_pulse")?.details?.code, "E_MAIN_THREAD_PULSE_UNAVAILABLE");
  assert.equal(Object.hasOwn(result.data as object, "command"), false);
});

test("doctor keeps the six live diagnostic owners independent", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const requests: BridgeRequest[] = [];
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, await doctorRuntime(fixture, healthyDoctorBridge(fixture, requests)), "req_doctor_live");
  const resultChecks = checks(result);

  assert.equal(resultChecks.find((check) => check.name === "registry_health_identity")?.status, "pass");
  assert.equal(resultChecks.find((check) => check.name === "registry_health_identity")?.details?.transportVerified, true);
  assert.equal(resultChecks.find((check) => check.name === "main_thread_pulse")?.status, "pass");
  assert.equal(resultChecks.find((check) => check.name === "business_diagnostic")?.details?.accepted, true);
  assert.equal(resultChecks.find((check) => check.name === "catalog_integrity")?.details?.toolCount, 327);
  assert.equal(resultChecks.find((check) => check.name === "compile")?.details?.hasCompileErrors, false);
  assert.ok(resultChecks.find((check) => check.name === "extensions"));
  assert.deepEqual(requests.map((request) => request.command).sort(), [
    "internal.doctor.business",
    "internal.doctor.catalog",
    "internal.doctor.compile",
  ]);
  assert.doesNotMatch(JSON.stringify(result), /endpoint|listenerId|listenerGeneration|actualPort|logPath|warnCount|fingerprintMode/);
});

test("business admission failure never rewrites verified transport as disconnected", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const bridge = healthyDoctorBridge(fixture);
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, await doctorRuntime(fixture, async (payload) => {
    if (payload.command !== "internal.doctor.business") return bridge(payload);
    return {
      ok: false,
      requestId: payload.requestId,
      error: {
        code: "E_ADMISSION_REQUIRED",
        message: "Root admission is required for this business read.",
        transient: false,
        retryAfterMs: 0,
        nextActions: ["Acquire one root admission for the exact request."],
      },
    };
  }), "req_doctor_admission");
  const resultChecks = checks(result);
  const identity = resultChecks.find((check) => check.name === "registry_health_identity");
  const business = resultChecks.find((check) => check.name === "business_diagnostic");

  assert.equal(identity?.status, "pass");
  assert.equal(identity?.details?.transportVerified, true);
  assert.equal(business?.status, "fail");
  assert.equal(business?.details?.code, "E_ADMISSION_REQUIRED");
  assert.equal(business?.details?.transient, false);
  assert.deepEqual(business?.details?.nextActions, ["Acquire one root admission for the exact request."]);
  assert.doesNotMatch(JSON.stringify(result), /disconnected|no Unity instance is connected/i);
});

test("stale main-thread pulse remains distinct from verified Health transport", async (t) => {
  const fixture = await doctorFixture({ ageMs: 5_000 });
  t.after(() => fixture.health.close());
  let bridgeCalls = 0;
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, await doctorRuntime(fixture, async () => {
    bridgeCalls++;
    throw new Error("stale pulse must skip queued diagnostics");
  }), "req_doctor_stale_pulse");
  const resultChecks = checks(result);

  assert.equal(resultChecks.find((check) => check.name === "registry_health_identity")?.details?.transportVerified, true);
  assert.equal(resultChecks.find((check) => check.name === "main_thread_pulse")?.details?.code, "E_MAIN_THREAD_UNRESPONSIVE");
  assert.equal(resultChecks.find((check) => check.name === "business_diagnostic")?.details?.code, "E_BUSINESS_DIAGNOSTIC_SKIPPED");
  assert.equal(resultChecks.find((check) => check.name === "catalog_integrity")?.details?.code, "E_DOCTOR_READ_SKIPPED");
  assert.equal(bridgeCalls, 0);
});

test("catalog and compile checks validate bounded business fields and stable codes", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const bridge = healthyDoctorBridge(fixture);
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, await doctorRuntime(fixture, async (payload) => {
    if (payload.command === "internal.doctor.catalog") {
      return { ok: true, requestId: payload.requestId, data: { algorithm: "unity-cli-project-tool-fingerprint-v2", catalogFingerprint: "b".repeat(64), toolCount: 327, blockedCount: 1, blockedToolsTruncated: false, blockedTools: [{ name: "project.audit", message: "fingerprint changed", rePinValue: `unity-cli-project-tool-fingerprint-v2:${"c".repeat(64)}` }] } };
    }
    if (payload.command === "internal.doctor.compile") {
      return { ok: true, requestId: payload.requestId, data: { compiling: false, scanComplete: true, hasCompileErrors: true, compileErrorCount: 1, compileErrorsTruncated: false, compileErrors: ["Assets/Broken.cs(1,1): error CS1002"], nextActions: ["Fix the compiler error."] } };
    }
    return bridge(payload);
  }), "req_doctor_failures");
  const resultChecks = checks(result);
  const catalog = resultChecks.find((check) => check.name === "catalog_integrity");
  const compile = resultChecks.find((check) => check.name === "compile");

  assert.equal(catalog?.status, "fail");
  assert.equal(catalog?.details?.code, "E_TOOL_FINGERPRINT_MISMATCH");
  assert.equal(catalog?.details?.blockedCount, 1);
  assert.equal(compile?.status, "fail");
  assert.equal(compile?.details?.code, "E_UNITY_COMPILE_FAILED");
  assert.equal(compile?.details?.compileErrorCount, 1);
  assert.deepEqual(compile?.details?.nextActions, ["Fix the compiler error."]);
});

test("doctor accepts a consistent bounded blocked Tool sample", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const bridge = healthyDoctorBridge(fixture);
  const blockedTools = Array.from({ length: 32 }, (_, index) => ({
    name: `project.tool-${index}`,
    message: "fingerprint changed",
    rePinValue: `unity-cli-project-tool-fingerprint-v2:${index.toString(16).padStart(64, "0")}`,
  }));
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, await doctorRuntime(fixture, async (payload) => {
    if (payload.command === "internal.doctor.catalog") {
      return { ok: true, requestId: payload.requestId, data: { algorithm: "unity-cli-project-tool-fingerprint-v2", catalogFingerprint: "b".repeat(64), toolCount: 327, blockedCount: 33, blockedToolsTruncated: true, blockedTools } };
    }
    return bridge(payload);
  }), "req_doctor_truncated_catalog");
  const catalog = checks(result).find((check) => check.name === "catalog_integrity");
  assert.equal(catalog?.status, "fail");
  assert.equal(catalog?.details?.blockedCount, 33);
  assert.equal(catalog?.details?.blockedToolsTruncated, true);
  assert.equal((catalog?.details?.blockedTools as unknown[]).length, 32);
});

test("doctor rejects catalog DTO field, byte, name, and aggregate boundary violations", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const bridge = healthyDoctorBridge(fixture);
  const validBlocked = {
    name: "project.audit",
    message: "fingerprint changed",
    rePinValue: `unity-cli-project-tool-fingerprint-v2:${"c".repeat(64)}`,
  };
  const base = {
    algorithm: "unity-cli-project-tool-fingerprint-v2",
    catalogFingerprint: "b".repeat(64),
    toolCount: 327,
    blockedCount: 1,
    blockedToolsTruncated: false,
    blockedTools: [validBlocked],
  };
  const invalidCatalogs = [
    { ...base, internalPath: "forbidden" },
    { ...base, blockedTools: [{ ...validBlocked, internalPath: "forbidden" }] },
    { ...base, blockedTools: [{ ...validBlocked, name: "project.数据" }] },
    { ...base, blockedTools: [{ ...validBlocked, name: `project.${"a".repeat(129)}` }] },
    { ...base, blockedTools: [{ ...validBlocked, message: "m".repeat(1025) }] },
    { ...base, blockedToolsTruncated: true },
    { ...base, blockedCount: 2 },
    { ...base, toolCount: 10_001 },
    { ...base, blockedTools: Array.from({ length: 32 }, (_, index) => ({ ...validBlocked, name: `project.tool-${index}`, message: "\u0001".repeat(1024) })), blockedCount: 32 },
  ];

  for (const [index, catalog] of invalidCatalogs.entries()) {
    const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, await doctorRuntime(fixture, async (payload) => {
      if (payload.command === "internal.doctor.catalog") return { ok: true, requestId: payload.requestId, data: catalog };
      return bridge(payload);
    }), `req_doctor_invalid_catalog_${index}`);
    const catalogCheck = checks(result).find((check) => check.name === "catalog_integrity");
    assert.equal(catalogCheck?.status, "fail");
    assert.equal(catalogCheck?.details?.code, "E_BRIDGE_RESPONSE_INVALID");
    assert.doesNotMatch(JSON.stringify(catalogCheck), /forbidden|数据/);
  }
});

test("doctor warns about invalid registry records while preserving a valid strict identity", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const invalidProject = await mkdtemp(join(tmpdir(), "unity-cli-doctor-invalid-project-"));
  await createUnityProject(invalidProject);
  const invalidIdentity = registryIdentity(invalidProject);
  const invalidPath = join(fixture.dataDir, "instances", `${invalidIdentity.instanceId}.json`);
  await writeFile(invalidPath, "{\"version\":2}\n", "utf8");

  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, await doctorRuntime(fixture), "req_doctor_invalid_registry");
  const identity = checks(result).find((check) => check.name === "registry_health_identity");
  assert.equal(identity?.status, "warn");
  assert.equal(identity?.details?.invalidRecordCount, 1);
  assert.equal(identity?.details?.transportVerified, true);
  assert.doesNotMatch(JSON.stringify(result), new RegExp(invalidPath.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i"));
});

test("doctor recursively excludes sensitive Bridge error details", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, await doctorRuntime(fixture, async (payload) => ({
    ok: false,
    requestId: payload.requestId,
    error: {
      code: "E_ADMISSION_REQUIRED",
      message: "Business admission failed.",
      nextActions: [],
      transient: false,
      retryAfterMs: 0,
      details: {
        endpoint: "http://127.0.0.1:54321",
        listener: { id: "secret", generation: 9 },
        nested: { hwnd: "0x123", commandLine: "Unity.exe -projectPath secret" },
      },
    },
  })), "req_doctor_scrub");

  const serialized = JSON.stringify(result);
  assert.doesNotMatch(serialized, /54321|secret|0x123|commandLine|endpoint|listener/);
  assert.equal(checks(result).find((check) => check.name === "business_diagnostic")?.details?.code, "E_ADMISSION_REQUIRED");
});

test("doctor reports obsolete local state without deleting it", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-doctor-obsolete-"));
  await mkdir(dataDir, { recursive: true });
  await writeFile(join(dataDir, "config.json"), "{}\n", "utf8");
  const result = await handleDoctorCommand({}, { dataDir }, "req_doctor_obsolete");
  const check = checks(result).find((entry) => entry.name === "obsolete_local_state");
  assert.equal(check?.status, "fail");
  assert.equal(check?.details?.code, "E_OBSOLETE_LOCAL_STATE");
  assert.ok((check?.details?.paths as string[] | undefined)?.some((path) => path.endsWith("config.json")));
});
