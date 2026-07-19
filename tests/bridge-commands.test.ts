import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "../src/cli.js";
import { JobRunner } from "../src/job-runner.js";
import { JobStore } from "../src/job-store.js";
import { bridgeCommandRegistry, bridgeHttpTimeoutMs, executeBridgeCommand, type BridgeRequest } from "../src/bridge/index.js";
import { ExtensionRegistry } from "../src/extensions/index.js";
import { installExtensionBundle } from "../src/extensions/installer.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-bridge-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("bridge HTTP timeout uses only the immutable absolute deadline remainder", () => {
  const request: BridgeRequest = {
    protocolVersion: 2,
    requestId: "req_timeout",
    instanceId: `unity-${"0".repeat(64)}`,
    command: "context.read",
    input: {},
    execution: {
      absoluteDeadlineUtc: "1970-01-01T00:01:00.000Z",
      completionContractVersion: 1,
      requestDigestVersion: 1,
    },
  };
  assert.equal(bridgeHttpTimeoutMs(request, 55_000), 5_000);
  assert.equal(bridgeHttpTimeoutMs(request, 60_001), 0);
});

test("CLI help exposes strict instance selection and no endpoint or active-target path", async () => {
  const result = await execute(["--help"]);
  assert.ok(result.ok);
  const commands = (result.data as { commands: string[] }).commands;
  assert.ok(commands.some((command) => command.includes("tools run <name>") && command.includes("--instance <id|projectPath|alias>")));
  assert.ok(commands.some((command) => command.includes("context read <name>") && command.includes("--instance <id|projectPath|alias>") && command.includes("--input-file <path>")));
  assert.ok(commands.includes("target alias set <projectPath|instanceId> <alias> [--force]"));
  assert.ok(!commands.some((command) => /bridge-endpoint|target current|target set|target clear/.test(command)));
});

test("editor.state is rejected as a direct Tool before transport execution", async () => {
  const runtime = await makeRuntime();
  let bridgeCalls = 0;
  const result = await execute(["tools", "run", "editor.state"], {
    ...runtime,
    bridgeExecute: async () => {
      bridgeCalls += 1;
      throw new Error("This operation was aborted");
    }
  });
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
  assert.equal(bridgeCalls, 0);
});

test("tools run keeps --target only as GameObject business input", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "go.inspect", "--target", "Main Camera", "--input", "{\"target\":\"Main Camera\"}"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { gameObject: { name: "Main Camera" } } };
    }
  });
  assert.ok(result.ok);
  assert.doesNotMatch(JSON.stringify(result.data), /bridgeEndpoint|listenerId|actualPort/);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input!, { name: "go.inspect", input: { target: "Main Camera" } });
});
test("query command forwards stable bridge envelope", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "scene.hierarchy", "--page-size", "50"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { items: [{ path: "Main Camera" }], pageInfo: { pageSize: payload.input!.pageSize } } };
    }
  });

  assert.ok(result.ok);
  assert.deepEqual(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input!, { name: "scene.hierarchy", input: { pageSize: 50 } });
  assert.equal(((result.data as { items: Array<{ path: string }> }).items[0]).path, "Main Camera");
});

test("console get forwards filter and cursor pagination", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "console.get", "--type", "error", "--filter", "AlignmentProbe", "--count", "2", "--cursor", "4"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, items: [], pageInfo: { pageSize: payload.input!.count, cursor: payload.input!.cursor } } };
    }
  });

  assert.ok(result.ok);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input!, { name: "console.get", input: { type: "error", filter: "AlignmentProbe", count: 2, cursor: 4 } });
});

test("console.get forwards structured --input fields", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "console.get", "--input", JSON.stringify({ type: "warning", search: "CS", count: 3, cursor: 6 })], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, items: [], pageInfo: { pageSize: payload.input!.count, cursor: payload.input!.cursor } } };
    }
  });

  assert.ok(result.ok);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input!, { name: "console.get", input: { type: "warning", filter: "CS", count: 3, cursor: 6 } });
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
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command } };
    }
  });

  assert.ok(result.ok);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input!, { name: "controller.add-transition", input: { ...input, layerIndex: 0 } });
});

test("controller transition rejects PowerShell-escaped nested condition arrays without a recovery parser", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const escapedInput = "{\\controllerPath\\:\\Assets/UnityCliAlignmentFixtures/Alignment.controller\\,\\fromState\\:\\Idle\\,\\toState\\:\\Run\\,\\hasExitTime\\:false,\\conditions\\:[{\\parameter\\:\\Speed\\,\\mode\\:\\greater\\,\\threshold\\:0.1}]}";

  const result = await execute(["tools", "run", "controller.add-transition", "--yes", "--input", escapedInput], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command } };
    }
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_TOOL_INPUT_JSON_INVALID");
  assert.deepEqual(requests, []);
});

test("edit command forwards target and plannedEffects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "component.set", "Player", "--component", "Camera", "--property", "enabled", "--value", "false"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      const input = (payload.input!.input ?? {}) as Record<string, unknown>;
      return { ok: true as const, requestId: payload.requestId, data: { plannedEffects: [{ type: payload.input!.name, property: input.property }] } };
    }
  });

  assert.ok(result.ok);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input!, { name: "component.set", input: { target: "Player", component: "Camera", property: "enabled", value: "false" } });
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
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command } };
    }
  });
  assert.ok(addResult.ok);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input!, { name: "component.add", input: addInput });

  const setInput = { target: "UnityCliAlignmentProbe", component: "Rigidbody", componentIndex: 1, properties: { mass: 5, useGravity: false } };
  const setResult = await execute(["tools", "run", "component.set", "--input", JSON.stringify(setInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command } };
    }
  });
  assert.ok(setResult.ok);
  assert.equal(requests[1].command, "project-tool.run");
  assert.deepEqual(requests[1].input!, { name: "component.set", input: setInput });

  const singleInput = { target: "Main Camera", component: "Camera", property: "enabled", value: false };
  const singleResult = await execute(["tools", "run", "component.set", "--input", JSON.stringify(singleInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command } };
    }
  });
  assert.ok(singleResult.ok);
  assert.equal(requests[2].command, "project-tool.run");
  assert.deepEqual(requests[2].input!, { name: "component.set", input: singleInput });

  const getInput = { target: "Main Camera", component: "Camera", paths: ["layerCullDistances"], maxArrayItems: 5 };
  const getResult = await execute(["tools", "run", "component.get", "--input", JSON.stringify(getInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command } };
    }
  });
  assert.ok(getResult.ok);
  assert.equal(requests[3].command, "project-tool.run");
  assert.deepEqual(requests[3].input!, { name: "component.get", input: { path: "Main Camera", component: "Camera", paths: ["layerCullDistances"], maxArrayItems: 5 } });

  const removeInput = { target: "UnityCliAlignmentProbe", component: "BoxCollider", componentIndex: 0 };
  const removeResult = await execute(["tools", "run", "component.remove", "--input", JSON.stringify(removeInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command } };
    }
  });
  assert.ok(removeResult.ok);
  assert.equal(requests[4].command, "project-tool.run");
  assert.deepEqual(requests[4].input!, { name: "component.remove", input: removeInput });

  const batchRemoveInput = { target: "UnityCliAlignmentProbe", components: ["BoxCollider", "Rigidbody"] };
  const batchRemoveResult = await execute(["tools", "run", "component.remove", "--input", JSON.stringify(batchRemoveInput)], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command } };
    }
  });
  assert.ok(batchRemoveResult.ok);
  assert.equal(requests[5].command, "project-tool.run");
  assert.deepEqual(requests[5].input!, { name: "component.remove", input: batchRemoveInput });
});

test("tools run parses valid JSON once and rejects PowerShell-escaped pseudo-JSON", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, path: payload.input!.path } };
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

  assert.ok(read.ok);
  assert.ok(!pwshEscaped.ok);
  assert.ok(!backslashQuoted.ok);
  assert.equal(pwshEscaped.error?.code, "E_TOOL_INPUT_JSON_INVALID");
  assert.equal(backslashQuoted.error?.code, "E_TOOL_INPUT_JSON_INVALID");
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "material.info", input: { path: "Assets/Materials/Probe.mat" } });
});

test("tools run rejects invalid --input JSON before bridge execution", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["tools", "run", "material.info", "--input", "{not-json"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: {} };
    }
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_TOOL_INPUT_JSON_INVALID");
  assert.deepEqual(requests, []);
});

