import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import test from "node:test";
import { execute } from "./cli.js";
import { JobRunner } from "./job-runner.js";
import { JobStore } from "./job-store.js";
import type { BridgeRequest } from "./bridge-commands.js";

process.env.ALPS_UNITY_ALLOW_NON_WINDOWS = "1";

async function makeRuntime() {
  const dataDir = await mkdtemp(join(tmpdir(), "alps-unity-bridge-"));
  const store = new JobStore(dataDir);
  return { dataDir, store, runner: new JobRunner(store, dataDir) };
}

test("query command forwards stable bridge envelope", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["scene", "hierarchy", "--page-size", "50"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { items: [{ path: "Main Camera" }], pageInfo: { pageSize: payload.args.pageSize } } };
    }
  });

  assert.equal(result.ok, true);
  assert.deepEqual(requests[0].command, "scene.hierarchy");
  assert.deepEqual(requests[0].args, { pageSize: 50 });
  assert.equal(((result.data as { items: Array<{ path: string }> }).items[0]).path, "Main Camera");
});

test("edit command forwards target and plannedEffects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const result = await execute(["component", "set", "Player", "--component", "Camera", "--property", "enabled", "--value", "false"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { plannedEffects: [{ type: payload.command, property: payload.args.property }] } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "component.set");
  assert.deepEqual(requests[0].args, { target: "Player", component: "Camera", property: "enabled", value: "false" });
  assert.deepEqual((result.data as { plannedEffects: unknown[] }).plannedEffects, [{ type: "component.set", property: "enabled" }]);
});

test("script create reads content file", async () => {
  const runtime = await makeRuntime();
  const contentFile = join(runtime.dataDir, "Player.cs");
  await writeFile(contentFile, "public class Player {}\n", "utf8");
  const requests: BridgeRequest[] = [];

  const result = await execute(["script", "create", "Assets/Scripts/Player.cs", "--content-file", contentFile], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { script: { path: payload.args.path } } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "script.create");
  assert.equal(requests[0].args.content, "public class Player {}\n");
});

test("screenshot command forwards output path", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["screenshot", "capture", "Artifacts/after.png", "--width", "1280", "--height", "720"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { screenshotPath: payload.args.path, plannedEffects: [{ type: payload.command, path: payload.args.path }] } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "screenshot.capture");
  assert.deepEqual(requests[0].args, { path: "Artifacts/after.png", width: 1280, height: 720 });
  assert.equal((result.data as { screenshotPath: string }).screenshotPath, "Artifacts/after.png");
});

test("camera commands forward target and screenshot options", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const list = await execute(["camera", "list"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { cameras: [{ name: "Main Camera" }] } };
    }
  });

  const info = await execute(["camera", "info", "Main Camera"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { camera: { name: payload.args.target } } };
    }
  });

  const screenshot = await execute(["camera", "screenshot", "Main Camera", "--output", "Artifacts/camera.png", "--format", "png", "--width", "1920", "--height", "1080", "--transparent", "--overwrite", "--quality", "90"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { screenshotPath: payload.args.path, format: payload.args.format, width: payload.args.width, height: payload.args.height } };
    }
  });

  assert.equal(list.ok, true);
  assert.equal(info.ok, true);
  assert.equal(screenshot.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["camera.list", "camera.info", "camera.screenshot"]);
  assert.deepEqual(requests[1].args, { target: "Main Camera" });
  assert.deepEqual(requests[2].args, { path: "Artifacts/camera.png", format: "png", width: 1920, height: 1080, transparent: true, quality: 90, overwrite: true, target: "Main Camera" });
});

test("scene screenshot forwards output path and overwrite", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];

  const result = await execute(["scene", "screenshot", "Artifacts/scene.jpg", "--width", "800", "--height", "600", "--format", "jpg", "--overwrite"], {
    ...runtime,
    bridgeExecute: async (payload: BridgeRequest) => {
      requests.push(payload);
      return { ok: true, data: { screenshotPath: payload.args.path, source: "scene" } };
    }
  });

  assert.equal(result.ok, true);
  assert.equal(requests[0].command, "scene.screenshot");
  assert.deepEqual(requests[0].args, { path: "Artifacts/scene.jpg", format: "jpg", width: 800, height: 600, overwrite: true });
});

