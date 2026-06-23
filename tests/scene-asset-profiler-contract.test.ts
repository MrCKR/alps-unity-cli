import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";

/**
 * Scene / Asset / Prefab / Screenshot / Profiler / Console / Build / Physics
 * 跨大域契约测试：验证大域 4-8 新增工具的 metadata/bridge/contract 一致性。
 */

test("scene domain exposes unload/set-active/list-opened/data/view-frame", () => {
  for (const name of ["scene.unload", "scene.set-active", "scene.list-opened", "scene.data", "scene.view-frame"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
  }
  const unload = findToolByName("scene.unload")!;
  assert.equal(unload.destructiveHint, true);
  assert.equal(unload.supportsDryRun, true);
  const data = findToolByName("scene.data")!;
  assert.equal(data.readOnlyHint, true);
});

test("asset domain exposes search-built-in/get-data/preview", () => {
  for (const name of ["asset.search-built-in", "asset.get-data", "asset.preview"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.readOnlyHint, true);
  }
});

test("prefab domain exposes instantiate/unpack", () => {
  const instantiate = findToolByName("prefab.instantiate")!;
  assert.ok(instantiate);
  assert.equal(instantiate.destructiveHint, false);
  const unpack = findToolByName("prefab.unpack")!;
  assert.ok(unpack);
  assert.equal(unpack.destructiveHint, true);
  assert.equal(unpack.supportsDryRun, true);
});

test("script domain exposes update-or-create/find-in-file/capabilities", () => {
  assert.ok(findToolByName("script.update-or-create")?.destructiveHint);
  assert.equal(findToolByName("script.find-in-file")?.readOnlyHint, true);
  assert.equal(findToolByName("script.capabilities")?.readOnlyHint, true);
});

// screenshot 与 profiler 域详细契约测试已拆分至 screenshot-contract.test.ts 与 profiler-contract.test.ts

test("console domain exposes get/clear; editor exposes selection.set/active-tool.set", () => {
  assert.ok(findToolByName("console.get")?.readOnlyHint);
  assert.ok(findToolByName("console.clear")?.destructiveHint);
  assert.ok(findToolByName("editor.selection.set")?.destructiveHint);
  assert.ok(findToolByName("editor.active-tool.set")?.destructiveHint);
});

test("package domain exposes embed", () => {
  const embed = findToolByName("package.embed")!;
  assert.ok(embed);
  assert.equal(embed.destructiveHint, true);
  assert.equal(embed.supportsDryRun, true);
  assert.equal(embed.riskLevel, "high");
});

test("build domain exposes scenes/platform/settings alongside run/status/logs/cancel", () => {
  const buildTools = filterToolsByGroup("build").map((t) => t.name);
  assert.ok(buildTools.includes("build.scenes"));
  assert.ok(buildTools.includes("build.platform"));
  assert.ok(buildTools.includes("build.settings"));
  assert.ok(buildTools.includes("build.run"));
});

test("physics domain exposes raycast-all/linecast/shapecast/validate", () => {
  for (const name of ["physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.equal(tool.readOnlyHint, true);
  }
});

test("bridge specs resolve for all new domain 4-8 tools", () => {
  const checks = [
    ["scene", "unload", "scene.unload"],
    ["scene", "data", "scene.data"],
    ["go", "set-parent", "go.set-parent"],
    ["prefab", "instantiate", "prefab.instantiate"],
    ["prefab", "unpack", "prefab.unpack"],
    ["asset", "get-data", "asset.get-data"],
    ["shader", "list", "shader.list"],
    ["texture", "generate", "texture.generate"],
    ["script", "update-or-create", "script.update-or-create"],
    ["console", "get", "console.get"],
    ["package", "embed", "package.embed"],
    ["editor", "selection-set", "editor.selection.set"],
    ["build", "scenes", "build.scenes"],
    ["physics", "validate", "physics.validate"]
  ] as const;
  for (const [group, command, bridgeCommand] of checks) {
    const spec = bridgeCommandRegistry.lookup(group, command);
    assert.ok(spec, `${group} ${command} should resolve in bridge registry`);
    assert.equal(spec.bridgeCommand, bridgeCommand);
  }
});

test("all new tools have contract entries with present C# handler", () => {
  // screenshot/profiler/object/component/reflection 专属契约测试已拆分至分域文件
  const newTools = [
    "scene.unload", "scene.set-active", "scene.list-opened", "scene.data", "scene.view-frame",
    "go.set-parent", "prefab.instantiate", "prefab.unpack",
    "asset.search-built-in", "asset.get-data", "asset.preview",
    "shader.list", "shader.get-data", "texture.generate",
    "script.update-or-create", "script.find-in-file", "script.capabilities",
    "console.get", "console.clear", "editor.selection.set", "editor.active-tool.set", "package.embed",
    "build.scenes", "build.platform", "build.settings",
    "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate"
  ];
  for (const name of newTools) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});
