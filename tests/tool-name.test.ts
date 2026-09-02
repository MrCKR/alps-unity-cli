import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { isRegisteredToolName, MAX_TOOL_NAME_UTF8_BYTES } from "../src/tool-name.js";
import { defineTool } from "../src/tools/define.js";

const invalidDefinition = (name: string) => ({
  name,
  summary: "Invalid name fixture.",
  description: "This fixture must be rejected before registration.",
  group: "test",
  destructiveHint: false,
  execute: () => ({}),
});

test("registered Tool names share one syntax and UTF-8 byte limit", () => {
  assert.equal(isRegisteredToolName("scene.data"), true);
  assert.equal(isRegisteredToolName("UnityPipeline_validation.official-result"), true);
  assert.equal(isRegisteredToolName("a".repeat(MAX_TOOL_NAME_UTF8_BYTES)), true);
  assert.equal(isRegisteredToolName("a".repeat(MAX_TOOL_NAME_UTF8_BYTES + 1)), false);
  assert.equal(isRegisteredToolName("scene.数据"), false);
  assert.equal(isRegisteredToolName("Scene.data"), false);
  assert.equal(isRegisteredToolName("scene..data"), false);

  assert.throws(() => defineTool(invalidDefinition("scene.数据")), /registered Tool syntax.*128 UTF-8 bytes/);
  assert.throws(() => defineTool(invalidDefinition("a".repeat(129))), /registered Tool syntax.*128 UTF-8 bytes/);
});

test("native and official C# registration both use the unified name validator", async () => {
  const [registry, official] = await Promise.all([
    readFile("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/OfficialAttributeToolRegistry.cs", "utf8"),
  ]);
  assert.match(registry, /RegisterProjectTool\(RegisteredTool definition\)[\s\S]*ValidateProjectToolName\(definition\.Name\)/);
  assert.match(official, /var toolName = OfficialAttributeToolPrefix[\s\S]*ValidateProjectToolName\(toolName\)/);
  assert.match(registry, /MaxProjectToolNameUtf8Bytes = 128[\s\S]*Encoding\.UTF8\.GetByteCount\(name\)/);
});
