import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { handleDoctorCommand, type DoctorRuntime, type DoctorCheck } from "../src/doctor.js";
import { currentProcessStartedAt } from "./helpers/instance-registry-v2.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

const DEFAULT_PROJECT_PATH = "C:\\tmp\\test-project";
const DEFAULT_CANONICAL_PROJECT_PATH = DEFAULT_PROJECT_PATH.toLowerCase();
const DEFAULT_INSTANCE_ID = `unity-${createHash("sha256").update(Buffer.from(DEFAULT_CANONICAL_PROJECT_PATH, "utf8")).digest("hex")}`;
const DEFAULT_SESSION_ID = `session-${"b".repeat(64)}`;
const DEFAULT_LISTENER_ID = `listener-${"c".repeat(32)}`;
let healthEndpoint = "http://127.0.0.1:47392";
let healthServer: Server | undefined;
let processStartedAt = "";
const healthIdentityByInstance = new Map<string, { canonicalProjectPath: string; editorSessionId: string; listenerId: string; buildStamp: string }>();

interface TestInstance {
  instanceId: string;
  projectPath: string;
  bridgeEndpoint: string;
  lastHeartbeatAt: string;
  state?: "ready" | "transitioning" | "blocked";
  canonicalProjectPath?: string;
  editorSessionId?: string;
  listenerId?: string;
  buildStamp?: string;
}

test.before(async () => {
  processStartedAt = await currentProcessStartedAt();
  healthEndpoint = await startHealthServer();
});

test.after(async () => {
  await new Promise<void>((resolve) => healthServer?.close(() => resolve()) ?? resolve());
});

async function makeDataDir(): Promise<string> {
  return mkdtemp(join(tmpdir(), "unity_cli-doctor-test-"));
}

function makeRuntime(dataDir: string, bridgeExecute?: DoctorRuntime["bridgeExecute"]): DoctorRuntime {
  return { dataDir, bridgeExecute };
}

