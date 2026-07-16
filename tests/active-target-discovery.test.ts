import assert from "node:assert/strict";
import { createServer, type Server } from "node:http";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../src/cli.js";
import type { BridgeRequest } from "../src/bridge/index.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { writeInstances, type UnityInstance } from "../src/instances.js";
import { ExtensionRegistry } from "../src/extensions/index.js";
import { installExtensionBundle } from "../src/extensions/installer.js";
import { probuilderPingTool } from "./helpers/live-tool-fixtures.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

let healthEndpoint = "http://127.0.0.1:47392";
let healthServer: Server | undefined;

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-active-target-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

function makeInstance(overrides: Partial<UnityInstance> = {}): UnityInstance {
  const now = new Date().toISOString();
  return {
    instanceId: overrides.instanceId ?? "unity-test-001",
    projectPath: overrides.projectPath ?? "D:/workspace/alps-unity-cli/Dev/UnityProject",
    projectHash: overrides.projectHash ?? "da1e42fad0daed9ec8a08a143749b6f2b891fa77317a5e84f632c8903c117a78",
    bridgeEndpoint: overrides.bridgeEndpoint ?? healthEndpoint,
    status: overrides.status ?? "healthy",
    registeredAt: overrides.registeredAt ?? now,
    lastHeartbeatAt: overrides.lastHeartbeatAt ?? now,
    ...overrides
  };
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

test.before(async () => {
  healthEndpoint = await startHealthServer();
});

test.after(async () => {
  await new Promise<void>((resolve) => healthServer?.close(() => resolve()) ?? resolve());
});

test("active target enables project tool discovery for tools run", async () => {
  const runtime = await makeRuntime();
  await writeInstances(runtime.dataDir, [makeInstance()]);

  const setResult = await execute(["target", "set", "unity-test-001"], runtime);
  assert.ok(setResult.ok);

  const payloads: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "sample.echo", "--input", '{"message":"hello"}'], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      payloads.push(payload);
      if (payload.command === "runtime-tool.list") {
        return { ok: true as const, requestId: payload.requestId, data: { tools: [] } };
      }
      if (payload.command === "project-tool.list") {
        return {
          ok: true,
          requestId: payload.requestId,
          data: {
            tools: [{
              name: "sample.echo",
              summary: "Echo a message.",
              description: "Echo a message.",
              group: "sample",
              source: "project",
              inputSchema: { type: "object", properties: { message: { type: "string" } }, required: ["message"] },
              outputSchema: { type: "object", properties: { message: { type: "string" } } },
              destructiveHint: false,
              target: "editor",
              playModeSupport: "editor-only",
              supportStatus: "live-bridge"
            }]
          }
        };
      }
      if (payload.command === "project-tool.run") {
        return { ok: true as const, requestId: payload.requestId, data: { result: { message: "hello" } } };
      }
      return { ok: true as const, requestId: payload.requestId, data: {} };
    }
  });

  assert.ok(result.ok);
  assert.deepEqual(payloads.map((payload) => payload.command), ["runtime-tool.list", "project-tool.list", "project-tool.run"]);
});

test("active target infers project for extension Context physical availability", async () => {
  const runtime = await makeRuntime();
  const projectPath = await mkdtemp(join(tmpdir(), "unity_cli-extension-target-"));
  await mkdir(join(projectPath, "Packages"), { recursive: true });
  await writeFile(join(projectPath, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }), "utf8");
  await writeFile(join(projectPath, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: {} }), "utf8");
  await writeInstances(runtime.dataDir, [makeInstance({ projectPath })]);
  const target = await execute(["target", "set", "unity-test-001"], runtime);
  assert.ok(target.ok);

  const result = await execute(["context", "read", "probuilder.scene"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => ({
      ok: true,
      requestId: payload.requestId,
      data: payload.command === "context.list" ? {
        contexts: [{
          name: "probuilder.scene",
          summary: "Read ProBuilder scene state.",
          description: "Read ProBuilder scene state without modifying it.",
          category: "probuilder",
          source: "extension",
          target: "editor",
          availabilityScope: "editorSession",
          playModeSupport: "supported",
          inputSchema: { type: "object", properties: {}, additionalProperties: false },
          outputSchema: { type: "object", properties: { objects: { type: "array", items: { type: "object" } } }, additionalProperties: false },
          examples: [{ description: "Read the current ProBuilder scene state.", input: {} }],
          requiresPackages: ["com.unity.probuilder"]
        }]
      } : {}
    })
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_EXTENSION_DEPENDENCY_MISSING");
});

