import assert from "node:assert/strict";
import test from "node:test";
import { handleToolsCommand, type ToolsRuntime } from "../src/tools/commands.js";
import { allRegisteredToolMetadata } from "../src/tools/metadata/index.js";
import { BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/index.js";
import { probuilderPingTool, sceneHierarchyTool } from "./helpers/live-tool-fixtures.js";
import "../src/extensions/index.js";

const requestId = "test_summary_routing_001";
const connectedRuntime: ToolsRuntime = { projectTools: [sceneHierarchyTool(), probuilderPingTool()] };
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

test("connected tools group summaries cover package-local and instance groups", async () => {
  const result = await handleToolsCommand("group", [], {}, connectedRuntime, requestId);
  assert.ok(result.ok);

  const data = result.data as { groups: Array<{ name: string; summary: string }> };
  const groups = new Map(data.groups.map((group) => [group.name, group.summary]));

  for (const [name, summary] of groups) {
    assertRouteSummary(`group ${name}`, summary);
  }

  assert.match(groups.get("docs") ?? "", /offline|documentation/i);
  assert.equal(groups.has("tools"), false);
  assert.match(groups.get("package") ?? "", /package/i);
  assertRouteSummary("group scene", groups.get("scene") ?? "");
  assertRouteSummary("group probuilder", groups.get("probuilder") ?? "");
});

test("live-discovered C# and extension groups still get route summaries through tools group", async () => {
  const runtime: ToolsRuntime = {
    projectTools: [sceneHierarchyTool(), probuilderPingTool()]
  };

  const result = await handleToolsCommand("group", [], {}, runtime, requestId);
  assert.ok(result.ok);

  const data = result.data as { groups: Array<{ name: string; summary: string }> };
  const scene = data.groups.find((group) => group.name === "scene");
  const probuilder = data.groups.find((group) => group.name === "probuilder");

  assert.ok(scene);
  assert.ok(probuilder);
  assertRouteSummary("group scene", scene!.summary);
  assertRouteSummary("group probuilder", probuilder!.summary);
  assert.match(scene!.summary, /hierarchy|save|load|view/i);
  assert.match(probuilder!.summary, /shape|mesh|face|repair/i);
});

test("package-local Tool summaries stay concise and route-specific", () => {
  const tools = allRegisteredToolMetadata();
  assert.ok(tools.length > 0);

  for (const tool of tools) {
    assertRouteSummary(tool.name, tool.summary);
  }

  assert.match(tools.find((tool) => tool.name === "docs.get")!.summary, /offline|fallback/i);
  assert.match(tools.find((tool) => tool.name === "docs.search")!.summary, /search|offline/i);
  assert.match(tools.find((tool) => tool.name === "reflect.search")!.summary, /search|paged/i);
  assert.match(tools.find((tool) => tool.name === "code.exec")!.summary, /execute|live|Editor/i);
  assert.doesNotMatch(tools.find((tool) => tool.name === "code.exec")!.summary, /batch|job|headless/i);
});

test("local read Tool descriptions are route-specific", () => {
  const tools = allRegisteredToolMetadata();
  for (const name of ["editor.state", "editor.dialogs"]) {
    const tool = tools.find((entry) => entry.name === name);
    assert.ok(tool);
    assert.ok(tool.description.trim(), `${name} description must not be empty`);
    assert.ok(wordCount(tool.description) <= 70, `${name} description is too long`);
    for (const pattern of GENERIC_PATTERNS) assert.doesNotMatch(tool.description, pattern);
  }
  assert.match(tools.find((entry) => entry.name === "editor.state")!.description, /selected Unity project's active Editor session/i);
  assert.match(tools.find((entry) => entry.name === "editor.dialogs")!.description, /Windows modal dialogs/i);
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