async function startHealthServer(): Promise<string> {
  healthServer = createServer((request, response) => {
    if (request.url === "/health") {
      const instanceId = String(request.headers["x-unity-cli-expected-instance-id"] ?? "");
      const identity = healthIdentityByInstance.get(instanceId);
      const address = healthServer?.address();
      const port = address && typeof address === "object" ? address.port : 0;
      response.writeHead(identity ? 200 : 409, { "content-type": "application/json" });
      response.end(identity
        ? JSON.stringify({ ok: true, requestId: "", data: { version: 2, instanceId, canonicalProjectPath: identity.canonicalProjectPath, canonicalizationVersion: 1, editorSessionId: identity.editorSessionId, listenerId: identity.listenerId, actualPort: port, buildStamp: identity.buildStamp } })
        : JSON.stringify({ ok: false, requestId: "", error: { code: "E_INSTANCE_IDENTITY_MISMATCH" } }));
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

function makeInstance(overrides: Partial<TestInstance> = {}): TestInstance {
  const now = new Date().toISOString();
  return {
    instanceId: DEFAULT_INSTANCE_ID,
    projectPath: DEFAULT_PROJECT_PATH,
    bridgeEndpoint: healthEndpoint,
    lastHeartbeatAt: now,
    state: "ready",
    editorSessionId: DEFAULT_SESSION_ID,
    listenerId: DEFAULT_LISTENER_ID,
    buildStamp: "doctor-test-build",
    ...overrides
  };
}

/** 测试模拟 C# registry v2 writer；产品 TS 始终只读。 */
async function writeInstances(dataDir: string, instances: TestInstance[]): Promise<void> {
  const directory = join(dataDir, "instances");
  await rm(directory, { recursive: true, force: true });
  await mkdir(directory, { recursive: true });
  healthIdentityByInstance.clear();
  for (const instance of instances) {
    const endpoint = new URL(instance.bridgeEndpoint);
    const canonicalProjectPath = instance.canonicalProjectPath ?? instance.projectPath.toLowerCase();
    const editorSessionId = instance.editorSessionId ?? DEFAULT_SESSION_ID;
    const listenerId = instance.listenerId ?? DEFAULT_LISTENER_ID;
    const buildStamp = instance.buildStamp ?? "doctor-test-build";
    healthIdentityByInstance.set(instance.instanceId, { canonicalProjectPath, editorSessionId, listenerId, buildStamp });
    const session = {
      editorSessionId,
      processId: process.pid,
      processStartedAt,
      lastHeartbeatAt: instance.lastHeartbeatAt,
      claimVerification: "verified",
      state: instance.state ?? "ready",
      lastUnitySnapshot: {
        schemaVersion: 1,
        observedAt: instance.lastHeartbeatAt,
        sourceEditorSessionId: editorSessionId,
        sourceListenerGeneration: 1,
        fields: {
          compile: { known: false }, import: { known: false }, build: { known: false },
          playMode: { known: false }, prefabStage: { known: false }, activeScene: { known: false }, runtime: { known: false },
        },
      },
      listener: { listenerId, generation: 1, startedAt: instance.lastHeartbeatAt, endpoint: { host: "127.0.0.1", preferredPort: Number(endpoint.port), actualPort: Number(endpoint.port), fallback: false, url: instance.bridgeEndpoint } },
      progress: [],
      blockers: [],
      unityVersion: "2022.3.62f1",
      buildStamp,
      toolCatalogFingerprint: "doctor-tools"
    };
    const record = { version: 2, revision: 1, canonicalizationVersion: 1, identity: { instanceId: instance.instanceId, canonicalProjectPath, displayProjectPath: instance.projectPath }, sessions: [session], updatedAt: instance.lastHeartbeatAt };
    await writeFile(join(directory, `${instance.instanceId}.json`), `${JSON.stringify(record)}\n`, "utf8");
  }
}

test("doctor returns all-pass when registry has healthy instance and bridge is reachable", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const bridgeCommands: string[] = [];
  const runtime = makeRuntime(dataDir, async (payload) => {
    bridgeCommands.push(payload.command);
    return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, status: "ok" } };
  });

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_1");
  assert.ok(result.ok);
  const data = result.data as { summary: string; checks: DoctorCheck[] };
  // extension_availability warns when no --project is provided.
  assert.equal(data.summary, "warn");
  assert.equal(data.checks.length, 13);

  const registryCheck = data.checks.find((c) => c.name === "instance_registry");
  assert.equal(registryCheck?.status, "pass");

  const readyCheck = data.checks.find((c) => c.name === "ready_instances");
  assert.equal(readyCheck?.status, "pass");

  const bridgeCheck = data.checks.find((c) => c.name === "bridge_connectivity");
  assert.equal(bridgeCheck?.status, "pass");
  assert.equal(bridgeCommands[0], "project-tool.run");

  const discoveryCheck = data.checks.find((c) => c.name === "unified_tool_discovery");
  assert.equal(discoveryCheck?.status, "pass");

  const extCheck = data.checks.find((c) => c.name === "extension_availability");
  assert.equal(extCheck?.status, "warn");
  assert.ok(extCheck?.message?.includes("project path"));
});

test("doctor returns warn when instance registry is empty", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, []);

  const runtime = makeRuntime(dataDir);

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_2");
  assert.ok(result.ok);
  const data = result.data as { summary: string; checks: DoctorCheck[]; suggestions: string[] };
  assert.equal(data.summary, "warn");

  const registryCheck = data.checks.find((c) => c.name === "instance_registry");
  assert.equal(registryCheck?.status, "warn");
  assert.ok(data.suggestions.some((s) => s.includes("init")));

  const discoveryCheck = data.checks.find((c) => c.name === "unified_tool_discovery");
  assert.equal(discoveryCheck?.status, "pass");
  assert.equal((discoveryCheck?.details as { projectDiscoveryMode?: string }).projectDiscoveryMode, "skipped");
});

