import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { getRuntimePaths } from "./paths.js";
import { readJsonFile, writeJsonFile } from "./json-file.js";
import { createInstanceId } from "./instance-id.js";

const DEFAULT_AGENT_ENDPOINT = "http://127.0.0.1:47391";

export class AgentUnavailableError extends Error {
  constructor(message, details = {}) {
    super(message);
    this.name = "AgentUnavailableError";
    this.details = details;
  }
}

export async function ensureLocalAgent(options = {}) {
  const runtimePaths = getRuntimePaths();
  await fs.promises.mkdir(path.dirname(runtimePaths.agentLog), { recursive: true });
  const endpoint = options.endpoint ?? DEFAULT_AGENT_ENDPOINT;

  if (process.env.ALPS_UNITY_DISABLE_AGENT_START === "1") {
    throw new AgentUnavailableError("Local Agent auto-start is disabled and no healthy agent was confirmed.", { endpoint });
  }

  const existing = await readJsonFile(runtimePaths.agentStateFile);
  if (existing?.running && (await isAgentHealthy(existing.endpoint))) {
    return { ...existing, startedByCli: false };
  }

  const agentProcess = spawn(process.execPath, [agentServerPath()], {
    detached: true,
    stdio: "ignore",
    env: { ...process.env, ALPS_UNITY_AGENT_ENDPOINT: endpoint }
  });
  agentProcess.unref();

  const agent = {
    running: true,
    endpoint,
    pid: agentProcess.pid,
    logPath: runtimePaths.agentLog,
    startedByCli: true
  };
  await writeJsonFile(runtimePaths.agentStateFile, agent);

  if (!(await waitForAgentHealth(endpoint))) {
    throw new AgentUnavailableError("Local Agent did not become healthy after being started.", { endpoint, pid: agentProcess.pid });
  }

  return agent;
}

export async function isAgentHealthy(endpoint) {
  try {
    const response = await fetchJson(new URL("/health", endpoint), { method: "GET" });
    return response.ok === true;
  } catch {
    return false;
  }
}

export async function waitForAgentHealth(endpoint, attempts = 20, delayMs = 50) {
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    if (await isAgentHealthy(endpoint)) {
      return true;
    }
    await new Promise((resolve) => setTimeout(resolve, delayMs));
  }
  return false;
}

export async function postBridgeHeartbeat(endpoint, heartbeat) {
  const response = await fetchJson(new URL("/bridge/heartbeat", endpoint), {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(heartbeat)
  });
  return response.instance;
}

export async function listRegisteredInstances() {
  const runtimePaths = getRuntimePaths();
  const instances = (await readJsonFile(runtimePaths.instancesFile)) ?? [];
  const now = Date.now();
  return instances.filter((instance) => {
    const heartbeat = Date.parse(instance.lastHeartbeatAt);
    return Number.isFinite(heartbeat) && now - heartbeat < 60_000;
  });
}

export async function registerInstance(instance) {
  const runtimePaths = getRuntimePaths();
  const instances = await listRegisteredInstances();
  const nextInstances = [instance, ...instances.filter((item) => item.id !== instance.id)];
  await writeJsonFile(runtimePaths.instancesFile, nextInstances);
}

export function instanceFromHeartbeat(heartbeat) {
  const projectPath = heartbeat.projectPath;
  return {
    id: heartbeat.id ?? createInstanceId(projectPath),
    projectPath,
    projectName: heartbeat.projectName ?? projectPath.split(/[\\/]/).filter(Boolean).at(-1) ?? "UnityProject",
    unityVersion: heartbeat.unityVersion ?? "unknown",
    editorPid: heartbeat.editorPid,
    bridge: {
      version: heartbeat.bridgeVersion ?? "1.0.0",
      supported: heartbeat.bridgeSupported !== false,
      logPath: heartbeat.bridgeLog ?? `${projectPath}/Logs/AlpsUnityBridge.log`
    },
    busy: {
      isBusy: Boolean(heartbeat.busy),
      compiling: Boolean(heartbeat.compiling),
      importing: Boolean(heartbeat.importing),
      building: Boolean(heartbeat.building),
      playModeChanging: Boolean(heartbeat.playModeChanging),
      domainReloading: Boolean(heartbeat.domainReloading),
      prefabStage: Boolean(heartbeat.prefabStage)
    },
    connection: {
      transport: heartbeat.transport ?? "http",
      endpoint: heartbeat.bridgeEndpoint ?? heartbeat.endpoint ?? "http://127.0.0.1:47392"
    },
    lastHeartbeatAt: heartbeat.lastHeartbeatAt ?? new Date().toISOString()
  };
}

export async function handleBridgeHeartbeat(heartbeat) {
  if (!heartbeat?.projectPath) {
    const error = new Error("Bridge heartbeat requires projectPath.");
    error.statusCode = 400;
    throw error;
  }
  const instance = instanceFromHeartbeat(heartbeat);
  await registerInstance(instance);
  return instance;
}

export async function getCurrentInstanceId() {
  try {
    const runtimePaths = getRuntimePaths();
    const raw = await fs.promises.readFile(runtimePaths.currentInstanceFile, "utf8");
    return raw.trim() || undefined;
  } catch (error) {
    if (error?.code === "ENOENT") {
      return undefined;
    }
    throw error;
  }
}

export async function setCurrentInstanceId(instanceId) {
  const runtimePaths = getRuntimePaths();
  await fs.promises.mkdir(path.dirname(runtimePaths.currentInstanceFile), { recursive: true });
  await fs.promises.writeFile(runtimePaths.currentInstanceFile, `${instanceId}\n`, "utf8");
}

export function createAgentServer() {
  return http.createServer(async (request, response) => {
    try {
      if (request.method === "GET" && request.url === "/health") {
        sendJson(response, 200, { ok: true, service: "alps-unity-local-agent" });
        return;
      }

      if (request.method === "POST" && request.url === "/bridge/heartbeat") {
        const body = await readRequestJson(request);
        const instance = await handleBridgeHeartbeat(body);
        sendJson(response, 200, { ok: true, instance });
        return;
      }

      sendJson(response, 404, { ok: false, error: "not_found" });
    } catch (error) {
      sendJson(response, error.statusCode ?? 500, { ok: false, error: error.message });
    }
  });
}

export async function startAgentServer(endpoint = DEFAULT_AGENT_ENDPOINT) {
  const url = new URL(endpoint);
  const server = createAgentServer();
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(Number(url.port), url.hostname, () => {
      server.off("error", reject);
      resolve();
    });
  });
  return server;
}

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  const payload = await response.json();
  if (!response.ok || payload.ok === false) {
    throw new AgentUnavailableError("Local Agent request failed.", { url: url.toString(), status: response.status, payload });
  }
  return payload;
}

function sendJson(response, statusCode, payload) {
  response.writeHead(statusCode, { "content-type": "application/json", "connection": "close" });
  response.end(`${JSON.stringify(payload)}\n`);
}

async function readRequestJson(request) {
  const chunks = [];
  for await (const chunk of request) {
    chunks.push(chunk);
  }
  const raw = Buffer.concat(chunks).toString("utf8");
  return raw ? JSON.parse(raw) : {};
}

function agentServerPath() {
  return path.join(path.dirname(fileURLToPath(import.meta.url)), "agent-server.js");
}
