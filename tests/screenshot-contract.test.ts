import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools } from "./helpers/csharp-test-helpers.js";

/** Screenshot 域契约测试：descriptor 改走 live discovery。 */

test("screenshot domain exposes game-view/scene-view/isolated-object/orbit/surround", () => {
  const screenshotTools = contractToolNames("screenshot");
  assert.ok(screenshotTools.includes("screenshot.game-view"));
  assert.ok(screenshotTools.includes("screenshot.scene-view"));
  assert.ok(screenshotTools.includes("screenshot.isolated-object"));
  assert.ok(screenshotTools.includes("screenshot.orbit"));
  assert.ok(screenshotTools.includes("screenshot.surround"));
  assert.ok(screenshotTools.includes("screenshot.screen"));
  assert.equal(screenshotTools.includes("screenshot.camera"), false);
});

test("screenshot live descriptors expose expected safety hints and schemas", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "screenshot.from-camera", summary: "Capture a camera screenshot.", description: "Capture a camera screenshot with optional inlineBase64 output.", group: "camera", inputSchema: { type: "object", properties: { path: { type: "string" }, output: { type: "string" }, inlineBase64: { type: "boolean", description: "Defaults false; optionally include base64 output." } } } }),
    makeLiveTool({ name: "screenshot.game-view", summary: "Capture the Game view.", description: "Capture the Game view.", group: "screenshot", inputSchema: { type: "object", properties: { inlineBase64: { type: "boolean", description: "Defaults false; optionally include base64 output." } } }, outputSchema: { type: "object", properties: { artifact: { type: "object" } } } }),
    makeLiveTool({ name: "screenshot.scene-view", summary: "Capture the Scene view.", description: "Capture the Scene view.", group: "screenshot", inputSchema: { type: "object", properties: { inlineBase64: { type: "boolean", description: "Defaults false; optionally include base64 output." } } }, outputSchema: { type: "object", properties: { artifact: { type: "object" } } } }),
    makeLiveTool({ name: "screenshot.isolated-object", summary: "Capture an isolated object.", description: "Capture an isolated object.", group: "screenshot", playModeSupport: "editor-only", inputSchema: { type: "object", properties: { inlineBase64: { type: "boolean", description: "Defaults false; optionally include base64 output." } } }, outputSchema: { type: "object", properties: { artifact: { type: "object" } } } }),
    makeLiveTool({ name: "screenshot.orbit", summary: "Capture orbit screenshots.", description: "Capture orbit screenshots.", group: "screenshot", playModeSupport: "editor-only", outputSchema: { type: "object", properties: { target: { type: "string" }, count: { type: "integer" }, artifacts: { type: "array" } } } }),
    makeLiveTool({ name: "screenshot.surround", summary: "Capture a surround contact sheet.", description: "Capture six fixed angles around a target; supports --dry-run.", group: "screenshot", destructiveHint: true, supportsDryRun: true, playModeSupport: "editor-only", inputSchema: { type: "object", properties: { contactSheetPath: { type: "string" }, inlineBase64: { type: "boolean", description: "Defaults false; optionally include base64 output." } } }, outputSchema: { type: "object", properties: { artifact: { type: "object" } } } })
  );

  for (const name of ["screenshot.game-view", "screenshot.scene-view", "screenshot.isolated-object", "screenshot.orbit"]) {
    const tool = tools.get(name)!;
    assert.equal(tool.destructiveHint, false, `${name} should not be destructive`);
    assert.equal(tool.handler, "bridge");
  }
  const surround = tools.get("screenshot.surround")!;
  assert.equal(surround.destructiveHint, true);
  assert.equal(surround.supportsDryRun, true);
  assert.equal(surround.handler, "bridge");

  const camera = tools.get("screenshot.from-camera")!;
  const cameraProps = camera.inputSchema.properties as Record<string, unknown>;
  assert.ok(cameraProps.path);
  assert.ok(cameraProps.output);
  assert.ok(cameraProps.inlineBase64);
  assert.match(camera.description, /inlineBase64/);

  const expected: Record<string, string> = {
    "screenshot.game-view": "supported",
    "screenshot.scene-view": "supported",
    "screenshot.isolated-object": "editor-only",
    "screenshot.orbit": "editor-only",
    "screenshot.surround": "editor-only"
  };
  for (const [name, playModeSupport] of Object.entries(expected)) {
    const tool = tools.get(name)!;
    assert.equal(tool.playModeSupport, playModeSupport, `${name} playModeSupport`);
    assert.ok(tool.outputSchema, `${name} should expose output schema`);
  }

  const orbit = tools.get("screenshot.orbit")!;
  const orbitProps = orbit.outputSchema?.properties as Record<string, unknown>;
  assert.ok(orbitProps.target);
  assert.ok(orbitProps.count);
  assert.ok(orbitProps.artifacts);
  assert.equal("outputDir" in orbitProps, false);
  assert.equal("captured" in orbitProps, false);

  for (const name of ["screenshot.game-view", "screenshot.scene-view", "screenshot.isolated-object"]) {
    const props = tools.get(name)!.inputSchema.properties as Record<string, { description?: string }>;
    const inlineBase64 = props.inlineBase64;
    assert.ok(inlineBase64, `${name} should document inlineBase64`);
    assert.match(inlineBase64.description ?? "", /Defaults false|base64/i);
  }
  const surroundProps = surround.inputSchema.properties as Record<string, { description?: string }>;
  assert.ok(surroundProps.contactSheetPath);
  assert.ok(surroundProps.inlineBase64);
  assert.match(surroundProps.inlineBase64.description ?? "", /Defaults false|base64/i);
  assert.match(surround.description, /six fixed angles/i);
  assert.match(surround.description, /--dry-run/);
});