test("doctor returns fail when all instances are stale", async () => {
  const dataDir = await makeDataDir();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(dataDir, [makeInstance({ bridgeEndpoint: "http://127.0.0.1:47991", lastHeartbeatAt: staleTime })]);

  const runtime = makeRuntime(dataDir);
  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_3");
  assert.ok(result.ok);
  const data = result.data as { summary: string; checks: DoctorCheck[] };
  assert.equal(data.summary, "fail");

  const readyCheck = data.checks.find((c) => c.name === "ready_instances");
  assert.equal(readyCheck?.status, "warn");
});

test("doctor returns fail when bridge is unreachable", async (payload) => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime = makeRuntime(dataDir, async (payload) => {
    return { ok: false as const, requestId: payload.requestId, error: { code: "BRIDGE_CONNECTION_FAILED", message: "Connection refused", nextActions: [], transient: false, retryAfterMs: 0 } };
  });

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_4");
  assert.ok(result.ok);
  const data = result.data as { summary: string; checks: DoctorCheck[] };
  assert.equal(data.summary, "fail");

  const bridgeCheck = data.checks.find((c) => c.name === "bridge_connectivity");
  assert.equal(bridgeCheck?.status, "fail");
});

test("doctor returns warn when data directory does not exist", async () => {
  const dataDir = join(await mkdtemp(join(tmpdir(), "unity_cli-doctor-")), "nonexistent");
  const runtime = makeRuntime(dataDir);

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_5");
  assert.ok(result.ok);
  const data = result.data as { summary: string; checks: DoctorCheck[] };

  const dirCheck = data.checks.find((c) => c.name === "data_directory");
  assert.equal(dirCheck?.status, "warn");
});

test("doctor returns pass for job store when no jobs have been run", async () => {
  const dataDir = await makeDataDir();
  const runtime = makeRuntime(dataDir);

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_6");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[] };

  const jobStoreCheck = data.checks.find((c) => c.name === "job_store");
  assert.equal(jobStoreCheck?.status, "pass");
  assert.ok(jobStoreCheck?.message.includes("no jobs run"));
});

test("doctor returns pass for job store when jobs exist", async () => {
  const dataDir = await makeDataDir();
  const { JobStore } = await import("../src/job-store.js");
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
  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_7");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[] };

  const jobStoreCheck = data.checks.find((c) => c.name === "job_store");
  assert.equal(jobStoreCheck?.status, "pass");
  assert.ok(jobStoreCheck?.message.includes("1 job"));
});

test("doctor reports obsolete active-target state without parsing or migration", async () => {
  const dataDir = await makeDataDir();
  await writeFile(join(dataDir, "config.json"), JSON.stringify({ activeTarget: "legacy" }), "utf8");
  const runtime = makeRuntime(dataDir);
  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_8");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[] };
  const obsolete = data.checks.find((check) => check.name === "obsolete_local_state");
  assert.equal(obsolete?.status, "fail");
  assert.equal((obsolete?.details as { code: string }).code, "E_OBSOLETE_LOCAL_STATE");
});

test("doctor surfaces bridge custom tool fingerprint summary", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime = makeRuntime(dataDir, async (payload) => {
    return {
      ok: true,
      requestId: payload.requestId,
      data: {
        command: payload.command,
        status: "ok",
        projectTools: { fingerprint: { algorithm: "sha256-handler-code-plus-descriptor-v1", mode: "block", toolCount: 1, blockedCount: 1, warnCount: 0, tools: [] } }
      }
    };
  });

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_fingerprint");
  assert.ok(result.ok);
  const data = result.data as { summary: string; checks: DoctorCheck[]; suggestions: string[] };
  assert.equal(data.summary, "fail");

  const fingerprintCheck = data.checks.find((c) => c.name === "project_tool_fingerprint");
  assert.equal(fingerprintCheck?.status, "fail");
  assert.ok(fingerprintCheck?.message.includes("1 blocked"));
  assert.ok(data.suggestions.some((s) => s.includes("fingerprint")));
});

