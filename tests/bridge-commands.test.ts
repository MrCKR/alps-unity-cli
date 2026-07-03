import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { bridgeCommandRegistry, bridgeHttpTimeoutMs, type BridgeRequest } from "../src/bridge/index.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

let healthEndpoint1 = "http://127.0.0.1:47392";
let healthEndpoint2 = "http://127.0.0.1:47393";
const healthServers: Server[] = [];

test.before(async () => {
  healthEndpoint1 = await startHealthServer();
  healthEndpoint2 = await startHealthServer();
});

test.after(async () => {
  await Promise.all(healthServers.map((server) => new Promise<void>((resolve) => server.close(() => resolve()))));
});

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-bridge-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

async function writeRegistry(dataDir: string, instances: Array<Record<string, unknown>>) {
  const registryDir = join(dataDir, "instances");
  await mkdir(registryDir, { recursive: true });
  await Promise.all(instances.map((item) => writeFile(join(registryDir, `${item.instanceId}.json`), JSON.stringify(item, null, 2), "utf8")));
}

async function startHealthServer(): Promise<string> {
  const server = createServer((request, response) => {
    if (request.url === "/health") {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, status: "healthy" }));
      return;
    }
    response.writeHead(404);
    response.end();
  });
  healthServers.push(server);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  return `http://127.0.0.1:${address.port}`;
}

test("bridge HTTP timeout matches Unity queue window for regular commands", () => {
  assert.equal(bridgeHttpTimeoutMs({ requestId: "req", command: "editor.state", args: {} }), 65_000);
  assert.equal(bridgeHttpTimeoutMs({ requestId: "req", command: "scene.hierarchy", args: {} }), 65_000);
});

test("CLI help advertises --instance for live target selection", async () => {
  const result = await execute(["--help"]);
  assert.equal(result.ok, true);
  const commands = (result.data as { commands: string[] }).commands;
  assert.ok(commands.some((command) => command.includes("tools run <name>") && command.includes("--instance <id|alias>")));
  assert.ok(commands.some((command) => command.includes("context read <name>") && command.includes("--instance <id|alias>") && command.includes("--input-file <path>")));
  assert.ok(!commands.some((command) => command.includes("--target <instance>")));
});

function instance(overrides: Record<string, unknown> = {}) {
  return {
    instanceId: "editor-1",
    projectPath: "/tmp/project-one",
    projectHash: "hash-one",
    unityVersion: "2022.3.45f1",
    packageVersion: "com.unity_cli",
    pid: 1234,
    bridgeEndpoint: healthEndpoint1,
    capabilitiesHash: "caps",
    status: "ready",
    editorState: { playMode: false, compiling: false, domainReloading: false },
    playModeState: { isPlaying: false, isPaused: false, isPlayingOrWillChangePlaymode: false },
    runtimeSessionId: null,
    toolCatalogFingerprint: null,
    registeredAt: new Date().toISOString(),
    lastHeartbeatAt: new Date().toISOString(),
    ...overrides
  };
}

test("instances list and prune normalize stale registry entries", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [
    instance(),
    instance({ instanceId: "editor-stale", projectHash: "hash-stale", status: "ready", bridgeEndpoint: healthEndpoint2, lastHeartbeatAt: new Date(Date.now() - 60_000).toISOString() })
  ]);

  const list = await execute(["instances", "list"], runtime);
  assert.equal(list.ok, true);
  assert.deepEqual((list.data as { instances: Array<{ state: string }> }).instances.map((item) => item.state), ["ready", "stale"]);

  const cleanup = await execute(["instances", "prune", "--yes"], runtime);
  assert.equal(cleanup.ok, true);
  assert.equal((cleanup.data as { removed: number }).removed, 1);
});

test("instances list keeps latest endpoint for the same instance", async () => {
  const runtime = await makeRuntime();
  const oldHeartbeat = new Date(Date.now() - 10_000).toISOString();
  const newHeartbeat = new Date().toISOString();
  await writeFile(join(runtime.dataDir, "instances.json"), JSON.stringify({
    version: 1,
    instances: [
      instance({ status: "stale", bridgeEndpoint: healthEndpoint1, lastHeartbeatAt: oldHeartbeat }),
      instance({ status: "ready", bridgeEndpoint: healthEndpoint2, lastHeartbeatAt: newHeartbeat })
    ]
  }, null, 2), "utf8");

  const list = await execute(["instances", "list"], runtime);

  assert.equal(list.ok, true);
  const instances = (list.data as { instances: Array<{ state: string; endpoint: string }> }).instances;
  assert.equal(instances.length, 1);
  assert.equal(instances[0].state, "ready");
  assert.equal(instances[0].endpoint, healthEndpoint2);
});

test("bridge command does not select superseded healthy endpoint during reload", async () => {
  const runtime = await makeRuntime();
  const oldHeartbeat = new Date(Date.now() - 10_000).toISOString();
  const newHeartbeat = new Date().toISOString();
  await writeRegistry(runtime.dataDir, [
    instance({ status: "ready", bridgeEndpoint: healthEndpoint1, lastHeartbeatAt: oldHeartbeat }),
    instance({ status: "stale", bridgeEndpoint: healthEndpoint1, lastHeartbeatAt: newHeartbeat })
  ]);

  const result = await execute(["tools", "run", "editor.state", "--instance", "editor-1"], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TARGET_STALE");
});

test("bridge command routes --instance to the matching ready endpoint", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [
    instance(),
    instance({ instanceId: "editor-2", projectPath: "/tmp/project-two", projectHash: "hash-two", bridgeEndpoint: healthEndpoint2 })
  ]);
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "scene.hierarchy", "--instance", "editor-2"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { items: [] } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal((result.data as { bridgeEndpoint: string }).bridgeEndpoint, healthEndpoint2);
  assert.equal("selectedInstance" in (result.data as Record<string, unknown>), false);
  assert.equal("target" in (result.data as Record<string, unknown>), false);
  assert.equal(requests[0].command, "project-tool.run");
});

test("tools run go.inspect keeps --target for instance selection and input target for GameObject", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [instance({ instanceId: "editor-2", projectPath: process.cwd(), projectHash: "hash-two", bridgeEndpoint: healthEndpoint2 })]);
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "go.inspect", "--target", "editor-2", "--input", "{\"target\":\"Main Camera\"}"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { gameObject: { name: "Main Camera" } } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal((result.data as { bridgeEndpoint: string }).bridgeEndpoint, healthEndpoint2);
  assert.equal("selectedInstance" in (result.data as Record<string, unknown>), false);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].args, { name: "go.inspect", input: { target: "Main Camera" } });
});

test("tools run go.find keeps --target for instance selection and input target as search term", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [instance({ instanceId: "editor-2", projectPath: process.cwd(), projectHash: "hash-two", bridgeEndpoint: healthEndpoint2 })]);
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "go.find", "--target", "editor-2", "--input", "{\"target\":\"Main Camera\",\"pageSize\":5}"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { items: [], pageInfo: { total: 0 } } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal((result.data as { bridgeEndpoint: string }).bridgeEndpoint, healthEndpoint2);
  assert.equal("selectedInstance" in (result.data as Record<string, unknown>), false);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].args, { name: "go.find", input: { searchTerm: "Main Camera", target: "Main Camera", pageSize: 5 } });
});


test("instances list and target selection mark failed health checks unreachable", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [instance({ bridgeEndpoint: "http://127.0.0.1:1" })]);

  const list = await execute(["instances", "list"], runtime);
  assert.equal(list.ok, true);
  const instances = (list.data as { instances: Array<{ state: string; status: string }> }).instances;
  assert.equal(instances[0].state, "unreachable");
  assert.equal(instances[0].status, "unreachable");

  const result = await execute(["tools", "run", "editor.state", "--instance", "editor-1"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TARGET_UNREACHABLE");
});

test("target commands manage active target and aliases", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [instance(), instance({ instanceId: "editor-2", projectPath: "/tmp/project-two", projectHash: "hash-two", bridgeEndpoint: healthEndpoint2 })]);

  const alias = await execute(["target", "alias", "set", "editor-2", "main"], runtime);
  const set = await execute(["target", "set", "main"], runtime);
  const get = await execute(["target", "current"], runtime);

  assert.equal(alias.ok, true);
  assert.equal(set.ok, true);
  assert.equal((set.data as { target: { id: string; alias: string } }).target.id, "editor-2");
  assert.equal((set.data as { target: { id: string; alias: string } }).target.alias, "main");
  assert.equal(get.ok, true);
  assert.equal((get.data as { target: { id: string } }).target.id, "editor-2");
});

test("target alias set requires --force to overwrite another target", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [instance(), instance({ instanceId: "editor-2", projectPath: "/tmp/project-two", projectHash: "hash-two", bridgeEndpoint: healthEndpoint2 })]);

  const first = await execute(["target", "alias", "set", "editor-1", "main"], runtime);
  const conflict = await execute(["target", "alias", "set", "editor-2", "main"], runtime);
  const forced = await execute(["target", "alias", "set", "editor-2", "main", "--force"], runtime);
  const set = await execute(["target", "set", "main"], runtime);

  assert.equal(first.ok, true);
  assert.equal(conflict.ok, false);
  assert.equal(conflict.error?.code, "E_TARGET_ALIAS_CONFLICT");
  assert.equal(forced.ok, true);
  assert.equal((forced.data as { replacedInstanceId: string }).replacedInstanceId, "editor-1");
  assert.equal(set.ok, true);
  assert.equal((set.data as { target: { id: string; alias: string } }).target.id, "editor-2");
  assert.equal((set.data as { target: { id: string; alias: string } }).target.alias, "main");
});

test("bridge command auto-selects by current project path", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [instance({ projectPath: process.cwd() })]);

  const result = await execute(["tools", "run", "editor.state"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: true, data: { state: "ok" } })
  });

  assert.equal(result.ok, true);
  assert.equal((result.data as { bridgeEndpoint: string }).bridgeEndpoint, healthEndpoint1);
  assert.equal("selectedInstance" in (result.data as Record<string, unknown>), false);
});

