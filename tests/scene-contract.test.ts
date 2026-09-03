import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readCsSources, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

const SCENE_COMMANDS = [
  "scene.active",
  "scene.create",
  "scene.gameobject",
  "scene.gameobject.component",
  "scene.gameobject.components",
  "scene.hierarchy",
  "scene.list-opened",
  "scene.load",
  "scene.move-object",
  "scene.save",
  "scene.set-active",
  "scene.unload",
  "scene.validate",
  "scene.view-frame"
];

test("scene group is contract-discovered without duplicate deep-read or Build Settings entries", () => {
  assert.deepEqual(contractToolNames("scene"), [...SCENE_COMMANDS].sort());
});

test("scene live descriptors expose expected read/write semantics", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "scene.active", summary: "Read active scene.", description: "Read active scene.", group: "scene", outputSchema: { type: "object", properties: { scene: { type: "object" } } } }),
    makeLiveTool({ name: "scene.list-opened", summary: "List opened scenes.", description: "List opened scenes.", group: "scene", outputSchema: { type: "object", properties: { scenes: { type: "array" } } } }),
    makeLiveTool({ name: "scene.gameobject", summary: "Read one GameObject summary.", description: "Read one selected GameObject after hierarchy discovery.", group: "scene", outputSchema: { type: "object", properties: { gameObject: { type: "object" } } } }),
    makeLiveTool({ name: "scene.hierarchy", summary: "Read a hierarchy page.", description: "Read a lightweight hierarchy page.", group: "scene", outputSchema: { type: "object", properties: { items: { type: "array" } } } }),
    makeLiveTool({ name: "scene.create", summary: "Create a scene.", description: "Create a scene.", group: "scene", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "scene.save", summary: "Save a scene.", description: "Save a scene.", group: "scene", destructiveHint: true, supportsDryRun: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "scene.unload", summary: "Unload a scene.", description: "Unload a scene.", group: "scene", supportsDryRun: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "scene.view-frame", summary: "Frame the Scene view.", description: "Frame the Scene view.", group: "scene", playModeSupport: "editor-only" })
  );

  for (const name of ["scene.active", "scene.list-opened", "scene.gameobject", "scene.hierarchy"]) {
    const tool = tools.get(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.playModeSupport, "supported", `${name} play mode supported`);
    assert.ok(tool.outputSchema, `${name} output schema`);
  }

  const create = tools.get("scene.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("scene-modify"));

  const save = tools.get("scene.save")!;
  assert.equal(save.destructiveHint, true);
  assert.equal(save.supportsDryRun, true);
  assert.equal(save.playModeSupport, "session-affecting");

  const unload = tools.get("scene.unload")!;
  assert.equal(unload.destructiveHint, false);
  assert.equal(unload.supportsDryRun, true);

  assert.equal(tools.get("scene.view-frame")?.playModeSupport, "editor-only");
});

test("bridge specs resolve for scene tools", () => {
  for (const [cmd, toolName] of [["active", "scene.active"], ["create", "scene.create"], ["load", "scene.load"], ["save", "scene.save"], ["unload", "scene.unload"], ["set-active", "scene.set-active"], ["list-opened", "scene.list-opened"], ["gameobject", "scene.gameobject"], ["hierarchy", "scene.hierarchy"], ["view-frame", "scene.view-frame"]] as const) {
    const spec = bridgeCommandRegistry.lookup("scene", cmd);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `scene ${cmd} should route via project-tool.run`);
    assert.equal(spec?.toolName, toolName, `scene ${cmd} toolName should match`);
  }
});

test("contract entries exist for scene tools with present C# handler", () => {
  for (const name of SCENE_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("scene C# handlers are registered", async () => {
  const allSource = await readEditorCsSources();
  for (const command of SCENE_COMMANDS) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("scene C# error recovery hints use current tools run forms", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/scene/", import.meta.url));
  assert.match(source, /tools run scene\.save/, "error hints should route to tools run scene.save");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("scene.load resolves a unique scene asset name before opening", async () => {
  const support = await readFile(new URL("../unitypkg/Editor/Tools/scene/SceneToolSupport.cs", import.meta.url), "utf8");
  const load = await readFile(new URL("../unitypkg/Editor/Tools/scene/load.cs", import.meta.url), "utf8");
  assert.match(support, /ResolveExistingScenePath\(string path, string name\)/);
  assert.match(support, /AssetDatabase\.FindAssets\("t:Scene"\)/);
  assert.match(support, /E_SCENE_AMBIGUOUS/);
  assert.match(load, /ResolveExistingScenePath\(request\.Path, request\.Name\)/);
  assert.match(load, /Summary = "Load or reload one scene; EditMode reload discards unsaved in-memory scene state\."/);
  assert.match(load, /OpenSceneMode\.Single, replacing the currently open scene state without saving scene assets/);
  assert.match(load, /avoids traversing unrelated shared Undo history/);
  assert.match(load, /Input = new Request \{ Name = "Main" \}/);
});

test("scene.gameobject and scene.hierarchy are the retained typed readers", async () => {
  const listSource = await readFile(new URL("../unitypkg/Editor/Tools/scene/list-opened.cs", import.meta.url), "utf8");
  const gameObjectSource = await readFile(new URL("../unitypkg/Editor/Tools/scene/gameobject.cs", import.meta.url), "utf8");
  const hierarchySource = await readFile(new URL("../unitypkg/Editor/Tools/scene/hierarchy.cs", import.meta.url), "utf8");
  assert.match(listSource, /class SceneListOpenedTool : IUnityCliTool</);
  assert.match(gameObjectSource, /Name = "scene\.gameobject"[\s\S]*PlayModeSupport = "supported"/);
  assert.match(gameObjectSource, /scene\.hierarchy or go\.find discovery/);
  assert.match(hierarchySource, /Name = "scene\.hierarchy"[\s\S]*PlayModeSupport = "supported"/);
});

test("scene bridge spec forwards structured file, GameObject, and hierarchy input", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/scene.js", import.meta.url), "utf8");
  assert.match(spec, /stringValue\(structured\.path\)/, "sceneFileArgs should forward structured path");
  assert.match(spec, /numberValue\(structured\.maxDepth\)/, "sceneHierarchyArgs should forward structured maxDepth");
  assert.match(spec, /numberValue\(structured\.pageSize\)/, "sceneHierarchyArgs should forward structured pageSize");
  assert.match(spec, /sceneGameObjectReadArgs/);
  assert.match(spec, /boolValue\(s\.frameAll\)/, "view-frame should forward structured frameAll");
});
