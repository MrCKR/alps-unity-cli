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

const GRAPHICS_COMMANDS = [
  "graphics.quality", "graphics.settings", "graphics.pipeline", "graphics.volume", "graphics.screenshot",
  "graphics.bake-start", "graphics.bake-cancel", "graphics.bake-status", "graphics.bake-clear", "graphics.bake-reflection-probe",
  "graphics.bake-settings", "graphics.light-probe-group", "graphics.reflection-probe", "graphics.probe-positions",
  "graphics.stats", "graphics.stats-counters", "graphics.stats-memory", "graphics.scene-debug",
  "graphics.skybox-get", "graphics.skybox-set-material", "graphics.skybox-set-properties", "graphics.skybox-set-ambient",
  "graphics.skybox-set-fog", "graphics.skybox-set-reflection", "graphics.skybox-set-sun",
  "graphics.pipeline-settings", "graphics.pipeline-quality", "graphics.pipeline-info"
];

/**
 * Graphics 域契约测试（28 工具）。
 * 验证 metadata/bridge/contract/C# 一致，错误恢复提示迁移到 `tools run` 形态，
 * 所有工具有 playModeSupport，bridge 透传结构化字段。
 */

test("graphics group exposes all 28 tools", () => {
  const tools = filterToolsByGroup("graphics").map((t) => t.name).sort();
  assert.equal(tools.length, 28);
  for (const name of GRAPHICS_COMMANDS) {
    assert.ok(tools.includes(name), `${name} should exist`);
  }
});

test("all graphics tools have playModeSupport", () => {
  for (const name of GRAPHICS_COMMANDS) {
    const tool = findToolByName(name)!;
    assert.ok(tool.playModeSupport, `${name} should have playModeSupport`);
  }
});

test("graphics read tools are supported", () => {
  for (const name of ["graphics.pipeline", "graphics.pipeline-info", "graphics.skybox-get", "graphics.stats", "graphics.stats-counters", "graphics.stats-memory", "graphics.bake-status", "graphics.volume"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "supported", `${name} should be supported`);
  }
});

test("bridge specs resolve for all graphics tools", () => {
  for (const name of GRAPHICS_COMMANDS) {
    const cmd = name.replace("graphics.", "");
    const spec = bridgeCommandRegistry.lookup("graphics", cmd);
    assert.ok(spec, `graphics ${cmd} should resolve`);
    assert.equal(spec.bridgeCommand, name);
  }
});

test("contract entries exist for all graphics tools with present C# handler", () => {
  for (const name of GRAPHICS_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for graphics commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of GRAPHICS_COMMANDS) {
    assert.ok(allSource.includes(`RegisterCommand(registry, "${command}"`), `${command} should be registered`);
  }
});

test("graphics C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/GraphicsCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli tools run"), false, "must not reference legacy 'unity_cli tools run' command form");
  assert.match(source, /tools run graphics\.quality/, "error hints should route to tools run graphics.quality");
});

test("graphics bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/graphics.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(s, "target"\)/, "bridge should forward structured target");
  assert.match(spec, /boolField\(s, "enabled"\)/, "bridge should forward structured enabled");
  assert.match(spec, /numberField\(s, "bounces"\)/, "bridge should forward structured bounces");
  assert.match(spec, /stringField\(s, "material"\)/, "bridge should forward structured material");
});
