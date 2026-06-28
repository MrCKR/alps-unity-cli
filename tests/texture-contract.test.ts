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

/**
 * Texture 域契约测试：texture.info / create / sprite / import-settings / generate。
 * 对齐 CoplayDev manage_texture 的 enum 文档化与生成能力，
 * 验证 metadata/bridge/contract/C# 四层一致，错误恢复提示迁移到 `tools run` 形态，
 * 且 bridge spec 透传结构化 --input 字段（避免 shader 组同类的 descriptor/实现不一致）。
 */

test("texture group tools exist with correct safety hints", () => {
  const tools = filterToolsByGroup("texture").map((t) => t.name).sort();
  assert.deepEqual(tools, ["texture.create", "texture.delete", "texture.generate", "texture.import-settings", "texture.info", "texture.modify", "texture.sprite"]);

  const info = findToolByName("texture.info")!;
  assert.equal(info.destructiveHint, false);
  assert.equal(info.handler, "bridge");
  assert.equal(info.playModeSupport, "supported");
  assert.equal(info.equivalentUnityMcp, "manage_texture.get_info");

  const create = findToolByName("texture.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const generate = findToolByName("texture.generate")!;
  assert.equal(generate.destructiveHint, true);
  assert.equal(generate.supportsDryRun, true);
  assert.equal(generate.playModeSupport, "session-affecting");

  const modify = findToolByName("texture.modify")!;
  assert.equal(modify.destructiveHint, true);
  assert.equal(modify.supportsDryRun, true);
  assert.ok(modify.sideEffects?.includes("asset-write"));

  const del = findToolByName("texture.delete")!;
  assert.equal(del.destructiveHint, true);
  assert.equal(del.supportsDryRun, true);
  assert.ok(del.sideEffects?.includes("asset-delete"));

  const importSettings = findToolByName("texture.import-settings")!;
  assert.equal(importSettings.playModeSupport, "session-affecting");
  assert.ok(importSettings.sideEffects?.includes("asset-import"));
});

test("texture.import-settings exposes documented enum values for importer fields", () => {
  const imp = findToolByName("texture.import-settings")!;
  const props = imp.inputSchema.properties as Record<string, { enum?: string[] }>;
  assert.ok(props.textureType?.enum?.length, "textureType should document enum values");
  assert.ok(props.textureType?.enum?.includes("Sprite"), "textureType enum should include Sprite");
  assert.ok(props.wrapMode?.enum?.includes("Clamp"), "wrapMode enum should include Clamp");
  assert.ok(props.filterMode?.enum?.includes("Bilinear"), "filterMode enum should include Bilinear");
  assert.ok(props.compression?.enum?.includes("Compressed"), "compression enum should include Compressed");
});

test("texture.info and import-settings expose output schemas", () => {
  const info = findToolByName("texture.info")!;
  const out = info.outputSchema?.properties as Record<string, unknown>;
  assert.ok(out.texture, "info output should expose texture object");
  assert.ok(out.importSettings, "info output should expose importSettings object");
});

test("texture.sprite and texture.modify expose structured mutation inputs", () => {
  const spriteProps = findToolByName("texture.sprite")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(spriteProps.slices, "texture.sprite should accept sprite sheet slices");
  const modifyProps = findToolByName("texture.modify")!.inputSchema.properties as Record<string, unknown>;
  assert.ok(modifyProps.fillColor, "texture.modify should expose fillColor alias");
  assert.ok(modifyProps.x, "texture.modify should expose region x");
});

test("bridge specs resolve for texture tools", () => {
  for (const command of ["info", "create", "sprite", "modify", "delete", "import-settings", "generate"]) {
    const spec = bridgeCommandRegistry.lookup("texture", command);
    assert.equal(spec?.bridgeCommand, `texture.${command}`);
  }
});

test("contract entries exist for texture tools with present C# handler", () => {
  for (const name of ["texture.info", "texture.create", "texture.sprite", "texture.modify", "texture.delete", "texture.import-settings", "texture.generate"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for texture commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["texture.info", "texture.create", "texture.sprite", "texture.modify", "texture.delete", "texture.import-settings", "texture.generate"]) {
    assert.ok(
      allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should be registered via RegisterCommand in C#`
    );
  }
});

test("texture C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/TextureCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli texture info"), false, "must not reference legacy 'unity_cli texture info'");
  assert.equal(source.includes("unity_cli asset search"), false, "must not reference legacy 'unity_cli asset search'");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("texture bridge spec forwards structured --input fields (width/enum/bool), not only flags", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/texture.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "textureType"\)/, "bridge should forward structured textureType");
  assert.match(spec, /numberField\(flags, "width"\)/, "bridge should forward structured width");
  assert.match(spec, /boolField\(flags, "readable"\)/, "bridge should forward structured readable bool");
  assert.match(spec, /stringField\(flags, "spriteMode"\)/, "bridge should forward structured spriteMode");
  assert.match(spec, /structured\.slices/, "bridge should forward structured sprite slices");
  assert.match(spec, /stringField\(flags, "fillColor"\)/, "bridge should forward structured fillColor");
});

test("texture C# implements sprite slices and texture-specific delete", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/TextureCommands.cs", import.meta.url), "utf8");
  assert.match(source, /ParseSpriteSlices[\s\S]*SpriteMetaData/, "C# should parse sprite slice metadata");
  assert.match(source, /TextureDelete[\s\S]*AssetDatabase\.DeleteAsset/, "texture.delete should delete through AssetDatabase");
  assert.match(source, /TextureModify[\s\S]*ImageConversion\.LoadImage[\s\S]*SetPixel/, "texture.modify should edit decoded texture pixels");
});