test("bridge command reports missing, stale and ambiguous instances", async () => {
  const missingRuntime = await makeRuntime();
  await writeRegistry(missingRuntime.dataDir, [instance()]);
  const missing = await execute(["tools", "run", "editor.state", "--instance", "missing"], missingRuntime);
  assert.equal(missing.ok, false);
  assert.equal(missing.error?.code, "E_TARGET_NOT_FOUND");

  const staleRuntime = await makeRuntime();
  await writeRegistry(staleRuntime.dataDir, [instance({ status: "stale" })]);
  const stale = await execute(["tools", "run", "editor.state", "--instance", "editor-1"], staleRuntime);
  assert.equal(stale.ok, false);
  assert.equal(stale.error?.code, "E_TARGET_STALE");

  const ambiguousRuntime = await makeRuntime();
  await writeRegistry(ambiguousRuntime.dataDir, [instance(), instance({ instanceId: "editor-2", projectPath: "/tmp/project-two", projectHash: "hash-two", bridgeEndpoint: healthEndpoint2 })]);
  const ambiguous = await execute(["tools", "run", "editor.state"], ambiguousRuntime);
  assert.equal(ambiguous.ok, false);
  assert.equal(ambiguous.error?.code, "E_TARGET_AMBIGUOUS");
});

test("bridge command marks expired healthy heartbeat as stale", async () => {
  const runtime = await makeRuntime();
  await writeRegistry(runtime.dataDir, [instance({ lastHeartbeatAt: new Date(Date.now() - 60_000).toISOString() })]);

  const result = await execute(["tools", "run", "editor.state", "--instance", "editor-1"], runtime);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TARGET_STALE");
});

test("query command forwards stable bridge envelope", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "scene.hierarchy", "--page-size", "50"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { items: [{ path: "Main Camera" }], pageInfo: { pageSize: payload.args.pageSize } } };
    }
  });

  assert.equal(result.ok, true);
  assert.deepEqual(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].args, { name: "scene.hierarchy", input: { pageSize: 50 } });
  assert.equal(((result.data as { items: Array<{ path: string }> }).items[0]).path, "Main Camera");
});

test("console get forwards filter and cursor pagination", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "console.get", "--type", "error", "--filter", "AlignmentProbe", "--count", "2", "--cursor", "4"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command, items: [], pageInfo: { pageSize: payload.args.count, cursor: payload.args.cursor } } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "console.get");
  assert.deepEqual(requests[0].args, { type: "error", filter: "AlignmentProbe", count: 2, cursor: 4 });
});

test("console.get forwards structured --input fields", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "console.get", "--input", JSON.stringify({ type: "warning", search: "CS", count: 3, cursor: 6 })], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command, items: [], pageInfo: { pageSize: payload.args.count, cursor: payload.args.cursor } } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "console.get");
  assert.deepEqual(requests[0].args, { type: "warning", filter: "CS", count: 3, cursor: 6 });
});

test("controller transition forwards structured conditions from --input", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const input = {
    controllerPath: "Assets/UnityCliAlignmentFixtures/Alignment.controller",
    fromState: "Idle",
    toState: "Run",
    hasExitTime: false,
    conditions: [{ parameter: "Speed", mode: "greater", threshold: 0.1 }]
  };

  const result = await execute(["tools", "run", "controller.add-transition", "--yes", "--input", JSON.stringify(input)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "controller.add-transition");
  assert.deepEqual(requests[0].args, { ...input, layerIndex: 0 });
});

test("controller transition recovers PowerShell-escaped nested condition arrays", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const escapedInput = "{\\controllerPath\\:\\Assets/UnityCliAlignmentFixtures/Alignment.controller\\,\\fromState\\:\\Idle\\,\\toState\\:\\Run\\,\\hasExitTime\\:false,\\conditions\\:[{\\parameter\\:\\Speed\\,\\mode\\:\\greater\\,\\threshold\\:0.1}]}";

  const result = await execute(["tools", "run", "controller.add-transition", "--yes", "--input", escapedInput], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command } };
    }
  });

  assert.equal(result.ok, true);
  assert.deepEqual(requests[0].args, {
    controllerPath: "Assets/UnityCliAlignmentFixtures/Alignment.controller",
    fromState: "Idle",
    toState: "Run",
    layerIndex: 0,
    hasExitTime: false,
    conditions: [{ parameter: "Speed", mode: "greater", threshold: 0.1 }]
  });
});

test("edit command forwards target and plannedEffects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "component.set", "Player", "--component", "Camera", "--property", "enabled", "--value", "false"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      const input = (payload.args.input ?? {}) as Record<string, unknown>;
      return { ok: true, data: { plannedEffects: [{ type: payload.args.name, property: input.property }] } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].args, { name: "component.set", input: { target: "Player", component: "Camera", property: "enabled", value: "false" } });
  assert.deepEqual((result.data as { plannedEffects: unknown[] }).plannedEffects, [{ type: "component.set", property: "enabled" }]);
});

test("component lifecycle tools forward structured --input fields", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const addInput = { target: "UnityCliAlignmentProbe", component: "Rigidbody", properties: { mass: 2, useGravity: false } };
  const addResult = await execute(["tools", "run", "component.add", "--input", JSON.stringify(addInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command } };
    }
  });
  assert.equal(addResult.ok, true);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].args, { name: "component.add", input: addInput });

  const setInput = { target: "UnityCliAlignmentProbe", component: "Rigidbody", componentIndex: 1, properties: { mass: 5, useGravity: false } };
  const setResult = await execute(["tools", "run", "component.set", "--input", JSON.stringify(setInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command } };
    }
  });
  assert.equal(setResult.ok, true);
  assert.equal(requests[1].command, "project-tool.run");
  assert.deepEqual(requests[1].args, { name: "component.set", input: setInput });

  const singleInput = { target: "Main Camera", component: "Camera", property: "enabled", value: false };
  const singleResult = await execute(["tools", "run", "component.set", "--input", JSON.stringify(singleInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command } };
    }
  });
  assert.equal(singleResult.ok, true);
  assert.equal(requests[2].command, "project-tool.run");
  assert.deepEqual(requests[2].args, { name: "component.set", input: singleInput });

  const getInput = { target: "Main Camera", component: "Camera", paths: ["layerCullDistances"], maxArrayItems: 5 };
  const getResult = await execute(["tools", "run", "component.get", "--input", JSON.stringify(getInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command } };
    }
  });
  assert.equal(getResult.ok, true);
  assert.equal(requests[3].command, "project-tool.run");
  assert.deepEqual(requests[3].args, { name: "component.get", input: { path: "Main Camera", component: "Camera", paths: ["layerCullDistances"], maxArrayItems: 5 } });

  const removeInput = { target: "UnityCliAlignmentProbe", component: "BoxCollider", componentIndex: 0 };
  const removeResult = await execute(["tools", "run", "component.remove", "--input", JSON.stringify(removeInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command } };
    }
  });
  assert.equal(removeResult.ok, true);
  assert.equal(requests[4].command, "project-tool.run");
  assert.deepEqual(requests[4].args, { name: "component.remove", input: removeInput });

  const batchRemoveInput = { target: "UnityCliAlignmentProbe", components: ["BoxCollider", "Rigidbody"] };
  const batchRemoveResult = await execute(["tools", "run", "component.remove", "--input", JSON.stringify(batchRemoveInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command } };
    }
  });
  assert.equal(batchRemoveResult.ok, true);
  assert.equal(requests[5].command, "project-tool.run");
  assert.deepEqual(requests[5].args, { name: "component.remove", input: batchRemoveInput });
});

test("tools run forwards structured --input fields with pwsh escaping", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { command: payload.command, path: payload.args.path } };
  };

  const read = await execute(["tools", "run", "material.info", "--input", JSON.stringify({ path: "Assets/Materials/Probe.mat" })], { ...runtime, bridgeExecute });
  const pwshEscaped = await execute([
    "tools",
    "run",
    "material.info",
    "--input",
    "{\\",
    "path\\:\\Assets/Materials/Probe.mat}"
  ], { ...runtime, bridgeExecute });
  const backslashQuoted = await execute(["tools", "run", "material.info", "--input", '{\\"path\\":\\"Assets/Materials/Probe.mat\\"}'], { ...runtime, bridgeExecute });

  assert.equal(read.ok, true);
  assert.equal(pwshEscaped.ok, true);
  assert.equal(backslashQuoted.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["material.info", "material.info", "material.info"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Materials/Probe.mat" });
  assert.deepEqual(requests[1].args, { path: "Assets/Materials/Probe.mat" });
  assert.deepEqual(requests[2].args, { path: "Assets/Materials/Probe.mat" });
});

test("tools run rejects invalid --input JSON before bridge execution", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "material.info", "--input", "{not-json"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: {} };
    }
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_INPUT_JSON_INVALID");
  assert.deepEqual(requests, []);
});

test("screenshot commands forward output and surround arguments", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "screenshot.capture", "Artifacts/after.png", "--width", "1280", "--height", "720", "--inline-base64"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { screenshotPath: "Artifacts/after.png", plannedEffects: [{ type: payload.command, path: "Artifacts/after.png" }] } };
    }
  });
  const surround = await execute(["tools", "run", "screenshot.surround", "Player", "--output-dir", "Assets/Shots", "--contact-sheet", "Assets/Shots/contact.png", "--width", "320", "--height", "240", "--radius", "6.5", "--fov", "55", "--inline-base64", "--dry-run"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { command: payload.command, dryRun: payload.dryRun, plannedArtifacts: [] } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(surround.ok, true);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].args, { name: "screenshot.capture", input: { path: "Artifacts/after.png", width: 1280, height: 720, inlineBase64: true } });
  assert.equal((result.data as { screenshotPath: string }).screenshotPath, "Artifacts/after.png");
  assert.equal(requests[1].command, "screenshot.surround");
  assert.equal(requests[1].dryRun, true);
  assert.deepEqual(requests[1].args, { target: "Player", "output-dir": "Assets/Shots", contactSheetPath: "Assets/Shots/contact.png", width: 320, height: 240, radius: 6.5, fov: 55, inlineBase64: true });
});

