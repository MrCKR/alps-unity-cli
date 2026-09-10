/**
 * bridge 启动阻断诊断回归测试：Unity 侧根因读取、连接失败透出、
 * doctor 编译标记校验，以及 init 包发布后的扩展升级自愈接线。
 */

import assert from "node:assert/strict";
import test from "node:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { readFileSync } from "node:fs";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

import { readBridgeStartBlockerDiagnostics } from "../src/bridge/start-diagnostics.js";
import { SessionSupervisor, SessionSupervisorError } from "../src/bridge/session-supervisor.js";
import { handleDoctorCommand } from "../src/doctor.js";
import { readInstances } from "../src/instances.js";
import { VFX_GRAPH_MANIFEST } from "../src/extensions/index.js";
import { installExtensionBundle } from "../src/extensions/installer.js";
import {
  createUnityProject,
  makeRegistry,
  makeRegistrySession,
  registryIdentity,
  startStrictHealthServer,
  writeRegistry,
} from "./helpers/instance-registry-v3.js";

// 夹具日志形状与真实 Unity 侧记录一致：`key: detail` 与 `bridge_start_blocked pipelineHandoff=...`。
const STALE_MARKER_LOG_LINES = (stamp: string): string =>
  `${stamp} tool_catalog_initialization_failed: Generated Extension assembly is loaded without its exact UnityCliExtensionAssembly marker: UnityCli.Extensions.Cinemachine.Editor.\n` +
  `${stamp} bridge_start_blocked pipelineHandoff=tool-registry-unhealthy\n`;

/** 创建安装了 vfx-graph 投影、注册了本进程真实启动时间的 Unity 项目夹具。 */
async function createBridgeDiagFixture(): Promise<{ dataDir: string; projectPath: string; instanceId: string; processStartedAt: string }> {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-bridge-diag-data-"));
  const projectPath = await mkdtemp(join(tmpdir(), "unity-cli-bridge-diag-proj-"));
  await createUnityProject(projectPath);
  // createUnityProject 不写 packages-lock；扩展依赖可用性判断需要它。
  await writeFile(join(projectPath, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: {} }));
  await installExtensionBundle(projectPath, VFX_GRAPH_MANIFEST);
  const identity = registryIdentity(projectPath);
  // 端口必须在 1024-65535 合法区间且 url 无尾斜杠，否则 registry 记录会被 canonical 校验整条拒绝。
  const session = await makeRegistrySession("http://127.0.0.1:65535");
  await writeRegistry(dataDir, makeRegistry(identity, [session]));
  return { dataDir, projectPath, instanceId: identity.instanceId, processStartedAt: session.processStartedAt };
}

/** 在项目内写入一段 bridge 启动阻断日志（时间戳晚于本次实例进程启动时间）。 */
async function writeBlockerLog(projectPath: string, stamp: string): Promise<void> {
  await mkdir(join(projectPath, "Library", "UnityCli"), { recursive: true });
  await writeFile(join(projectPath, "Library", "UnityCli", "UnityCliBridge.log"), STALE_MARKER_LOG_LINES(stamp));
}

/** 新鲜阻断日志时间戳：基于 registry 记录的本次进程启动时刻 +1s，避免 WMI 与系统时钟的跨源偏差抖动。 */
function freshStamp(processStartedAt: string): string {
  return new Date(Date.parse(processStartedAt) + 1_000).toISOString();
}

