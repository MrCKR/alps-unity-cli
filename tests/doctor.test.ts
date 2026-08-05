import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import type { BridgeRequest } from "../src/bridge/index.js";
import { handleDoctorCommand } from "../src/doctor.js";
import {
  createUnityProject,
  makeRegistry,
  makeRegistrySession,
  registryIdentity,
  writeRegistry,
} from "./helpers/instance-registry-v2.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

/** 创建含严格 v2 instance claim 的 doctor 夹具。 */
async function doctorFixture() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-doctor-data-"));
  const projectPath = await mkdtemp(join(tmpdir(), "unity-cli-doctor-project-"));
  await createUnityProject(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:54321");
  const identity = registryIdentity(projectPath);
  await writeRegistry(dataDir, makeRegistry(identity, [session]));
  return { dataDir, projectPath, identity, session };
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

test("doctor uses strict instance selection and live diagnose.preflight", async () => {
  const fixture = await doctorFixture();
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
            bridge: { ready: true, toolCount: 327, contextCount: 22 },
            customTools: { fingerprint: { algorithm: "unity-cli-project-tool-fingerprint-v2", toolCount: 327, blockedCount: 0, tools: [] } },
            customContexts: { fingerprint: { algorithm: "unity-cli-project-context-fingerprint-v2", contextCount: 2, blockedCount: 0, contexts: [] } },
            checks: [],
          },
        },
      };
    }
    if (payload.command === "project-tool.list") return { ok: true as const, requestId: payload.requestId, data: { tools: [] } };
    if (payload.command === "context.list") return { ok: true as const, requestId: payload.requestId, data: { contexts: [] } };
    return { ok: true as const, requestId: payload.requestId, data: {} };
  };
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, {
    dataDir: fixture.dataDir,
    bridgeExecute,
  } as never, "req_doctor_live");

  assert.ok(result.ok);
  const data = result.data as { checks: Array<{ name: string; status: string }> };
  assert.equal(data.checks.find((check) => check.name === "bridge_connectivity")?.status, "pass");
  assert.equal(data.checks.find((check) => check.name === "project_tool_fingerprint")?.status, "pass");
  assert.equal(data.checks.find((check) => check.name === "project_context_fingerprint")?.status, "pass");
  assert.ok(requests.some((request) => request.command === "project-tool.run" && request.input?.name === "diagnose.preflight"));
  assert.doesNotMatch(JSON.stringify(result), /endpoint|listenerId|listenerGeneration|actualPort|logPath|warnCount|fingerprintMode/);
});

test("doctor recursively scrubs sensitive Bridge error details", async () => {
  const fixture = await doctorFixture();
  const result = await handleDoctorCommand({ instance: fixture.identity.instanceId }, {
    dataDir: fixture.dataDir,
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