test("prefab commands forward paths, pagination and planned effects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { prefab: { path: payload.args.path }, plannedEffects: payload.command === "prefab.create" ? [{ type: payload.command, path: payload.args.path }] : [] } };
  };

  const info = await execute(["prefab", "info", "Assets/Prefabs/Player.prefab"], { ...runtime, bridgeExecute });
  const create = await execute(["prefab", "create", "Player", "--path", "Assets/Prefabs/Player.prefab", "--overwrite"], { ...runtime, bridgeExecute });
  const open = await execute(["prefab", "stage-open", "Assets/Prefabs/Player.prefab"], { ...runtime, bridgeExecute });
  const hierarchy = await execute(["prefab", "hierarchy", "Assets/Prefabs/Player.prefab", "--page-size", "25", "--cursor", "50"], { ...runtime, bridgeExecute });
  const save = await execute(["prefab", "stage-save"], { ...runtime, bridgeExecute });
  const close = await execute(["prefab", "stage-close", "--save"], { ...runtime, bridgeExecute });

  assert.equal(info.ok, true);
  assert.equal(create.ok, true);
  assert.equal(open.ok, true);
  assert.equal(hierarchy.ok, true);
  assert.equal(save.ok, true);
  assert.equal(close.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["prefab.info", "prefab.create", "prefab.stage-open", "prefab.hierarchy", "prefab.stage-save", "prefab.stage-close"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Prefabs/Player.prefab" });
  assert.deepEqual(requests[1].args, { source: "Player", path: "Assets/Prefabs/Player.prefab", overwrite: true });
  assert.deepEqual(requests[3].args, { pageSize: 25, cursor: "50", path: "Assets/Prefabs/Player.prefab" });
  assert.deepEqual(requests[4].args, {});
  assert.deepEqual(requests[5].args, { save: true });
  assert.deepEqual((create.data as { plannedEffects: unknown[] }).plannedEffects, [{ type: "prefab.create", path: "Assets/Prefabs/Player.prefab" }]);
});

test("material commands forward asset paths, target, values and planned effects", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { material: { path: payload.args.path ?? payload.args.material }, plannedEffects: payload.command === "material.info" ? [] : [{ type: payload.command, path: payload.args.path ?? payload.args.material }] } };
  };

  const info = await execute(["material", "info", "Assets/Materials/Player.mat"], { ...runtime, bridgeExecute });
  const create = await execute(["material", "create", "Assets/Materials/Player.mat", "--shader", "Standard", "--overwrite"], { ...runtime, bridgeExecute });
  const assign = await execute(["material", "assign", "--target", "Canvas/Button", "--material", "Assets/Materials/Player.mat", "--slot", "1"], { ...runtime, bridgeExecute });
  const set = await execute(["material", "set", "Assets/Materials/Player.mat", "--property", "_Color", "--value", "#ff00ffff", "--type", "color"], { ...runtime, bridgeExecute });

  assert.equal(info.ok, true);
  assert.equal(create.ok, true);
  assert.equal(assign.ok, true);
  assert.equal(set.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["material.info", "material.create", "material.assign", "material.set"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Materials/Player.mat" });
  assert.deepEqual(requests[1].args, { path: "Assets/Materials/Player.mat", shader: "Standard", overwrite: true });
  assert.deepEqual(requests[2].args, { target: "Canvas/Button", material: "Assets/Materials/Player.mat", slot: 1 });
  assert.deepEqual(requests[3].args, { path: "Assets/Materials/Player.mat", property: "_Color", value: "#ff00ffff", type: "color" });
  assert.deepEqual((set.data as { plannedEffects: unknown[] }).plannedEffects, [{ type: "material.set", path: "Assets/Materials/Player.mat" }]);
});

test("material bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["material", "set", "Assets/Materials/Player.mat", "--property", "_Missing", "--value", "1"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_MATERIAL_PROPERTY_NOT_FOUND", message: "Material property not found: _Missing" }, nextActions: ["Run alps-unity material info <path> to inspect shader properties."] })
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_MATERIAL_PROPERTY_NOT_FOUND");
  assert.deepEqual(result.error?.next_actions, ["Run alps-unity material info <path> to inspect shader properties."]);
});

