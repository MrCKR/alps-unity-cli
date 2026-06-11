import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { liveQueryCommand } from "../src/commands.js";
import { registerInstance, startAgentServer } from "../src/agent.js";
import { createInstanceId } from "../src/instances.js";
import { writeJsonFile } from "../src/json-file.js";

async function withRuntime(run) {
  const home = await fs.promises.mkdtemp(path.join(os.tmpdir(), "alps-unity-query-"));
  const project = await createUnityProject(home, "QueryGame");
  const endpoint = `http://127.0.0.1:${await getFreePort()}`;
  const bridgeEndpoint = `http://127.0.0.1:${await getFreePort()}`;
  const previousHome = process.env.ALPS_UNITY_HOME;
  process.env.ALPS_UNITY_HOME = path.join(home, "runtime");
  try {
    await writeJsonFile(path.join(process.env.ALPS_UNITY_HOME, "config.json"), { agentEndpoint: endpoint });
    await run({ home, project, endpoint, bridgeEndpoint });
  } finally {
    if (previousHome === undefined) {
      delete process.env.ALPS_UNITY_HOME;
    } else {
      process.env.ALPS_UNITY_HOME = previousHome;
    }
    await fs.promises.rm(home, { recursive: true, force: true });
  }
}

async function createUnityProject(root, name) {
  const project = path.join(root, name);
  await fs.promises.mkdir(path.join(project, "Assets"), { recursive: true });
  await fs.promises.mkdir(path.join(project, "Packages"), { recursive: true });
  await fs.promises.mkdir(path.join(project, "ProjectSettings"), { recursive: true });
  await fs.promises.writeFile(path.join(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.1f1\n", "utf8");
  await fs.promises.writeFile(path.join(project, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }), "utf8");
  return project;
}

function makeInstance(project, bridgeEndpoint) {
  return {
    id: createInstanceId(project),
    projectPath: project,
    projectName: path.basename(project),
    unityVersion: "2022.3.1f1",
    editorPid: 1234,
    bridge: { version: "1.0.0", supported: true, logPath: path.join(project, "Logs", "AlpsUnityBridge.log") },
    busy: { isBusy: false },
    connection: { transport: "http", endpoint: bridgeEndpoint },
    lastHeartbeatAt: new Date().toISOString()
  };
}

async function getFreePort() {
  const server = http.createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  await new Promise((resolve) => server.close(resolve));
  return address.port;
}

async function withAgentServer(endpoint, run) {
  const server = await startAgentServer(endpoint);
  await writeJsonFile(path.join(process.env.ALPS_UNITY_HOME, "agent.json"), {
    running: true,
    endpoint,
    pid: process.pid,
    logPath: path.join(process.env.ALPS_UNITY_HOME, "logs", "agent.log"),
    startedByCli: false
  });
  try {
    await run();
  } finally {
    server.closeAllConnections?.();
    await new Promise((resolve) => server.close(resolve));
  }
}

async function withRegisteredBridge({ project, endpoint, bridgeEndpoint }, handler, run) {
  await withAgentServer(endpoint, async () => {
    await registerInstance(makeInstance(project, bridgeEndpoint));
    await withFakeBridge(bridgeEndpoint, handler, run);
  });
}

async function withFakeBridge(endpoint, handler, run) {
  const url = new URL(endpoint);
  const requests = [];
  const server = http.createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) {
      chunks.push(chunk);
    }
    const body = Buffer.concat(chunks).toString("utf8");
    const payload = body ? JSON.parse(body) : {};
    requests.push(payload);
    const result = handler(payload, requests);
    response.writeHead(200, { "content-type": "application/json" });
    response.end(`${JSON.stringify(result)}\n`);
  });
  await new Promise((resolve) => server.listen(Number(url.port), url.hostname, resolve));
  try {
    await run(requests);
  } finally {
    server.closeAllConnections?.();
    await new Promise((resolve) => server.close(resolve));
  }
}

function okResult(payload, data, nextActions = []) {
  return { ok: true, requestId: payload.requestId, data, nextActions };
}

function pageInfo(payload, overrides = {}) {
  return {
    pageSize: payload.args.pageSize,
    cursor: payload.args.cursor ?? "0",
    nextCursor: null,
    total: 0,
    hasMore: false,
    ...overrides
  };
}

test("live query forwards command and returns stable envelope", async () => {
  await withRuntime(async (runtime) => {
    await withRegisteredBridge(runtime, (payload) => okResult(payload, {
      items: [{ path: "Main Camera", componentTypes: ["UnityEngine.Transform", "UnityEngine.Camera"] }],
      pageInfo: pageInfo(payload, { total: 1 })
    }), async () => {
      const result = await liveQueryCommand("req-query", "scene.hierarchy", { pageSize: 50 }, { project: runtime.project });
      assert.equal(result.ok, true);
      assert.equal(result.mode, "live");
      assert.equal(result.data.command, "scene.hierarchy");
      assert.equal(result.data.items[0].path, "Main Camera");
      assert.equal(result.data.pageInfo.pageSize, 50);
      assert.equal(result.data.project.name, "QueryGame");
    });
  });
});

