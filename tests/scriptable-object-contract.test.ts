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
 * ScriptableObject 域契约测试：scriptable-object.create / info / list-types / modify。
 * 对齐 CoplayDev manage_scriptable_object 的 SerializedObject property 修改思路，
 * 验证 metadata/bridge/contract/C# 四层一致，错误恢复提示迁移到 `tools run` 形态，
 * 且 bridge spec 透传结构化 --input 字段。
 */

test("scriptable-object group tools exist with correct safety hints", () => {
  const tools = filterToolsByGroup("scriptable-object").map((t) => t.name).sort();
  assert.deepEqual(tools, ["scriptable-object.create", "scriptable-object.info", "scriptable-object.list-types", "scriptable-object.modify"]);

  const create = findToolByName("scriptable-object.create")!;
  assert.equal(create.destructiveHint, true);
  assert.equal(create.supportsDryRun, true);
  assert.equal(create.handler, "bridge");
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const info = findToolByName("scriptable-object.info")!;
  assert.equal(info.playModeSupport, "supported");

  const listTypes = findToolByName("scriptable-object.list-types")!;
  assert.equal(listTypes.destructiveHint, false);
  assert.equal(listTypes.playModeSupport, "supported");

  const modify = findToolByName("scriptable-object.modify")!;
  assert.equal(modify.destructiveHint, true);
  assert.equal(modify.supportsDryRun, true);
  assert.equal(modify.playModeSupport, "session-affecting");
  assert.ok(modify.sideEffects?.includes("asset-write"));
  assert.equal(modify.riskLevel, "high");
});

test("scriptable-object.info exposes output schema with properties", () => {
  const info = findToolByName("scriptable-object.info")!;
  const out = info.outputSchema?.properties as Record<string, unknown>;
  assert.ok(out.scriptableObject, "info output should expose scriptableObject object");
});

test("bridge specs resolve for scriptable-object tools", () => {
  for (const command of ["create", "info", "list-types", "modify"]) {
    const spec = bridgeCommandRegistry.lookup("scriptable-object", command);
    assert.equal(spec?.bridgeCommand, "project-tool.run");
  }
});

test("contract entries exist for scriptable-object tools with present C# handler", () => {
  for (const name of ["scriptable-object.create", "scriptable-object.info", "scriptable-object.list-types", "scriptable-object.modify"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls removed for scriptable-object commands (migrated to IUnityCliTool)", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["scriptable-object.create", "scriptable-object.info", "scriptable-object.list-types", "scriptable-object.modify"]) {
    assert.ok(
      !allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should NOT be registered (migrated to IUnityCliTool)`
    );
  }
});

test("scriptable-object C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/scriptable-object/ScriptableObjectToolSupport.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli asset search"), false, "must not reference legacy 'unity_cli asset search'");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("scriptable-object bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/scriptable-object.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(input, flags, "type"\)/, "bridge should forward structured type");
  assert.match(spec, /stringField\(input, flags, "property"\)/, "bridge should forward structured property");
  assert.match(spec, /inputField\(input, "value"\)/, "bridge should forward structured value");
  assert.match(spec, /inputField\(input, "patches"\)/, "bridge should forward structured patches");
  assert.match(spec, /boolField\(input, flags, "overwrite"\)/, "bridge should forward structured overwrite");
});
