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
