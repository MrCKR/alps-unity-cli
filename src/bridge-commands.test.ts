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

test("shader commands forward paths, content and overwrite", async () => {
  const runtime = await makeRuntime();
  const contentFile = join(runtime.dataDir, "Unlit.shader");
  await writeFile(contentFile, "Shader \"Alps/Test\" {}\n", "utf8");
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { shader: { path: payload.args.path }, plannedEffects: payload.command.startsWith("shader.") ? [{ type: payload.command, path: payload.args.path }] : [] } };
  };

  const info = await execute(["shader", "info", "Assets/Shaders/Unlit.shader"], { ...runtime, bridgeExecute });
  const read = await execute(["shader", "read", "Assets/Shaders/Unlit.shader"], { ...runtime, bridgeExecute });
  const create = await execute(["shader", "create", "Assets/Shaders/Created.shader", "--content-file", contentFile, "--overwrite"], { ...runtime, bridgeExecute });
  const update = await execute(["shader", "update", "Assets/Shaders/Unlit.shader", "--content", "Shader X {}"], { ...runtime, bridgeExecute });

  assert.equal(info.ok, true);
  assert.equal(read.ok, true);
  assert.equal(create.ok, true);
  assert.equal(update.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["shader.info", "shader.read", "shader.create", "shader.update"]);
  assert.deepEqual(requests[0].args, { path: "Assets/Shaders/Unlit.shader", target: "Assets/Shaders/Unlit.shader" });
  assert.equal(requests[2].args.content, "Shader \"Alps/Test\" {}\n");
  assert.equal(requests[2].args.overwrite, true);
  assert.equal(requests[3].args.content, "Shader X {}");
});

test("texture commands forward importer, sprite and creation options", async () => {
  const runtime = await makeRuntime();
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { texture: { path: payload.args.path }, importSettings: { textureType: payload.args.textureType }, plannedEffects: [{ type: payload.command, path: payload.args.path }] } };
  };

  const info = await execute(["texture", "info", "Assets/Textures/Icon.png"], { ...runtime, bridgeExecute });
  const create = await execute(["texture", "create", "Assets/Textures/Icon.png", "--source", "C:/tmp/icon.png", "--overwrite", "--texture-type", "Sprite"], { ...runtime, bridgeExecute });
  const sprite = await execute(["texture", "sprite", "Assets/Textures/Icon.png", "--pixels-per-unit", "128", "--pivot-x", "0.25", "--pivot-y", "0.75", "--sprite-mode", "single"], { ...runtime, bridgeExecute });
  const importSettings = await execute(["texture", "import-settings", "Assets/Textures/Icon.png", "--readable", "false", "--mipmap", "false", "--srgb", "true", "--alpha-is-transparency", "--wrap-mode", "Clamp", "--filter-mode", "Point", "--max-texture-size", "512", "--compression", "Uncompressed"], { ...runtime, bridgeExecute });

  assert.equal(info.ok, true);
  assert.equal(create.ok, true);
  assert.equal(sprite.ok, true);
  assert.equal(importSettings.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["texture.info", "texture.create", "texture.sprite", "texture.import-settings"]);
  assert.deepEqual(requests[1].args, { path: "Assets/Textures/Icon.png", target: "Assets/Textures/Icon.png", source: "C:/tmp/icon.png", overwrite: true, textureType: "Sprite" });
  assert.deepEqual(requests[2].args, { path: "Assets/Textures/Icon.png", target: "Assets/Textures/Icon.png", spriteMode: "single", pixelsPerUnit: 128, pivotX: "0.25", pivotY: "0.75" });
  assert.equal(requests[3].args.readable, false);
  assert.equal(requests[3].args.mipmap, false);
  assert.equal(requests[3].args.sRGB, true);
  assert.equal(requests[3].args.alphaIsTransparency, true);
  assert.equal(requests[3].args.maxTextureSize, 512);
});

