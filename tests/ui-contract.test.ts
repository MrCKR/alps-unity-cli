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

const UI_COMMANDS = ["ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.info", "ui.document.create", "ui.document.update", "ui.document.delete", "ui.hierarchy", "ui.attach-document", "ui.detach-document", "ui.create-panel-settings", "ui.update-panel-settings", "ui.get-visual-tree", "ui.modify-visual-element", "ui.link-stylesheet", "ui.list-documents", "ui.render-preview"];

/**
 * UI 域契约测试：uGUI (canvas/text/button/image) + UIToolkit (document/hierarchy/attach/panel/visual-tree)。
 * 对齐 CoplayDev manage_ui，验证 metadata/bridge/contract/C# 一致，错误恢复提示迁移到 `tools run` 形态，
 * ui.get-visual-tree 业务字段不被 envelope 吞掉。
 */

test("ui group exposes the UI tools", () => {
  const tools = filterToolsByGroup("ui").map((t) => t.name).sort();
  assert.deepEqual(tools, ["ui.attach-document", "ui.button.create", "ui.canvas.create", "ui.create-panel-settings", "ui.detach-document", "ui.document.create", "ui.document.delete", "ui.document.info", "ui.document.update", "ui.get-visual-tree", "ui.hierarchy", "ui.image.create", "ui.link-stylesheet", "ui.list-documents", "ui.modify-visual-element", "ui.render-preview", "ui.text.create", "ui.update-panel-settings"]);
});

test("ui create tools are session-affecting with scene/asset side effects", () => {
  for (const name of ["ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.create"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.playModeSupport, "session-affecting", `${name} session-affecting`);
    assert.ok(tool.sideEffects?.length, `${name} should declare side effects`);
    assert.ok(tool.examples?.length, `${name} examples`);
  }
});

test("ui read tools are supported in any play mode", () => {
  for (const name of ["ui.document.info", "ui.hierarchy", "ui.get-visual-tree", "ui.list-documents"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.playModeSupport, "supported", `${name} play mode supported`);
    assert.ok(tool.outputSchema, `${name} output schema`);
  }
});

test("ui mutating tools declare safety and dry-run support", () => {
  const attach = findToolByName("ui.attach-document")!;
  assert.equal(attach.destructiveHint, false);
  assert.equal(attach.riskLevel, "medium");
  assert.equal(attach.playModeSupport, "session-affecting");

  const detach = findToolByName("ui.detach-document")!;
  assert.equal(detach.destructiveHint, false);
  assert.equal(detach.supportsDryRun, true);

  for (const name of ["ui.document.update", "ui.document.delete", "ui.create-panel-settings", "ui.update-panel-settings", "ui.link-stylesheet", "ui.render-preview"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, true, `${name} should require confirmation`);
    assert.equal(tool.playModeSupport, "session-affecting");
  }
});

test("bridge specs resolve for ui tools", () => {
  const lookups: Record<string, string> = { "canvas/create": "ui.canvas.create", "text/create": "ui.text.create", "button/create": "ui.button.create", "image/create": "ui.image.create", "document/info": "ui.document.info", "document/create": "ui.document.create", "document/update": "ui.document.update", "document/delete": "ui.document.delete", "hierarchy": "ui.hierarchy", "attach-document": "ui.attach-document", "detach-document": "ui.detach-document", "create-panel-settings": "ui.create-panel-settings", "update-panel-settings": "ui.update-panel-settings", "get-visual-tree": "ui.get-visual-tree", "modify-visual-element": "ui.modify-visual-element", "link-stylesheet": "ui.link-stylesheet", "list-documents": "ui.list-documents", "render-preview": "ui.render-preview" };
  for (const [cmd, expected] of Object.entries(lookups)) {
    const spec = bridgeCommandRegistry.lookup("ui", cmd);
    assert.equal(spec?.bridgeCommand, expected, `ui ${cmd} should resolve`);
  }
});

test("contract entries exist for ui tools with present C# handler", () => {
  for (const name of UI_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for ui commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of UI_COMMANDS) {
    assert.ok(allSource.includes(`RegisterCommand(registry, "${command}"`), `${command} should be registered`);
  }
});

test("ui C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/UiCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli ui"), false, "must not reference legacy 'unity_cli ui'");
});

test("ui C# implements document CRUD, stylesheet linking, listing and preview", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/UiCommands.cs", import.meta.url), "utf8");
  const block = source.slice(source.indexOf("UiGetVisualTree(BridgeRequest request)"), source.indexOf("private static void CollectVisualElement"));
  assert.match(block, /return SuccessJson\(w\.EndObject/, "UiGetVisualTree must wrap payload in SuccessJson");
  assert.match(source, /UiDocumentUpdate[\s\S]*File\.WriteAllText/, "ui.document.update should write UXML content");
  assert.match(source, /UiDocumentDelete[\s\S]*AssetDatabase\.DeleteAsset/, "ui.document.delete should delete UI assets");
  assert.match(source, /UiDetachDocument[\s\S]*Undo\.DestroyObjectImmediate/, "ui.detach-document should remove UIDocument component");
  assert.match(source, /UiLinkStylesheet[\s\S]*project:\/\/database/, "ui.link-stylesheet should write Unity project stylesheet references");
  assert.match(source, /UiListDocuments[\s\S]*AssetDatabase\.FindAssets/, "ui.list-documents should discover UI assets");
  assert.match(source, /UiRenderPreview[\s\S]*AssetPreview/, "ui.render-preview should use Unity AssetPreview");
  assert.match(source, /UiModifyVisualElement[\s\S]*FindVisualElementByName/, "ui.modify-visual-element should edit a live VisualElement");
  assert.match(source, /ApplyOptionalPanelSettings[\s\S]*targetTexture/, "PanelSettings tools should accept optional PanelSettings fields");
});

test("ui bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/ui.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "parent"\)/, "bridge should forward structured parent");
  assert.match(spec, /numberField\(flags, "fontSize"\)/, "bridge should forward structured fontSize");
  assert.match(spec, /stringField\(flags, "scaleMode"\)/, "bridge should forward structured scaleMode");
  assert.match(spec, /numberField\(flags, "maxDepth"\)/, "bridge should forward structured maxDepth");
  assert.match(spec, /resolveUiContent/, "bridge should support ui.document.update content-file");
  assert.match(spec, /ui\.list-documents/, "bridge should register ui.list-documents");
  assert.match(spec, /uiVisualElementArgs/, "bridge should forward ui.modify-visual-element args");
});
