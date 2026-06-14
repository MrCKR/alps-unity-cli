/**
 * Unit tests for tools list/groups/search/describe CLI commands.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { handleToolsCommand, type ToolsRuntime } from "./tools.js";
import { builtinToolMetadata, builtinToolGroups, findToolByName, filterToolsByGroup, searchTools } from "./tool-metadata.js";

const requestId = "test_req_tools_001";
const emptyRuntime: ToolsRuntime = {};

// ── tool-metadata module ───────────────────────────────────

test("builtinToolMetadata returns a non-empty array", () => {
  const tools = builtinToolMetadata();
  assert.ok(Array.isArray(tools));
  assert.ok(tools.length > 0, "Expected at least one built-in tool");
});

test("every tool has the required frozen fields", () => {
  const tools = builtinToolMetadata();
  for (const tool of tools) {
    assert.ok(tool.name, `missing name in ${JSON.stringify(tool)}`);
    assert.ok(tool.title, `missing title in ${tool.name}`);
    assert.ok(tool.description, `missing description in ${tool.name}`);
    assert.ok(tool.group, `missing group in ${tool.name}`);
    assert.ok(tool.inputSchema, `missing inputSchema in ${tool.name}`);
    assert.ok(typeof tool.readOnlyHint === "boolean", `missing readOnlyHint in ${tool.name}`);
    assert.ok(typeof tool.destructiveHint === "boolean", `missing destructiveHint in ${tool.name}`);
  }
});

test("tool names are unique", () => {
  const tools = builtinToolMetadata();
  const names = tools.map((t) => t.name);
  const unique = new Set(names);
  assert.equal(names.length, unique.size, "Duplicate tool names found");
});

test("builtinToolGroups returns sorted groups", () => {
  const groups = builtinToolGroups();
  assert.ok(groups.length > 0);
  const sorted = [...groups].sort();
  assert.deepEqual(groups, sorted, "Groups should be sorted");
});

test("scene.hierarchy is in the built-in tools", () => {
  const tool = findToolByName("scene.hierarchy");
  assert.ok(tool, "scene.hierarchy should exist");
  assert.equal(tool.group, "scene");
  assert.equal(tool.readOnlyHint, true);
  assert.equal(tool.destructiveHint, false);
});

test("scene.save is marked destructive", () => {
  const tool = findToolByName("scene.save");
  assert.ok(tool, "scene.save should exist");
  assert.equal(tool.destructiveHint, true);
});

test("findToolByName returns undefined for unknown tool", () => {
  const tool = findToolByName("nonexistent.tool");
  assert.equal(tool, undefined);
});

test("filterToolsByGroup returns only tools from that group", () => {
  const sceneTools = filterToolsByGroup("scene");
  assert.ok(sceneTools.length > 0);
  for (const t of sceneTools) {
    assert.equal(t.group, "scene");
  }
});

test("searchTools finds by name substring", () => {
  const results = searchTools("hierarchy");
  assert.ok(results.length > 0);
  const names = results.map((t) => t.name);
  assert.ok(names.includes("scene.hierarchy"), `Expected scene.hierarchy in ${names}`);
});

test("searchTools finds by group", () => {
  const results = searchTools("probuilder");
  assert.ok(results.length > 0);
  for (const t of results) {
    assert.ok(t.name.includes("probuilder") || t.group === "probuilder" || t.description.toLowerCase().includes("probuilder"), `${t.name} should match probuilder`);
  }
});

test("searchTools returns empty for no match", () => {
  const results = searchTools("zzzz_nonexistent_zzzz");
  assert.equal(results.length, 0);
});

// ── tools list ─────────────────────────────────────────────

test("tools list returns all built-in tools", async () => {
  const result = await handleToolsCommand("list", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: Array<{ name: string }> };
  assert.ok(data.count > 0);
  assert.ok(data.tools.length > 0);
  const names = data.tools.map((t) => t.name);
  assert.ok(names.includes("scene.hierarchy"), "scene.hierarchy should be in list");
});

test("tools list with --group filters by group", async () => {
  const result = await handleToolsCommand("list", [], { group: "scene" }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: Array<{ name: string; group: string }> };
  assert.ok(data.count > 0);
  for (const t of data.tools) {
    assert.equal(t.group, "scene");
  }
});

test("tools list with --group unknown returns empty", async () => {
  const result = await handleToolsCommand("list", [], { group: "nonexistent" }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: unknown[] };
  assert.equal(data.count, 0);
  assert.equal(data.tools.length, 0);
});

test("tools list --format text includes text field", async () => {
  const result = await handleToolsCommand("list", [], { format: "text" }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { text: string };
  assert.ok(data.text.length > 0);
  assert.ok(data.text.includes("scene.hierarchy"));
});

test("tools list --include-custom with runtime custom tools merges them", async () => {
  const customTool = {
    name: "custom.my-tool",
    title: "My Custom Tool",
    description: "A custom tool for testing",
    group: "custom",
    inputSchema: { type: "object" as const, properties: {} },
    readOnlyHint: true,
    destructiveHint: false
  };
  const runtime: ToolsRuntime = { customTools: [customTool] };
  const result = await handleToolsCommand("list", [], { "include-custom": true }, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { tools: Array<{ name: string }> };
  const names = data.tools.map((t) => t.name);
  assert.ok(names.includes("custom.my-tool"));
});

// ── tools groups ───────────────────────────────────────────

test("tools groups returns group names", async () => {
  const result = await handleToolsCommand("groups", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; groups: string[] };
  assert.ok(data.count > 0);
  assert.ok(data.groups.includes("scene"));
  assert.ok(data.groups.includes("camera"));
});

// ── tools search ───────────────────────────────────────────

test("tools search finds tools by keyword", async () => {
  const result = await handleToolsCommand("search", ["hierarchy"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: Array<{ name: string }> };
  assert.ok(data.count > 0);
  const names = data.tools.map((t) => t.name);
  assert.ok(names.includes("scene.hierarchy"));
});

test("tools search without keyword returns error", async () => {
  const result = await handleToolsCommand("search", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "TOOLS_SEARCH_KEYWORD_REQUIRED");
});

test("tools search --format text returns text field", async () => {
  const result = await handleToolsCommand("search", ["scene"], { format: "text" }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { text: string };
  assert.ok(data.text.length > 0);
});

// ── tools describe ─────────────────────────────────────────

test("tools describe scene.hierarchy returns full metadata", async () => {
  const result = await handleToolsCommand("describe", ["scene.hierarchy"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "scene.hierarchy");
  assert.equal(data.group, "scene");
  assert.equal(data.readOnlyHint, true);
  assert.equal(data.destructiveHint, false);
  assert.ok(data.inputSchema);
  assert.ok(data.description);
  assert.ok(Array.isArray(data.examples));
  assert.equal((data as { mode: string }).mode, "offline");
  assert.equal((data as { requiresUnity: boolean }).requiresUnity, false);
});

test("tools describe for unknown tool returns TOOL_NOT_FOUND", async () => {
  const result = await handleToolsCommand("describe", ["nonexistent.tool"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "TOOL_NOT_FOUND");
});

test("tools describe without name returns error", async () => {
  const result = await handleToolsCommand("describe", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "TOOLS_DESCRIBE_NAME_REQUIRED");
});

test("tools describe for destructive tool shows destructiveHint", async () => {
  const result = await handleToolsCommand("describe", ["go.delete"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.destructiveHint, true);
});

// ── tools unknown subcommand ───────────────────────────────

test("tools unknown subcommand returns error", async () => {
  const result = await handleToolsCommand("unknown", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_TOOLS_COMMAND");
});

// ── tools list is default subcommand ───────────────────────

test("tools without subcommand defaults to list", async () => {
  const result = await handleToolsCommand(undefined, [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { count: number; tools: unknown[] };
  assert.ok(data.count > 0);
});