test("shader and texture bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const shader = await execute(["shader", "info", "Assets/Shaders/Missing.shader"], {
    ...runtime,
    bridgeExecute: async () => ({ ok: false, error: { code: "E_SHADER_NOT_FOUND", message: "Shader asset not found." }, nextActions: ["Check the path."] })
  });
  const texture = await execute(["texture", "import-settings", "Assets/Textures/Missing.png"], {
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
  const requests: BridgeRequest[] = [];
  const bridgeExecute = async (payload: BridgeRequest) => {
    requests.push(payload);
    return { ok: true, data: { plannedEffects: payload.command.includes("create") ? [{ type: payload.command, args: payload.args }] : [], document: payload.command.includes("document") ? { path: payload.args.uxml ?? payload.args.path } : undefined } };
  };

  const canvas = await execute(["ui", "canvas/create", "HUD"], { ...runtime, bridgeExecute });
  const text = await execute(["ui", "text/create", "Title", "--parent", "HUD", "--text", "Start", "--width", "320", "--height", "80", "--x", "10", "--y", "20", "--font-size", "32"], { ...runtime, bridgeExecute });
  const button = await execute(["ui", "button/create", "PlayButton", "--canvas", "HUD", "--label", "Play"], { ...runtime, bridgeExecute });
  const image = await execute(["ui", "image/create", "Logo", "--parent", "HUD", "--sprite", "Assets/UI/logo.png", "--color", "#ffffff"], { ...runtime, bridgeExecute });
  const documentCreate = await execute(["ui", "document/create", "--name", "MainUIDocument", "--uxml", "Assets/UI/Main.uxml", "--uss", "Assets/UI/Main.uss", "--panel-settings", "Assets/UI/Panel.asset", "--overwrite"], { ...runtime, bridgeExecute });
  const documentInfo = await execute(["ui", "document/info", "MainUIDocument", "--uss", "Assets/UI/Main.uss"], { ...runtime, bridgeExecute });
  const hierarchy = await execute(["ui", "hierarchy", "HUD", "--page-size", "25", "--cursor", "50"], { ...runtime, bridgeExecute });

  assert.equal(canvas.ok, true);
  assert.equal(text.ok, true);
  assert.equal(button.ok, true);
  assert.equal(image.ok, true);
  assert.equal(documentCreate.ok, true);
  assert.equal(documentInfo.ok, true);
  assert.equal(hierarchy.ok, true);
  assert.deepEqual(requests.map((request) => request.command), ["ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.create", "ui.document.info", "ui.hierarchy"]);
  assert.deepEqual(requests[0].args, { name: "HUD" });
  assert.deepEqual(requests[1].args, { name: "Title", parent: "HUD", text: "Start", width: 320, height: 80, x: 10, y: 20, fontSize: 32 });
  assert.deepEqual(requests[2].args, { name: "PlayButton", parent: "HUD", text: "Play" });
  assert.deepEqual(requests[3].args, { name: "Logo", parent: "HUD", sprite: "Assets/UI/logo.png", color: "#ffffff" });
  assert.deepEqual(requests[4].args, { name: "MainUIDocument", uxml: "Assets/UI/Main.uxml", uss: "Assets/UI/Main.uss", panelSettings: "Assets/UI/Panel.asset", overwrite: true });
  assert.deepEqual(requests[5].args, { target: "MainUIDocument", uss: "Assets/UI/Main.uss" });
  assert.deepEqual(requests[6].args, { pageSize: 25, cursor: "50", target: "HUD" });
});

test("ui document info reads UXML offline when bridge endpoint is absent", async () => {
  const runtime = await makeRuntime();
  const uxmlPath = join(runtime.dataDir, "Main.uxml");
  await writeFile(uxmlPath, "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:VisualElement name=\"Root\" /></ui:UXML>\n", "utf8");

  const result = await execute(["ui", "document/info", uxmlPath], runtime);

  assert.equal(result.ok, true);
  assert.equal((result.data as { mode: string }).mode, "offline-reader");
  assert.equal((result.data as { document: { kind: string } }).document.kind, "uxml");
  assert.deepEqual((result.data as { diagnostics: Array<{ code: string }> }).diagnostics.map((item) => item.code), ["UI_DOCUMENT_READ_OK"]);
});

test("ui bridge errors preserve structured diagnostics", async () => {
  const runtime = await makeRuntime();
  const result = await execute(["ui", "document/create", "--uxml", "Assets/UI/Main.uxml", "--panel-settings", "Assets/UI/Missing.asset"], {
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
    { name: "parent", args: ["ui", "text/create", "Title", "--parent", "HUD"], message: "UI parent target is ambiguous: HUD", nextAction: "Pass a hierarchy path or instance id for the intended Canvas/RectTransform parent." },
    { name: "document", args: ["ui", "document/info", "HUD"], message: "UIDocument target is ambiguous: HUD", nextAction: "Pass a hierarchy path or instance id for the intended UIDocument GameObject." },
    { name: "hierarchy", args: ["ui", "hierarchy", "HUD"], message: "UI hierarchy target is ambiguous: HUD", nextAction: "Pass a hierarchy path or instance id for the intended UI hierarchy root." }
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

  const state = await execute(["editor", "state"], { ...runtime, bridgeExecute });
  const play = await execute(["editor", "play", "--timeout-ms", "2500"], { ...runtime, bridgeExecute });
  const pause = await execute(["editor", "pause"], { ...runtime, bridgeExecute });
  const stop = await execute(["editor", "stop", "--timeout", "3000"], { ...runtime, bridgeExecute });
  const menu = await execute(["editor", "menu", "File/Save", "--timeout-ms", "7000"], { ...runtime, bridgeExecute });

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
    { name: "busy", args: ["editor", "menu", "File/Save"], response: { code: "E_UNITY_BUSY", message: "Unity Editor is busy and cannot run this editor command right now." }, nextActions: ["Wait for compiling, asset importing, build, PlayMode switching, or domain reload to complete, then retry."] },
    { name: "playmode timeout", args: ["editor", "stop"], response: { code: "E_PLAYMODE_TIMEOUT", message: "Unity PlayMode did not reach the requested state before the timeout." }, nextActions: ["Wait for PlayMode switching or domain reload to finish, then retry."] },
    { name: "menu not found", args: ["editor", "menu", "Tools/Missing"], response: { code: "E_MENU_NOT_FOUND", message: "Unity menu item was not found or could not be executed." }, nextActions: ["Use the exact Unity Editor menu path, such as File/Save or Assets/Refresh."] },
    { name: "menu timeout", args: ["editor", "menu", "Tools/Slow"], response: { code: "E_MENU_TIMEOUT", message: "Unity menu item execution exceeded the requested timeout." }, nextActions: ["Check Unity Editor and Alps Bridge logs for long-running menu work."] }
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