test("active target gives extension tools one physical state for list, describe, and run", async () => {
  const runtime = await makeRuntime();
  const projectPath = await mkdtemp(join(tmpdir(), "unity_cli-extension-tool-target-"));
  await mkdir(join(projectPath, "Packages"), { recursive: true });
  await writeFile(join(projectPath, "Packages", "manifest.json"), JSON.stringify({ dependencies: { "com.unity.probuilder": "5.0.0" } }), "utf8");
  await writeFile(join(projectPath, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: { "com.unity.probuilder": { version: "5.0.0" } } }), "utf8");
  const registry = new ExtensionRegistry();
  await installExtensionBundle(projectPath, registry.getExtension("probuilder")!.manifest);
  await writeInstances(runtime.dataDir, [makeInstance({ projectPath })]);
  assert.ok((await execute(["target", "set", "unity-test-001"], runtime)).ok);

  const requests: BridgeRequest[] = [];
  const toolRuntime = {
    ...runtime,
    projectTools: [probuilderPingTool()],
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: {} };
    }
  };
  const listed = await execute(["tools", "group", "list", "probuilder"], toolRuntime);
  assert.ok(listed.ok);
  const listedTool = (listed.data as { tools: Array<Record<string, unknown>> }).tools.find((tool) => tool.name === "probuilder.ping");
  assert.equal(listedTool?.extensionAvailability, "available");

  const described = await execute(["tools", "describe", "probuilder.ping"], toolRuntime);
  assert.ok(described.ok);
  assert.equal((described.data as Record<string, unknown>).extensionAvailability, "available");

  const run = await execute(["tools", "run", "probuilder.ping"], toolRuntime);
  assert.ok(run.ok);
  assert.equal(requests.at(-1)?.command, "project-tool.run");
});

test("active target enables built-in live context discovery for context read", async () => {
  const runtime = await makeRuntime();
  await writeInstances(runtime.dataDir, [makeInstance()]);

  const setResult = await execute(["target", "set", "unity-test-001"], runtime);
  assert.ok(setResult.ok);

  const payloads: BridgeRequest[] = [];
  const result = await execute(["context", "read", "project.info"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      payloads.push(payload);
      if (payload.command === "context.list") {
        return {
          ok: true,
          requestId: payload.requestId,
          data: {
            contexts: [{
              name: "project.info",
              summary: "Project metadata.",
              description: "Project metadata.",
              category: "project",
              source: "built-in",
              target: "editor",
              availabilityScope: "editorSession",
              playModeSupport: "supported",
              inputSchema: { type: "object", properties: {}, additionalProperties: false },
              outputSchema: { type: "object", properties: { projectName: { type: "string" } } },
              examples: [{ description: "Read project metadata.", input: {} }],
              cliCommand: "context read project.info",
              supportStatus: "live-bridge"
            }]
          }
        };
      }
      if (payload.command === "context.read") {
        return { ok: true as const, requestId: payload.requestId, data: { projectName: "UnityProject" } };
      }
      return { ok: true as const, requestId: payload.requestId, data: {} };
    }
  });

  assert.ok(result.ok);
  assert.deepEqual(payloads.map((payload) => payload.command), ["context.list", "context.read"]);
  assert.equal((result.data as { projectName: string }).projectName, "UnityProject");
});