test("build commands forward settings and dry-run arguments", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { command: payload.command, plannedEffects: payload.dryRun ? [{ type: payload.command }] : [] } };
  };

  const scenes = await execute(["tools", "run", "build.scenes", "--input", "{\"action\":\"add\",\"path\":\"Assets/Scenes/Main.unity\"}", "--dry-run"], { ...runtime, bridgeExecute });
  const platform = await execute(["tools", "run", "build.platform", "--input", "{\"action\":\"set\",\"target\":\"StandaloneWindows64\"}", "--dry-run"], { ...runtime, bridgeExecute });
  const profiles = await execute(["tools", "run", "build.profiles", "--input", "{\"action\":\"create\",\"path\":\"Assets/BuildProfiles/Windows.asset\",\"name\":\"Windows\"}", "--dry-run"], { ...runtime, bridgeExecute });
  const settings = await execute(["tools", "run", "build.settings", "--input", "{}"], { ...runtime, bridgeExecute });

  assert.equal(scenes.ok, true);
  assert.equal(platform.ok, true);
  assert.equal(profiles.ok, true);
  assert.equal(settings.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["build.scenes", "build.platform", "build.profiles", "build.settings"]);
  assert.deepEqual(requests[0].args, { action: "add", path: "Assets/Scenes/Main.unity", dryRun: true });
  assert.equal(requests[0].dryRun, true);
  assert.deepEqual(requests[1].args, { action: "set", target: "StandaloneWindows64", dryRun: true });
  assert.equal(requests[1].dryRun, true);
  assert.deepEqual(requests[2].args, { action: "create", path: "Assets/BuildProfiles/Windows.asset", name: "Windows", dryRun: true });
  assert.equal(requests[2].dryRun, true);
  assert.deepEqual(requests[3].args, {});
});

test("camera commands forward target and screenshot options", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const list = await execute(["tools", "run", "camera.list"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { cameras: [{ name: "Main Camera" }] } };
    }
  });

  const info = await execute(["tools", "run", "camera.info", "Main Camera"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { camera: { name: payload.args.target } } };
    }
  });

  const screenshot = await execute(["tools", "run", "camera.screenshot", "Main Camera", "--output", "Artifacts/camera.png", "--format", "png", "--width", "1920", "--height", "1080", "--transparent", "--overwrite", "--quality", "90"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { screenshotPath: payload.args.path, format: payload.args.format, width: payload.args.width, height: payload.args.height } };
    }
  });

  assert.equal(list.ok, true);
  assert.equal(info.ok, true);
  assert.equal(screenshot.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[1].args, { name: "camera.info", input: { target: "Main Camera" } });
  assert.deepEqual(requests[2].args, { name: "camera.screenshot", input: { path: "Artifacts/camera.png", format: "png", width: 1920, height: 1080, transparent: true, quality: 90, overwrite: true, target: "Main Camera" } });
});

test("camera mutation tools forward structured --input fields", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { command: payload.command, plannedEffects: [{ type: payload.command }] } };
  };

  const create = await execute(["tools", "run", "camera.create", "--input", "{\"name\":\"AlignmentCam\",\"position\":\"0,2,-6\",\"rotation\":\"15,0,0\",\"fieldOfView\":60,\"orthographic\":false,\"orthographicSize\":5,\"clearFlags\":\"SolidColor\",\"depth\":2,\"tag\":\"MainCamera\"}"], { ...runtime, bridgeExecute });
  const target = await execute(["tools", "run", "camera.target", "--input", "{\"target\":\"AlignmentCam\",\"lookAt\":\"UnityCliAlignmentProbe\",\"position\":\"0,3,-8\",\"rotation\":\"20,0,0\"}"], { ...runtime, bridgeExecute });
  const lens = await execute(["tools", "run", "camera.lens", "--input", "{\"target\":\"AlignmentCam\",\"fieldOfView\":45,\"orthographic\":true,\"orthographicSize\":10,\"nearClipPlane\":0.1,\"farClipPlane\":500,\"clearFlags\":\"Skybox\",\"depth\":3}"], { ...runtime, bridgeExecute });
  const multiview = await execute(["tools", "run", "camera.multiview", "--input", "{\"targets\":\"Main Camera,AlignmentCam\",\"output\":\"Artifacts/cameras\",\"width\":320,\"height\":180,\"format\":\"png\"}"], { ...runtime, bridgeExecute });

  for (const result of [create, target, lens, multiview]) {
    assert.equal(result.ok, true);
  }
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].args, { name: "camera.create", input: { name: "AlignmentCam", position: "0,2,-6", rotation: "15,0,0", fieldOfView: 60, orthographic: false, orthographicSize: 5, clearFlags: "SolidColor", depth: 2, tag: "MainCamera" } });
  assert.deepEqual(requests[1].args, { name: "camera.target", input: { target: "AlignmentCam", lookAt: "UnityCliAlignmentProbe", position: "0,3,-8", rotation: "20,0,0" } });
  assert.deepEqual(requests[2].args, { name: "camera.lens", input: { target: "AlignmentCam", fieldOfView: 45, orthographic: true, orthographicSize: 10, nearClipPlane: 0.1, farClipPlane: 500, clearFlags: "Skybox", depth: 3 } });
  assert.deepEqual(requests[3].args, { name: "camera.multiview", input: { targets: "Main Camera,AlignmentCam", output: "Artifacts/cameras", width: 320, height: 180, format: "png" } });
});

test("scene screenshot forwards output path and overwrite", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "scene.screenshot", "Artifacts/scene.jpg", "--width", "800", "--height", "600", "--format", "jpg", "--overwrite"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { screenshotPath: payload.args.path, source: "scene" } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].args, { name: "scene.screenshot", input: { path: "Artifacts/scene.jpg", format: "jpg", width: 800, height: 600, overwrite: true } });
});

test("prefab commands forward paths, pagination, modify args and planned effects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { prefab: { path: payload.args.path }, plannedEffects: payload.command === "prefab.create" ? [{ type: payload.command, path: payload.args.path }] : [] } };
  };

  const info = await execute(["tools", "run", "prefab.info", "Assets/Prefabs/Player.prefab"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "prefab.create", "Player", "--path", "Assets/Prefabs/Player.prefab", "--overwrite"], { ...runtime, bridgeExecute });
  const modify = await execute(["tools", "run", "prefab.modify", "Assets/Prefabs/Player.prefab", "--name", "Hero", "--add-child", "Socket", "--remove-child", "OldSocket", "--overwrite"], { ...runtime, bridgeExecute });
  const open = await execute(["tools", "run", "prefab.stage-open", "Assets/Prefabs/Player.prefab"], { ...runtime, bridgeExecute });
  const hierarchy = await execute(["tools", "run", "prefab.hierarchy", "Assets/Prefabs/Player.prefab", "--page-size", "25", "--cursor", "50", "--parent", "Player", "--query", "Socket", "--component-type", "Transform"], { ...runtime, bridgeExecute });
  const save = await execute(["tools", "run", "prefab.stage-save"], { ...runtime, bridgeExecute });
  const close = await execute(["tools", "run", "prefab.stage-close", "--save"], { ...runtime, bridgeExecute });

  assert.equal(info.ok, true);
  assert.equal(create.ok, true);
  assert.equal(modify.ok, true);
  assert.equal(open.ok, true);
  assert.equal(hierarchy.ok, true);
  assert.equal(save.ok, true);
  assert.equal(close.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["prefab.info", "prefab.create", "prefab.modify", "prefab.stage-open", "prefab.hierarchy", "prefab.stage-save", "prefab.stage-close"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Prefabs/Player.prefab" });
  assert.deepEqual(requests[1].args, { source: "Player", path: "Assets/Prefabs/Player.prefab", overwrite: true });
  assert.deepEqual(requests[2].args, { path: "Assets/Prefabs/Player.prefab", name: "Hero", addChild: "Socket", removeChild: "OldSocket", overwrite: true });
  assert.deepEqual(requests[4].args, { pageSize: 25, cursor: "50", path: "Assets/Prefabs/Player.prefab", parent: "Player", query: "Socket", componentType: "Transform" });
  assert.deepEqual(requests[5].args, {});
  assert.deepEqual(requests[6].args, { save: true });
  assert.deepEqual((create.data as { plannedEffects: unknown[] }).plannedEffects, [{ type: "prefab.create", path: "Assets/Prefabs/Player.prefab" }]);
});