test("screenshot commands forward output and surround arguments", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["tools", "run", "screenshot.scene-view", "Artifacts/after.png", "--width", "1280", "--height", "720", "--inline-base64", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { screenshotPath: "Artifacts/after.png", plannedEffects: [{ type: payload.command, path: "Artifacts/after.png" }] } };
    }
  });
  const surround = await execute(["tools", "run", "screenshot.surround", "Player", "--output-dir", "Assets/Shots", "--contact-sheet", "Assets/Shots/contact.png", "--width", "320", "--height", "240", "--radius", "6.5", "--fov", "55", "--inline-base64", "--dry-run"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, dryRun: payload.execution?.dryRun, plannedArtifacts: [] } };
    }
  });

  assert.ok(result.ok);
  assert.ok(surround.ok);
  assert.equal(requests[0].command, "project-tool.run");
  assert.deepEqual(requests[0].input!, { name: "screenshot.scene-view", input: { path: "Artifacts/after.png", width: 1280, height: 720, inlineBase64: true } });
  assert.equal((result.data as { screenshotPath: string }).screenshotPath, "Artifacts/after.png");
  assert.equal(requests[1].command, "project-tool.run");
  assert.deepEqual(requests[1].input!, { name: "screenshot.surround", input: { target: "Player", "output-dir": "Assets/Shots", contactSheetPath: "Assets/Shots/contact.png", width: 320, height: 240, radius: 6.5, fov: 55, inlineBase64: true, dryRun: true } });
});

test("build commands forward settings and dry-run arguments", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, plannedEffects: payload.execution?.dryRun ? [{ type: payload.command }] : [] } };
  };

  const scenes = await execute(["tools", "run", "build.scene.add", "--input", "{\"path\":\"Assets/Scenes/Main.unity\"}", "--dry-run"], { ...runtime, bridgeExecute });
  const platform = await execute(["tools", "run", "build.platform.set", "--input", "{\"target\":\"StandaloneWindows64\"}", "--dry-run"], { ...runtime, bridgeExecute });
  const profiles = await execute(["tools", "run", "build.profile.create", "--input", "{\"path\":\"Assets/BuildProfiles/Windows.asset\",\"name\":\"Windows\"}", "--dry-run"], { ...runtime, bridgeExecute });
  const settings = await execute(["tools", "run", "build.settings.get", "--input", "{}"], { ...runtime, bridgeExecute });

  assert.ok(scenes.ok);
  assert.ok(platform.ok);
  assert.ok(profiles.ok);
  assert.ok(settings.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "build.scene.add", input: { path: "Assets/Scenes/Main.unity" } });
  assert.deepEqual(requests[1].input!, { name: "build.platform.set", input: { target: "StandaloneWindows64" } });
  assert.deepEqual(requests[2].input!, { name: "build.profile.create", input: { path: "Assets/BuildProfiles/Windows.asset", name: "Windows" } });
  assert.deepEqual(requests[3].input!, { name: "build.settings.get", input: {} });
});

test("camera commands forward target and screenshot options", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const list = await execute(["tools", "run", "camera.list"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { cameras: [{ name: "Main Camera" }] } };
    }
  });

  const info = await execute(["tools", "run", "camera.info", "Main Camera"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { camera: { name: payload.input!.target } } };
    }
  });

  const screenshot = await execute(["tools", "run", "camera.screenshot", "Main Camera", "--output", "Artifacts/camera.png", "--format", "png", "--width", "1920", "--height", "1080", "--transparent", "--overwrite", "--quality", "90", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { screenshotPath: payload.input!.path, format: payload.input!.format, width: payload.input!.width, height: payload.input!.height } };
    }
  });

  assert.ok(list.ok);
  assert.ok(info.ok);
  assert.ok(screenshot.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[1].input!, { name: "camera.info", input: { target: "Main Camera" } });
  assert.deepEqual(requests[2].input!, { name: "camera.screenshot", input: { path: "Artifacts/camera.png", format: "png", width: 1920, height: 1080, transparent: true, quality: 90, overwrite: true, target: "Main Camera" } });
});

test("camera mutation tools forward structured --input fields", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { command: payload.command, plannedEffects: [{ type: payload.command }] } };
  };

  const create = await execute(["tools", "run", "camera.create", "--input", "{\"name\":\"AlignmentCam\",\"position\":\"0,2,-6\",\"rotation\":\"15,0,0\",\"fieldOfView\":60,\"orthographic\":false,\"orthographicSize\":5,\"clearFlags\":\"SolidColor\",\"depth\":2,\"tag\":\"MainCamera\"}"], { ...runtime, bridgeExecute });
  const target = await execute(["tools", "run", "camera.target", "--input", "{\"target\":\"AlignmentCam\",\"lookAt\":\"UnityCliAlignmentProbe\",\"position\":\"0,3,-8\",\"rotation\":\"20,0,0\"}"], { ...runtime, bridgeExecute });
  const lens = await execute(["tools", "run", "camera.lens", "--input", "{\"target\":\"AlignmentCam\",\"fieldOfView\":45,\"orthographic\":true,\"orthographicSize\":10,\"nearClipPlane\":0.1,\"farClipPlane\":500,\"clearFlags\":\"Skybox\",\"depth\":3}"], { ...runtime, bridgeExecute });
  const multiview = await execute(["tools", "run", "camera.multiview", "--input", "{\"targets\":\"Main Camera,AlignmentCam\",\"output\":\"Artifacts/cameras\",\"width\":320,\"height\":180,\"format\":\"png\"}", "--yes"], { ...runtime, bridgeExecute });

  for (const result of [create, target, lens, multiview]) {
    assert.ok(result.ok);
  }
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "camera.create", input: { name: "AlignmentCam", position: "0,2,-6", rotation: "15,0,0", fieldOfView: 60, orthographic: false, orthographicSize: 5, clearFlags: "SolidColor", depth: 2, tag: "MainCamera" } });
  assert.deepEqual(requests[1].input!, { name: "camera.target", input: { target: "AlignmentCam", lookAt: "UnityCliAlignmentProbe", position: "0,3,-8", rotation: "20,0,0" } });
  assert.deepEqual(requests[2].input!, { name: "camera.lens", input: { target: "AlignmentCam", fieldOfView: 45, orthographic: true, orthographicSize: 10, nearClipPlane: 0.1, farClipPlane: 500, clearFlags: "Skybox", depth: 3 } });
  assert.deepEqual(requests[3].input!, { name: "camera.multiview", input: { targets: "Main Camera,AlignmentCam", output: "Artifacts/cameras", width: 320, height: 180, format: "png" } });
});

test("prefab commands forward paths, pagination, modify args and planned effects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    const input = (payload.input!.input ?? {}) as Record<string, unknown>;
    return { ok: true as const, requestId: payload.requestId, data: { prefab: { path: input.path }, plannedEffects: payload.input!.name === "prefab.create" ? [{ type: payload.input!.name, path: input.path }] : [] } };
  };

  const info = await execute(["tools", "run", "prefab.info", "Assets/Prefabs/Player.prefab"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "prefab.create", "Player", "--path", "Assets/Prefabs/Player.prefab", "--overwrite", "--yes"], { ...runtime, bridgeExecute });
  const modify = await execute(["tools", "run", "prefab.modify", "Assets/Prefabs/Player.prefab", "--name", "Hero", "--add-child", "Socket", "--remove-child", "OldSocket", "--overwrite", "--yes"], { ...runtime, bridgeExecute });
  const open = await execute(["tools", "run", "prefab.stage-open", "Assets/Prefabs/Player.prefab"], { ...runtime, bridgeExecute });
  const hierarchy = await execute(["tools", "run", "prefab.hierarchy", "Assets/Prefabs/Player.prefab", "--page-size", "25", "--cursor", "50", "--parent", "Player", "--query", "Socket", "--component-type", "Transform"], { ...runtime, bridgeExecute });
  const save = await execute(["tools", "run", "prefab.stage-save", "--yes"], { ...runtime, bridgeExecute });
  const close = await execute(["tools", "run", "prefab.stage-close"], { ...runtime, bridgeExecute });

  assert.ok(info.ok);
  assert.ok(create.ok);
  assert.ok(modify.ok);
  assert.ok(open.ok);
  assert.ok(hierarchy.ok);
  assert.ok(save.ok);
  assert.ok(close.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "prefab.info", input: { path: "Assets/Prefabs/Player.prefab" } });
  assert.deepEqual(requests[1].input!, { name: "prefab.create", input: { source: "Player", path: "Assets/Prefabs/Player.prefab", overwrite: true } });
  assert.deepEqual(requests[2].input!, { name: "prefab.modify", input: { path: "Assets/Prefabs/Player.prefab", name: "Hero", addChild: "Socket", removeChild: "OldSocket", overwrite: true } });
  assert.deepEqual(requests[4].input!, { name: "prefab.hierarchy", input: { pageSize: 25, cursor: "50", path: "Assets/Prefabs/Player.prefab", parent: "Player", query: "Socket", componentType: "Transform" } });
  assert.deepEqual(requests[5].input!, { name: "prefab.stage-save", input: {} });
  assert.deepEqual(requests[6].input!, { name: "prefab.stage-close", input: {} });
  assert.deepEqual((create.data as { plannedEffects: unknown[] }).plannedEffects, [{ type: "prefab.create", path: "Assets/Prefabs/Player.prefab" }]);
});

