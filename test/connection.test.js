import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { registerInstance, setCurrentInstanceId, startAgentServer, postBridgeHeartbeat } from "../src/agent.js";
import { spawn } from "node:child_process";
import { statusCommand, instanceCurrentCommand } from "../src/commands.js";
import { createInstanceId } from "../src/instances.js";
import { writeJsonFile } from "../src/json-file.js";

async function withRuntime(run) {
  const home = await fs.promises.mkdtemp(path.join(os.tmpdir(), "alps-unity-home-"));
  const project = await createUnityProject(home, "SampleGame");
  const endpoint = `http://127.0.0.1:${await getFreePort()}`;
  const previousHome = process.env.ALPS_UNITY_HOME;
  const previousDisable = process.env.ALPS_UNITY_DISABLE_AGENT_START;
  process.env.ALPS_UNITY_HOME = path.join(home, "runtime");
  try {
    await writeJsonFile(path.join(process.env.ALPS_UNITY_HOME, "config.json"), { agentEndpoint: endpoint });
    await run({ home, project, endpoint });
  } finally {
    if (previousHome === undefined) {
      delete process.env.ALPS_UNITY_HOME;
    } else {
      process.env.ALPS_UNITY_HOME = previousHome;
    }
    if (previousDisable === undefined) {
      delete process.env.ALPS_UNITY_DISABLE_AGENT_START;
    } else {
      process.env.ALPS_UNITY_DISABLE_AGENT_START = previousDisable;
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
  await fs.promises.writeFile(path.join(project, "Packages", "manifest.json"), JSON.stringify({ dependencies: { "com.unity.test-framework": "1.1.33" } }), "utf8");
  return project;
}

function makeInstance(project, overrides = {}) {
  return {
    id: createInstanceId(project),
    projectPath: project,
    projectName: path.basename(project),
    unityVersion: "2022.3.1f1",
    editorPid: 1234,
    bridge: { version: "1.0.0", supported: true, logPath: path.join(project, "Logs", "AlpsUnityBridge.log") },
    busy: { isBusy: false },
    connection: { transport: "http", endpoint: "http://127.0.0.1:47392" },
    lastHeartbeatAt: new Date().toISOString(),
    ...overrides
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

test("status reports E_AGENT_UNAVAILABLE when local agent cannot be started or reached", async () => {
  await withRuntime(async ({ project }) => {
    process.env.ALPS_UNITY_DISABLE_AGENT_START = "1";
    const result = await statusCommand("req-agent-unavailable", { project });
    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.code, "E_AGENT_UNAVAILABLE");
      assert.ok(result.nextActions.some((action) => action.action === "restartAgent"));
    }
  });
});

test("status reports E_INSTANCE_NOT_FOUND when no bridge registered", async () => {
  await withRuntime(async ({ project, endpoint }) => {
    await withAgentServer(endpoint, async () => {
      const result = await statusCommand("req-no-instance", { project });
      assert.equal(result.ok, false);
      if (!result.ok) {
        assert.equal(result.error.code, "E_INSTANCE_NOT_FOUND");
        assert.ok(result.nextActions.some((action) => action.action === "openUnity"));
      }
    });
  });
});

test("status returns project, unity, agent, bridge and busy state for one instance", async () => {
  await withRuntime(async ({ project, endpoint }) => {
    await withAgentServer(endpoint, async () => {
      await registerInstance(makeInstance(project, { busy: { isBusy: true, compiling: true } }));
      const result = await statusCommand("req-one", { project });
      assert.equal(result.ok, true);
      if (result.ok) {
        assert.equal(result.mode, "live");
        assert.equal(result.data.project.name, "SampleGame");
        assert.equal(result.data.instance.busy.compiling, true);
        assert.ok(result.data.agent.running);
        assert.ok(result.nextActions.some((action) => action.action === "waitForUnity"));
      }
    });
  });
});

test("local agent registers bridge heartbeat over HTTP contract", async () => {
  await withRuntime(async ({ project, endpoint }) => {
    await withAgentServer(endpoint, async () => {
      const instance = await postBridgeHeartbeat(endpoint, {
        projectPath: project,
        unityVersion: "2022.3.1f1",
        bridgeVersion: "1.0.0",
        bridgeLog: path.join(project, "Logs", "AlpsUnityBridge.log"),
        busy: true,
        importing: true,
        transport: "http",
        endpoint: "http://127.0.0.1:47392"
      });
      assert.equal(instance.projectPath, project);
      assert.equal(instance.busy.importing, true);

      const result = await statusCommand("req-heartbeat", { project });
      assert.equal(result.ok, true);
      if (result.ok) {
        assert.equal(result.data.instance.id, createInstanceId(project));
        assert.equal(result.data.instance.bridge.version, "1.0.0");
      }
    });
  });
});

test("CLI bridge register posts heartbeat through Local Agent contract", async () => {
  await withRuntime(async ({ project, endpoint }) => {
    await withAgentServer(endpoint, async () => {
      const result = await runCli(["bridge", "register", "--project", project, "--unityVersion", "2022.3.1f1", "--busy", "--importing"]);
      assert.equal(result.status, 0, result.stderr || result.stdout);
      const payload = JSON.parse(result.stdout);
      assert.equal(payload.ok, true);
      assert.equal(payload.data.instance.busy.importing, true);

      const status = await statusCommand("req-cli-register", { project });
      assert.equal(status.ok, true);
      if (status.ok) {
        assert.equal(status.data.instance.projectPath, project);
      }
    });
  });
});

async function runCli(args) {
  return await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ["src/cli.js", ...args], {
      cwd: path.join(import.meta.dirname, ".."),
      env: { ...process.env, ALPS_UNITY_HOME: process.env.ALPS_UNITY_HOME },
      stdio: ["ignore", "pipe", "pipe"]
    });
    let stdout = "";
    let stderr = "";
    const timer = setTimeout(() => {
      child.kill("SIGKILL");
      reject(new Error(`CLI timed out. stdout=${stdout} stderr=${stderr}`));
    }, 5_000);
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
    });
    child.on("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on("close", (status) => {
      clearTimeout(timer);
      resolve({ status, stdout, stderr });
    });
  });
}

