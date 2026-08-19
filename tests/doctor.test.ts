import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
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

/** 创建含严格 v3 instance claim 的 doctor 夹具。 */
async function doctorFixture() {
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
    mainThread: { observed: true, sequence: 10, ageMs: 0, listenerGeneration: 1, lifecycle: "ready" },
  }));
  const session = await makeRegistrySession(health.endpoint, { sessionId, editorSessionId: sessionId });
  await writeRegistry(dataDir, makeRegistry(identity, [session]));
  return { dataDir, projectPath, identity, session, health };
}

test("doctor reports an empty registry through a public success envelope", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-doctor-empty-"));
  const result = await handleDoctorCommand({}, { dataDir } as never, "req_doctor_empty");
  assert.ok(result.ok);
  const data = result.data as { summary: string; checks: Array<{ name: string; status: string }> };
  assert.ok(["warn", "fail"].includes(data.summary));
  assert.equal(data.checks.find((check) => check.name === "instance_registry")?.status, "warn");
  assert.equal(Object.hasOwn(data, "command"), false);
});

test("doctor warns about invalid registry records and continues with valid instances", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const invalidProject = await mkdtemp(join(tmpdir(), "unity-cli-doctor-invalid-project-"));
  await createUnityProject(invalidProject);
  const invalidIdentity = registryIdentity(invalidProject);
  const invalidPath = join(fixture.dataDir, "instances", `${invalidIdentity.instanceId}.json`);
  await writeFile(invalidPath, "{\"version\":2}\n", "utf8");

  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, {
    dataDir: fixture.dataDir,
    selectInstance: async () => ({ instance: (await readInstances(fixture.dataDir))[0]!, matchedBy: "instanceId" }),
    bridgeExecute: async (payload: BridgeRequest) => payload.command === "project-tool.list"
      ? { ok: true as const, requestId: payload.requestId, data: { tools: [] } }
      : { ok: true as const, requestId: payload.requestId, data: { result: { checks: [], customTools: { fingerprint: { toolCount: 0, blockedCount: 0 } } } } },
  } as never, "req_doctor_invalid_registry");

  assert.ok(result.ok, JSON.stringify(result));
  const data = result.data as { summary: string; checks: Array<{ name: string; status: string; details?: Record<string, unknown> }> };
  const registry = data.checks.find((check) => check.name === "instance_registry");
  assert.equal(registry?.status, "warn");
  assert.deepEqual(registry?.details, { instanceCount: 1, invalidRecordCount: 1 });
  assert.equal(data.checks.find((check) => check.name === "ready_instances")?.status, "pass");
  assert.doesNotMatch(JSON.stringify(result), new RegExp(invalidPath.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i"));
});

test("doctor uses strict instance selection and live diagnose.preflight", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    if (payload.command === "project-tool.run" && payload.input?.name === "diagnose.preflight") {
      // 真实 Editor 主线程响应可能超过 1 秒；doctor 必须使用统一的即时观察预算。
      await new Promise((resolve) => setTimeout(resolve, 1_500));
      return {
        ok: true as const,
        requestId: payload.requestId,
        data: {
          result: {
            project: { path: fixture.projectPath },
            editor: { compiling: false, importing: false },
            bridge: { ready: true, toolCount: 327 },
            customTools: { fingerprint: { algorithm: "unity-cli-project-tool-fingerprint-v2", toolCount: 327, blockedCount: 0, tools: [] } },
            checks: [],
          },
        },
      };
    }
    if (payload.command === "project-tool.list") return { ok: true as const, requestId: payload.requestId, data: { tools: [] } };
    return { ok: true as const, requestId: payload.requestId, data: {} };
  };
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, {
    dataDir: fixture.dataDir,
    selectInstance: async () => ({ instance: (await readInstances(fixture.dataDir))[0]!, matchedBy: "instanceId" }),
    bridgeExecute,
  } as never, "req_doctor_live");

  assert.ok(result.ok);
  const data = result.data as { checks: Array<{ name: string; status: string }> };
  assert.equal(data.checks.find((check) => check.name === "bridge_connectivity")?.status, "pass");
  assert.equal(data.checks.find((check) => check.name === "project_tool_fingerprint")?.status, "pass");
  assert.equal(data.checks.some((check) => check.name.includes("context")), false);
  assert.ok(requests.some((request) => request.command === "project-tool.run" && request.input?.name === "diagnose.preflight"));
  assert.doesNotMatch(JSON.stringify(result), /endpoint|listenerId|listenerGeneration|actualPort|logPath|warnCount|fingerprintMode/);
});

test("doctor recursively scrubs sensitive Bridge error details", async (t) => {
  const fixture = await doctorFixture();
  t.after(() => fixture.health.close());
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, {
    dataDir: fixture.dataDir,
    selectInstance: async () => ({ instance: (await readInstances(fixture.dataDir))[0]!, matchedBy: "instanceId" }),
    bridgeExecute: async (payload: BridgeRequest) => ({
      ok: false as const,
      requestId: payload.requestId,
      error: {
        code: "BRIDGE_CONNECTION_FAILED",
        message: "Bridge failed.",
        nextActions: [],
        transient: false,
        retryAfterMs: 0,
        details: {
          endpoint: "http://127.0.0.1:54321",
          listener: { id: "secret", generation: 9 },
          nested: { hwnd: "0x123", commandLine: "Unity.exe -projectPath secret", business: "kept" },
        },
      },
    }),
  } as never, "req_doctor_scrub");

  assert.ok(result.ok);
  const serialized = JSON.stringify(result);
  assert.doesNotMatch(serialized, /54321|secret|0x123|commandLine|endpoint|listener/);
});

test("doctor reports obsolete local state without deleting it", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-doctor-obsolete-"));
  await mkdir(dataDir, { recursive: true });
  await writeFile(join(dataDir, "config.json"), "{}\n", "utf8");
  const result = await handleDoctorCommand({}, { dataDir } as never, "req_doctor_obsolete");
  assert.ok(result.ok);
  const check = (result.data as { checks: Array<{ name: string; status: string; details?: { paths?: string[] } }> }).checks
    .find((entry) => entry.name === "obsolete_local_state");
  assert.equal(check?.status, "fail");
  assert.ok(check?.details?.paths?.some((path) => path.endsWith("config.json")));
});
