import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { handleDoctorCommand, type DoctorRuntime, type DoctorCheck } from "../src/doctor.js";
import { readInstances, writeInstances, type UnityInstance } from "../src/instances.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

let healthEndpoint = "http://127.0.0.1:47392";
let healthServer: Server | undefined;

test.before(async () => {
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
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, status: "healthy" }));
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
    instanceId: "inst_test001",
    projectPath: "/tmp/test-project",
    projectHash: "abc123",
    bridgeEndpoint: healthEndpoint,
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
  // extension_availability warns when no --project is provided.
  assert.equal(data.summary, "warn");
  assert.equal(data.checks.length, 12);

  const registryCheck = data.checks.find((c) => c.name === "instance_registry");
  assert.equal(registryCheck?.status, "pass");

  const healthyCheck = data.checks.find((c) => c.name === "healthy_instances");
  assert.equal(healthyCheck?.status, "pass");

  const bridgeCheck = data.checks.find((c) => c.name === "bridge_connectivity");
  assert.equal(bridgeCheck?.status, "pass");

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

  const result = await handleDoctorCommand({}, runtime, "req_doctor_2");
  assert.equal(result.ok, true);
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
  const result = await handleDoctorCommand({}, runtime, "req_doctor_7");
  assert.equal(result.ok, true);
  const data = result.data as { checks: DoctorCheck[] };

  const jobStoreCheck = data.checks.find((c) => c.name === "job_store");
  assert.equal(jobStoreCheck?.status, "pass");
  assert.ok(jobStoreCheck?.message.includes("1 job"));
});

test("doctor config integrity check warns on missing projectHash", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance({ projectHash: "" })]);

  const runtime = makeRuntime(dataDir);
  const result = await handleDoctorCommand({}, runtime, "req_doctor_8");
  assert.equal(result.ok, true);
  const data = result.data as { checks: DoctorCheck[] };

  const configCheck = data.checks.find((c) => c.name === "config_integrity");
  assert.equal(configCheck?.status, "warn");
  const details = configCheck?.details as { issues: string[] };
  assert.ok(details.issues.some((i) => i.includes("missing projectHash")));
});

test("doctor surfaces bridge custom tool fingerprint summary", async () => {
  const dataDir = await makeDataDir();
  await writeInstances(dataDir, [makeInstance()]);

  const runtime = makeRuntime(dataDir, async (payload) => {
    return {
      ok: true,
      data: {
        command: payload.command,
        status: "ok",
        projectTools: { fingerprint: { algorithm: "sha256-handler-code-plus-descriptor-v1", mode: "block", toolCount: 1, blockedCount: 1, warnCount: 0, tools: [] } }
      }
    };
  });

  const result = await handleDoctorCommand({}, runtime, "req_doctor_fingerprint");
  assert.equal(result.ok, true);
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
      title: "Project Echo",
      description: "Echo project data.",
      group: "project",
      inputSchema: { type: "object" },
      readOnlyHint: true,
      destructiveHint: false
    }]
  };

  const result = await handleDoctorCommand({}, runtime, "req_doctor_custom_runtime");
  assert.equal(result.ok, true);
  const data = result.data as { checks: DoctorCheck[] };
  const discoveryCheck = data.checks.find((c) => c.name === "unified_tool_discovery");
  assert.equal(discoveryCheck?.status, "pass");
  assert.equal((discoveryCheck?.details as { projectToolCount?: number; projectDiscoveryMode?: string }).projectToolCount, 1);
  assert.equal((discoveryCheck?.details as { projectToolCount?: number; projectDiscoveryMode?: string }).projectDiscoveryMode, "runtime");
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

  assert.ok(data.suggestions.some((s) => s.includes("prune") || s.includes("Reopen")));
});

test("doctor includes context_discovery check when bridge is reachable", async () => {
  const dataDir2 = await makeDataDir();
  await writeInstances(dataDir2, [makeInstance()]);

  const runtime2 = makeRuntime(dataDir2, async (payload) => {
    return { ok: true, data: { command: payload.command, status: "ok" } };
  });

  const result = await handleDoctorCommand({}, runtime2, "req_doctor_ctx_1");
  assert.equal(result.ok, true);
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
      return { ok: true, data: { command: payload.command, status: "ok" } };
    },
    projectContexts: [
      {
        name: "sample.project-summary",
        title: "Project Summary",
        description: "Sample project summary context",
        category: "sample",
        kind: "context",
        source: "project",
        readOnlyHint: true,
        requiresUnity: true,
        inputSchema: { type: "object" }
      }
    ]
  };

  const result = await handleDoctorCommand({ "include-custom": true }, runtime, "req_doctor_ctx_2");
  assert.equal(result.ok, true);
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
      data: {
        command: payload.command,
        status: "ok",
        customContexts: {
          fingerprint: { algorithm: "sha256-descriptor-v1", mode: "block", contextCount: 2, blockedCount: 1, warnCount: 0 }
        }
      }
    };
  });

  const result = await handleDoctorCommand({}, runtime, "req_doctor_ctx_fp_1");
  assert.equal(result.ok, true);
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
      data: {
        command: payload.command,
        status: "ok",
        customContexts: {
          fingerprint: { algorithm: "sha256-descriptor-v1", mode: "warn", contextCount: 2, blockedCount: 0, warnCount: 1 }
        }
      }
    };
  });

  const result = await handleDoctorCommand({}, runtime, "req_doctor_ctx_fp_2");
  assert.equal(result.ok, true);
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
      data: {
        command: payload.command,
        status: "ok",
        customContexts: {
          fingerprint: { algorithm: "sha256-descriptor-v1", mode: "pass", contextCount: 2, blockedCount: 0, warnCount: 0 }
        }
      }
    };
  });

  const result = await handleDoctorCommand({}, runtime, "req_doctor_ctx_fp_3");
  assert.equal(result.ok, true);
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
      return { ok: true, data: { command: payload.command, status: "ok" } };
    },
    projectContexts: [
      {
        name: "conflict.context",
        title: "Conflict",
        description: "Duplicate context",
        category: "test",
        kind: "context",
        source: "project",
        readOnlyHint: true,
        requiresUnity: true,
        inputSchema: { type: "object" },
        supportStatus: "unavailable"
      }
    ]
  };

  const result = await handleDoctorCommand({ "include-custom": true }, runtime, "req_doctor_ctx_suggest");
  assert.equal(result.ok, true);
  const data = result.data as { suggestions: string[] };

  // No suggestion should mention "tools run" as a custom context fix
  const toolsRunSuggestions = data.suggestions.filter((s) => s.includes("tools run"));
  assert.equal(toolsRunSuggestions.length, 0, "No 'tools run' suggestions for custom context issues");
});