test("destructive bridge commands require --yes before execution", async () => {
  const runtime = await makeRuntime();
  let called = false;

  const result = await execute(["tools", "run", "asset.delete", "Assets/DeleteMe.asset"], {
    ...runtime,
    bridgeExecute: async () => {
      called = true;
      return { ok: true, data: {} };
    }
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_DESTRUCTIVE_CONFIRMATION_REQUIRED");
  assert.equal(called, false);
});

test("destructive bridge commands pass dryRun and security proof when explicitly allowed", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const confirmed = await execute(["tools", "run", "asset.delete", "Assets/DeleteMe.asset", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { plannedEffects: [] } };
    }
  });
  const dryRun = await execute(["tools", "run", "asset.delete", "Assets/DeleteMe.asset", "--dry-run"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { dryRun: payload.dryRun } };
    }
  });

  assert.equal(confirmed.ok, true);
  assert.equal(requests[0].security?.confirmed, true);
  assert.equal(requests[0].security?.destructiveFingerprint, "destructive-safety-v1");
  assert.equal(dryRun.ok, true);
  assert.equal(requests[1].dryRun, true);
  assert.equal(requests[1].args.dryRun, true);
});

 test("asset commands forward CRUD and move-style envelopes", async () => {
  const runtime = await makeRuntime();
  const contentFile = join(runtime.dataDir, "asset.txt");
  await writeFile(contentFile, "hello", "utf8");
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { asset: { path: payload.args.path ?? payload.args.destination }, plannedEffects: [{ type: payload.command }] } };
  };

  const search = await execute(["tools", "run", "asset.search", "Player", "--type", "Prefab", "--page-size", "10"], { ...runtime, bridgeExecute });
  const info = await execute(["tools", "run", "asset.info", "Assets/Prefabs/Player.prefab"], { ...runtime, bridgeExecute });
  const components = await execute(["tools", "run", "asset.get-components", "Assets/Prefabs/Player.prefab", "--page-size", "25", "--query", "Camera", "--component-type", "Transform"], { ...runtime, bridgeExecute });
  const refresh = await execute(["tools", "run", "asset.refresh", "Assets/Generated", "--force-update", "--dry-run"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "asset.create", "Assets/Generated/config.json", "--content", "{}", "--overwrite"], { ...runtime, bridgeExecute });
  const mkdir = await execute(["tools", "run", "asset.create", "Assets/Generated"], { ...runtime, bridgeExecute });
  const imported = await execute(["tools", "run", "asset.import", "Assets/Generated/asset.txt", "--source", contentFile, "--overwrite"], { ...runtime, bridgeExecute });
  const modify = await execute(["tools", "run", "asset.modify", "Assets/Generated/config.json", "--content-file", contentFile], { ...runtime, bridgeExecute });
  const duplicate = await execute(["tools", "run", "asset.duplicate", "Assets/Generated/config.json", "--to", "Assets/Generated/config-copy.json"], { ...runtime, bridgeExecute });
  const move = await execute(["tools", "run", "asset.move", "Assets/Generated/config-copy.json", "--to", "Assets/Generated/config-moved.json"], { ...runtime, bridgeExecute });
  const rename = await execute(["tools", "run", "asset.rename", "Assets/Generated/config-moved.json", "--name", "config-renamed.json"], { ...runtime, bridgeExecute });
  const deleted = await execute(["tools", "run", "asset.delete", "Assets/Generated/config-renamed.json", "--yes"], { ...runtime, bridgeExecute });

  for (const result of [search, info, components, refresh, create, mkdir, imported, modify, duplicate, move, rename, deleted]) {
    assert.equal(result.ok, true);
  }
  assert.deepEqual(requests.map((request) => request.command), ["asset.search", "asset.info", "asset.get-components", "asset.refresh", "asset.create", "asset.create", "asset.import", "asset.modify", "asset.duplicate", "asset.move", "asset.rename", "asset.delete"]);
  assert.deepEqual(requests[0].args, { pageSize: 10, target: "Player", query: "Player", type: "Prefab" });
  assert.deepEqual(requests[2].args, { assetPath: "Assets/Prefabs/Player.prefab", query: "Camera", componentType: "Transform", pageSize: 25 });
  assert.deepEqual(requests[3].args, { path: "Assets/Generated", forceUpdate: true, dryRun: true });
  assert.equal(requests[3].dryRun, true);
  assert.deepEqual(requests[4].args, { path: "Assets/Generated/config.json", content: "{}", overwrite: true });
  assert.deepEqual(requests[5].args, { path: "Assets/Generated" });
  assert.deepEqual(requests[6].args, { path: "Assets/Generated/asset.txt", source: contentFile, overwrite: true });
  assert.deepEqual(requests[7].args, { path: "Assets/Generated/config.json", content: "hello" });
  assert.deepEqual(requests[8].args, { path: "Assets/Generated/config.json", destination: "Assets/Generated/config-copy.json" });
  assert.deepEqual(requests[9].args, { path: "Assets/Generated/config-copy.json", destination: "Assets/Generated/config-moved.json" });
  assert.deepEqual(requests[10].args, { path: "Assets/Generated/config-moved.json", name: "config-renamed.json" });
  assert.deepEqual(requests[11].args, { path: "Assets/Generated/config-renamed.json" });
  assert.equal(requests[11].security?.destructiveFingerprint, "destructive-safety-v1");
});

test("asset mutation tools forward structured --input fields", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { asset: { path: payload.args.path ?? payload.args.destination }, plannedEffects: [{ type: payload.command }] } };
  };

  const create = await execute(["tools", "run", "asset.create", "--input", "{\"path\":\"Assets/Generated/config.json\",\"type\":\"file\",\"content\":\"{}\",\"overwrite\":true}"], { ...runtime, bridgeExecute });
  const duplicate = await execute(["tools", "run", "asset.duplicate", "--input", "{\"source\":\"Assets/Generated/config.json\",\"destination\":\"Assets/Generated/config-copy.json\"}"], { ...runtime, bridgeExecute });
  const rename = await execute(["tools", "run", "asset.rename", "--input", "{\"path\":\"Assets/Generated/config-copy.json\",\"name\":\"config-renamed.json\"}"], { ...runtime, bridgeExecute });
  const deleted = await execute(["tools", "run", "asset.delete", "--input", "{\"path\":\"Assets/Generated/config-renamed.json\"}", "--dry-run"], { ...runtime, bridgeExecute });

  for (const result of [create, duplicate, rename, deleted]) {
    assert.equal(result.ok, true);
  }
  assert.deepEqual(requests.map((request) => request.command), ["asset.create", "asset.duplicate", "asset.rename", "asset.delete"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Generated/config.json", type: "file", content: "{}", overwrite: true });
  assert.deepEqual(requests[1].args, { path: "Assets/Generated/config.json", source: "Assets/Generated/config.json", destination: "Assets/Generated/config-copy.json" });
  assert.deepEqual(requests[2].args, { path: "Assets/Generated/config-copy.json", name: "config-renamed.json" });
  assert.deepEqual(requests[3].args, { path: "Assets/Generated/config-renamed.json", dryRun: true });
  assert.equal(requests[3].dryRun, true);
});

test("material commands forward asset paths, target, values and planned effects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { material: { path: payload.args.path ?? payload.args.material }, plannedEffects: payload.command === "material.info" ? [] : [{ type: payload.command, path: payload.args.path ?? payload.args.material }] } };
  };

  const info = await execute(["tools", "run", "material.info", "Assets/Materials/Player.mat"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "material.create", "Assets/Materials/Player.mat", "--shader", "Standard", "--overwrite"], { ...runtime, bridgeExecute });
  const assign = await execute(["tools", "run", "material.assign", "--target", "Canvas/Button", "--material", "Assets/Materials/Player.mat", "--slot", "1"], { ...runtime, bridgeExecute });
  const set = await execute(["tools", "run", "material.set", "Assets/Materials/Player.mat", "--property", "_Metallic", "--input", "{\"floatValue\":0.8,\"type\":\"float\"}"], { ...runtime, bridgeExecute });
  const rendererColor = await execute(["tools", "run", "material.set-renderer-color", "Player", "--color", "#ff00ffff", "--slot", "0", "--dry-run"], { ...runtime, bridgeExecute });

  assert.equal(info.ok, true);
  assert.equal(create.ok, true);
  assert.equal(assign.ok, true);
  assert.equal(set.ok, true);
  assert.equal(rendererColor.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["material.info", "material.create", "material.assign", "material.set", "material.set-renderer-color"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Materials/Player.mat" });
  assert.deepEqual(requests[1].args, { path: "Assets/Materials/Player.mat", shader: "Standard", overwrite: true });
  assert.deepEqual(requests[2].args, { target: "Canvas/Button", material: "Assets/Materials/Player.mat", slot: 1 });
  assert.deepEqual(requests[3].args, { path: "Assets/Materials/Player.mat", property: "_Metallic", floatValue: "0.8", type: "float" });
  assert.deepEqual(requests[4].args, { target: "Player", color: "#ff00ffff", slot: 0, dryRun: true });
  assert.deepEqual((set.data as { plannedEffects: unknown[] }).plannedEffects, [{ type: "material.set", path: "Assets/Materials/Player.mat" }]);
});


test("material bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "material.set", "Assets/Materials/Player.mat", "--property", "_Missing", "--value", "1"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_MATERIAL_PROPERTY_NOT_FOUND", message: "Material property not found: _Missing" }, nextActions: ["Run unity_cli tools run material.info --input '{\"path\":\"<path>\"}' to inspect shader properties."] })
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_MATERIAL_PROPERTY_NOT_FOUND");
  assert.deepEqual(result.error?.next_actions, ["Run unity_cli tools run material.info --input '{\"path\":\"<path>\"}' to inspect shader properties."]);
});

test("graphics commands forward settings and screenshot diagnostics", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { graphics: { renderPipeline: { active: "Built-in" } }, plannedEffects: payload.args } };
  };

  const quality = await execute(["tools", "run", "graphics.quality", "--input", JSON.stringify({ level: "High", vSyncCount: 1, shadowDistance: 75.5, softParticles: false })], { ...runtime, bridgeExecute });
  const settings = await execute(["tools", "run", "graphics.settings", "--render-pipeline", "Assets/Settings/URP.asset", "--anti-aliasing", "4"], { ...runtime, bridgeExecute });
  const pipeline = await execute(["tools", "run", "graphics.pipeline"], { ...runtime, bridgeExecute });
  const pipelineQuality = await execute(["tools", "run", "graphics.pipeline-quality", "--input", JSON.stringify({ level: "Medium" })], { ...runtime, bridgeExecute });
  const pipelineSettings = await execute(["tools", "run", "graphics.pipeline-settings", "--input", JSON.stringify({ renderPipeline: "built-in" })], { ...runtime, bridgeExecute });
  const pipelineInfo = await execute(["tools", "run", "graphics.pipeline-info"], { ...runtime, bridgeExecute });
  const volume = await execute(["tools", "run", "graphics.volume", "Global Volume"], { ...runtime, bridgeExecute });
  const screenshot = await execute(["tools", "run", "graphics.screenshot", "Artifacts/graphics.png", "--width", "1024", "--height", "768", "--include-diagnostics", "--overwrite"], { ...runtime, bridgeExecute });

  for (const result of [quality, settings, pipeline, pipelineQuality, pipelineSettings, pipelineInfo, volume, screenshot]) {
    assert.equal(result.ok, true);
  }
  assert.deepEqual(requests.map((request) => request.command), ["graphics.quality", "graphics.settings", "graphics.pipeline", "graphics.pipeline-quality", "graphics.pipeline-settings", "graphics.pipeline-info", "graphics.volume", "graphics.screenshot"]);
  assert.deepEqual(requests[0].args, { qualityLevel: "High", vSyncCount: 1, shadowDistance: 75.5, softParticles: false });
  assert.deepEqual(requests[1].args, { antiAliasing: 4, renderPipeline: "Assets/Settings/URP.asset" });
  assert.deepEqual(requests[2].args, {});
  assert.deepEqual(requests[3].args, { qualityLevel: "Medium" });
  assert.deepEqual(requests[4].args, { renderPipeline: "built-in" });
  assert.deepEqual(requests[5].args, {});
  assert.deepEqual(requests[6].args, { target: "Global Volume" });
  assert.deepEqual(requests[7].args, { path: "Artifacts/graphics.png", width: 1024, height: 768, overwrite: true, includeDiagnostics: true });
});