test("readBridgeStartBlockerDiagnostics extracts a fresh Unity log root cause with reinstall actions", async () => {
  const fixture = await createBridgeDiagFixture();
  try {
    await writeBlockerLog(fixture.projectPath, freshStamp(fixture.processStartedAt));
    const diagnostics = await readBridgeStartBlockerDiagnostics(fixture.dataDir, fixture.instanceId);
    assert.ok(diagnostics, "fresh blocker log must yield diagnostics");
    assert.equal(diagnostics.details.code, "bridge_start_blocked");
    assert.match(diagnostics.details.catalogDetail ?? "", /UnityCliExtensionAssembly marker: UnityCli\.Extensions\.Cinemachine\.Editor/);
    assert.match(diagnostics.message, /Unity blocked Bridge startup: .*UnityCliExtensionAssembly/);
    assert.match(diagnostics.nextActions[0] ?? "", /unitycli doctor --project/);
    assert.ok(
      diagnostics.nextActions.some((action) => /extensions remove <id>/.test(action) && /extensions install <id>/.test(action)),
      "marker failures must suggest extension reinstall actions",
    );
  } finally {
    await rm(fixture.projectPath, { recursive: true, force: true });
    await rm(fixture.dataDir, { recursive: true, force: true });
  }
});

test("readBridgeStartBlockerDiagnostics ignores blocker records older than the instance process start", async () => {
  const fixture = await createBridgeDiagFixture();
  try {
    // 旧 Unity 进程留下的记录：时间戳早于 processStartedAt - 5000ms。
    await writeBlockerLog(fixture.projectPath, "2026-07-16T00:00:00.000Z");
    const diagnostics = await readBridgeStartBlockerDiagnostics(fixture.dataDir, fixture.instanceId);
    assert.equal(diagnostics, undefined);
  } finally {
    await rm(fixture.projectPath, { recursive: true, force: true });
    await rm(fixture.dataDir, { recursive: true, force: true });
  }
});

test("readBridgeStartBlockerDiagnostics returns undefined when the project has no blocker log", async () => {
  const fixture = await createBridgeDiagFixture();
  try {
    const diagnostics = await readBridgeStartBlockerDiagnostics(fixture.dataDir, fixture.instanceId);
    assert.equal(diagnostics, undefined);
  } finally {
    await rm(fixture.projectPath, { recursive: true, force: true });
    await rm(fixture.dataDir, { recursive: true, force: true });
  }
});

test("supervisor attaches Unity log root cause to connection failures instead of a bare error", async () => {
  const fixture = await createBridgeDiagFixture();
  try {
    await writeBlockerLog(fixture.projectPath, freshStamp(fixture.processStartedAt));
    const supervisor = new SessionSupervisor({ instance: fixture.instanceId }, { dataDir: fixture.dataDir }, {
      post: async () => {
        throw new Error("connect ECONNREFUSED 127.0.0.1:65535");
      },
      selectObservation: selectObservationFromFixture(fixture.dataDir, fixture.instanceId),
    });
    let caught: unknown;
    try {
      await supervisor.observeNow({ requestId: "req_diag", command: "internal.editor.state.snapshot", input: {}, timeoutMs: 1_000, mutation: false });
    } catch (failure) {
      caught = failure;
    }
    assert.ok(caught instanceof SessionSupervisorError && caught.code === "BRIDGE_CONNECTION_FAILED", String(caught));
    const error = caught;
    assert.match(error.message, /connect ECONNREFUSED 127\.0\.0\.1:65535/);
    assert.match(error.message, /Unity blocked Bridge startup: .*UnityCliExtensionAssembly/);
    assert.equal((error.details?.bridgeStartBlocked as { code: string } | undefined)?.code, "bridge_start_blocked");
    assert.match(error.nextActions[0] ?? "", /unitycli doctor --project/);
  } finally {
    await rm(fixture.projectPath, { recursive: true, force: true });
    await rm(fixture.dataDir, { recursive: true, force: true });
  }
});