test("doctor reports runtime custom tools in unified discovery", async () => {
  const dataDir = await makeDataDir();
  const runtime: DoctorRuntime = {
    dataDir,
    projectTools: [{
      name: "project.echo",
      summary: "Project Echo",
      description: "Echo project data.",
      group: "project",
      inputSchema: { type: "object" },
      destructiveHint: false
    }]
  };

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_custom_runtime");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[] };
  const discoveryCheck = data.checks.find((c) => c.name === "unified_tool_discovery");
  assert.equal(discoveryCheck?.status, "pass");
  assert.equal((discoveryCheck?.details as { projectToolCount?: number; projectDiscoveryMode?: string }).projectToolCount, 1);
  assert.equal((discoveryCheck?.details as { projectToolCount?: number; projectDiscoveryMode?: string }).projectDiscoveryMode, "runtime");
});

test("doctor rejects endpoint bypass at selection boundary", async () => {
  const dataDir = await makeDataDir();
  const runtime = makeRuntime(dataDir, async (payload) => ({ ok: true as const, requestId: payload.requestId, data: { command: payload.command, status: "ok" } }));
  const result = await handleDoctorCommand({ "bridge-endpoint": "http://127.0.0.1:9999" }, runtime, "req_doctor_9");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[] };
  const bridgeCheck = data.checks.find((check) => check.name === "bridge_connectivity");
  assert.equal(bridgeCheck?.status, "warn");
  assert.match(bridgeCheck?.message ?? "", /removed/);
});

test("doctor provides actionable suggestions when checks fail", async () => {
  const dataDir = await makeDataDir();
  const staleTime = new Date(Date.now() - 60000).toISOString();
  await writeInstances(dataDir, [makeInstance({ bridgeEndpoint: "http://127.0.0.1:47991", lastHeartbeatAt: staleTime })]);

  const runtime = makeRuntime(dataDir);
  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_10");
  assert.ok(result.ok);
  const data = result.data as { suggestions: string[] };

  assert.ok(data.suggestions.some((s) => s.includes("selected --instance") || s.includes("instances list")));
});

test("doctor includes context_discovery check when bridge is reachable", async () => {
  const dataDir2 = await makeDataDir();
  await writeInstances(dataDir2, [makeInstance()]);

  const runtime2 = makeRuntime(dataDir2, async (payload) => {
    return { ok: true as const, requestId: payload.requestId, data: payload.command === "context.list" ? { contexts: [] } : { command: payload.command, status: "ok" } };
  });

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime2, "req_doctor_ctx_1");
  assert.ok(result.ok);
  const data = result.data as { summary: string; checks: DoctorCheck[] };

  const contextCheck = data.checks.find((c) => c.name === "project_context_discovery");
  assert.ok(contextCheck, "context_discovery check should be present");
  assert.equal(contextCheck!.status, "pass");
});

test("doctor context_discovery reports project custom contexts from runtime", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime: DoctorRuntime = {
    dataDir,
    bridgeExecute: async (payload) => {
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, status: "ok" } };
    },
    projectContexts: [
      {
        name: "sample.project-summary",
        summary: "Read a sample project summary.",
        description: "Read a sample project summary without changing project state.",
        category: "sample",
        source: "project",
        target: "editor",
        availabilityScope: "editorSession",
        playModeSupport: "supported",
        inputSchema: { type: "object", properties: {}, additionalProperties: false },
        outputSchema: { type: "object", properties: {}, additionalProperties: false },
        examples: [{ description: "Read the summary.", input: {} }],
        cliCommand: "context read sample.project-summary"
      }
    ]
  };

  const result = await handleDoctorCommand({ "include-custom": true, instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_ctx_2");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[] };

  const contextCheck = data.checks.find((c) => c.name === "project_context_discovery");
  assert.ok(contextCheck, "context_discovery check should be present");
  assert.equal(contextCheck!.status, "pass");
  const details = contextCheck!.details as { mode: string; projectCustomCount: number; builtInCount: number; categories: string[] };
  assert.equal(details.projectCustomCount, 1);
  assert.ok(details.categories.includes("sample"));
});