test("physics commands forward settings, queries, joint and material assignment inputs", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { physics: {}, plannedEffects: payload.command === "physics.raycast" ? [] : [{ type: payload.command }] } };
  };

  const settings = await execute(["tools", "run", "physics.settings", "--gravity-y", "-9.81", "--gravity2d-y", "-12.5", "--queries-hit-triggers", "false", "--default-solver-iterations", "8"], { ...runtime, bridgeExecute });
  const raycast = await execute(["tools", "run", "physics.raycast", "--mode", "2d", "--origin-x", "1", "--origin-y", "2", "--direction-x", "0", "--direction-y", "-1", "--distance", "50", "--layer-mask", "5", "--all"], { ...runtime, bridgeExecute });
  const shapecast = await execute(["tools", "run", "physics.shapecast", "--mode", "2d", "--shape", "capsule", "--origin-x", "1", "--origin-y", "2", "--direction-x", "1", "--direction-y", "0", "--distance", "3.5", "--radius", "0.5", "--height", "2", "--capsule-direction", "horizontal", "--angle", "15"], { ...runtime, bridgeExecute });
  const assign = await execute(["tools", "run", "physics.assign-material", "--target", "Ball", "--path", "Assets/Physics/Bouncy.physicMaterial", "--collider", "BoxCollider", "--collider-index", "1", "--dry-run"], { ...runtime, bridgeExecute });
  const joint = await execute(["tools", "run", "physics.joint", "--input", "{\"target\":\"Ball\",\"type\":\"FixedJoint\",\"action\":\"remove\",\"componentIndex\":0}"], { ...runtime, bridgeExecute });
  const simulation = await execute(["tools", "run", "physics.simulation", "--steps", "3", "--delta-time", "0.016", "--simulate"], { ...runtime, bridgeExecute });

  assert.equal(settings.ok, true);
  assert.equal(raycast.ok, true);
  assert.equal(shapecast.ok, true);
  assert.equal(assign.ok, true);
  assert.equal(joint.ok, true);
  assert.equal(simulation.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["physics.settings", "physics.raycast", "physics.shapecast", "physics.assign-material", "physics.joint", "physics.simulation"]);
  assert.deepEqual(requests[0].args, { gravityY: -9.81, gravity2dY: -12.5, defaultSolverIterations: 8, queriesHitTriggers: false });
  assert.deepEqual(requests[1].args, { mode: "2d", originX: 1, originY: 2, directionX: 0, directionY: -1, distance: 50, layerMask: 5, all: true });
  assert.deepEqual(requests[2].args, { shape: "capsule", radius: 0.5, height: 2, capsuleDirection: "horizontal", angle: 15, mode: "2d", originX: 1, originY: 2, directionX: 1, directionY: 0, distance: 3.5 });
  assert.deepEqual(requests[3].args, { target: "Ball", materialPath: "Assets/Physics/Bouncy.physicMaterial", path: "Assets/Physics/Bouncy.physicMaterial", collider: "BoxCollider", colliderIndex: 1 });
  assert.equal(requests[3].dryRun, true);
  assert.deepEqual(requests[4].args, { target: "Ball", type: "FixedJoint", action: "remove", componentIndex: 0 });
  assert.deepEqual(requests[5].args, { steps: 3, deltaTime: 0.016, simulate: true });
});

test("profiler commands forward session, counter, memory, frame and export args", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { profiler: { enabled: payload.command === "profiler.start" }, counters: [], memory: {}, frame: {}, artifactPath: payload.args.path, plannedEffects: [{ type: payload.command }] } };
  };

  const status = await execute(["tools", "run", "profiler.status"], { ...runtime, bridgeExecute });
  const start = await execute(["tools", "run", "profiler.start", "--deep-profiling"], { ...runtime, bridgeExecute });
  const pause = await execute(["tools", "run", "profiler.pause", "--paused", "true"], { ...runtime, bridgeExecute });
  const counter = await execute(["tools", "run", "profiler.counter", "--category", "memory", "--counter", "MonoUsedSizeLong"], { ...runtime, bridgeExecute });
  const memory = await execute(["tools", "run", "profiler.memory"], { ...runtime, bridgeExecute });
  const frame = await execute(["tools", "run", "profiler.frame", "--module", "CPU Usage"], { ...runtime, bridgeExecute });
  const capture = await execute(["tools", "run", "profiler.capture", "Artifacts/profile.json", "--duration-ms", "100", "--frames", "2", "--format", "json", "--overwrite"], { ...runtime, bridgeExecute });
  const exportResult = await execute(["tools", "run", "profiler.export", "Artifacts/profile-export.json", "--format", "json", "--overwrite"], { ...runtime, bridgeExecute });
  const stop = await execute(["tools", "run", "profiler.stop"], { ...runtime, bridgeExecute });

  assert.equal(status.ok, true);
  assert.equal(start.ok, true);
  assert.equal(pause.ok, true);
  assert.equal(counter.ok, true);
  assert.equal(memory.ok, true);
  assert.equal(frame.ok, true);
  assert.equal(capture.ok, true);
  assert.equal(exportResult.ok, true);
  assert.equal(stop.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["profiler.status", "profiler.start", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export", "profiler.stop"]);
  assert.deepEqual(requests[1].args, { deepProfiling: true });
  assert.deepEqual(requests[2].args, { paused: true });
  assert.deepEqual(requests[3].args, { counter: "MonoUsedSizeLong", category: "memory" });
  assert.deepEqual(requests[4].args, {});
  assert.deepEqual(requests[5].args, { module: "CPU Usage" });
  assert.deepEqual(requests[6].args, { path: "Artifacts/profile.json", format: "json", durationMs: 100, frames: 2, overwrite: true });
  assert.deepEqual(requests[7].args, { path: "Artifacts/profile-export.json", format: "json", overwrite: true });
});

test("animation and vfx commands forward Windows paths, targets and effects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { plannedEffects: [{ type: payload.command }], clip: { path: payload.args.path }, graph: { path: payload.args.path }, target: { path: payload.args.target } } };
  };

  const clipInfo = await execute(["tools", "run", "animation.clip", "Assets\\Animations\\Idle.anim", "--max-curve-keys", "2"], { ...runtime, bridgeExecute });
  const clipCreate = await execute(["tools", "run", "animation.clip.create", "Assets\\Animations\\Run.anim", "--overwrite", "--frame-rate", "30", "--legacy", "false", "--wrap-mode", "Loop", "--loop-time", "true", "--length", "1", "--max-curve-keys", "2", "--yes"], { ...runtime, bridgeExecute });
  const clipModify = await execute(["tools", "run", "animation.clip.modify", "Assets\\Animations\\Run.anim", "--frame-rate", "24", "--wrap-mode", "ClampForever", "--loop-time", "false", "--yes"], { ...runtime, bridgeExecute });
  const state = await execute(["tools", "run", "animation.state", "Rig/Player", "--layer", "1"], { ...runtime, bridgeExecute });
  const play = await execute(["tools", "run", "animation.play", "Rig/Player", "--state", "Base Layer.Run", "--layer", "0", "--normalized-time", "0.25"], { ...runtime, bridgeExecute });
  const stop = await execute(["tools", "run", "animation.stop", "Rig/Player"], { ...runtime, bridgeExecute });
  const graphCreate = await execute(["tools", "run", "vfx.graph", "--input", JSON.stringify({ path: "Assets\\VFX\\Explosion.vfx", create: true, overwrite: true })], { ...runtime, bridgeExecute });
  const effect = await execute(["tools", "run", "vfx.effect", "--input", JSON.stringify({ target: "Effects/Emitter", graph: "Assets\\VFX\\Explosion.vfx", add: true })], { ...runtime, bridgeExecute });
  const particleModuleInput = { target: "Dust", module: "trails", enabled: true, ratio: 0.5, lifetime: 0.75, width: 0.08, color: "#FFAA33FF" };
  const particleModule = await execute(["tools", "run", "vfx.particle-module", "--input", JSON.stringify(particleModuleInput)], { ...runtime, bridgeExecute });
  const lineCreateInput = {
    name: "AimLine",
    material: "Assets\\Materials\\Line.mat",
    positions: [{ x: 0, y: 0, z: 0 }, { x: 1, y: 2, z: 3 }],
    widthCurve: [{ time: 0, value: 0.2 }, { time: 1, value: 0.05 }],
    colorGradient: [{ time: 0, color: "#FFFFFFFF" }, { time: 1, color: "#00FFFFFF" }],
    alignment: "View",
    textureMode: "Tile"
  };
  const lineCreate = await execute(["tools", "run", "vfx.line-create", "--input", JSON.stringify(lineCreateInput)], { ...runtime, bridgeExecute });
  const linePositionsInput = { target: "AimLine", positions: [{ x: 2, y: 0, z: 0 }, { x: 4, y: 0, z: 0 }] };
  const linePositions = await execute(["tools", "run", "vfx.line-positions", "--input", JSON.stringify(linePositionsInput)], { ...runtime, bridgeExecute });
  const trailInput = {
    name: "DashTrail",
    create: true,
    material: "Assets\\Materials\\Trail.mat",
    widthCurve: [{ time: 0, value: 0.3 }, { time: 1, value: 0 }],
    colorGradient: [{ time: 0, color: "#FFFFFFFF" }, { time: 1, color: "#00FFFFFF" }],
    emitting: true
  };
  const trail = await execute(["tools", "run", "vfx.trail", "--input", JSON.stringify(trailInput)], { ...runtime, bridgeExecute });

  assert.equal(clipInfo.ok, true);
  assert.equal(clipCreate.ok, true);
  assert.equal(clipModify.ok, true);
  assert.equal(state.ok, true);
  assert.equal(play.ok, true);
  assert.equal(stop.ok, true);
  assert.equal(graphCreate.ok, true);
  assert.equal(effect.ok, true);
  assert.equal(particleModule.ok, true);
  assert.equal(lineCreate.ok, true);
  assert.equal(linePositions.ok, true);
  assert.equal(trail.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["animation.clip", "animation.clip.create", "animation.clip.modify", "animation.state", "animation.play", "animation.stop", "vfx.graph", "vfx.effect", "vfx.particle-module", "vfx.line-create", "vfx.line-positions", "vfx.trail"]);
  assert.deepEqual(requests[0].args, { path: "Assets\\Animations\\Idle.anim", maxCurveKeys: 2 });
  assert.deepEqual(requests[1].args, { path: "Assets\\Animations\\Run.anim", overwrite: true, frameRate: 30, legacy: false, wrapMode: "Loop", loopTime: true, length: 1, maxCurveKeys: 2 });
  assert.deepEqual(requests[2].args, { path: "Assets\\Animations\\Run.anim", frameRate: 24, wrapMode: "ClampForever", loopTime: false });
  assert.deepEqual(requests[3].args, { target: "Rig/Player", layer: 1 });
  assert.deepEqual(requests[4].args, { target: "Rig/Player", state: "Base Layer.Run", layer: 0, normalizedTime: 0.25 });
  assert.deepEqual(requests[5].args, { target: "Rig/Player" });
  assert.deepEqual(requests[6].args, { action: "create", path: "Assets\\VFX\\Explosion.vfx", overwrite: true });
  assert.deepEqual(requests[7].args, { target: "Effects/Emitter", graph: "Assets\\VFX\\Explosion.vfx", add: true });
  assert.deepEqual(requests[8].args, particleModuleInput);
  assert.deepEqual(requests[9].args, lineCreateInput);
  assert.deepEqual(requests[10].args, linePositionsInput);
  assert.deepEqual(requests[11].args, trailInput);
});