test("destructive bridge commands require --yes before execution", async () => {
  const runtime = await makeRuntime();
  let called = false;

  const bridgeExecute = async (payload: BridgeRequest) => {
    called = true;
    return { ok: true as const, requestId: payload.requestId, data: {} };
  };
  const assetResult = await execute(["tools", "run", "asset.delete", "Assets/DeleteMe.asset"], { ...runtime, bridgeExecute });
  const prefabResult = await execute(["tools", "run", "prefab.create", "--input", "{\"path\":\"Assets/Prefabs/Player.prefab\",\"source\":\"Player\"}"], { ...runtime, bridgeExecute });

  for (const result of [assetResult, prefabResult]) {
    assert.ok(!result.ok);
    assert.equal(result.error?.code, "E_DESTRUCTIVE_CONFIRMATION_REQUIRED");
  }
  assert.equal(called, false);
});

test("destructive bridge commands pass dryRun and security proof when explicitly allowed", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const confirmed = await execute(["tools", "run", "asset.delete", "Assets/DeleteMe.asset", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { plannedEffects: [] } };
    }
  });
  const prefabConfirmed = await execute(["tools", "run", "prefab.create", "--input", "{\"path\":\"Assets/Prefabs/Player.prefab\",\"source\":\"Player\"}", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { plannedEffects: [] } };
    }
  });
  const dryRun = await execute(["tools", "run", "asset.delete", "Assets/DeleteMe.asset", "--dry-run"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true as const, requestId: payload.requestId, data: { dryRun: payload.execution?.dryRun } };
    }
  });

  assert.ok(confirmed.ok);
  assert.equal(requests[0].command, "project-tool.run");
  assert.equal(requests[0].input!.name, "asset.delete");
  assert.equal(requests[0].execution.securityProof?.confirmed, true);
  assert.equal(requests[0].execution.securityProof?.toolName, "asset.delete");
  assert.match(requests[0].execution.securityProof?.descriptorFingerprint ?? "", /^[a-f0-9]{64}$/);
  assert.match(requests[0].execution.securityProof?.inputSha256 ?? "", /^[a-f0-9]{64}$/);
  assert.ok(prefabConfirmed.ok);
  assert.equal(requests[1].input!.name, "prefab.create");
  assert.equal(requests[1].execution.securityProof?.confirmed, true);
  assert.match(requests[1].execution.securityProof?.descriptorFingerprint ?? "", /^[a-f0-9]{64}$/);
  assert.ok(dryRun.ok);
  assert.equal(requests[2].command, "project-tool.run");
  assert.equal(requests[2].input!.name, "asset.delete");
  assert.equal(requests[2].execution?.dryRun, true);
  assert.equal((requests[2].input!.input as Record<string, unknown>).dryRun, true);
  assert.equal(requests[2].execution.securityProof?.confirmed, false);
  assert.equal(requests[2].execution.securityProof?.toolName, "asset.delete");
  assert.match(requests[2].execution.securityProof?.descriptorFingerprint ?? "", /^[a-f0-9]{64}$/);
  assert.match(requests[2].execution.securityProof?.inputSha256 ?? "", /^[a-f0-9]{64}$/);
});

 test("asset commands forward CRUD and move-style envelopes", async () => {
  const runtime = await makeRuntime();
  const contentFile = join(runtime.dataDir, "asset.txt");
  await writeFile(contentFile, "hello", "utf8");
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { asset: { path: payload.input!.path ?? payload.input!.destination }, plannedEffects: [{ type: payload.command }] } };
  };

  const search = await execute(["tools", "run", "asset.search", "Player", "--type", "Prefab", "--page-size", "10"], { ...runtime, bridgeExecute });
  const info = await execute(["tools", "run", "asset.info", "Assets/Prefabs/Player.prefab"], { ...runtime, bridgeExecute });
  const components = await execute(["tools", "run", "asset.get-components", "Assets/Prefabs/Player.prefab", "--page-size", "25", "--query", "Camera", "--component-type", "Transform"], { ...runtime, bridgeExecute });
  const refresh = await execute(["tools", "run", "asset.refresh", "Assets/Generated", "--force-update", "--dry-run"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "asset.create", "Assets/Generated/config.json", "--content", "{}", "--overwrite", "--yes"], { ...runtime, bridgeExecute });
  const mkdir = await execute(["tools", "run", "asset.create", "Assets/Generated", "--yes"], { ...runtime, bridgeExecute });
  const imported = await execute(["tools", "run", "asset.import", "Assets/Generated/asset.txt", "--source", contentFile, "--overwrite", "--yes"], { ...runtime, bridgeExecute });
  const modify = await execute(["tools", "run", "asset.modify", "Assets/Generated/config.json", "--content-file", contentFile, "--yes"], { ...runtime, bridgeExecute });
  const duplicate = await execute(["tools", "run", "asset.duplicate", "Assets/Generated/config.json", "--to", "Assets/Generated/config-copy.json", "--yes"], { ...runtime, bridgeExecute });
  const move = await execute(["tools", "run", "asset.move", "Assets/Generated/config-copy.json", "--to", "Assets/Generated/config-moved.json", "--yes"], { ...runtime, bridgeExecute });
  const rename = await execute(["tools", "run", "asset.rename", "Assets/Generated/config-moved.json", "--name", "config-renamed.json", "--yes"], { ...runtime, bridgeExecute });
  const deleted = await execute(["tools", "run", "asset.delete", "Assets/Generated/config-renamed.json", "--yes"], { ...runtime, bridgeExecute });

  for (const result of [search, info, components, refresh, create, mkdir, imported, modify, duplicate, move, rename, deleted]) {
    assert.ok(result.ok);
  }
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests.map((request) => request.input!.name), ["asset.search", "asset.info", "asset.get-components", "asset.refresh", "asset.create", "asset.create", "asset.import", "asset.modify", "asset.duplicate", "asset.move", "asset.rename", "asset.delete"]);
  assert.deepEqual(requests[0].input!.input, { pageSize: 10, target: "Player", query: "Player", type: "Prefab" });
  assert.deepEqual(requests[2].input!.input, { assetPath: "Assets/Prefabs/Player.prefab", query: "Camera", componentType: "Transform", pageSize: 25 });
  assert.deepEqual(requests[3].input!.input, { path: "Assets/Generated", forceUpdate: true });
  assert.equal(requests[3].execution?.dryRun, true);
  assert.deepEqual(requests[4].input!.input, { path: "Assets/Generated/config.json", content: "{}", overwrite: true });
  assert.deepEqual(requests[5].input!.input, { path: "Assets/Generated" });
  assert.deepEqual(requests[6].input!.input, { path: "Assets/Generated/asset.txt", source: contentFile, overwrite: true });
  assert.deepEqual(requests[7].input!.input, { path: "Assets/Generated/config.json", content: "hello" });
  assert.deepEqual(requests[8].input!.input, { path: "Assets/Generated/config.json", destination: "Assets/Generated/config-copy.json" });
  assert.deepEqual(requests[9].input!.input, { path: "Assets/Generated/config-copy.json", destination: "Assets/Generated/config-moved.json" });
  assert.deepEqual(requests[10].input!.input, { path: "Assets/Generated/config-moved.json", name: "config-renamed.json" });
  assert.deepEqual(requests[11].input!.input, { path: "Assets/Generated/config-renamed.json" });
  assert.equal(requests[11].execution.securityProof?.toolName, "asset.delete");
  assert.match(requests[11].execution.securityProof?.descriptorFingerprint ?? "", /^[a-f0-9]{64}$/);
  assert.match(requests[11].execution.securityProof?.inputSha256 ?? "", /^[a-f0-9]{64}$/);
});

test("asset mutation tools forward structured --input fields", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { asset: { path: payload.input!.path ?? payload.input!.destination }, plannedEffects: [{ type: payload.command }] } };
  };

  const create = await execute(["tools", "run", "asset.create", "--input", "{\"path\":\"Assets/Generated/config.json\",\"type\":\"file\",\"content\":\"{}\",\"overwrite\":true}", "--yes"], { ...runtime, bridgeExecute });
  const duplicate = await execute(["tools", "run", "asset.duplicate", "--input", "{\"source\":\"Assets/Generated/config.json\",\"destination\":\"Assets/Generated/config-copy.json\"}", "--yes"], { ...runtime, bridgeExecute });
  const rename = await execute(["tools", "run", "asset.rename", "--input", "{\"path\":\"Assets/Generated/config-copy.json\",\"name\":\"config-renamed.json\"}", "--yes"], { ...runtime, bridgeExecute });
  const deleted = await execute(["tools", "run", "asset.delete", "--input", "{\"path\":\"Assets/Generated/config-renamed.json\"}", "--dry-run"], { ...runtime, bridgeExecute });

  for (const result of [create, duplicate, rename, deleted]) {
    assert.ok(result.ok);
  }
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests.map((request) => request.input!.name), ["asset.create", "asset.duplicate", "asset.rename", "asset.delete"]);
  assert.deepEqual(requests[0].input!.input, { path: "Assets/Generated/config.json", type: "file", content: "{}", overwrite: true });
  assert.deepEqual(requests[1].input!.input, { path: "Assets/Generated/config.json", source: "Assets/Generated/config.json", destination: "Assets/Generated/config-copy.json" });
  assert.deepEqual(requests[2].input!.input, { path: "Assets/Generated/config-copy.json", name: "config-renamed.json" });
  assert.deepEqual(requests[3].input!.input, { path: "Assets/Generated/config-renamed.json", dryRun: true });
  assert.equal(requests[3].execution?.dryRun, true);
});

