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

test("native Tool names retain the exact syntax and UTF-8 byte limit", () => {
  assert.equal(isRegisteredToolName("scene.snapshot"), true);
  assert.equal(isRegisteredToolName("scene.hotfix-container"), true);
  assert.equal(isRegisteredToolName("a".repeat(MAX_TOOL_NAME_UTF8_BYTES)), true);
  assert.equal(isRegisteredToolName("a".repeat(MAX_TOOL_NAME_UTF8_BYTES + 1)), false);
  assert.equal(isRegisteredToolName("scene.hotfix_container"), false);
  assert.equal(isRegisteredToolName("UnityPipeline_project_reload_data"), false);
  assert.equal(isRegisteredToolName("scene.数据"), false);
  assert.equal(isRegisteredToolName("Scene.data"), false);
  assert.equal(isRegisteredToolName("scene..data"), false);
  assert.equal(isRegisteredToolName("scene.snapshot\n"), false);
  assert.equal(isRegisteredToolName("scene.snapshot\r\n"), false);
  assert.equal(isRegisteredToolName("scene.snapshot "), false);

  assert.throws(() => defineTool(invalidDefinition("scene.数据")), /registered Tool syntax.*128 UTF-8 bytes/);
  assert.throws(() => defineTool(invalidDefinition("a".repeat(129))), /registered Tool syntax.*128 UTF-8 bytes/);
});

test("official attribute names preserve legal snake_case suffixes within the total byte budget", () => {
  assert.equal(isRegisteredToolName("UnityPipeline_hotfix_container_inspect", "official-attribute"), true);
  assert.equal(isRegisteredToolName("UnityPipeline_project_reload_data", "official-attribute"), true);
  assert.equal(isRegisteredToolName("UnityPipeline_validation.official-result", "official-attribute"), true);
  assert.equal(isRegisteredToolName("UnityPipeline_scene.数据", "official-attribute"), false);
  assert.equal(isRegisteredToolName("project_reload_data", "official-attribute"), false);
  assert.equal(isRegisteredToolName(`UnityPipeline_${"a".repeat(114)}`, "official-attribute"), true);
  assert.equal(isRegisteredToolName(`UnityPipeline_${"a".repeat(115)}`, "official-attribute"), false);
  assert.equal(isRegisteredToolName("UnityPipeline_project_reload_data\n", "official-attribute"), false);
  assert.equal(isRegisteredToolName("UnityPipeline_project_reload_data\r\n", "official-attribute"), false);
  assert.equal(isRegisteredToolName("UnityPipeline_project_reload_data\t", "official-attribute"), false);
});

test("native and official C# registration both use the profile-aware name validator", async () => {
  const [registry, official, validator] = await Promise.all([
    readFile("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/OfficialAttributeToolRegistry.cs", "utf8"),
    readFile("unitypkg/Editor/ProjectTools/UnityCliToolName.cs", "utf8"),
  ]);
  assert.match(registry, /UnityCliToolName\.Validate\(definition\.Name, definition\.ContractProfile\)/);
  assert.match(official, /var toolName = OfficialAttributeToolPrefix[\s\S]*UnityCliToolName\.Validate\(toolName, "official-attribute"\)/);
  assert.match(validator, /MaxUtf8Bytes = 128[\s\S]*Encoding\.UTF8\.GetByteCount\(name\)/);
  assert.match(validator, /NativePattern[\s\S]*OfficialSuffixPattern/);
  assert.equal((validator.match(/\\\\z/g) ?? []).length, 2);
});