test("bridge specs resolve for screenshot tools", () => {
  const checks = [
    ["screenshot", "game-view", "catalog.editor.run"],
    ["screenshot", "scene-view", "catalog.editor.run"],
    ["screenshot", "isolated-object", "catalog.editor.run"],
    ["screenshot", "orbit", "catalog.editor.run"],
    ["screenshot", "surround", "catalog.editor.run"]
  ] as const;
  for (const [group, command, bridgeCommand] of checks) {
    const spec = bridgeCommandRegistry.lookup(group, command);
    assert.ok(spec, `${group} ${command} should resolve in bridge registry`);
    assert.equal(spec?.bridgeCommand, bridgeCommand);
  }
});

test("screenshot tools restore render state and require a non-empty written artifact", async () => {
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/screenshot/ScreenshotToolSupport.cs", import.meta.url), "utf8");
  const gameViewSource = await readFile(new URL("../unitypkg/Editor/Tools/screenshot/game-view.cs", import.meta.url), "utf8");
  const isolatedSource = await readFile(new URL("../unitypkg/Editor/Tools/screenshot/isolated-object.cs", import.meta.url), "utf8");
  const orbitSource = await readFile(new URL("../unitypkg/Editor/Tools/screenshot/orbit.cs", import.meta.url), "utf8");
  assert.match(supportSource, /E_SCREENSHOT_ARTIFACT_EMPTY/);
  assert.match(supportSource, /E_SCREENSHOT_ARTIFACT_MISSING/);
  assert.match(supportSource, /RenderTexture\.active = previousActive/);
  assert.match(supportSource, /camera\.targetTexture = previousTarget/);
  assert.match(gameViewSource, /RenderTexture\.active = previousActive/);
  assert.match(gameViewSource, /finally \{ cam\.targetTexture = prevTarget; \}/);
  assert.match(isolatedSource, /RenderScreenshotTexture\(camera, width, height, captureBudget\)/);
  assert.match(isolatedSource, /finally \{ UnityEngine\.Object\.DestroyImmediate\(texture\); \}/);
  assert.match(orbitSource, /previousPosition = camera\.transform\.position/);
  assert.match(orbitSource, /camera\.transform\.position = previousPosition/);
  assert.match(orbitSource, /finally \{ UnityEngine\.Object\.DestroyImmediate\(texture\); \}/);
});

test("contract entries exist for screenshot tools with present C# handler", () => {
  for (const name of ["screenshot.game-view", "screenshot.scene-view", "screenshot.isolated-object", "screenshot.orbit", "screenshot.surround"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});