test("material commands forward asset paths, target, values and planned effects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    const input = (payload.input!.input ?? {}) as Record<string, unknown>;
    return { ok: true as const, requestId: payload.requestId, data: { material: { path: input.path ?? input.material }, plannedEffects: payload.input!.name === "material.info" ? [] : [{ type: payload.input!.name, path: input.path ?? input.material }] } };
  };

  const info = await execute(["tools", "run", "material.info", "Assets/Materials/Player.mat"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "material.create", "Assets/Materials/Player.mat", "--shader", "Standard", "--overwrite", "--yes"], { ...runtime, bridgeExecute });
  const assign = await execute(["tools", "run", "material.assign", "--target", "Canvas/Button", "--material", "Assets/Materials/Player.mat", "--slot", "1"], { ...runtime, bridgeExecute });
  const set = await execute(["tools", "run", "material.set", "Assets/Materials/Player.mat", "--property", "_Metallic", "--input", "{\"value\":0.8,\"type\":\"float\"}", "--yes"], { ...runtime, bridgeExecute });
  const rendererColor = await execute(["tools", "run", "material.set-renderer-color", "Player", "--color", "#ff00ffff", "--slot", "0", "--dry-run"], { ...runtime, bridgeExecute });

  assert.ok(info.ok);
  assert.ok(create.ok);
  assert.ok(assign.ok);
  assert.ok(set.ok);
  assert.ok(rendererColor.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "material.info", input: { path: "Assets/Materials/Player.mat" } });
  assert.deepEqual(requests[1].input!, { name: "material.create", input: { path: "Assets/Materials/Player.mat", shader: "Standard", overwrite: true } });
  assert.deepEqual(requests[2].input!, { name: "material.assign", input: { target: "Canvas/Button", material: "Assets/Materials/Player.mat", slot: 1 } });
  assert.deepEqual(requests[3].input!, { name: "material.set", input: { path: "Assets/Materials/Player.mat", property: "_Metallic", value: "0.8", type: "float" } });
  assert.deepEqual(requests[4].input!, { name: "material.set-renderer-color", input: { target: "Player", color: "#ff00ffff", slot: 0, dryRun: true } });
  assert.deepEqual((set.data as { plannedEffects: unknown[] }).plannedEffects, [{ type: "material.set", path: "Assets/Materials/Player.mat" }]);
});


test("material bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "material.set", "Assets/Materials/Player.mat", "--property", "_Missing", "--value", "1", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { code: "E_MATERIAL_PROPERTY_NOT_FOUND", message: "Material property not found: _Missing", nextActions: ["Run unity_cli tools run material.info --input '{\"path\":\"<path>\"}' to inspect shader properties."], transient: false, retryAfterMs: 0 } })
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_MATERIAL_PROPERTY_NOT_FOUND");
  assert.deepEqual(result.error?.nextActions, ["Run unity_cli tools run material.info --input '{\"path\":\"<path>\"}' to inspect shader properties."]);
});

test("graphics commands forward settings, pipeline info, and volume inputs", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { graphics: { renderPipeline: { active: "Built-in" } }, plannedEffects: payload.input! } };
  };

  const quality = await execute(["tools", "run", "graphics.quality", "--input", JSON.stringify({ level: "High", vSyncCount: 1, shadowDistance: 75.5, softParticles: false }), "--yes"], { ...runtime, bridgeExecute });
  const settings = await execute(["tools", "run", "graphics.settings", "--render-pipeline", "Assets/Settings/URP.asset", "--anti-aliasing", "4", "--yes"], { ...runtime, bridgeExecute });
  const pipelineQuality = await execute(["tools", "run", "graphics.pipeline-quality", "--input", JSON.stringify({ level: "Medium" }), "--yes"], { ...runtime, bridgeExecute });
  const pipelineSettings = await execute(["tools", "run", "graphics.pipeline-settings", "--input", JSON.stringify({ renderPipeline: "built-in" }), "--yes"], { ...runtime, bridgeExecute });
  const pipelineInfo = await execute(["tools", "run", "graphics.pipeline-info"], { ...runtime, bridgeExecute });
  const volume = await execute(["tools", "run", "graphics.volume", "Global Volume"], { ...runtime, bridgeExecute });

  for (const result of [quality, settings, pipelineQuality, pipelineSettings, pipelineInfo, volume]) {
    assert.ok(result.ok);
  }
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "graphics.quality", input: { qualityLevel: "High", vSyncCount: 1, shadowDistance: 75.5, softParticles: false } });
  assert.deepEqual(requests[1].input!, { name: "graphics.settings", input: { antiAliasing: 4, renderPipeline: "Assets/Settings/URP.asset" } });
  assert.deepEqual(requests[2].input!, { name: "graphics.pipeline-quality", input: { qualityLevel: "Medium" } });
  assert.deepEqual(requests[3].input!, { name: "graphics.pipeline-settings", input: { renderPipeline: "built-in" } });
  assert.deepEqual(requests[4].input!, { name: "graphics.pipeline-info", input: {} });
  assert.deepEqual(requests[5].input!, { name: "graphics.volume", input: { target: "Global Volume" } });
});

test("physics commands forward settings, queries, joint and material assignment inputs", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { physics: {}, plannedEffects: payload.input!.name === "physics.raycast" ? [] : [{ type: payload.input!.name ?? payload.command }] } };
  };

  const settings = await execute(["tools", "run", "physics.settings", "--gravity-y", "-9.81", "--gravity2d-y", "-12.5", "--queries-hit-triggers", "false", "--default-solver-iterations", "8", "--yes"], { ...runtime, bridgeExecute });
  const raycast = await execute(["tools", "run", "physics.raycast", "--mode", "2d", "--origin-x", "1", "--origin-y", "2", "--direction-x", "0", "--direction-y", "-1", "--distance", "50", "--layer-mask", "5", "--all"], { ...runtime, bridgeExecute });
  const shapecast = await execute(["tools", "run", "physics.shapecast", "--mode", "2d", "--shape", "capsule", "--origin-x", "1", "--origin-y", "2", "--direction-x", "1", "--direction-y", "0", "--distance", "3.5", "--radius", "0.5", "--height", "2", "--capsule-direction", "horizontal", "--angle", "15"], { ...runtime, bridgeExecute });
  const assign = await execute(["tools", "run", "physics.assign-material", "--target", "Ball", "--path", "Assets/Physics/Bouncy.physicMaterial", "--collider", "BoxCollider", "--collider-index", "1", "--dry-run"], { ...runtime, bridgeExecute });
  const joint = await execute(["tools", "run", "physics.joint.remove", "--input", "{\"target\":\"Ball\",\"type\":\"FixedJoint\",\"componentIndex\":0}"], { ...runtime, bridgeExecute });
  const simulation = await execute(["tools", "run", "physics.simulation", "--steps", "3", "--delta-time", "0.016", "--simulate"], { ...runtime, bridgeExecute });

  assert.ok(settings.ok);
  assert.ok(raycast.ok);
  assert.ok(shapecast.ok);
  assert.ok(assign.ok);
  assert.ok(joint.ok);
  assert.equal(simulation.ok, false);
  assert.equal(simulation.error?.code, "E_DIRECT_BUSINESS_PROOF_UNAVAILABLE");
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "physics.settings", input: { gravityY: -9.81, gravity2dY: -12.5, defaultSolverIterations: 8, queriesHitTriggers: false } });
  assert.deepEqual(requests[1].input!, { name: "physics.raycast", input: { mode: "2d", originX: 1, originY: 2, directionX: 0, directionY: -1, distance: 50, layerMask: 5, all: true } });
  assert.deepEqual(requests[2].input!, { name: "physics.shapecast", input: { shape: "capsule", radius: 0.5, height: 2, capsuleDirection: "horizontal", angle: 15, mode: "2d", originX: 1, originY: 2, directionX: 1, directionY: 0, distance: 3.5 } });
  assert.deepEqual(requests[3].input!, { name: "physics.assign-material", input: { target: "Ball", materialPath: "Assets/Physics/Bouncy.physicMaterial", path: "Assets/Physics/Bouncy.physicMaterial", collider: "BoxCollider", colliderIndex: 1 } });
  assert.equal(requests[3].execution?.dryRun, true);
  assert.deepEqual(requests[4].input!, { name: "physics.joint.remove", input: { target: "Ball", type: "FixedJoint", componentIndex: 0 } });
});