test("animation and vfx bridge errors preserve diagnostics", async () => {
  const runtime = await makeRuntime();
  const cases: Array<{ argv: string[]; code: string; message: string; nextActions: string[] }> = [
    { argv: ["tools", "run", "animation.play", "Player", "--state", "Missing"], code: "E_TARGET_AMBIGUOUS", message: "Multiple GameObjects matched: Player", nextActions: ["Pass a hierarchy path or instance id."] },
    { argv: ["tools", "run", "animation.clip", "Assets/Animations/Missing.anim"], code: "E_ANIMATION_CLIP_NOT_FOUND", message: "AnimationClip asset not found", nextActions: ["Check the path or create one with unity_cli tools run animation.clip.create --input '{\"path\":\"<path>\"}' --yes."] },
    { argv: ["tools", "run", "vfx.graph", "--input", JSON.stringify({ path: "Assets/VFX/Explosion.vfx" })], code: "E_VFX_PACKAGE_MISSING", message: "Visual Effect Graph package is not installed", nextActions: ["Install com.unity.visualeffectgraph manually via Unity Package Manager or Packages/manifest.json, run unity_cli tools run package.resolve, then retry."] },
    { argv: ["tools", "run", "vfx.graph", "--input", JSON.stringify({ path: "Assets/VFX/Explosion.vfx", create: true })], code: "E_VFX_GRAPH_CREATE_FAILED", message: "VFX Graph asset creation did not produce a readable VisualEffectAsset", nextActions: ["Check Unity Console and UnityCliBridge.log; the Visual Effect Graph package may have changed its internal authoring API."] },
    { argv: ["tools", "run", "animation.clip", "D:\\tmp\\Bad.anim"], code: "E_ANIMATION_CLIP_PATH_INVALID", message: "AnimationClip path must be under Assets/ and end with .anim: D:\\tmp\\Bad.anim", nextActions: ["Pass a Unity asset path such as Assets/Animations/Player.anim."] }
  ];

  for (const item of cases) {
    const result = await execute(item.argv, {
      ...runtime,
      bridgeExecute: async () => ({ ok: false, error: { code: item.code, message: item.message }, nextActions: item.nextActions })
    });

    assert.equal(result.ok, false, item.code);
    assert.equal(result.error?.code, item.code, item.code);
    assert.equal(result.error?.message, item.message, item.code);
    assert.deepEqual(result.error?.next_actions, item.nextActions, item.code);
  }
});

test("shader commands forward paths, content and overwrite", async () => {
  const runtime = await makeRuntime();
  const contentFile = join(runtime.dataDir, "Unlit.shader");
  await writeFile(contentFile, "Shader \"UnityCli/Test\" {}\n", "utf8");
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { shader: { path: payload.args.path }, plannedEffects: payload.command.startsWith("shader.") ? [{ type: payload.command, path: payload.args.path }] : [] } };
  };

  const info = await execute(["tools", "run", "shader.info", "Assets/Shaders/Unlit.shader"], { ...runtime, bridgeExecute });
  const read = await execute(["tools", "run", "shader.read", "Assets/Shaders/Unlit.shader", "--offset", "5", "--max-bytes", "128"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "shader.create", "Assets/Shaders/Created.shader", "--content-file", contentFile, "--overwrite"], { ...runtime, bridgeExecute });
  const update = await execute(["tools", "run", "shader.update", "Assets/Shaders/Unlit.shader", "--content", "Shader X {}"], { ...runtime, bridgeExecute });
  const del = await execute(["tools", "run", "shader.delete", "Assets/Shaders/Unlit.shader", "--dry-run"], { ...runtime, bridgeExecute });
  const getData = await execute(["tools", "run", "shader.get-data", "--input", "{\"assetPath\":\"Assets/Shaders/Unlit.shader\",\"includeSubshaders\":true,\"includeSourceCode\":false,\"maxPasses\":4}"], { ...runtime, bridgeExecute });

  assert.equal(info.ok, true);
  assert.equal(read.ok, true);
  assert.equal(create.ok, true);
  assert.equal(update.ok, true);
  assert.equal(del.ok, true);
  assert.equal(getData.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["shader.info", "shader.read", "shader.create", "shader.update", "shader.delete", "shader.get-data"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Shaders/Unlit.shader", target: "Assets/Shaders/Unlit.shader" });
  assert.deepEqual(requests[1].args, { path: "Assets/Shaders/Unlit.shader", target: "Assets/Shaders/Unlit.shader", offset: 5, maxBytes: 128 });
  assert.equal(requests[2].args.content, "Shader \"UnityCli/Test\" {}\n");
  assert.equal(requests[2].args.overwrite, true);
  assert.equal(requests[3].args.content, "Shader X {}");
  assert.equal(requests[4].dryRun, true);
  assert.deepEqual(requests[5].args, { path: "Assets/Shaders/Unlit.shader", assetPath: "Assets/Shaders/Unlit.shader", target: "Assets/Shaders/Unlit.shader", includeSubshaders: true, includeSourceCode: false, maxPasses: 4 });
});

test("texture commands forward importer, sprite and creation options", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { texture: { path: payload.args.path }, importSettings: { textureType: payload.args.textureType }, plannedEffects: [{ type: payload.command, path: payload.args.path }] } };
  };

  const info = await execute(["tools", "run", "texture.info", "Assets/Textures/Icon.png"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "texture.create", "Assets/Textures/Icon.png", "--source", "C:/tmp/icon.png", "--overwrite", "--texture-type", "Sprite"], { ...runtime, bridgeExecute });
  const sprite = await execute(["tools", "run", "texture.sprite", "Assets/Textures/Icon.png", "--input", "{\"pixelsPerUnit\":128,\"pivotX\":\"0.25\",\"pivotY\":\"0.75\",\"spriteMode\":\"Multiple\",\"slices\":[{\"name\":\"left\",\"x\":0,\"y\":0,\"width\":16,\"height\":16}]}"], { ...runtime, bridgeExecute });
  const modify = await execute(["tools", "run", "texture.modify", "Assets/Textures/Icon.png", "--fill-color", "#ff0000", "--x", "1", "--y", "2", "--width", "3", "--height", "4", "--dry-run"], { ...runtime, bridgeExecute });
  const del = await execute(["tools", "run", "texture.delete", "Assets/Textures/Icon.png", "--dry-run"], { ...runtime, bridgeExecute });
  const importSettings = await execute(["tools", "run", "texture.import-settings", "Assets/Textures/Icon.png", "--readable", "false", "--mipmap", "false", "--srgb", "true", "--alpha-is-transparency", "--wrap-mode", "Clamp", "--filter-mode", "Point", "--max-texture-size", "512", "--compression", "Uncompressed"], { ...runtime, bridgeExecute });

  assert.equal(info.ok, true);
  assert.equal(create.ok, true);
  assert.equal(sprite.ok, true);
  assert.equal(modify.ok, true);
  assert.equal(del.ok, true);
  assert.equal(importSettings.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["texture.info", "texture.create", "texture.sprite", "texture.modify", "texture.delete", "texture.import-settings"]);
  assert.deepEqual(requests[1].args, { path: "Assets/Textures/Icon.png", target: "Assets/Textures/Icon.png", source: "C:/tmp/icon.png", overwrite: true, textureType: "Sprite" });
  assert.deepEqual(requests[2].args, { path: "Assets/Textures/Icon.png", target: "Assets/Textures/Icon.png", spriteMode: "Multiple", pixelsPerUnit: 128, pivotX: "0.25", pivotY: "0.75", slices: [{ name: "left", x: 0, y: 0, width: 16, height: 16 }] });
  assert.deepEqual(requests[3].args, { path: "Assets/Textures/Icon.png", target: "Assets/Textures/Icon.png", width: 3, height: 4, fillColor: "#ff0000", x: 1, y: 2, dryRun: true });
  assert.equal(requests[4].dryRun, true);
  assert.equal(requests[5].args.readable, false);
  assert.equal(requests[5].args.mipmap, false);
  assert.equal(requests[5].args.sRGB, true);
  assert.equal(requests[5].args.alphaIsTransparency, true);
  assert.equal(requests[5].args.maxTextureSize, 512);
});

test("shader and texture bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const shader = await execute(["tools", "run", "shader.info", "Assets/Shaders/Missing.shader"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_SHADER_NOT_FOUND", message: "Shader asset not found." }, nextActions: ["Check the path."] })
  });
  const texture = await execute(["tools", "run", "texture.import-settings", "Assets/Textures/Missing.png"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_IMPORT_FAILED", message: "Failed to apply texture import settings." }, nextActions: ["Check Unity Console."] })
  });

  assert.equal(shader.ok, false);
  assert.equal(shader.error?.code, "E_SHADER_NOT_FOUND");
  assert.deepEqual(shader.error?.next_actions, ["Check the path."]);
  assert.equal(texture.ok, false);
  assert.equal(texture.error?.code, "E_IMPORT_FAILED");
  assert.deepEqual(texture.error?.next_actions, ["Check Unity Console."]);
});

