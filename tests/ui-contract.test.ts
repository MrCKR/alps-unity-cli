import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { dispatchTool } from "../src/tools/dispatcher.js";
import { contractToolNames, discoverLiveTools, readEditorCsSources } from "./helpers/csharp-test-helpers.js";
import { createUnityProject } from "./helpers/instance-registry-v3.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";

const UI_COMMANDS = [
  "ui.attach-document",
  "ui.button.create",
  "ui.canvas.create",
  "ui.create-panel-settings",
  "ui.detach-document",
  "ui.document.create",
  "ui.document.delete",
  "ui.document.info",
  "ui.document.update",
  "ui.get-visual-tree",
  "ui.hierarchy",
  "ui.image.create",
  "ui.link-stylesheet",
  "ui.list-documents",
  "ui.modify-visual-element",
  "ui.render-preview",
  "ui.text.create",
  "ui.update-panel-settings"
];

test("ui group exposes the UI tools via contract discovery", () => {
  assert.deepEqual(contractToolNames("ui"), [...UI_COMMANDS].sort());
});

test("ui live descriptors expose representative safety and schema semantics", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "ui.canvas.create", summary: "Create a Canvas.", description: "Create a Canvas.", group: "ui", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "ui.text.create", summary: "Create a UI Text.", description: "Create a UI Text.", group: "ui", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "ui.button.create", summary: "Create a UI Button.", description: "Create a UI Button.", group: "ui", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "ui.image.create", summary: "Create a UI Image.", description: "Create a UI Image.", group: "ui", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "ui.document.create", summary: "Create a UI Document.", description: "Create a UI Document.", group: "ui", playModeSupport: "session-affecting", sideEffects: ["asset-write"] }),
    makeLiveTool({ name: "ui.document.info", summary: "Read UI document info.", description: "Read UI document info.", group: "ui", outputSchema: { type: "object", properties: { document: { type: "object" } } } }),
    makeLiveTool({ name: "ui.hierarchy", summary: "Read UI hierarchy.", description: "Read UI hierarchy.", group: "ui", inputSchema: { type: "object", properties: { parent: { type: "string" }, query: { type: "string" }, className: { type: "string" }, pageSize: { type: "integer" } } }, outputSchema: { type: "object", properties: { items: { type: "array" } } } }),
    makeLiveTool({ name: "ui.get-visual-tree", summary: "Read UI Toolkit visual tree.", description: "Read UI Toolkit visual tree.", group: "ui", inputSchema: { type: "object", properties: { parent: { type: "string" }, query: { type: "string" }, className: { type: "string" }, pageSize: { type: "integer" } } }, outputSchema: { type: "object", properties: { items: { type: "array" } } } }),
    makeLiveTool({ name: "ui.list-documents", summary: "List UI documents.", description: "List UI documents.", group: "ui", outputSchema: { type: "object", properties: { items: { type: "array" } } } }),
    makeLiveTool({ name: "ui.attach-document", summary: "Attach a UIDocument.", description: "Attach a UIDocument.", group: "ui", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "ui.detach-document", summary: "Detach a UIDocument.", description: "Detach a UIDocument.", group: "ui", supportsDryRun: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "ui.document.update", summary: "Update a UI document asset.", description: "Update a UI document asset.", group: "ui", destructiveHint: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "ui.document.delete", summary: "Delete a UI document asset.", description: "Delete a UI document asset.", group: "ui", destructiveHint: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "ui.create-panel-settings", summary: "Create PanelSettings.", description: "Create PanelSettings.", group: "ui", destructiveHint: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "ui.update-panel-settings", summary: "Update PanelSettings.", description: "Update PanelSettings.", group: "ui", destructiveHint: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "ui.link-stylesheet", summary: "Link a stylesheet.", description: "Link a stylesheet.", group: "ui", destructiveHint: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "ui.render-preview", summary: "Render a preview.", description: "Render a preview.", group: "ui", destructiveHint: true, playModeSupport: "session-affecting" })
  );

  for (const name of ["ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.create"]) {
    const tool = tools.get(name)!;
    assert.equal(tool.destructiveHint, false);
    assert.equal(tool.playModeSupport, "session-affecting");
    assert.ok(tool.sideEffects?.length);
  }

  for (const name of ["ui.document.info", "ui.hierarchy", "ui.get-visual-tree", "ui.list-documents"]) {
    const tool = tools.get(name)!;
    assert.equal(tool.destructiveHint, false);
    assert.equal(tool.playModeSupport, "supported");
    assert.ok(tool.outputSchema);
  }

  const hierarchyProps = tools.get("ui.hierarchy")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(hierarchyProps.parent);
  assert.ok(hierarchyProps.query);
  assert.ok(hierarchyProps.className);
  assert.ok(hierarchyProps.pageSize);
  const visualProps = tools.get("ui.get-visual-tree")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(visualProps.parent);
  assert.ok(visualProps.query);
  assert.ok(visualProps.className);
  assert.ok(visualProps.pageSize);

  const attach = tools.get("ui.attach-document")!;
  assert.equal(attach.destructiveHint, false);
  assert.equal(attach.riskLevel, "medium");
  assert.equal(attach.playModeSupport, "session-affecting");

  const detach = tools.get("ui.detach-document")!;
  assert.equal(detach.destructiveHint, false);
  assert.equal(detach.supportsDryRun, true);

  for (const name of ["ui.document.update", "ui.document.delete", "ui.create-panel-settings", "ui.update-panel-settings", "ui.link-stylesheet", "ui.render-preview"]) {
    const tool = tools.get(name)!;
    assert.equal(tool.destructiveHint, true, `${name} should expose destructiveHint`);
    assert.equal(tool.playModeSupport, "session-affecting");
  }
});