test("profiler commands forward session, counter, memory, frame and export args", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { profiler: { enabled: payload.command === "profiler.start" }, counters: [], memory: {}, frame: {}, artifactPath: payload.input!.path, plannedEffects: [{ type: payload.command }] } };
  };

  const status = await execute(["tools", "run", "profiler.status"], { ...runtime, bridgeExecute });
  const start = await execute(["tools", "run", "profiler.start", "--deep-profiling"], { ...runtime, bridgeExecute });
  const pause = await execute(["tools", "run", "profiler.pause", "--paused", "true"], { ...runtime, bridgeExecute });
  const counter = await execute(["tools", "run", "profiler.counter", "--category", "memory", "--counter", "MonoUsedSizeLong"], { ...runtime, bridgeExecute });
  const memory = await execute(["tools", "run", "profiler.memory"], { ...runtime, bridgeExecute });
  const frame = await execute(["tools", "run", "profiler.frame", "--module", "CPU Usage"], { ...runtime, bridgeExecute });
  const capture = await execute(["tools", "run", "profiler.capture", "Artifacts/profile.json", "--duration-ms", "100", "--frames", "2", "--format", "json", "--overwrite", "--yes"], { ...runtime, bridgeExecute });
  const exportResult = await execute(["tools", "run", "profiler.export", "Artifacts/profile-export.json", "--format", "json", "--overwrite", "--yes"], { ...runtime, bridgeExecute });
  const stop = await execute(["tools", "run", "profiler.stop"], { ...runtime, bridgeExecute });

  assert.ok(status.ok);
  assert.ok(start.ok);
  assert.ok(pause.ok);
  assert.ok(counter.ok);
  assert.ok(memory.ok);
  assert.ok(frame.ok);
  assert.ok(capture.ok);
  assert.ok(exportResult.ok);
  assert.ok(stop.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[1].input!, { name: "profiler.start", input: { deepProfiling: true } });
  assert.deepEqual(requests[2].input!, { name: "profiler.pause", input: { paused: true } });
  assert.deepEqual(requests[3].input!, { name: "profiler.counter", input: { counter: "MonoUsedSizeLong", category: "memory" } });
  assert.deepEqual(requests[4].input!, { name: "profiler.memory", input: {} });
  assert.deepEqual(requests[5].input!, { name: "profiler.frame", input: { module: "CPU Usage" } });
  assert.deepEqual(requests[6].input!, { name: "profiler.capture", input: { path: "Artifacts/profile.json", format: "json", durationMs: 100, frames: 2, overwrite: true } });
  assert.deepEqual(requests[7].input!, { name: "profiler.export", input: { path: "Artifacts/profile-export.json", format: "json", overwrite: true } });
});

test("animation and vfx commands forward Windows paths, targets and effects", async () => {
  const vfxNames = ["vfx.graph.create", "vfx.effect.add", "vfx.line-create", "vfx.line-positions", "vfx.trail.create"];
  const projectPath = await mkdtemp(join(tmpdir(), "unity_cli-vfx-extension-"));
  await mkdir(join(projectPath, "Packages"), { recursive: true });
  await writeFile(join(projectPath, "Packages", "manifest.json"), JSON.stringify({ dependencies: { "com.unity.visualeffectgraph": "14.0.0" } }), "utf8");
  await writeFile(join(projectPath, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: { "com.unity.visualeffectgraph": { version: "14.0.0" } } }), "utf8");
  const extensionRegistry = new ExtensionRegistry();
  await installExtensionBundle(projectPath, extensionRegistry.getExtension("vfx-graph")!.manifest);
  const extensionArgs = ["--project", projectPath];
  const runtime = {
    ...await makeRuntime(),
    projectTools: vfxNames.map((name) => ({
      name,
      summary: `${name} tool`,
      description: `${name} tool`,
      group: "vfx",
      source: "extension" as const,
      requiresPackages: ["com.unity.visualeffectgraph"],
      target: "editor",
      inputSchema: { type: "object", properties: {}, additionalProperties: true },
      destructiveHint: false,
      sideEffects: ["test-session-mutation"]
    }))
  };
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { plannedEffects: [{ type: payload.command }], clip: { path: payload.input!.path }, graph: { path: payload.input!.path }, target: { path: payload.input!.target } } };
  };

  const clipInfo = await execute(["tools", "run", "animation.clip", "Assets\\Animations\\Idle.anim", "--max-curve-keys", "2"], { ...runtime, bridgeExecute });
  const clipCreate = await execute(["tools", "run", "animation.clip.create", "Assets\\Animations\\Run.anim", "--overwrite", "--frame-rate", "30", "--legacy", "false", "--wrap-mode", "Loop", "--loop-time", "true", "--length", "1", "--max-curve-keys", "2", "--yes"], { ...runtime, bridgeExecute });
  const clipModify = await execute(["tools", "run", "animation.clip.modify", "Assets\\Animations\\Run.anim", "--frame-rate", "24", "--wrap-mode", "ClampForever", "--loop-time", "false", "--yes"], { ...runtime, bridgeExecute });
  const state = await execute(["tools", "run", "animation.state", "Rig/Player", "--layer", "1"], { ...runtime, bridgeExecute });
  const play = await execute(["tools", "run", "animation.play", "Rig/Player", "--state", "Base Layer.Run", "--layer", "0", "--normalized-time", "0.25"], { ...runtime, bridgeExecute });
  const stop = await execute(["tools", "run", "animation.stop", "Rig/Player"], { ...runtime, bridgeExecute });
  const graphCreate = await execute(["tools", "run", "vfx.graph.create", "--input", JSON.stringify({ path: "Assets\\VFX\\Explosion.vfx", overwrite: true }), "--yes", ...extensionArgs], { ...runtime, bridgeExecute });
  const effect = await execute(["tools", "run", "vfx.effect.add", "--input", JSON.stringify({ target: "Effects/Emitter", graph: "Assets\\VFX\\Explosion.vfx" }), ...extensionArgs], { ...runtime, bridgeExecute });
  const lineCreateInput = {
    name: "AimLine",
    material: "Assets\\Materials\\Line.mat",
    positions: [{ x: 0, y: 0, z: 0 }, { x: 1, y: 2, z: 3 }],
    widthCurve: [{ time: 0, value: 0.2 }, { time: 1, value: 0.05 }],
    colorGradient: [{ time: 0, color: "#FFFFFFFF" }, { time: 1, color: "#00FFFFFF" }],
    alignment: "View",
    textureMode: "Tile"
  };
  const lineCreate = await execute(["tools", "run", "vfx.line-create", "--input", JSON.stringify(lineCreateInput), ...extensionArgs], { ...runtime, bridgeExecute });
  const linePositionsInput = { target: "AimLine", positions: [{ x: 2, y: 0, z: 0 }, { x: 4, y: 0, z: 0 }] };
  const linePositions = await execute(["tools", "run", "vfx.line-positions", "--input", JSON.stringify(linePositionsInput), ...extensionArgs], { ...runtime, bridgeExecute });
  const trailInput = {
    name: "DashTrail",
    material: "Assets\\Materials\\Trail.mat",
    widthCurve: [{ time: 0, value: 0.3 }, { time: 1, value: 0 }],
    colorGradient: [{ time: 0, color: "#FFFFFFFF" }, { time: 1, color: "#00FFFFFF" }],
    emitting: true
  };
  const trail = await execute(["tools", "run", "vfx.trail.create", "--input", JSON.stringify(trailInput), ...extensionArgs], { ...runtime, bridgeExecute });

  assert.ok(clipInfo.ok);
  assert.ok(clipCreate.ok);
  assert.ok(clipModify.ok);
  assert.ok(state.ok);
  assert.ok(play.ok);
  assert.ok(stop.ok);
  assert.ok(graphCreate.ok);
  assert.ok(effect.ok);
  assert.ok(lineCreate.ok, JSON.stringify(lineCreate));
  assert.ok(linePositions.ok);
  assert.ok(trail.ok);
  assert.ok(requests.every((request) => request.command === "project-tool.run"));
  assert.equal(requests.length, 11);
  assert.deepEqual(requests[0].input!, { name: "animation.clip", input: { path: "Assets\\Animations\\Idle.anim", maxCurveKeys: 2 } });
  assert.deepEqual(requests[1].input!, { name: "animation.clip.create", input: { path: "Assets\\Animations\\Run.anim", overwrite: true, frameRate: 30, legacy: false, wrapMode: "Loop", loopTime: true, length: 1, maxCurveKeys: 2 } });
  assert.deepEqual(requests[2].input!, { name: "animation.clip.modify", input: { path: "Assets\\Animations\\Run.anim", frameRate: 24, wrapMode: "ClampForever", loopTime: false } });
  assert.deepEqual(requests[3].input!, { name: "animation.state", input: { target: "Rig/Player", layer: 1 } });
  assert.deepEqual(requests[4].input!, { name: "animation.play", input: { target: "Rig/Player", state: "Base Layer.Run", layer: 0, normalizedTime: 0.25 } });
  assert.deepEqual(requests[5].input!, { name: "animation.stop", input: { target: "Rig/Player" } });
  assert.deepEqual(requests[6].input!, { name: "vfx.graph.create", input: { path: "Assets\\VFX\\Explosion.vfx", overwrite: true } });
  assert.deepEqual(requests[7].input!, { name: "vfx.effect.add", input: { target: "Effects/Emitter", graph: "Assets\\VFX\\Explosion.vfx" } });
  assert.deepEqual(requests[8].input!, { name: "vfx.line-create", input: lineCreateInput });
  assert.deepEqual(requests[9].input!, { name: "vfx.line-positions", input: linePositionsInput });
  assert.deepEqual(requests[10].input!, { name: "vfx.trail.create", input: trailInput });
});