test("ui commands forward creation, document and hierarchy arguments", async () => {
  const runtime = await makeRuntime();
  const contentFile = join(runtime.dataDir, "Main.uxml");
  await writeFile(contentFile, "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />\n", "utf8");
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { plannedEffects: payload.command.includes("create") ? [{ type: payload.command, args: payload.args }] : [], document: payload.command.includes("document") ? { path: payload.args.uxml ?? payload.args.path } : undefined } };
  };

  const canvas = await execute(["tools", "run", "ui.canvas.create", "HUD"], { ...runtime, bridgeExecute });
  const text = await execute(["tools", "run", "ui.text.create", "Title", "--parent", "HUD", "--text", "Start", "--width", "320", "--height", "80", "--x", "10", "--y", "20", "--font-size", "32"], { ...runtime, bridgeExecute });
  const button = await execute(["tools", "run", "ui.button.create", "PlayButton", "--canvas", "HUD", "--label", "Play"], { ...runtime, bridgeExecute });
  const image = await execute(["tools", "run", "ui.image.create", "Logo", "--parent", "HUD", "--sprite", "Assets/UI/logo.png", "--color", "#ffffff"], { ...runtime, bridgeExecute });
  const documentCreate = await execute(["tools", "run", "ui.document.create", "--name", "MainUIDocument", "--uxml", "Assets/UI/Main.uxml", "--uss", "Assets/UI/Main.uss", "--panel-settings", "Assets/UI/Panel.asset", "--overwrite"], { ...runtime, bridgeExecute });
  const documentInfo = await execute(["tools", "run", "ui.document.info", "MainUIDocument", "--uss", "Assets/UI/Main.uss"], { ...runtime, bridgeExecute });
  const documentUpdate = await execute(["tools", "run", "ui.document.update", "Assets/UI/Main.uxml", "--content-file", contentFile, "--dry-run"], { ...runtime, bridgeExecute });
  const documentDelete = await execute(["tools", "run", "ui.document.delete", "Assets/UI/Main.uxml", "--dry-run"], { ...runtime, bridgeExecute });
  const hierarchy = await execute(["tools", "run", "ui.hierarchy", "HUD", "--page-size", "25", "--cursor", "50", "--query", "Canvas", "--type", "RectTransform", "--class-name", "screen"], { ...runtime, bridgeExecute });
  const attach = await execute(["tools", "run", "ui.attach-document", "UIDoc", "--source-asset", "Assets/UI/Main.uxml", "--panel-settings", "Assets/UI/Panel.asset", "--sort-order", "5"], { ...runtime, bridgeExecute });
  const detach = await execute(["tools", "run", "ui.detach-document", "UIDoc", "--dry-run"], { ...runtime, bridgeExecute });
  const panelCreate = await execute(["tools", "run", "ui.create-panel-settings", "Assets/UI/Panel.asset", "--reference-width", "1920", "--reference-height", "1080", "--sorting-order", "3", "--target-texture", "Assets/UI/Target.renderTexture", "--yes"], { ...runtime, bridgeExecute });
  const panelUpdate = await execute(["tools", "run", "ui.update-panel-settings", "Assets/UI/Panel.asset", "--scale-mode", "ScaleWithScreenSize", "--reference-width", "1280", "--reference-height", "720", "--sorting-layer-name", "UI", "--match", "0.5", "--dry-run"], { ...runtime, bridgeExecute });
  const visualTree = await execute(["tools", "run", "ui.get-visual-tree", "UIDoc", "--max-depth", "4", "--parent", "root", "--query", "button", "--type", "Button", "--class-name", "primary", "--page-size", "10", "--cursor", "20"], { ...runtime, bridgeExecute });
  const modifyVisual = await execute(["tools", "run", "ui.modify-visual-element", "UIDoc", "--element-name", "Root", "--add-class", "selected", "--background-color", "#ff0000", "--dry-run"], { ...runtime, bridgeExecute });
  const linkStylesheet = await execute(["tools", "run", "ui.link-stylesheet", "Assets/UI/Main.uxml", "--uss", "Assets/UI/Main.uss", "--dry-run"], { ...runtime, bridgeExecute });
  const listDocuments = await execute(["tools", "run", "ui.list-documents", "uxml", "--page-size", "20"], { ...runtime, bridgeExecute });
  const renderPreview = await execute(["tools", "run", "ui.render-preview", "Assets/UI/Main.uxml", "--output", "Assets/UI/preview.png", "--dry-run"], { ...runtime, bridgeExecute });

  assert.equal(canvas.ok, true);
  assert.equal(text.ok, true);
  assert.equal(button.ok, true);
  assert.equal(image.ok, true);
  assert.equal(documentCreate.ok, true);
  assert.equal(documentInfo.ok, true);
  assert.equal(documentUpdate.ok, true);
  assert.equal(documentDelete.ok, true);
  assert.equal(hierarchy.ok, true);
  assert.equal(attach.ok, true);
  assert.equal(detach.ok, true);
  assert.equal(panelCreate.ok, true);
  assert.equal(panelUpdate.ok, true);
  assert.equal(visualTree.ok, true);
  assert.equal(modifyVisual.ok, true);
  assert.equal(linkStylesheet.ok, true);
  assert.equal(listDocuments.ok, true);
  assert.equal(renderPreview.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.create", "ui.document.info", "ui.document.update", "ui.document.delete", "ui.hierarchy", "ui.attach-document", "ui.detach-document", "ui.create-panel-settings", "ui.update-panel-settings", "ui.get-visual-tree", "ui.modify-visual-element", "ui.link-stylesheet", "ui.list-documents", "ui.render-preview"]);
  assert.deepEqual(requests[0].args, { name: "HUD" });
  assert.deepEqual(requests[1].args, { name: "Title", parent: "HUD", text: "Start", width: 320, height: 80, x: 10, y: 20, fontSize: 32 });
  assert.deepEqual(requests[2].args, { name: "PlayButton", parent: "HUD", text: "Play" });
  assert.deepEqual(requests[3].args, { name: "Logo", parent: "HUD", sprite: "Assets/UI/logo.png", color: "#ffffff" });
  assert.deepEqual(requests[4].args, { name: "MainUIDocument", path: "Assets/UI/Main.uxml", uxml: "Assets/UI/Main.uxml", uss: "Assets/UI/Main.uss", panelSettings: "Assets/UI/Panel.asset", overwrite: true });
  assert.deepEqual(requests[5].args, { target: "MainUIDocument", uss: "Assets/UI/Main.uss" });
  assert.equal(requests[6].args.content, "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />\n");
  assert.equal(requests[6].args.dryRun, true);
  assert.equal(requests[7].dryRun, true);
  assert.deepEqual(requests[8].args, { pageSize: 25, cursor: "50", target: "HUD", query: "Canvas", type: "RectTransform", className: "screen" });
  assert.deepEqual(requests[9].args, { target: "UIDoc", sourceAsset: "Assets/UI/Main.uxml", panelSettings: "Assets/UI/Panel.asset", sortOrder: 5 });
  assert.deepEqual(requests[10].args, { target: "UIDoc", dryRun: true });
  assert.deepEqual(requests[11].args, { path: "Assets/UI/Panel.asset", referenceWidth: 1920, referenceHeight: 1080, sortingOrder: 3, targetTexture: "Assets/UI/Target.renderTexture" });
  assert.deepEqual(requests[12].args, { path: "Assets/UI/Panel.asset", scaleMode: "ScaleWithScreenSize", referenceWidth: 1280, referenceHeight: 720, sortingLayerName: "UI", match: 0.5, dryRun: true });
  assert.deepEqual(requests[13].args, { target: "UIDoc", parent: "root", query: "button", type: "Button", className: "primary", pageSize: 10, cursor: "20", maxDepth: 4 });
  assert.deepEqual(requests[14].args, { target: "UIDoc", elementName: "Root", addClass: "selected", backgroundColor: "#ff0000", dryRun: true });
  assert.deepEqual(requests[15].args, { target: "Assets/UI/Main.uxml", path: "Assets/UI/Main.uxml", uxml: "Assets/UI/Main.uxml", uss: "Assets/UI/Main.uss", dryRun: true });
  assert.deepEqual(requests[16].args, { kind: "uxml", pageSize: 20 });
  assert.deepEqual(requests[17].args, { target: "Assets/UI/Main.uxml", path: "Assets/UI/Main.uxml", uxml: "Assets/UI/Main.uxml", output: "Assets/UI/preview.png", dryRun: true });
});

test("ui document info reads UXML offline when bridge endpoint is absent", async () => {
  const runtime = await makeRuntime();
  const uxmlPath = join(runtime.dataDir, "Main.uxml");
  await writeFile(uxmlPath, "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:VisualElement name=\"Root\" /></ui:UXML>\n", "utf8");

  const result = await execute(["tools", "run", "ui.document.info", uxmlPath], runtime);

  assert.equal(result.ok, true);
  assert.equal((result.data as { mode: string }).mode, "offline-reader");
  assert.equal((result.data as { document: { kind: string } }).document.kind, "uxml");
  assert.deepEqual((result.data as { diagnostics: Array<{ code: string }> }).diagnostics.map((item) => item.code), ["UI_DOCUMENT_READ_OK"]);
});

test("ui bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "ui.document.create", "--uxml", "Assets/UI/Main.uxml", "--panel-settings", "Assets/UI/Missing.asset"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_UI_PANEL_SETTINGS_NOT_FOUND", message: "PanelSettings asset not found: Assets/UI/Missing.asset" }, nextActions: ["Create a PanelSettings asset in Unity."] })
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_UI_PANEL_SETTINGS_NOT_FOUND");
  assert.deepEqual(result.error?.next_actions, ["Create a PanelSettings asset in Unity."]);
});

test("ui target ambiguity diagnostics are preserved for parent, document and hierarchy", async () => {
  const runtime = await makeRuntime();
  const cases: Array<{ name: string; args: string[]; message: string; nextAction: string }> = [
    { name: "parent", args: ["tools", "run", "ui.text.create", "Title", "--parent", "HUD"], message: "UI parent target is ambiguous: HUD", nextAction: "Pass a hierarchy path or instance id for the intended Canvas/RectTransform parent." },
    { name: "document", args: ["tools", "run", "ui.document.info", "HUD"], message: "UIDocument target is ambiguous: HUD", nextAction: "Pass a hierarchy path or instance id for the intended UIDocument GameObject." },
    { name: "hierarchy", args: ["tools", "run", "ui.hierarchy", "HUD"], message: "UI hierarchy target is ambiguous: HUD", nextAction: "Pass a hierarchy path or instance id for the intended UI hierarchy root." }
  ];

  for (const item of cases) {
    const result = await execute(item.args, {
      ...runtime,
      bridgeExecute: async () => ({
        ok: false,
        error: {
          code: "E_TARGET_AMBIGUOUS",
          message: item.message,
          target: "HUD",
          matchCount: 2,
          matches: [
            { id: 101, name: "HUD", path: "Canvas/HUD", components: ["RectTransform"] },
            { id: 202, name: "HUD", path: "Overlay/HUD", components: ["UIDocument"] }
          ]
        },
        nextActions: [item.nextAction]
      })
    });

    assert.equal(result.ok, false, item.name);
    assert.equal(result.error?.code, "E_TARGET_AMBIGUOUS", item.name);
    assert.equal(result.error?.message, item.message, item.name);
    assert.equal(result.error?.target, "HUD", item.name);
    assert.equal(result.error?.matchCount, 2, item.name);
    assert.deepEqual(result.error?.matches, [
      { id: 101, name: "HUD", path: "Canvas/HUD", components: ["RectTransform"] },
      { id: 202, name: "HUD", path: "Overlay/HUD", components: ["UIDocument"] }
    ], item.name);
    assert.deepEqual(result.error?.next_actions, [item.nextAction], item.name);
  }
});

