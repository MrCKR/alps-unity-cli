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

const PREFAB_COMMANDS = ["prefab.info", "prefab.create", "prefab.modify", "prefab.stage-open", "prefab.stage-save", "prefab.stage-close", "prefab.hierarchy", "prefab.instantiate", "prefab.unpack"];

/**
 * Prefab 域契约测试。对齐 CoplayDev manage_prefabs 的 headless/interactive 两模式与 hierarchy/stage 工作流，
 * 验证 metadata/bridge/contract/C# 一致，错误恢复提示迁移到 `tools run` 形态，bridge 透传结构化字段。
 */

test("prefab group tools exist with correct safety hints", () => {
  const tools = filterToolsByGroup("prefab").map((t) => t.name).sort();
  assert.deepEqual(tools, ["prefab.create", "prefab.hierarchy", "prefab.info", "prefab.instantiate", "prefab.modify", "prefab.stage-close", "prefab.stage-open", "prefab.stage-save", "prefab.unpack"]);

  const info = findToolByName("prefab.info")!;
  assert.equal(info.destructiveHint, false);
  assert.equal(info.playModeSupport, "supported");

  const create = findToolByName("prefab.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("asset-write"));

  const stageOpen = findToolByName("prefab.stage-open")!;
  assert.equal(stageOpen.playModeSupport, "editor-only", "stage-open is an editor UI session op");

  const instantiate = findToolByName("prefab.instantiate")!;
  assert.equal(instantiate.destructiveHint, false);
  assert.equal(instantiate.supportsDryRun, true);
  assert.equal(instantiate.playModeSupport, "session-affecting");

  const unpack = findToolByName("prefab.unpack")!;
  assert.equal(unpack.destructiveHint, false);
  assert.equal(unpack.supportsDryRun, true);
});

test("prefab.hierarchy exposes output schema with paging", () => {
  const hierarchy = findToolByName("prefab.hierarchy")!;
  const out = hierarchy.outputSchema?.properties as Record<string, unknown>;
  assert.ok(out.children, "hierarchy output should expose children");
  assert.ok(out.pageInfo, "hierarchy output should expose pageInfo");
});

test("bridge specs resolve for prefab tools", () => {
  const lookups: Record<string, string> = { info: "prefab.info", create: "prefab.create", modify: "prefab.modify", "stage-open": "prefab.stage-open", "stage-save": "prefab.stage-save", "stage-close": "prefab.stage-close", hierarchy: "prefab.hierarchy", instantiate: "prefab.instantiate", unpack: "prefab.unpack" };
  for (const [cmd, expected] of Object.entries(lookups)) {
    const spec = bridgeCommandRegistry.lookup("prefab", cmd);
    assert.equal(spec?.bridgeCommand, expected, `prefab ${cmd} should resolve`);
  }
});

test("contract entries exist for prefab tools with present C# handler", () => {
  for (const name of PREFAB_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in command contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for prefab commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of PREFAB_COMMANDS) {
    assert.ok(
      allSource.includes(`RegisterCommand(registry, "${command}"`),
      `${command} should be registered via RegisterCommand in C#`
    );
  }
});

test("prefab C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/PrefabCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli prefab"), false, "must not reference legacy 'unity_cli prefab'");
  assert.equal(source.includes("unity_cli scene hierarchy"), false, "must not reference legacy 'unity_cli scene hierarchy'");
  assert.equal(source.includes("unity_cli asset search"), false, "must not reference legacy 'unity_cli asset search'");
  assert.match(source, /tools run prefab\.hierarchy/, "error hints should route to tools run prefab.hierarchy");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("prefab bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/prefab.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "addChild"\)/, "bridge should forward structured addChild");
  assert.match(spec, /stringField\(flags, "removeChild"\)/, "bridge should forward structured removeChild");
  assert.match(spec, /boolField\(flags, "save"\)/, "bridge should forward structured save");
  assert.match(spec, /numberField\(flags, "pageSize"\)/, "bridge should forward structured pageSize");
});
