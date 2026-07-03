import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { readFile, readdir } from "node:fs/promises";

/** 递归列举 .cs 文件。 */
async function listCsFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCsFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

const SCENE_COMMANDS = ["scene.active", "scene.build-settings", "scene.screenshot", "scene.create", "scene.load", "scene.save", "scene.unload", "scene.set-active", "scene.list-opened", "scene.data", "scene.move-object", "scene.validate", "scene.view-frame"];

/**
 * Scene 域契约测试（除已 PASS scene.hierarchy 外的 11 个工具）。
 * 对齐 CoplayDev manage_scene，验证 metadata/bridge/contract/C# 一致，
 * 错误恢复提示迁移到 `tools run` 形态，SceneListOpened/SceneData 业务字段不被 envelope 吞掉。
 */

test("scene group exposes 14 tools total", () => {
  const tools = filterToolsByGroup("scene").map((t) => t.name).sort();
  assert.equal(tools.length, 14);
  for (const name of SCENE_COMMANDS) {
    assert.ok(tools.includes(name), `${name} should exist`);
  }
});

test("scene read tools are supported/non-destructive", () => {
  for (const name of ["scene.active", "scene.build-settings", "scene.list-opened", "scene.data"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.playModeSupport, "supported", `${name} play mode supported`);
    assert.ok(tool.outputSchema, `${name} output schema`);
  }
});

test("scene write tools are session-affecting with proper safety", () => {
  const create = findToolByName("scene.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("scene-modify"));

  const save = findToolByName("scene.save")!;
  assert.equal(save.destructiveHint, true);
  assert.equal(save.supportsDryRun, true);
  assert.equal(save.playModeSupport, "session-affecting");

  const unload = findToolByName("scene.unload")!;
  assert.equal(unload.destructiveHint, false);
  assert.equal(unload.supportsDryRun, true);
});

test("scene editor-only tools (screenshot/view-frame)", () => {
  for (const name of ["scene.screenshot", "scene.view-frame"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "editor-only", `${name} editor-only`);
  }
});

test("bridge specs resolve for scene tools", () => {
  // scene.* 全部迁移为 IUnityCliTool → 路由 project-tool.run,toolName=scene.<cmd>
  for (const [cmd, toolName] of [["active", "scene.active"], ["build-settings", "scene.build-settings"], ["screenshot", "scene.screenshot"], ["create", "scene.create"], ["load", "scene.load"], ["save", "scene.save"], ["unload", "scene.unload"], ["set-active", "scene.set-active"], ["list-opened", "scene.list-opened"], ["data", "scene.data"], ["view-frame", "scene.view-frame"]] as const) {
    const spec = bridgeCommandRegistry.lookup("scene", cmd);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `scene ${cmd} should route via project-tool.run`);
    assert.equal(spec?.toolName, toolName, `scene ${cmd} toolName should match`);
  }
});

test("contract entries exist for scene tools with present C# handler", () => {
  for (const name of SCENE_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# handlers exist for scene commands (RegisterCommand or IUnityCliTool)", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of SCENE_COMMANDS) {
    // 接受 RegisterCommand(旧)或 IUnityCliTool(新,Name="...")两种形态
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("scene C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  // scene.* 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/scene/*.cs);错误提示在工具文件内。
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/Tools/scene/", import.meta.url));
  const source = (await Promise.all(csFiles.map((f) => readFile(f, "utf8")))).join("\n");
  assert.equal(source.includes("unity_cli scene"), false, "must not reference legacy 'unity_cli scene'");
  assert.equal(source.includes("unity_cli asset search"), false, "must not reference legacy 'unity_cli asset search'");
  assert.match(source, /tools run scene\.save/, "error hints should route to tools run scene.save");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("scene.list-opened and scene.data are IUnityCliTool returning typed Result", async () => {
  // scene.* 已迁移为 IUnityCliTool:工具返回 typed Result(框架序列化),不再手拼 SuccessJson。
  const listSource = await readFile(new URL("../unitypkg/Editor/Tools/scene/list-opened.cs", import.meta.url), "utf8");
  assert.match(listSource, /class SceneListOpenedTool : IUnityCliTool</, "SceneListOpened should be an IUnityCliTool");
  assert.match(listSource, /Name = "scene\.list-opened"/, "list-opened tool name");
  const dataSource = await readFile(new URL("../unitypkg/Editor/Tools/scene/data.cs", import.meta.url), "utf8");
  assert.match(dataSource, /class SceneDataTool : IUnityCliTool</, "SceneData should be an IUnityCliTool");
  assert.match(dataSource, /Name = "scene\.data"/, "data tool name");
});

test("scene bridge spec forwards structured --input fields for file ops and data", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/scene.js", import.meta.url), "utf8");
  assert.match(spec, /stringValue\(structured\.path\)/, "sceneFileArgs should forward structured path");
  assert.match(spec, /numberValue\(structured\.maxDepth\)/, "sceneDataArgs should forward structured maxDepth");
  assert.match(spec, /boolValue\(s\.frameAll\)/, "view-frame should forward structured frameAll");
});