test("prefab bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "prefab.stage-close"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_PREFAB_DIRTY_CONFLICT", message: "Prefab Stage has unsaved changes." }, nextActions: ["Run unity_cli tools run prefab.stage-close --save."] })
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_PREFAB_DIRTY_CONFLICT");
  assert.deepEqual(result.error?.next_actions, ["Run unity_cli tools run prefab.stage-close --save."]);
});

test("go commands forward structured search and transform inputs", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { gameObject: { path: payload.args.target ?? payload.args.name }, items: [], pageInfo: { total: 0 }, plannedEffects: [{ type: payload.command }] } };
  };

  const find = await execute(["tools", "run", "go.find", "--input", JSON.stringify({ searchTerm: "Camera", searchMethod: "by_component", includeInactive: true, pageSize: 10, cursor: 20 })], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "go.create", "--input", JSON.stringify({ name: "AlignmentCube", primitive: "cube", position: { x: 1, y: 2, z: 3 }, rotation: { x: 0, y: 45, z: 0 }, scale: { x: 2, y: 2, z: 2 }, active: false })], { ...runtime, bridgeExecute });
  const modify = await execute(["tools", "run", "go.modify", "--input", JSON.stringify({ target: "AlignmentCube", newName: "AlignmentCubeRenamed", tagValue: "Untagged", layerValue: "Default", active: false })], { ...runtime, bridgeExecute });
  const batchModify = await execute(["tools", "run", "go.modify", "--input", JSON.stringify({ targets: ["Batch/A", "Batch/B"], position: { x: 4, y: 5, z: 6 }, active: true })], { ...runtime, bridgeExecute });
  const move = await execute(["tools", "run", "go.move-relative", "--input", JSON.stringify({ target: "AlignmentCubeRenamed", offset: { x: 0, y: 1, z: -2 } })], { ...runtime, bridgeExecute });
  const parent = await execute(["tools", "run", "go.set-parent", "--dry-run", "--input", JSON.stringify({ target: "AlignmentCubeRenamed", parent: "Parent", worldPositionStays: false })], { ...runtime, bridgeExecute });

  for (const result of [find, create, modify, batchModify, move, parent]) {
    assert.equal(result.ok, true);
  }
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].args, { name: "go.find", input: { pageSize: 10, cursor: 20, searchTerm: "Camera", searchMethod: "by_component", includeInactive: true } });
  assert.deepEqual(requests[1].args, { name: "go.create", input: { name: "AlignmentCube", active: false, primitive: "cube", x: 1, y: 2, z: 3, rotationX: 0, rotationY: 45, rotationZ: 0, scaleX: 2, scaleY: 2, scaleZ: 2 } });
  assert.deepEqual(requests[2].args, { name: "go.modify", input: { target: "AlignmentCube", newName: "AlignmentCubeRenamed", active: false, layerValue: "Default", tagValue: "Untagged" } });
  assert.deepEqual(requests[3].args, { name: "go.modify", input: { targets: ["Batch/A", "Batch/B"], active: true, x: 4, y: 5, z: 6 } });
  assert.deepEqual(requests[4].args, { name: "go.move-relative", input: { target: "AlignmentCubeRenamed", offsetX: 0, offsetY: 1, offsetZ: -2 } });
  assert.deepEqual(requests[5].args, { name: "go.set-parent", input: { target: "AlignmentCubeRenamed", parent: "Parent", worldPositionStays: false, dryRun: true } });
  assert.equal(requests[5].dryRun, true);
});

test("bridge errors return CLI failure envelope", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "go.inspect", "Player"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_TARGET_AMBIGUOUS", message: "Multiple targets matched." }, nextActions: ["Pass --path or --id."] })
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TARGET_AMBIGUOUS");
  assert.deepEqual(result.error?.next_actions, ["Pass --path or --id."]);
});

test("camera screenshot bridge target and IO errors preserve protocol envelopes", async () => {
  const runtime = await makeRuntime();
  const cases: Array<{ name: string; response: { code: string; message: string }; nextActions: string[] }> = [
    { name: "empty target", response: { code: "E_TARGET_NOT_FOUND", message: "Camera screenshot requires a target Camera." }, nextActions: ["Pass a Camera name, hierarchy path or instance id."] },
    { name: "missing camera", response: { code: "E_TARGET_NOT_FOUND", message: "Camera not found: Missing Camera" }, nextActions: ["Run unity_cli tools run camera.list to inspect available cameras."] },
    { name: "io failure", response: { code: "E_IO_ERROR", message: "Failed to write screenshot output." }, nextActions: ["Check output path, permissions, format parameters and UnityCliBridge.log."] }
  ];

  for (const item of cases) {
    const result = await execute(["tools", "run", "camera.screenshot", item.name === "empty target" ? "" : "Missing Camera", "--output", "Artifacts/out.png"], {
      ...runtime,
      bridgeExecute: async () => ({ ok: false, error: item.response, nextActions: item.nextActions })
    });

    assert.equal(result.ok, false, item.name);
    assert.equal(result.error?.code, item.response.code, item.name);
    assert.equal(result.error?.message, item.response.message, item.name);
    assert.deepEqual(result.error?.next_actions, item.nextActions, item.name);
  }
});

test("editor state/play/pause/stop/menu commands forward args and return envelope", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    if (payload.command === "editor.state") {
      return { ok: true, data: { command: payload.command, playMode: { isPlaying: false, isPaused: false }, activeScene: { path: "Assets/Scenes/Main.unity" } } };
    }
    if (payload.command === "editor.menu") {
      return { ok: true, data: { command: payload.command, plannedEffects: [{ type: "editor.menu", path: payload.args.path }], result: { path: payload.args.path, executed: true } } };
    }
    return { ok: true, data: { command: payload.command, plannedEffects: [{ type: payload.command }], result: { requested: payload.command.split(".")[1] } } };
  };

  const state = await execute(["tools", "run", "editor.state"], { ...runtime, bridgeExecute });
  const play = await execute(["tools", "run", "editor.play", "--timeout-ms", "2500"], { ...runtime, bridgeExecute });
  const pause = await execute(["tools", "run", "editor.pause"], { ...runtime, bridgeExecute });
  const stop = await execute(["tools", "run", "editor.stop", "--timeout", "3000"], { ...runtime, bridgeExecute });
  const menu = await execute(["tools", "run", "editor.menu", "File/Save", "--timeout-ms", "7000", "--yes"], { ...runtime, bridgeExecute });

  assert.equal(state.ok, true);
  assert.equal(play.ok, true);
  assert.equal(pause.ok, true);
  assert.equal(stop.ok, true);
  assert.equal(menu.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["editor.state", "editor.play", "editor.pause", "editor.stop", "editor.menu"]);
  assert.deepEqual(requests[1].args, { timeoutMs: 2500 });
  assert.deepEqual(requests[2].args, {});
  assert.deepEqual(requests[3].args, { timeoutMs: 3000 });
  assert.deepEqual(requests[4].args, { path: "File/Save", timeoutMs: 7000 });
});

test("editor bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const cases: Array<{ name: string; args: string[]; response: { code: string; message: string }; nextActions: string[] }> = [
    { name: "busy", args: ["tools", "run", "editor.menu", "File/Save", "--yes"], response: { code: "E_UNITY_BUSY", message: "Unity Editor is busy and cannot run this editor command right now." }, nextActions: ["Wait for compiling, asset importing, build, PlayMode switching, or domain reload to complete, then retry."] },
    { name: "playmode timeout", args: ["tools", "run", "editor.stop"], response: { code: "E_PLAYMODE_TIMEOUT", message: "Unity PlayMode did not reach the requested state before the timeout." }, nextActions: ["Wait for PlayMode switching or domain reload to finish, then retry."] },
    { name: "menu not found", args: ["tools", "run", "editor.menu", "Tools/Missing", "--yes"], response: { code: "E_MENU_NOT_FOUND", message: "Unity menu item was not found or could not be executed." }, nextActions: ["Use the exact Unity Editor menu path, such as File/Save or Assets/Refresh."] },
    { name: "menu timeout", args: ["tools", "run", "editor.menu", "Tools/Slow", "--yes"], response: { code: "E_MENU_TIMEOUT", message: "Unity menu item execution exceeded the requested timeout." }, nextActions: ["Check Unity Editor and Unity CLI Bridge logs for long-running menu work."] }
  ];

  for (const item of cases) {
    const result = await execute(item.args, {
      ...runtime,
      bridgeExecute: async () => ({ ok: false, error: item.response, nextActions: item.nextActions })
    });

    assert.equal(result.ok, false, item.name);
    assert.equal(result.error?.code, item.response.code, item.name);
    assert.deepEqual(result.error?.next_actions, item.nextActions, item.name);
  }
});

test("runtime-tool list is blocked from CLI as internal command", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "runtime-tool.list"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("runtime-tool run is blocked from CLI as internal command", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "runtime-tool.run", "sample.runtime-echo", "--yes"], runtime);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("runtime-tool list is registered in bridge command registry as internal", () => {
  const spec = bridgeCommandRegistry.lookup("runtime-tool", "list");
  assert.ok(spec, "runtime-tool list should be registered");
  assert.equal(spec.bridgeCommand, "runtime-tool.list");
  assert.equal(spec.internal, true);
});

test("runtime-tool run is registered in bridge command registry as internal", () => {
  const spec = bridgeCommandRegistry.lookup("runtime-tool", "run");
  assert.ok(spec, "runtime-tool run should be registered");
  assert.equal(spec.bridgeCommand, "runtime-tool.run");
  assert.equal(spec.destructiveHint, true);
  assert.equal(spec.internal, true);
});