test("animation and vfx bridge errors preserve diagnostics", async () => {
  const runtime = await makeRuntime();
  const cases: Array<{ argv: string[]; code: string; message: string; nextActions: string[] }> = [
    { argv: ["tools", "run", "animation.play", "Player", "--state", "Missing"], code: "E_TARGET_AMBIGUOUS", message: "Multiple GameObjects matched: Player", nextActions: ["Pass a hierarchy path or instance id."] },
    { argv: ["tools", "run", "animation.clip", "Assets/Animations/Missing.anim"], code: "E_ANIMATION_CLIP_NOT_FOUND", message: "AnimationClip asset not found", nextActions: ["Check the path or create one with unity_cli tools run animation.clip.create --input '{\"path\":\"<path>\"}' --yes."] },
    { argv: ["tools", "run", "vfx.graph.info", "--input", JSON.stringify({ path: "Assets/VFX/Explosion.vfx" })], code: "E_VFX_PACKAGE_MISSING", message: "Visual Effect Graph package is not installed", nextActions: ["Install com.unity.visualeffectgraph manually via Unity Package Manager or Packages/manifest.json, run unity_cli tools run package.resolve, then retry."] },
    { argv: ["tools", "run", "vfx.graph.create", "--input", JSON.stringify({ path: "Assets/VFX/Explosion.vfx" }), "--yes"], code: "E_VFX_GRAPH_CREATE_FAILED", message: "VFX Graph asset creation did not produce a readable VisualEffectAsset", nextActions: ["Check Unity Console and UnityCliBridge.log; the Visual Effect Graph package may have changed its internal authoring API."] },
    { argv: ["tools", "run", "animation.clip", "D:\\tmp\\Bad.anim"], code: "E_ANIMATION_CLIP_PATH_INVALID", message: "AnimationClip path must be under Assets/ and end with .anim: D:\\tmp\\Bad.anim", nextActions: ["Pass a Unity asset path such as Assets/Animations/Player.anim."] }
  ];

  for (const item of cases) {
    const result = await execute(item.argv, {
      ...runtime,
      bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { code: item.code, message: item.message , nextActions: item.nextActions, transient: false, retryAfterMs: 0 }})
    });

    assert.equal(result.ok, false, item.code);
    assert.equal(result.error?.code, item.code, item.code);
    assert.equal(result.error?.message, item.message, item.code);
    assert.deepEqual(result.error?.nextActions, item.nextActions, item.code);
  }
});

test("shader commands forward paths, content and overwrite", async () => {
  const runtime = await makeRuntime();
  const contentFile = join(runtime.dataDir, "Unlit.shader");
  await writeFile(contentFile, "Shader \"UnityCli/Test\" {}\n", "utf8");
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { shader: { path: payload.input!.path }, plannedEffects: payload.command.startsWith("shader.") ? [{ type: payload.command, path: payload.input!.path }] : [] } };
  };

  const info = await execute(["tools", "run", "shader.info", "Assets/Shaders/Unlit.shader"], { ...runtime, bridgeExecute });
  const read = await execute(["tools", "run", "shader.read", "Assets/Shaders/Unlit.shader", "--offset", "5", "--max-bytes", "128"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "shader.create", "Assets/Shaders/Created.shader", "--content-file", contentFile, "--overwrite", "--yes"], { ...runtime, bridgeExecute });
  const update = await execute(["tools", "run", "shader.update", "Assets/Shaders/Unlit.shader", "--content", "Shader X {}", "--yes"], { ...runtime, bridgeExecute });
  const del = await execute(["tools", "run", "shader.delete", "Assets/Shaders/Unlit.shader", "--dry-run"], { ...runtime, bridgeExecute });
  const getData = await execute(["tools", "run", "shader.get-data", "--input", "{\"assetPath\":\"Assets/Shaders/Unlit.shader\",\"includeSubshaders\":true,\"includeSourceCode\":false,\"maxPasses\":4}"], { ...runtime, bridgeExecute });

  assert.ok(info.ok);
  assert.ok(read.ok);
  assert.ok(create.ok);
  assert.ok(update.ok);
  assert.ok(del.ok);
  assert.ok(getData.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests.map((request) => request.input!.name), ["shader.info", "shader.read", "shader.create", "shader.update", "shader.delete", "shader.get-data"]);
  assert.deepEqual(requests[0].input!.input, { path: "Assets/Shaders/Unlit.shader", target: "Assets/Shaders/Unlit.shader" });
  assert.deepEqual(requests[1].input!.input, { path: "Assets/Shaders/Unlit.shader", target: "Assets/Shaders/Unlit.shader", offset: 5, maxBytes: 128 });
  assert.equal((requests[2].input!.input as Record<string, unknown>).content, "Shader \"UnityCli/Test\" {}\n");
  assert.equal((requests[2].input!.input as Record<string, unknown>).overwrite, true);
  assert.equal((requests[3].input!.input as Record<string, unknown>).content, "Shader X {}");
  assert.equal(requests[4].execution?.dryRun, true);
  assert.deepEqual(requests[5].input!.input, { path: "Assets/Shaders/Unlit.shader", assetPath: "Assets/Shaders/Unlit.shader", target: "Assets/Shaders/Unlit.shader", includeSubshaders: true, includeSourceCode: false, maxPasses: 4 });
});

test("texture commands forward importer, sprite and creation options", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { texture: { path: payload.input!.path }, importSettings: { textureType: payload.input!.textureType }, plannedEffects: [{ type: payload.command, path: payload.input!.path }] } };
  };

  const info = await execute(["tools", "run", "texture.info", "Assets/Textures/Icon.png"], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "texture.create", "Assets/Textures/Icon.png", "--source", "C:/tmp/icon.png", "--overwrite", "--texture-type", "Sprite", "--yes"], { ...runtime, bridgeExecute });
  const sprite = await execute(["tools", "run", "texture.sprite", "Assets/Textures/Icon.png", "--input", "{\"pixelsPerUnit\":128,\"pivotX\":\"0.25\",\"pivotY\":\"0.75\",\"spriteMode\":\"Multiple\",\"slices\":[{\"name\":\"left\",\"x\":0,\"y\":0,\"width\":16,\"height\":16}]}", "--yes"], { ...runtime, bridgeExecute });
  const modify = await execute(["tools", "run", "texture.modify", "Assets/Textures/Icon.png", "--fill-color", "#ff0000", "--x", "1", "--y", "2", "--width", "3", "--height", "4", "--dry-run"], { ...runtime, bridgeExecute });
  const del = await execute(["tools", "run", "texture.delete", "Assets/Textures/Icon.png", "--dry-run"], { ...runtime, bridgeExecute });
  const importSettings = await execute(["tools", "run", "texture.import-settings", "Assets/Textures/Icon.png", "--readable", "false", "--mipmap", "false", "--srgb", "true", "--alpha-is-transparency", "--wrap-mode", "Clamp", "--filter-mode", "Point", "--max-texture-size", "512", "--compression", "Uncompressed", "--yes"], { ...runtime, bridgeExecute });

  assert.ok(info.ok);
  assert.ok(create.ok);
  assert.ok(sprite.ok);
  assert.ok(modify.ok);
  assert.ok(del.ok);
  assert.ok(importSettings.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests.map((request) => request.input!.name), ["texture.info", "texture.create", "texture.sprite", "texture.modify", "texture.delete", "texture.import-settings"]);
  assert.deepEqual(requests[1].input!.input, { path: "Assets/Textures/Icon.png", target: "Assets/Textures/Icon.png", source: "C:/tmp/icon.png", overwrite: true, textureType: "Sprite" });
  assert.deepEqual(requests[2].input!.input, { path: "Assets/Textures/Icon.png", target: "Assets/Textures/Icon.png", spriteMode: "Multiple", pixelsPerUnit: 128, pivotX: "0.25", pivotY: "0.75", slices: [{ name: "left", x: 0, y: 0, width: 16, height: 16 }] });
  assert.deepEqual(requests[3].input!.input, { path: "Assets/Textures/Icon.png", target: "Assets/Textures/Icon.png", width: 3, height: 4, fillColor: "#ff0000", x: 1, y: 2, dryRun: true });
  assert.equal(requests[4].execution?.dryRun, true);
  assert.equal((requests[5].input!.input as Record<string, unknown>).readable, false);
  assert.equal((requests[5].input!.input as Record<string, unknown>).mipmap, false);
  assert.equal((requests[5].input!.input as Record<string, unknown>).sRGB, true);
  assert.equal((requests[5].input!.input as Record<string, unknown>).alphaIsTransparency, true);
  assert.equal((requests[5].input!.input as Record<string, unknown>).maxTextureSize, 512);
});