test("live query preserves pagination cursors and empty result pageInfo", async () => {
  await withRuntime(async (runtime) => {
    await withRegisteredBridge(runtime, (payload) => okResult(payload, {
      items: [],
      pageInfo: pageInfo(payload, { cursor: "20", total: 20 })
    }), async (requests) => {
      const result = await liveQueryCommand("req-empty-page", "asset.search", { query: "Missing", pageSize: 10, cursor: "20" }, { project: runtime.project });
      assert.equal(result.ok, true);
      assert.equal(result.data.items.length, 0);
      assert.deepEqual(result.data.pageInfo, { pageSize: 10, cursor: "20", nextCursor: null, total: 20, hasMore: false });
      assert.deepEqual(requests[0].args, { query: "Missing", pageSize: 10, cursor: "20" });
    });
  });
});

test("live query preserves hasMore pagination metadata", async () => {
  await withRuntime(async (runtime) => {
    await withRegisteredBridge(runtime, (payload) => okResult(payload, {
      items: [{ path: "Root/A" }, { path: "Root/B" }],
      pageInfo: pageInfo(payload, { cursor: "0", nextCursor: "2", total: 5, hasMore: true })
    }), async () => {
      const result = await liveQueryCommand("req-next-page", "scene.hierarchy", { pageSize: 2, cursor: "0" }, { project: runtime.project });
      assert.equal(result.ok, true);
      assert.equal(result.data.pageInfo.nextCursor, "2");
      assert.equal(result.data.pageInfo.hasMore, true);
      assert.equal(result.data.pageInfo.total, 5);
      assert.equal(result.data.items.length, 2);
    });
  });
});

test("live query maps bridge target ambiguity into CLI failure envelope", async () => {
  await withRuntime(async (runtime) => {
    await withRegisteredBridge(runtime, (payload) => ({
      ok: false,
      requestId: payload.requestId,
      error: { code: "E_TARGET_AMBIGUOUS", message: "Multiple GameObjects matched.", details: { matchCount: 2 } },
      nextActions: [{ action: "narrowTarget", description: "Pass --path or --id." }]
    }), async () => {
      const result = await liveQueryCommand("req-ambiguous", "go.inspect", { name: "Player" }, { project: runtime.project });
      assert.equal(result.ok, false);
      assert.equal(result.error.code, "E_TARGET_AMBIGUOUS");
      assert.equal(result.error.details.matchCount, 2);
      assert.equal(result.nextActions[0].action, "narrowTarget");
    });
  });
});

test("live query maps bridge path errors with diagnostics", async () => {
  await withRuntime(async (runtime) => {
    await withRegisteredBridge(runtime, (payload) => ({
      ok: false,
      requestId: payload.requestId,
      error: {
        code: "E_PATH_INVALID",
        message: "Asset path must be relative to Assets/ or Packages/.",
        details: { path: "C:\\Game\\Player.prefab" }
      },
      nextActions: [{ action: "useUnityAssetPath", description: "Use a Unity asset path such as Assets/Scenes/Main.unity." }]
    }), async () => {
      const result = await liveQueryCommand("req-path-error", "asset.info", { path: "C:\\Game\\Player.prefab" }, { project: runtime.project });
      assert.equal(result.ok, false);
      assert.equal(result.error.code, "E_PATH_INVALID");
      assert.equal(result.error.details.path, "C:\\Game\\Player.prefab");
      assert.equal(result.nextActions[0].action, "useUnityAssetPath");
    });
  });
});

test("live query forwards console filter count and clear flags", async () => {
  await withRuntime(async (runtime) => {
    await withRegisteredBridge(runtime, (payload) => okResult(payload, {
      cleared: payload.args.clear,
      items: [{ type: "error", message: "NullReferenceException", file: "Assets/Scripts/Player.cs", line: 42, mode: 1 }],
      pageInfo: pageInfo(payload, { pageSize: payload.args.count, total: 1 })
    }), async (requests) => {
      const result = await liveQueryCommand("req-console", "editor.console", { type: "error", count: 5, filter: "NullReference", clear: true }, { project: runtime.project });
      assert.equal(result.ok, true);
      assert.equal(result.data.cleared, true);
      assert.equal(result.data.items[0].type, "error");
      assert.equal(result.data.pageInfo.pageSize, 5);
      assert.deepEqual(requests[0].args, { type: "error", count: 5, filter: "NullReference", clear: true });
    });
  });
});

test("live query forwards Windows-style Unity asset paths for bridge normalization", async () => {
  await withRuntime(async (runtime) => {
    await withRegisteredBridge(runtime, (payload) => okResult(payload, {
      asset: {
        guid: "asset-guid",
        path: payload.args.path.replaceAll("\\\\", "/"),
        name: "Player",
        type: "UnityEngine.GameObject"
      }
    }), async (requests) => {
      const result = await liveQueryCommand("req-windows-path", "asset.info", { path: "Assets\\\\Prefabs\\\\Player.prefab" }, { project: runtime.project });
      assert.equal(result.ok, true);
      assert.equal(requests[0].args.path, "Assets\\\\Prefabs\\\\Player.prefab");
      assert.equal(result.data.asset.path, "Assets/Prefabs/Player.prefab");
    });
  });
});