test("connection failures without a blocker log stay bare so unrelated errors are not masked", async () => {
  const fixture = await createBridgeDiagFixture();
  try {
    const supervisor = new SessionSupervisor({ instance: fixture.instanceId }, { dataDir: fixture.dataDir }, {
      post: async () => {
        throw new Error("connect ECONNREFUSED 127.0.0.1:65535");
      },
      selectObservation: selectObservationFromFixture(fixture.dataDir, fixture.instanceId),
    });
    let caught: unknown;
    try {
      await supervisor.observeNow({ requestId: "req_plain", command: "internal.editor.state.snapshot", input: {}, timeoutMs: 1_000, mutation: false });
    } catch (failure) {
      caught = failure;
    }
    assert.ok(caught instanceof SessionSupervisorError && caught.code === "BRIDGE_CONNECTION_FAILED", String(caught));
    const error = caught;
    assert.doesNotMatch(error.message, /Unity blocked Bridge startup/);
    assert.equal(error.details?.bridgeStartBlocked, undefined);
  } finally {
    await rm(fixture.projectPath, { recursive: true, force: true });
    await rm(fixture.dataDir, { recursive: true, force: true });
  }
});

/** doctor 夹具：严格 Health v3 transport identity + 注入的健康 doctor 桥接传输。 */
async function createDoctorFixture(projectPath: string, instanceId: string): Promise<{ dataDir: string; close(): Promise<void> }> {
  const dataDir = await mkdtemp(join(tmpdir(), "unity-cli-bridge-diag-doctor-"));
  const sessionId = `session-${"1".repeat(64)}`;
  const listenerId = `listener-${"2".repeat(32)}`;
  const health = await startStrictHealthServer((_request, port) => ({
    version: 3,
    target: "editor",
    instanceId,
    canonicalProjectPath: registryIdentity(projectPath).canonicalProjectPath,
    canonicalizationVersion: 1,
    sessionId,
    listenerId,
    listenerGeneration: 1,
    actualPort: port,
    buildStamp: "test-build",
    mainThread: { observed: true, sequence: 10, ageMs: 0, listenerGeneration: 1, lifecycle: "ready" },
  }));
  const session = await makeRegistrySession(health.endpoint, { sessionId, editorSessionId: sessionId });
  await writeRegistry(dataDir, makeRegistry(registryIdentity(projectPath), [session]));
  return { dataDir, close: health.close };
}

/** 从夹具 registry 构造 selection，绕开默认 selector 但保留真实 instance 记录。 */
function selectObservationFromFixture(dataDir: string, instanceId: string) {
  return async () => ({
    instance: (await readInstances(dataDir)).find((entry) => entry.instanceId === instanceId)!,
    matchedBy: "instanceId" as const,
  });
}

function doctorRuntime(dataDir: string, instanceId: string) {
  return {
    dataDir,
    selectInstance: async () => ({ instance: (await readInstances(dataDir)).find((entry) => entry.instanceId === instanceId)!, matchedBy: "instanceId" as const }),
    bridgeExecute: async (payload: { command: string; requestId: string }) => {
      if (payload.command === "internal.doctor.business") {
        return { ok: true as const, requestId: payload.requestId, data: { accepted: true, projectPath: "Assets", sceneLoaded: true, activeScenePath: "Assets/Main.unity", playMode: false } };
      }
      if (payload.command === "internal.doctor.catalog") {
        return { ok: true as const, requestId: payload.requestId, data: {
          identity: "unity-cli-immutable-domain-catalog",
          toolCount: 1,
          providerCounts: { builtIn: 1, extension: 0, projectInterface: 0, officialAttribute: 0 },
          publicationDiagnostics: { assemblyCount: 1, providerAssemblyCount: 1, elapsedMilliseconds: 1, discoveryPassCount: 1, providerMetadataReadCount: 1, aggregateSuccessLogCount: 1 },
        } };
      }
      if (payload.command === "internal.doctor.compile") {
        return { ok: true as const, requestId: payload.requestId, data: { compiling: false, scanComplete: true, hasCompileErrors: false, compileErrorCount: 0, compileErrorsTruncated: false, compileErrors: [], nextActions: [] } };
      }
      throw new Error(`Unexpected doctor command: ${payload.command}`);
    },
  };
}