test("shader and texture bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const shader = await execute(["tools", "run", "shader.info", "Assets/Shaders/Missing.shader"], {
    ...runtime,
    bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { code: "E_SHADER_NOT_FOUND", message: "Shader asset not found." , nextActions: ["Check the path."], transient: false, retryAfterMs: 0 }})
  });
  const texture = await execute(["tools", "run", "texture.import-settings", "Assets/Textures/Missing.png"], {
    ...runtime,
    bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { code: "E_IMPORT_FAILED", message: "Failed to apply texture import settings." , nextActions: ["Check Unity Console."], transient: false, retryAfterMs: 0 }})
  });

  assert.ok(!shader.ok);
  assert.equal(shader.error?.code, "E_SHADER_NOT_FOUND");
  assert.deepEqual(shader.error?.nextActions, ["Check the path."]);
  assert.ok(!texture.ok);
  assert.equal(texture.error?.code, "E_IMPORT_FAILED");
  assert.deepEqual(texture.error?.nextActions, ["Check Unity Console."]);
});

test("ui commands forward creation, document and hierarchy arguments", async () => {
  const runtime = await makeRuntime();
  const contentFile = join(runtime.dataDir, "Main.uxml");
  await writeFile(contentFile, "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />\n", "utf8");
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { plannedEffects: payload.command.includes("create") ? [{ type: payload.command, args: payload.input! }] : [], document: payload.command.includes("document") ? { path: payload.input!.uxml ?? payload.input!.path } : undefined } };
  };

  const canvas = await execute(["tools", "run", "ui.canvas.create", "HUD"], { ...runtime, bridgeExecute });
  const text = await execute(["tools", "run", "ui.text.create", "Title", "--parent", "HUD", "--text", "Start", "--width", "320", "--height", "80", "--x", "10", "--y", "20", "--font-size", "32"], { ...runtime, bridgeExecute });
  const button = await execute(["tools", "run", "ui.button.create", "PlayButton", "--canvas", "HUD", "--label", "Play"], { ...runtime, bridgeExecute });
  const image = await execute(["tools", "run", "ui.image.create", "Logo", "--parent", "HUD", "--sprite", "Assets/UI/logo.png", "--color", "#ffffff"], { ...runtime, bridgeExecute });
  const documentCreate = await execute(["tools", "run", "ui.document.create", "--name", "MainUIDocument", "--uxml", "Assets/UI/Main.uxml", "--uss", "Assets/UI/Main.uss", "--panel-settings", "Assets/UI/Panel.asset", "--overwrite", "--yes"], { ...runtime, bridgeExecute });
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

  assert.ok(canvas.ok);
  assert.ok(text.ok);
  assert.ok(button.ok);
  assert.ok(image.ok);
  assert.ok(documentCreate.ok);
  assert.ok(documentInfo.ok);
  assert.ok(documentUpdate.ok);
  assert.ok(documentDelete.ok);
  assert.ok(hierarchy.ok);
  assert.ok(attach.ok);
  assert.ok(detach.ok);
  assert.ok(panelCreate.ok);
  assert.ok(panelUpdate.ok);
  assert.ok(visualTree.ok);
  assert.ok(modifyVisual.ok);
  assert.ok(linkStylesheet.ok);
  assert.ok(listDocuments.ok);
  assert.ok(renderPreview.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "ui.canvas.create", input: { name: "HUD" } });
  assert.deepEqual(requests[1].input!, { name: "ui.text.create", input: { name: "Title", parent: "HUD", text: "Start", width: 320, height: 80, x: 10, y: 20, fontSize: 32 } });
  assert.deepEqual(requests[2].input!, { name: "ui.button.create", input: { name: "PlayButton", parent: "HUD", text: "Play" } });
  assert.deepEqual(requests[3].input!, { name: "ui.image.create", input: { name: "Logo", parent: "HUD", sprite: "Assets/UI/logo.png", color: "#ffffff" } });
  assert.deepEqual(requests[4].input!, { name: "ui.document.create", input: { name: "MainUIDocument", uxml: "Assets/UI/Main.uxml", uss: "Assets/UI/Main.uss", panelSettings: "Assets/UI/Panel.asset", overwrite: true } });
  assert.deepEqual(requests[5].input!, { name: "ui.document.info", input: { target: "MainUIDocument", uss: "Assets/UI/Main.uss" } });
  assert.deepEqual(requests[6].input!, { name: "ui.document.update", input: { path: "Assets/UI/Main.uxml", content: "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />\n" } });
  assert.equal(requests[7].execution?.dryRun, true);
  assert.deepEqual(requests[8].input!, { name: "ui.hierarchy", input: { pageSize: 25, cursor: "50", target: "HUD", query: "Canvas", type: "RectTransform", className: "screen" } });
  assert.deepEqual(requests[9].input!, { name: "ui.attach-document", input: { target: "UIDoc", sourceAsset: "Assets/UI/Main.uxml", panelSettings: "Assets/UI/Panel.asset", sortOrder: 5 } });
  assert.deepEqual(requests[10].input!, { name: "ui.detach-document", input: { target: "UIDoc", dryRun: true } });
  assert.deepEqual(requests[11].input!, { name: "ui.create-panel-settings", input: { path: "Assets/UI/Panel.asset", referenceWidth: 1920, referenceHeight: 1080, sortingOrder: 3, targetTexture: "Assets/UI/Target.renderTexture" } });
  assert.deepEqual(requests[12].input!, { name: "ui.update-panel-settings", input: { path: "Assets/UI/Panel.asset", scaleMode: "ScaleWithScreenSize", referenceWidth: 1280, referenceHeight: 720, sortingLayerName: "UI", match: 0.5, dryRun: true } });
  assert.deepEqual(requests[13].input!, { name: "ui.get-visual-tree", input: { target: "UIDoc", parent: "root", query: "button", type: "Button", className: "primary", pageSize: 10, cursor: "20", maxDepth: 4 } });
  assert.deepEqual(requests[14].input!, { name: "ui.modify-visual-element", input: { target: "UIDoc", elementName: "Root", addClass: "selected", backgroundColor: "#ff0000", dryRun: true } });
  assert.deepEqual(requests[15].input!, { name: "ui.link-stylesheet", input: { path: "Assets/UI/Main.uxml", uss: "Assets/UI/Main.uss" } });
  assert.deepEqual(requests[16].input!, { name: "ui.list-documents", input: { kind: "uxml", pageSize: 20 } });
  assert.deepEqual(requests[17].input!, { name: "ui.render-preview", input: { target: "Assets/UI/Main.uxml", output: "Assets/UI/preview.png" } });
});

test("ui document info resolves statically but execution still requires a Unity instance", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "ui.document.info", "Assets/UI/Main.uxml"], runtime);

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_INSTANCE_REQUIRED");
});

test("ui bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "ui.document.create", "--uxml", "Assets/UI/Main.uxml", "--panel-settings", "Assets/UI/Missing.asset", "--yes"], {
    ...runtime,
    bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { code: "E_UI_PANEL_SETTINGS_NOT_FOUND", message: "PanelSettings asset not found: Assets/UI/Missing.asset" , nextActions: ["Create a PanelSettings asset in Unity."], transient: false, retryAfterMs: 0 }})
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_UI_PANEL_SETTINGS_NOT_FOUND");
  assert.deepEqual(result.error?.nextActions, ["Create a PanelSettings asset in Unity."]);
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
      bridgeExecute: async (payload) => ({
        ok: false,
        requestId: payload.requestId,
        error: {
          code: "E_TARGET_AMBIGUOUS",
          message: item.message,
          details: {
            target: "HUD",
            matchCount: 2,
            matches: [
              { id: 101, name: "HUD", path: "Canvas/HUD", components: ["RectTransform"] },
              { id: 202, name: "HUD", path: "Overlay/HUD", components: ["UIDocument"] }
            ]
          },
          nextActions: [item.nextAction],
          transient: false,
          retryAfterMs: 0
        }
      })
    });

    assert.equal(result.ok, false, item.name);
    assert.equal(result.error?.code, "E_TARGET_AMBIGUOUS", item.name);
    assert.equal(result.error?.message, item.message, item.name);
    const details = result.error?.details as { target: string; matchCount: number; matches: unknown[] };
    assert.equal(details.target, "HUD", item.name);
    assert.equal(details.matchCount, 2, item.name);
    assert.deepEqual(details.matches, [
      { id: 101, name: "HUD", path: "Canvas/HUD", components: ["RectTransform"] },
      { id: 202, name: "HUD", path: "Overlay/HUD", components: ["UIDocument"] }
    ], item.name);
    assert.deepEqual(result.error?.nextActions, [item.nextAction], item.name);
  }
});

test("prefab bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "prefab.stage-close"], {
    ...runtime,
    bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { code: "E_PREFAB_DIRTY_CONFLICT", message: "Prefab Stage has unsaved changes." , nextActions: ["Run unity_cli tools run prefab.stage-save, then retry prefab.stage-close."], transient: false, retryAfterMs: 0 }})
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_PREFAB_DIRTY_CONFLICT");
  assert.deepEqual(result.error?.nextActions, ["Run unity_cli tools run prefab.stage-save, then retry prefab.stage-close."]);
});

