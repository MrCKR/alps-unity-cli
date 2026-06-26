import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";

/**
 * Screenshot 域契约测试：game-view / scene-view / isolated-object / orbit。
 * 对齐 IvanMurzak screenshot 与 CoplayDev orbit/surround。
 * camera.screenshot 保留为主入口，不新增 screenshot.camera 重复主实现。
 */

test("screenshot domain exposes game-view/scene-view/isolated-object/orbit", () => {
  const screenshotTools = filterToolsByGroup("screenshot").map((t) => t.name);
  assert.ok(screenshotTools.includes("screenshot.game-view"));
  assert.ok(screenshotTools.includes("screenshot.scene-view"));
  assert.ok(screenshotTools.includes("screenshot.isolated-object"));
  assert.ok(screenshotTools.includes("screenshot.orbit"));
  // camera.screenshot remains the main camera entry (not duplicated as screenshot.camera)
  assert.ok(findToolByName("camera.screenshot"));
  assert.ok(!findToolByName("screenshot.camera"));
});

test("screenshot tools have correct safety hints", () => {
  for (const name of ["screenshot.game-view", "screenshot.scene-view", "screenshot.isolated-object", "screenshot.orbit"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false, `${name} should not be destructive`);
    assert.equal(tool.handler, "bridge");
  }
});

test("bridge specs resolve for screenshot tools", () => {
  const checks = [
    ["screenshot", "game-view", "screenshot.game-view"],
    ["screenshot", "scene-view", "screenshot.scene-view"],
    ["screenshot", "isolated-object", "screenshot.isolated-object"],
    ["screenshot", "orbit", "screenshot.orbit"]
  ] as const;
  for (const [group, command, bridgeCommand] of checks) {
    const spec = bridgeCommandRegistry.lookup(group, command);
    assert.ok(spec, `${group} ${command} should resolve in bridge registry`);
    assert.equal(spec.bridgeCommand, bridgeCommand);
  }
});

test("contract entries exist for screenshot tools with present C# handler", () => {
  for (const name of ["screenshot.capture", "screenshot.game-view", "screenshot.scene-view", "screenshot.isolated-object", "screenshot.orbit"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("screenshot tools expose play mode support, side effects and output schema", () => {
  const expected: Record<string, { playModeSupport: string; sideEffects?: string[] }> = {
    "screenshot.capture": { playModeSupport: "editor-only", sideEffects: ["asset-write"] },
    "screenshot.game-view": { playModeSupport: "supported", sideEffects: ["asset-write"] },
    "screenshot.scene-view": { playModeSupport: "editor-only", sideEffects: ["asset-write"] },
    "screenshot.isolated-object": { playModeSupport: "editor-only", sideEffects: ["asset-write"] },
    "screenshot.orbit": { playModeSupport: "editor-only", sideEffects: ["asset-write"] }
  };
  for (const [name, exp] of Object.entries(expected)) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, exp.playModeSupport, `${name} playModeSupport`);
    assert.ok(tool.sideEffects?.some((s) => exp.sideEffects!.includes(s)), `${name} should declare side effects`);
    assert.ok(tool.outputSchema, `${name} should expose output schema`);
    assert.ok(tool.examples?.length, `${name} should expose examples`);
  }
});