test("ui.document.info descriptor states that every mode uses the selected open Editor", async () => {
  const source = await readFile("unitypkg/Editor/Tools/ui/document/info.cs", "utf8");
  assert.match(source, /Both modes execute through the selected open Editor\./);
});

test("ui.document.info cannot read a local file when the selected Unity Editor is offline", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-ui-document-live-only-"));
  const project = join(root, "Project");
  const dataDir = join(root, "data");
  const arbitraryFile = join(root, "readable.txt");
  try {
    await createUnityProject(project);
    await writeFile(arbitraryFile, "private local content\n", "utf8");
    const result = await dispatchTool("ui.document.info", undefined, {
      input: JSON.stringify({ path: arbitraryFile }),
      instance: project,
    }, { dataDir }, "req_ui_document_live_only");
    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.code, "E_INSTANCE_NOT_RUNNING");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("bridge specs resolve for ui tools", () => {
  const lookups: Record<string, string> = {
    "canvas.create": "ui.canvas.create",
    "text.create": "ui.text.create",
    "button.create": "ui.button.create",
    "image.create": "ui.image.create",
    "document.info": "ui.document.info",
    "document.create": "ui.document.create",
    "document.update": "ui.document.update",
    "document.delete": "ui.document.delete",
    hierarchy: "ui.hierarchy",
    "attach-document": "ui.attach-document",
    "detach-document": "ui.detach-document",
    "create-panel-settings": "ui.create-panel-settings",
    "update-panel-settings": "ui.update-panel-settings",
    "get-visual-tree": "ui.get-visual-tree",
    "modify-visual-element": "ui.modify-visual-element",
    "link-stylesheet": "ui.link-stylesheet",
    "list-documents": "ui.list-documents",
    "render-preview": "ui.render-preview"
  };
  for (const [cmd, expected] of Object.entries(lookups)) {
    const spec = bridgeCommandRegistry.lookup("ui", cmd);
    assert.equal(spec?.bridgeCommand, "catalog.editor.run", `ui ${cmd} should route via catalog.editor.run`);
    assert.equal(spec?.toolName, expected, `ui ${cmd} toolName should match`);
  }
});

test("contract entries exist for ui tools with present C# handler", () => {
  for (const name of UI_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("ui C# handlers are registered", async () => {
  const allSource = await readEditorCsSources();
  for (const command of UI_COMMANDS) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("ui C# error recovery hints identify valid targets", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/ui/document/info.cs", import.meta.url), "utf8");
  assert.match(source, /Pass a UIDocument GameObject or an Assets\/\*\.uxml path\./);
});

test("ui C# keeps one capability per physical file with CRUD, stylesheet, list, and preview behavior", async () => {
  const files = {
    update: "document/update.cs",
    delete: "document/delete.cs",
    detach: "detach-document.cs",
    stylesheet: "link-stylesheet.cs",
    list: "list-documents.cs",
    preview: "render-preview.cs",
    modify: "modify-visual-element.cs",
    panel: "create-panel-settings.cs"
  } as const;
  const sources = Object.fromEntries(await Promise.all(Object.entries(files).map(async ([key, file]) => [
    key,
    await readFile(new URL(`../unitypkg/Editor/Tools/ui/${file}`, import.meta.url), "utf8")
  ]))) as Record<keyof typeof files, string>;

  assert.match(sources.update, /class UiDocumentUpdateTool : IUnityCliTool[\s\S]*File\.WriteAllText[\s\S]*File\.ReadAllText[\s\S]*LoadAssetAtPath<VisualTreeAsset>/);
  assert.match(sources.update, /E_UI_DOCUMENT_READBACK_FAILED/);
  assert.match(sources.delete, /class UiDocumentDeleteTool : IUnityCliTool[\s\S]*AssetDatabase\.DeleteAsset[\s\S]*LoadAssetAtPath<UnityEngine\.Object>/);
  assert.match(sources.delete, /E_UI_DOCUMENT_READBACK_FAILED/);
  assert.match(sources.detach, /class UiDetachDocumentTool : IUnityCliTool[\s\S]*UnityCliSceneMutation\.DestroyObject\(doc\)[\s\S]*GetComponent<UIDocument>\(\)/);
  assert.match(sources.detach, /bool Attached/);
  assert.match(sources.stylesheet, /class UiLinkStylesheetTool : IUnityCliTool[\s\S]*File\.ReadAllText[\s\S]*File\.WriteAllText[\s\S]*LoadAssetAtPath<VisualTreeAsset>[\s\S]*XDocument\.Parse/);
  assert.match(sources.list, /class UiListDocumentsTool : IUnityCliTool[\s\S]*AssetDatabase\.FindAssets/);
  assert.match(sources.preview, /class UiRenderPreviewTool : IUnityCliTool/);
  assert.match(sources.preview, /public async ValueTask<Result> ExecuteAsync/);
  assert.match(sources.preview, /ScriptableObject\.CreateInstance<PanelSettings>\(\)/);
  assert.match(sources.preview, /new RenderTexture\(width, height, 24, RenderTextureFormat\.ARGB32\)/);
  assert.match(sources.preview, /await ctx\.Wait\.NextFrame\(2, ct\)/);
  assert.match(sources.preview, /ReadPixels\(new Rect\(0, 0, width, height\)/);
  assert.match(sources.preview, /ScreenshotToolSupport\.WriteScreenshotBytes\(outputPath, bytes, captureBudget\)/);
  assert.match(sources.preview, /finally[\s\S]*DestroyImmediate\(panelSettings\)/);
  assert.match(sources.preview, /ctx\.DryRun/);
  assert.doesNotMatch(sources.preview, /new Texture2D\(1, 1/);
  assert.match(sources.modify, /class UiModifyVisualElementTool : IUnityCliTool[\s\S]*rootVisualElement\.Q\(r\.ElementName\)[\s\S]*E_UI_ELEMENT_READBACK_FAILED/);
  assert.match(sources.modify, /UiVisualElementInfo[\s\S]*ClassListContains/);
  assert.match(sources.panel, /class UiCreatePanelSettingsTool : IUnityCliTool[\s\S]*referenceResolution[\s\S]*ImportAsset\(path, ImportAssetOptions\.ForceUpdate\)[\s\S]*LoadAssetAtPath<PanelSettings>/);
  assert.match(sources.panel, /observed\.scaleMode != scaleMode[\s\S]*observed\.referenceResolution != resolution[\s\S]*observed\.sortingOrder != sortingOrder/, "PanelSettings readback must compare the values fixed before the effect barrier");
});

test("ui bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/ui.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "parent"\)/);
  assert.match(spec, /numberField\(flags, "fontSize"\)/);
  assert.match(spec, /stringField\(flags, "scaleMode"\)/);
  assert.match(spec, /numberField\(flags, "maxDepth"\)/);
  assert.match(spec, /resolveUiContent/);
  assert.match(spec, /ui\.list-documents/);
  assert.match(spec, /uiVisualElementArgs/);
});