test("go commands forward structured search and transform inputs", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true as const, requestId: payload.requestId, data: { gameObject: { path: payload.input!.target ?? payload.input!.name }, items: [], pageInfo: { total: 0 }, plannedEffects: [{ type: payload.command }] } };
  };

  const find = await execute(["tools", "run", "go.find", "--input", JSON.stringify({ searchTerm: "Camera", searchMethod: "by_component", includeInactive: true, pageSize: 10, cursor: 20 })], { ...runtime, bridgeExecute });
  const create = await execute(["tools", "run", "go.create", "--input", JSON.stringify({ name: "AlignmentCube", primitive: "cube", position: { x: 1, y: 2, z: 3 }, rotation: { x: 0, y: 45, z: 0 }, scale: { x: 2, y: 2, z: 2 }, active: false })], { ...runtime, bridgeExecute });
  const modify = await execute(["tools", "run", "go.modify", "--input", JSON.stringify({ target: "AlignmentCube", newName: "AlignmentCubeRenamed", tagValue: "Untagged", layerValue: "Default", active: false })], { ...runtime, bridgeExecute });
  const batchModify = await execute(["tools", "run", "go.modify", "--input", JSON.stringify({ targets: ["Batch/A", "Batch/B"], position: { x: 4, y: 5, z: 6 }, active: true })], { ...runtime, bridgeExecute });
  const move = await execute(["tools", "run", "go.move-relative", "--input", JSON.stringify({ target: "AlignmentCubeRenamed", offset: { x: 0, y: 1, z: -2 } })], { ...runtime, bridgeExecute });
  const parent = await execute(["tools", "run", "go.set-parent", "--dry-run", "--input", JSON.stringify({ target: "AlignmentCubeRenamed", parent: "Parent", worldPositionStays: false })], { ...runtime, bridgeExecute });

  for (const result of [find, create, modify, batchModify, move, parent]) {
    assert.ok(result.ok);
  }
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "go.find", input: { pageSize: 10, cursor: 20, searchTerm: "Camera", searchMethod: "by_component", includeInactive: true } });
  assert.deepEqual(requests[1].input!, { name: "go.create", input: { name: "AlignmentCube", active: false, primitive: "cube", x: 1, y: 2, z: 3, rotationX: 0, rotationY: 45, rotationZ: 0, scaleX: 2, scaleY: 2, scaleZ: 2 } });
  assert.deepEqual(requests[2].input!, { name: "go.modify", input: { target: "AlignmentCube", newName: "AlignmentCubeRenamed", active: false, layerValue: "Default", tagValue: "Untagged" } });
  assert.deepEqual(requests[3].input!, { name: "go.modify", input: { targets: ["Batch/A", "Batch/B"], active: true, x: 4, y: 5, z: 6 } });
  assert.deepEqual(requests[4].input!, { name: "go.move-relative", input: { target: "AlignmentCubeRenamed", offsetX: 0, offsetY: 1, offsetZ: -2 } });
  assert.deepEqual(requests[5].input!, { name: "go.set-parent", input: { target: "AlignmentCubeRenamed", parent: "Parent", worldPositionStays: false, dryRun: true } });
  assert.equal(requests[5].execution?.dryRun, true);
});

test("go.create keeps top-level numeric example fields as numbers", async () => {
  const runtime = await makeRuntime();
  let request: BridgeRequest | undefined;
  const result = await execute(["tools", "run", "go.create", "--input", JSON.stringify({ name: "AlignmentCube", primitive: "cube", x: 0, y: 1, z: 0 })], {
    ...runtime,
    bridgeExecute: async (payload) => {
      request = payload;
      return { ok: true as const, requestId: payload.requestId, data: { gameObject: { path: "AlignmentCube" }, plannedEffects: [] } };
    }
  });

  assert.ok(result.ok);
  assert.deepEqual(request?.input, { name: "go.create", input: { name: "AlignmentCube", primitive: "cube", x: 0, y: 1, z: 0 } });
});

test("bridge errors return CLI failure envelope", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "go.inspect", "Player"], {
    ...runtime,
    bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { code: "E_TARGET_AMBIGUOUS", message: "Multiple targets matched." , nextActions: ["Pass --path or --id."], transient: false, retryAfterMs: 0 }})
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_TARGET_AMBIGUOUS");
  assert.deepEqual(result.error?.nextActions, ["Pass --path or --id."]);
});

test("camera screenshot bridge target and IO errors preserve protocol envelopes", async () => {
  const runtime = await makeRuntime();
  const cases: Array<{ name: string; response: { code: string; message: string }; nextActions: string[] }> = [
    { name: "empty target", response: { code: "E_TARGET_NOT_FOUND", message: "Camera screenshot requires a target Camera." }, nextActions: ["Pass a Camera name, hierarchy path or instance id."] },
    { name: "missing camera", response: { code: "E_TARGET_NOT_FOUND", message: "Camera not found: Missing Camera" }, nextActions: ["Run unity_cli tools run camera.list to inspect available cameras."] },
    { name: "io failure", response: { code: "E_IO_ERROR", message: "Failed to write screenshot output." }, nextActions: ["Check output path, permissions, format parameters and UnityCliBridge.log."] }
  ];

  for (const item of cases) {
    const result = await execute(["tools", "run", "camera.screenshot", item.name === "empty target" ? "" : "Missing Camera", "--output", "Artifacts/out.png", "--yes"], {
      ...runtime,
      bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { ...item.response, nextActions: item.nextActions, transient: false, retryAfterMs: 0 } })
    });

    assert.equal(result.ok, false, item.name);
    assert.equal(result.error?.code, item.response.code, item.name);
    assert.equal(result.error?.message, item.response.message, item.name);
    assert.deepEqual(result.error?.nextActions, item.nextActions, item.name);
  }
});

test("editor state/play/pause/stop/menu commands forward args and return envelope", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    const name = (payload.input!.name as string) ?? payload.command;
    if (name === "editor.state") {
      return { ok: true as const, requestId: payload.requestId, data: { command: name, playMode: { isPlaying: false, isPaused: false }, activeScene: { path: "Assets/Scenes/Main.unity" } } };
    }
    if (name === "editor.menu") {
      const input = (payload.input!.input ?? {}) as Record<string, unknown>;
      return { ok: true as const, requestId: payload.requestId, data: { command: name, plannedEffects: [{ type: "editor.menu", path: input.path }], result: { path: input.path, executed: true } } };
    }
    return { ok: true as const, requestId: payload.requestId, data: { command: name, plannedEffects: [{ type: name }], result: { requested: name.split(".")[1] } } };
  };

  const state = await execute(["tools", "run", "editor.state"], { ...runtime, bridgeExecute });
  const play = await execute(["tools", "run", "editor.play", "--timeout-ms", "2500"], { ...runtime, bridgeExecute });
  const pause = await execute(["tools", "run", "editor.pause"], { ...runtime, bridgeExecute });
  const stop = await execute(["tools", "run", "editor.stop", "--timeout", "3000"], { ...runtime, bridgeExecute });
  const menu = await execute(["tools", "run", "editor.menu", "File/Save", "--timeout-ms", "7000", "--yes"], { ...runtime, bridgeExecute });

  assert.equal(state.ok, false);
  assert.equal(state.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
  assert.ok(play.ok);
  assert.ok(pause.ok);
  assert.ok(stop.ok);
  assert.ok(menu.ok);
  assert.deepEqual(requests.map((request) => request.command), ["project-tool.run", "project-tool.run", "project-tool.run", "project-tool.run"]);
  assert.deepEqual(requests[0].input!, { name: "editor.play", input: { timeoutMs: 2500 } });
  assert.deepEqual(requests[1].input!, { name: "editor.pause", input: {} });
  assert.deepEqual(requests[2].input!, { name: "editor.stop", input: { timeoutMs: 3000 } });
  assert.deepEqual(requests[3].input!, { name: "editor.menu", input: { path: "File/Save", timeoutMs: 7000 } });
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
      bridgeExecute: async (payload) => ({ ok: false as const, requestId: payload.requestId, error: { ...item.response, nextActions: item.nextActions, transient: false, retryAfterMs: 0 } })
    });

    assert.equal(result.ok, false, item.name);
    assert.equal(result.error?.code, item.response.code, item.name);
    assert.deepEqual(result.error?.nextActions, item.nextActions, item.name);
  }
});

test("runtime-tool list is blocked from CLI as internal command", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "runtime-tool.list"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("runtime-tool run is blocked from CLI as internal command", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["tools", "run", "runtime-tool.run", "sample.runtime-echo", "--yes"], runtime);
  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_TOOL_NOT_FOUND_FOR_TARGET");
});

test("runtime-tool run direct bridge command does not require confirmation before reaching bridge", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await executeBridgeCommand("req_runtime", "runtime-tool.run", {}, "sample.runtime-echo", {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: false as const, requestId: payload.requestId, error: { code: "E_RUNTIME_NOT_READY", message: "not ready", transient: true, retryAfterMs: 3000, nextActions: ["Enter Play Mode first."] }};
    }
  });

  assert.ok(!result.ok);
  assert.equal(result.error?.code, "E_RUNTIME_NOT_READY");
  assert.equal(requests[0].command, "runtime-tool.run");
  assert.equal(requests[0].execution.securityProof, undefined);
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
  assert.equal(spec.destructiveHint, undefined);
  assert.equal(spec.internal, true);
});
