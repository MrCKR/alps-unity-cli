import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readCsSources, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

const TEXTURE_COMMANDS = ["texture.create", "texture.delete", "texture.generate", "texture.import-settings", "texture.info", "texture.modify", "texture.sprite"];

test("texture group tools are contract-discovered with correct live descriptor hints", async () => {
  assert.deepEqual(contractToolNames("texture"), [...TEXTURE_COMMANDS].sort());

  const tools = await discoverLiveTools(
    makeLiveTool({ name: "texture.info", summary: "Read texture info.", description: "Read texture info.", group: "texture", outputSchema: { type: "object", properties: { texture: { type: "object" }, importSettings: { type: "object" } } } }),
    makeLiveTool({ name: "texture.create", summary: "Create a texture asset.", description: "Create a texture asset.", group: "texture", playModeSupport: "session-affecting", sideEffects: ["asset-write"] }),
    makeLiveTool({ name: "texture.generate", summary: "Generate a texture.", description: "Generate a texture.", group: "texture", destructiveHint: true, supportsDryRun: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "texture.modify", summary: "Modify texture pixels.", description: "Modify texture pixels.", group: "texture", destructiveHint: true, supportsDryRun: true, sideEffects: ["asset-write"], inputSchema: { type: "object", properties: { fillColor: { type: "string" }, x: { type: "integer" } } } }),
    makeLiveTool({ name: "texture.delete", summary: "Delete a texture asset.", description: "Delete a texture asset.", group: "texture", destructiveHint: true, supportsDryRun: true, sideEffects: ["asset-delete"] }),
    makeLiveTool({ name: "texture.import-settings", summary: "Update texture importer settings.", description: "Update texture importer settings.", group: "texture", destructiveHint: true, playModeSupport: "editor-only", sideEffects: ["asset-import"], inputSchema: { type: "object", properties: { textureType: { type: "string", enum: ["Default", "Sprite"] }, wrapMode: { type: "string", enum: ["Repeat", "Clamp"] }, filterMode: { type: "string", enum: ["Point", "Bilinear"] }, compression: { type: "string", enum: ["None", "Compressed"] } } }, outputSchema: { type: "object", properties: { changed: { type: "boolean" } } } }),
    makeLiveTool({ name: "texture.sprite", summary: "Configure sprite slices.", description: "Configure sprite slices.", group: "texture", destructiveHint: true, playModeSupport: "editor-only", sideEffects: ["asset-import"], inputSchema: { type: "object", properties: { slices: { type: "array" } } } })
  );

  const info = tools.get("texture.info")!;
  assert.equal(info.destructiveHint, false);
  assert.equal(info.handler, "bridge");
  assert.equal(info.playModeSupport, "supported");

  const create = tools.get("texture.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const generate = tools.get("texture.generate")!;
  assert.equal(generate.destructiveHint, true);
  assert.equal(generate.supportsDryRun, true);
  assert.equal(generate.playModeSupport, "session-affecting");

  const modify = tools.get("texture.modify")!;
  assert.equal(modify.destructiveHint, true);
  assert.equal(modify.supportsDryRun, true);
  assert.ok(modify.sideEffects?.includes("asset-write"));

  const del = tools.get("texture.delete")!;
  assert.equal(del.destructiveHint, true);
  assert.equal(del.supportsDryRun, true);
  assert.ok(del.sideEffects?.includes("asset-delete"));

  const importSettings = tools.get("texture.import-settings")!;
  assert.equal(importSettings.destructiveHint, true);
  assert.equal(importSettings.playModeSupport, "editor-only");
  assert.ok(importSettings.sideEffects?.includes("asset-import"));
  const imp = importSettings.inputSchema.properties as Record<string, { enum?: string[] }>;
  assert.ok(imp.textureType?.enum?.includes("Sprite"));
  assert.ok(imp.wrapMode?.enum?.includes("Clamp"));
  assert.ok(imp.filterMode?.enum?.includes("Bilinear"));
  assert.ok(imp.compression?.enum?.includes("Compressed"));

  const out = info.outputSchema?.properties as Record<string, unknown>;
  assert.ok(out.texture);
  assert.ok(out.importSettings);

  const sprite = tools.get("texture.sprite")!;
  assert.equal(sprite.destructiveHint, true);
  assert.equal(sprite.playModeSupport, "editor-only");
  const spriteProps = sprite.inputSchema.properties as Record<string, unknown>;
  assert.ok(spriteProps.slices);
  const modifyProps = tools.get("texture.modify")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(modifyProps.fillColor);
  assert.ok(modifyProps.x);
});

test("bridge specs resolve for texture tools", () => {
  for (const command of ["info", "create", "sprite", "modify", "delete", "import-settings", "generate"]) {
    const spec = bridgeCommandRegistry.lookup("texture", command);
    assert.equal(spec?.bridgeCommand, "catalog.editor.run");
    assert.equal(spec?.toolName, `texture.${command}`);
  }
});

test("contract entries exist for texture tools with present C# handler", () => {
  for (const name of TEXTURE_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("texture C# handlers are registered", async () => {
  const allSource = await readEditorCsSources();
  for (const command of TEXTURE_COMMANDS) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool);
  }
});

test("texture C# error recovery hints use current tools run forms", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/texture/", import.meta.url));
  assert.match(source, /tools run asset\.search/);
});

test("texture bridge spec forwards structured --input fields (width/enum/bool), not only flags", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/texture.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "textureType"\)/);
  assert.match(spec, /numberField\(flags, "width"\)/);
  assert.match(spec, /boolField\(flags, "readable"\)/);
  assert.match(spec, /stringField\(flags, "spriteMode"\)/);
  assert.match(spec, /structured\.slices/);
  assert.match(spec, /stringField\(flags, "fillColor"\)/);
});

test("texture C# implements sprite slices and texture-specific delete", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/texture/", import.meta.url));
  assert.match(source, /BuildSpriteSlices\(List<TextureSliceInput> slices\)[\s\S]*SpriteMetaData/);
  assert.match(source, /AssetDatabase\.DeleteAsset/);
  assert.match(source, /ImageConversion\.LoadImage[\s\S]*SetPixel/);
});

test("texture delete and generate return only observed disk and AssetDatabase state", async () => {
  const deleteSource = await readFile(new URL("../unitypkg/Editor/Tools/texture/delete.cs", import.meta.url), "utf8");
  const generateSource = await readFile(new URL("../unitypkg/Editor/Tools/texture/generate.cs", import.meta.url), "utf8");

  assert.match(deleteSource, /ctx\.Wait\.Until\([\s\S]*!File\.Exists\(fullPath\)[\s\S]*!Directory\.Exists\(fullPath\)[\s\S]*!AssetDatabase\.IsValidFolder\(path\)[\s\S]*LoadMainAssetAtPath\(path\) == null/);
  assert.doesNotMatch(deleteSource, /AssetPathToGUID\(path\)/);
  assert.match(deleteSource, /E_TEXTURE_DELETE_PROOF_MISSING/);
  assert.match(generateSource, /bytes == null \|\| bytes\.Length == 0/);
  assert.match(generateSource, /File\.Exists\(fullPath\) \|\| new FileInfo\(fullPath\)\.Length <= 0/);
  assert.match(generateSource, /AssetDatabase\.ImportAsset\(path,[\s\S]*LoadAssetAtPath<Texture2D>\(path\)/);
  assert.match(generateSource, /SizeBytes = sizeBytes/);
  assert.match(generateSource, /E_TEXTURE_GENERATE_FAILED/);
});