test("status reports E_INSTANCE_AMBIGUOUS for multiple matching instances", async () => {
  await withRuntime(async ({ project, endpoint }) => {
    await withAgentServer(endpoint, async () => {
      await registerInstance(makeInstance(project, { id: "editor-a" }));
      await registerInstance(makeInstance(project, { id: "editor-b" }));
      const result = await statusCommand("req-many", { project });
      assert.equal(result.ok, false);
      if (!result.ok) {
        assert.equal(result.error.code, "E_INSTANCE_AMBIGUOUS");
        assert.ok(result.nextActions.some((action) => action.action === "selectInstance"));
      }
    });
  });
});

test("status reports E_INSTANCE_PROJECT_MISMATCH when --instance targets another project", async () => {
  await withRuntime(async ({ home, project, endpoint }) => {
    await withAgentServer(endpoint, async () => {
      const otherProject = await createUnityProject(home, "OtherGame");
      await registerInstance(makeInstance(otherProject, { id: "other-instance" }));
      const result = await statusCommand("req-mismatch", { project, instance: "other-instance" });
      assert.equal(result.ok, false);
      if (!result.ok) {
        assert.equal(result.error.code, "E_INSTANCE_PROJECT_MISMATCH");
      }
    });
  });
});

test("status reports E_BRIDGE_VERSION_UNSUPPORTED for incompatible bridge", async () => {
  await withRuntime(async ({ project, endpoint }) => {
    await withAgentServer(endpoint, async () => {
      await registerInstance(makeInstance(project, { bridge: { version: "2.0.0", supported: true, logPath: "bridge.log" } }));
      const result = await statusCommand("req-bridge", { project });
      assert.equal(result.ok, false);
      if (!result.ok) {
        assert.equal(result.error.code, "E_BRIDGE_VERSION_UNSUPPORTED");
        assert.ok(result.nextActions.some((action) => action.action === "updateBridge"));
      }
    });
  });
});

test("instance current returns selected instance details", async () => {
  await withRuntime(async ({ project }) => {
    const instance = makeInstance(project, { id: "current-instance" });
    await registerInstance(instance);
    await setCurrentInstanceId(instance.id);
    const result = await instanceCurrentCommand("req-current");
    assert.equal(result.ok, true);
    if (result.ok) {
      assert.equal(result.data.instance.id, "current-instance");
      assert.equal(result.data.instance.unityVersion, "2022.3.1f1");
    }
  });
});