test("graphics commands forward settings and screenshot diagnostics", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { graphics: { renderPipeline: { active: "Built-in" } }, plannedEffects: payload.args } };
  };

  const quality = await execute(["graphics", "quality", "--quality-level", "2", "--v-sync-count", "1", "--shadow-distance", "75.5", "--soft-particles", "false"], { ...runtime, bridgeExecute });
  const settings = await execute(["graphics", "settings", "--render-pipeline", "Assets/Settings/URP.asset", "--anti-aliasing", "4"], { ...runtime, bridgeExecute });
  const screenshot = await execute(["graphics", "screenshot", "Artifacts/graphics.png", "--width", "1024", "--height", "768", "--include-diagnostics", "--overwrite"], { ...runtime, bridgeExecute });

  assert.equal(quality.ok, true);
  assert.equal(settings.ok, true);
  assert.equal(screenshot.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["graphics.quality", "graphics.settings", "graphics.screenshot"]);
  assert.deepEqual(requests[0].args, { qualityLevel: 2, vSyncCount: 1, shadowDistance: 75.5, softParticles: false });
  assert.deepEqual(requests[1].args, { antiAliasing: 4, renderPipeline: "Assets/Settings/URP.asset" });
  assert.deepEqual(requests[2].args, { path: "Artifacts/graphics.png", width: 1024, height: 768, overwrite: true, includeDiagnostics: true });
});

test("physics commands forward settings, raycast and simulation inputs", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { physics: {}, plannedEffects: payload.command === "physics.raycast" ? [] : [{ type: payload.command }] } };
  };

  const settings = await execute(["physics", "settings", "--gravity-y", "-9.81", "--gravity2d-y", "-12.5", "--queries-hit-triggers", "false", "--default-solver-iterations", "8"], { ...runtime, bridgeExecute });
  const raycast = await execute(["physics", "raycast", "--mode", "2d", "--origin-x", "1", "--origin-y", "2", "--direction-x", "0", "--direction-y", "-1", "--distance", "50", "--layer-mask", "5", "--all"], { ...runtime, bridgeExecute });
  const simulation = await execute(["physics", "simulation", "--steps", "3", "--delta-time", "0.016", "--simulate"], { ...runtime, bridgeExecute });

  assert.equal(settings.ok, true);
  assert.equal(raycast.ok, true);
  assert.equal(simulation.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["physics.settings", "physics.raycast", "physics.simulation"]);
  assert.deepEqual(requests[0].args, { gravityY: -9.81, gravity2dY: -12.5, defaultSolverIterations: 8, queriesHitTriggers: false });
  assert.deepEqual(requests[1].args, { mode: "2d", originX: 1, originY: 2, directionX: 0, directionY: -1, distance: 50, layerMask: 5, all: true });
  assert.deepEqual(requests[2].args, { steps: 3, deltaTime: 0.016, simulate: true });
});

test("prefab bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["prefab", "stage-close"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_PREFAB_DIRTY_CONFLICT", message: "Prefab Stage has unsaved changes." }, nextActions: ["Run alps-unity prefab stage-close --save."] })
  });

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_PREFAB_DIRTY_CONFLICT");
  assert.deepEqual(result.error?.next_actions, ["Run alps-unity prefab stage-close --save."]);
});

test("bridge errors return CLI failure envelope", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["go", "inspect", "Player"], {
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
    { name: "missing camera", response: { code: "E_TARGET_NOT_FOUND", message: "Camera not found: Missing Camera" }, nextActions: ["Run alps-unity camera list to inspect available cameras."] },
    { name: "io failure", response: { code: "E_IO_ERROR", message: "Failed to write screenshot output." }, nextActions: ["Check output path, permissions, format parameters and AlpsUnityBridge.log."] }
  ];

  for (const item of cases) {
    const result = await execute(["camera", "screenshot", item.name === "empty target" ? "" : "Missing Camera", "--output", "Artifacts/out.png"], {
      ...runtime,
      bridgeExecute: async () => ({ ok: false, error: item.response, nextActions: item.nextActions })
    });

    assert.equal(result.ok, false, item.name);
    assert.equal(result.error?.code, item.response.code, item.name);
    assert.equal(result.error?.message, item.response.message, item.name);
    assert.deepEqual(result.error?.next_actions, item.nextActions, item.name);
  }
});