test("doctor surfaces a stale compiled extension marker as a warn with actionable nextActions", async () => {
  const fixture = await createBridgeDiagFixture();
  const doctor = await createDoctorFixture(fixture.projectPath, fixture.instanceId);
  try {
    // 升级现场：投影已重铺（fixture 内置），但 Library/ScriptAssemblies 仍是旧编译产物。
    await mkdir(join(fixture.projectPath, "Library", "ScriptAssemblies"), { recursive: true });
    await writeFile(join(fixture.projectPath, "Library", "ScriptAssemblies", "UnityCli.Extensions.VfxGraph.Editor.dll"), Buffer.from("stale assembly without the marker", "utf8"));

    const stale = await handleDoctorCommand(
      { instance: fixture.instanceId, project: fixture.projectPath },
      doctorRuntime(doctor.dataDir, fixture.instanceId),
      "req_doctor_stale",
    );
    assert.ok(stale.ok, JSON.stringify(stale));
    const extensionsCheck = (stale.data as { checks: Array<{ name: string; status: string; message: string; details?: Record<string, unknown> }> })
      .checks.find((check) => check.name === "extensions");
    assert.ok(extensionsCheck, "doctor must always report the extensions check");
    assert.equal(extensionsCheck.status, "warn");
    assert.equal(extensionsCheck.details?.code, "E_EXTENSION_COMPILED_MARKER_MISSING");
    assert.match(String(extensionsCheck.message), /UnityCliExtensionAssembly marker/);
    const nextActions = extensionsCheck.details?.nextActions as string[] | undefined;
    assert.ok(nextActions?.some((action) => /editor\.refresh|Switch focus to Unity Editor/.test(action)));
    assert.ok(nextActions?.some((action) => /extensions remove <id>/.test(action) && /extensions install <id>/.test(action)));

    // Unity 重编译后：编译产物带精确标记，stale marker 分支不再命中（剩余 warn 为未安装的可选扩展）。
    await writeFile(join(fixture.projectPath, "Library", "ScriptAssemblies", "UnityCli.Extensions.VfxGraph.Editor.dll"), Buffer.concat([
      Buffer.from([0, 1, 2, 0]),
      Buffer.from("UnityCliExtensionAssembly", "utf8"),
    ]));
    const healthy = await handleDoctorCommand(
      { instance: fixture.instanceId, project: fixture.projectPath },
      doctorRuntime(doctor.dataDir, fixture.instanceId),
      "req_doctor_healthy",
    );
    assert.ok(healthy.ok, JSON.stringify(healthy));
    const healthyCheck = (healthy.data as { checks: Array<{ name: string; status: string; message: string; details?: Record<string, unknown> }> })
      .checks.find((check) => check.name === "extensions");
    assert.notEqual(healthyCheck?.details?.code, "E_EXTENSION_COMPILED_MARKER_MISSING");
    assert.match(healthyCheck?.message ?? "", /optional extension/);
  } finally {
    await doctor.close();
    await rm(fixture.projectPath, { recursive: true, force: true });
    await rm(fixture.dataDir, { recursive: true, force: true });
    await rm(doctor.dataDir, { recursive: true, force: true });
  }
});

test("init package flow refreshes installed extension projections and surfaces repair results", () => {
  // 接线回归：init 包发布必须触发升级自愈，把失败与重铺结果写进公开返回。
  const source = readFileSync(join(process.cwd(), "src", "cli-commands", "package.ts"), "utf8");
  assert.match(source, /refreshInstalledExtensionProjections\(identity\.canonicalProjectPath, absoluteDeadlineMs\)/);
  assert.match(source, /extensionRepair\.failures/);
  assert.match(source, /extensionRepair\.repaired\.length > 0/);
});

test("doctor wires the compiled marker check with reinstall guidance", () => {
  const source = readFileSync(join(process.cwd(), "src", "doctor.ts"), "utf8");
  assert.match(source, /inspectCompiledExtensionMarkers/);
  assert.match(source, /E_EXTENSION_COMPILED_MARKER_MISSING/);
  assert.match(source, /extensions remove <id>/);
});
