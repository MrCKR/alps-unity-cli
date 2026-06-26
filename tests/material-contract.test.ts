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
 * Material 域契约测试：material.info / create / assign / set。
 * 对齐 CoplayDev manage_material 与 IvanMurzak assets-material-create，
 * 验证 metadata/bridge/contract/C# 四层一致，且错误恢复提示已迁移到当前 `tools run` 形态。
 */

test("material group tools exist with correct safety hints", () => {
  const tools = filterToolsByGroup("material").map((t) => t.name).sort();
  assert.deepEqual(tools, ["material.assign", "material.create", "material.info", "material.set"]);

  const info = findToolByName("material.info")!;
  assert.equal(info.destructiveHint, false);
  assert.equal(info.handler, "bridge");
  assert.equal(info.playModeSupport, "supported");
  assert.equal(info.equivalentUnityMcp, "manage_material.get_material_info");

  const create = findToolByName("material.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));
  assert.equal(create.equivalentUnityMcp, "assets-material-create");

  const assign = findToolByName("material.assign")!;
  assert.equal(assign.playModeSupport, "session-affecting");
  assert.ok(assign.sideEffects?.includes("scene-modify"));

  const set = findToolByName("material.set")!;
  assert.equal(set.playModeSupport, "session-affecting");
  assert.ok(set.sideEffects?.includes("asset-write"));
});

test("material.info supports token-safe namePattern and output schema", () => {
  const info = findToolByName("material.info")!;
  const props = info.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.namePattern, "namePattern should be supported for token-safe reads");
  const output = info.outputSchema?.properties as Record<string, unknown>;
  assert.ok(output.material, "output schema should expose material object");
  assert.ok(output.nameFilterApplied, "output schema should expose nameFilterApplied");
});

test("bridge specs resolve for material tools", () => {
  for (const command of ["info", "create", "assign", "set"]) {
    const spec = bridgeCommandRegistry.lookup("material", command);
    assert.equal(spec?.bridgeCommand, `material.${command}`);
  }
});

test("contract entries exist for material tools with present C# handler", () => {
  for (const name of ["material.info", "material.create", "material.assign", "material.set"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for material commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of ["material.info", "material.create", "material.assign", "material.set"]) {
    assert.ok(
      allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should be registered via RegisterCommand in C#`
    );
  }
});

test("material C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/MaterialCommands.cs", import.meta.url), "utf8");
  // 旧顶层命令形态不得出现在错误恢复提示中
  assert.equal(source.includes("unity_cli material info"), false, "must not reference legacy 'unity_cli material info'");
  assert.equal(source.includes("unity_cli asset search"), false, "must not reference legacy 'unity_cli asset search'");
  // 当前形态应出现在恢复提示中
  assert.match(source, /tools run material\.info/, "error hints should route to tools run material.info");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("material.info C# implements namePattern filtering", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/MaterialCommands.cs", import.meta.url), "utf8");
  assert.match(source, /ParseNameFilter[\s\S]*namePattern/, "C# should parse namePattern into a filter set");
  assert.match(source, /nameFilterApplied/, "C# should report whether a name filter was applied");
});
