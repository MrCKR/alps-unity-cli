import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";

/**
 * Screenshot 域契约测试：game-view / scene-view / isolated-object / orbit / surround。
 * 对齐 IvanMurzak screenshot 与 CoplayDev orbit/surround。
 * camera.screenshot 保留为主入口，不新增 screenshot.camera 重复主实现。
 */

test("screenshot domain exposes game-view/scene-view/isolated-object/orbit/surround", () => {
  const screenshotTools = filterToolsByGroup("screenshot").map((t) => t.name);
  assert.ok(screenshotTools.includes("screenshot.game-view"));
  assert.ok(screenshotTools.includes("screenshot.scene-view"));
  assert.ok(screenshotTools.includes("screenshot.isolated-object"));
  assert.ok(screenshotTools.includes("screenshot.orbit"));
  assert.ok(screenshotTools.includes("screenshot.surround"));
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
  const surround = findToolByName("screenshot.surround")!;
  assert.equal(surround.destructiveHint, true);
  assert.equal(surround.supportsDryRun, true);
  assert.equal(surround.handler, "bridge");
});

test("bridge specs resolve for screenshot tools", () => {
  const checks = [
    ["screenshot", "game-view", "project-tool.run"],
    ["screenshot", "scene-view", "project-tool.run"],
    ["screenshot", "isolated-object", "project-tool.run"],
    ["screenshot", "orbit", "project-tool.run"],
    ["screenshot", "surround", "project-tool.run"]
  ] as const;
  for (const [group, command, bridgeCommand] of checks) {
    const spec = bridgeCommandRegistry.lookup(group, command);
    assert.ok(spec, `${group} ${command} should resolve in bridge registry`);
    assert.equal(spec.bridgeCommand, bridgeCommand);
  }
});

test("contract entries exist for screenshot tools with present C# handler", () => {
  for (const name of ["screenshot.capture", "screenshot.game-view", "screenshot.scene-view", "screenshot.isolated-object", "screenshot.orbit", "screenshot.surround"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("screenshot tools expose play mode support, side effects and output schema", () => {
  const expected: Record<string, { playModeSupport: string; sideEffects?: string[] }> = {
    "screenshot.capture": { playModeSupport: "supported", sideEffects: ["asset-write"] },
    "screenshot.game-view": { playModeSupport: "supported", sideEffects: ["asset-write"] },
    "screenshot.scene-view": { playModeSupport: "supported", sideEffects: ["asset-write"] },
    "screenshot.isolated-object": { playModeSupport: "editor-only", sideEffects: ["asset-write"] },
    "screenshot.orbit": { playModeSupport: "editor-only", sideEffects: ["asset-write"] },
    "screenshot.surround": { playModeSupport: "editor-only", sideEffects: ["asset-write"] }
  };
  for (const [name, exp] of Object.entries(expected)) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, exp.playModeSupport, `${name} playModeSupport`);
    assert.ok(tool.sideEffects?.some((s) => exp.sideEffects!.includes(s)), `${name} should declare side effects`);
    assert.ok(tool.outputSchema, `${name} should expose output schema`);
    assert.ok(tool.examples?.length, `${name} should expose examples`);
  }
});

test("screenshot.orbit output schema matches artifact-array runtime envelope", () => {
  const orbit = findToolByName("screenshot.orbit")!;
  const props = orbit.outputSchema!.properties as Record<string, unknown>;
  assert.ok(props.target, "orbit output should include resolved target");
  assert.ok(props.count, "orbit output should include captured count");
  assert.ok(props.artifacts, "orbit output should expose artifacts array");
  assert.equal("outputDir" in props, false, "orbit output should not advertise removed outputDir field");
  assert.equal("captured" in props, false, "orbit output should not advertise removed captured field");
});

test("screenshot inline base64 and surround fields are self-describing", () => {
  for (const name of ["screenshot.capture", "screenshot.game-view", "screenshot.scene-view", "screenshot.isolated-object"]) {
    const props = findToolByName(name)!.inputSchema.properties as Record<string, unknown>;
    const inlineBase64 = props.inlineBase64 as { description?: string };
    assert.ok(inlineBase64, `${name} should document inlineBase64`);
    assert.match(inlineBase64.description ?? "", /Defaults false|base64/i, `${name} should explain bounded optional inline output`);
  }
  const surround = findToolByName("screenshot.surround")!;
  const props = surround.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.contactSheetPath, "surround should document contactSheetPath");
  const inlineBase64 = props.inlineBase64 as { description?: string };
  assert.ok(inlineBase64, "surround should document optional contact sheet base64");
  assert.match(inlineBase64.description ?? "", /Defaults false|base64/i);
  assert.match(surround.description, /six fixed angles/i);
  assert.match(surround.description, /--dry-run/);
});
