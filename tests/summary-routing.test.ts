import assert from "node:assert/strict";
import test from "node:test";
import { handleToolsCommand, type ToolsRuntime } from "../src/tools/commands.js";
import { allRegisteredToolMetadata } from "../src/tools/metadata/index.js";
import { builtinContextDescriptors } from "../src/contexts/registry.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/index.js";
import { probuilderPingTool, sceneHierarchyTool } from "./helpers/live-tool-fixtures.js";
import "../src/extensions/index.js";

const requestId = "test_summary_routing_001";
const emptyRuntime: ToolsRuntime = {};
const GENERIC_PATTERNS = [/\bvarious\b/i, /\butilities\b/i, /\bhelpers\b/i, /\bmanage things\b/i, /^run\s+[\w.-]+\.?$/i, /^manage\s+\w+\.?$/i, /\.\.\./];

function wordCount(text: string): number {
  return text.trim().split(/\s+/).filter(Boolean).length;
}

function assertRouteSummary(name: string, summary: string): void {
  assert.ok(summary.trim(), `${name} summary must not be empty`);
  assert.ok(wordCount(summary) <= 32, `${name} summary is too long: ${wordCount(summary)} words`);
  for (const pattern of GENERIC_PATTERNS) {
    assert.doesNotMatch(summary, pattern, `${name} summary is not route-specific: ${summary}`);
  }
}

test("offline tools group summaries are route-specific for TS-local groups", async () => {
  const result = await handleToolsCommand("group", [], {}, emptyRuntime, requestId);
  assert.ok(result.ok);

  const data = result.data as { groups: Array<{ name: string; summary: string }> };
  const groups = new Map(data.groups.map((group) => [group.name, group.summary]));

  for (const [name, summary] of groups) {
    assertRouteSummary(`group ${name}`, summary);
  }

  assert.match(groups.get("docs") ?? "", /offline|documentation/i);
  assert.match(groups.get("tools") ?? "", /describe|search|group/i);
  assert.match(groups.get("package") ?? "", /package/i);
  assert.equal(groups.has("scene"), false, "scene should require live discovery");
});

test("live-discovered C# and extension groups still get route summaries through tools group", async () => {
  const runtime: ToolsRuntime = {
    projectTools: [sceneHierarchyTool(), probuilderPingTool()]
  };

  const result = await handleToolsCommand("group", [], {}, runtime, requestId);
  assert.ok(result.ok);

  const data = result.data as { groups: Array<{ name: string; summary: string; source: string }> };
  const scene = data.groups.find((group) => group.name === "scene");
  const probuilder = data.groups.find((group) => group.name === "probuilder");

  assert.ok(scene);
  assert.ok(probuilder);
  assertRouteSummary("group scene", scene!.summary);
  assertRouteSummary("group probuilder", probuilder!.summary);
  assert.equal(scene!.source, "built-in");
  assert.equal(probuilder!.source, "extension");
  assert.match(scene!.summary, /hierarchy|save|load|view/i);
  assert.match(probuilder!.summary, /shape|mesh|face|repair/i);
});

test("offline tool summaries stay concise and route-specific", () => {
  const tools = allRegisteredToolMetadata();
  assert.ok(tools.length > 0);

  for (const tool of tools) {
    assertRouteSummary(tool.name, tool.summary);
  }

  assert.match(tools.find((tool) => tool.name === "docs.get")!.summary, /offline|fallback/i);
  assert.match(tools.find((tool) => tool.name === "docs.search")!.summary, /search|offline/i);
  assert.match(tools.find((tool) => tool.name === "reflect.search")!.summary, /search|paged/i);
  assert.match(tools.find((tool) => tool.name === "code.exec")!.summary, /execute|batchmode|Editor/i);
  assert.match(tools.find((tool) => tool.name === "tools.describe")!.summary, /metadata|schema|examples/i);
});

test("local context discovery descriptions are route-specific", () => {
  const contexts = builtinContextDescriptors();
  assert.ok(contexts.length > 0);
  for (const context of contexts) {
    assert.ok(context.description.trim(), `${context.name} description must not be empty`);
    assert.ok(wordCount(context.description) <= 70, `${context.name} context discovery description is too long`);
    for (const pattern of GENERIC_PATTERNS) {
      assert.doesNotMatch(context.description, pattern, `${context.name} context description is not route-specific`);
    }
  }
  assert.ok(contexts.some((context) => context.name === "tools.groups" && /local CLI registry/i.test(context.description)));
  assert.ok(contexts.some((context) => context.name === "tools.custom" && /custom Unity project tools/i.test(context.description)));
});

test("extension manifest descriptions remain route-specific even though tool descriptors are live-only", () => {
  for (const manifest of BUILTIN_EXTENSION_MANIFESTS) {
    assert.ok(manifest.description.trim(), `${manifest.id} manifest description missing`);
    assert.ok(wordCount(manifest.description) <= 32, `${manifest.id} manifest description too long`);
    for (const pattern of GENERIC_PATTERNS) {
      assert.doesNotMatch(manifest.description, pattern, `${manifest.id} manifest description is not route-specific`);
    }
  }
});