test("doctor project_context_fingerprint reports blocked as fail", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime = makeRuntime(dataDir, async (payload) => {
    return {
      ok: true,
      requestId: payload.requestId,
      data: {
        command: payload.command,
        status: "ok",
        customContexts: {
          fingerprint: { algorithm: "sha256-descriptor-v1", mode: "block", contextCount: 2, blockedCount: 1, warnCount: 0 }
        }
      }
    };
  });

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_ctx_fp_1");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[]; suggestions: string[] };

  const fpCheck = data.checks.find((c) => c.name === "project_context_fingerprint");
  assert.ok(fpCheck, "project_context_fingerprint check should be present");
  assert.equal(fpCheck!.status, "fail");
  assert.ok(fpCheck!.message.includes("1 blocked"));
  // Suggestions should point to context describe/read, NOT tools run
  const ctxSuggestions = data.suggestions.filter((s) => s.includes("context"));
  assert.ok(ctxSuggestions.length > 0, "Should have context-related suggestions");
  assert.ok(!data.suggestions.some((s) => s.includes("tools run") && !s.includes("context")), "No tools run suggestion for context issues");
});

test("doctor project_context_fingerprint reports warnCount as warn", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime = makeRuntime(dataDir, async (payload) => {
    return {
      ok: true,
      requestId: payload.requestId,
      data: {
        command: payload.command,
        status: "ok",
        customContexts: {
          fingerprint: { algorithm: "sha256-descriptor-v1", mode: "warn", contextCount: 2, blockedCount: 0, warnCount: 1 }
        }
      }
    };
  });

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_ctx_fp_2");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[] };

  const fpCheck = data.checks.find((c) => c.name === "project_context_fingerprint");
  assert.ok(fpCheck, "project_context_fingerprint check should be present");
  assert.equal(fpCheck!.status, "warn");
});

test("doctor project_context_fingerprint reports pass when all match", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime = makeRuntime(dataDir, async (payload) => {
    return {
      ok: true,
      requestId: payload.requestId,
      data: {
        command: payload.command,
        status: "ok",
        customContexts: {
          fingerprint: { algorithm: "sha256-descriptor-v1", mode: "pass", contextCount: 2, blockedCount: 0, warnCount: 0 }
        }
      }
    };
  });

  const result = await handleDoctorCommand({ instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_ctx_fp_3");
  assert.ok(result.ok);
  const data = result.data as { checks: DoctorCheck[] };

  const fpCheck = data.checks.find((c) => c.name === "project_context_fingerprint");
  assert.ok(fpCheck, "project_context_fingerprint check should be present");
  assert.equal(fpCheck!.status, "pass");
});

test("doctor context_discovery suggestions never suggest tools run", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime: DoctorRuntime = {
    dataDir,
    bridgeExecute: async (payload) => {
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, status: "ok" } };
    },
    projectContexts: [
      {
        name: "conflict.context",
        summary: "Read an unavailable project context.",
        description: "Read an unavailable project context without changing project state.",
        category: "test",
        source: "project",
        target: "editor",
        availabilityScope: "editorSession",
        playModeSupport: "supported",
        inputSchema: { type: "object", properties: {}, additionalProperties: false },
        outputSchema: { type: "object", properties: {}, additionalProperties: false },
        examples: [{ description: "Read the context.", input: {} }],
        cliCommand: "context read conflict.context",
        supportStatus: "unavailable"
      }
    ]
  };

  const result = await handleDoctorCommand({ "include-custom": true, instance: DEFAULT_INSTANCE_ID }, runtime, "req_doctor_ctx_suggest");
  assert.ok(result.ok);
  const data = result.data as { suggestions: string[] };

  // No suggestion should mention "tools run" as a custom context fix
  const toolsRunSuggestions = data.suggestions.filter((s) => s.includes("tools run"));
  assert.equal(toolsRunSuggestions.length, 0, "No 'tools run' suggestions for custom context issues");
});
