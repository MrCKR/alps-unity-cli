/**
 * Unit tests for context command family: context list, context describe, context read.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { handleContextCommand } from "../src/context/commands.js";
import { builtinContextDescriptors, builtinContextCategories, findBuiltinContextByName, filterContextsByCategory, searchContexts } from "../src/context/registry.js";
import { BUILTIN_CONTEXTS } from "../src/context/builtin.js";
import type { ContextDescriptor } from "../src/context/types.js";
import type { BridgeRuntime } from "../src/bridge/index.js";

const requestId = "test_req_ctx_001";

function makeRuntime(bridgeExecute?: BridgeRuntime["bridgeExecute"]): BridgeRuntime {
  return { bridgeExecute };
}

// ── Built-in context descriptors ──────────────────────────────────────────

test("builtin context descriptors cover all required contexts", () => {
  const contexts = builtinContextDescriptors();
  assert.equal(contexts.length, 25, `Expected 25 built-in contexts, got ${contexts.length}`);
});

test("all builtin contexts are read-only", () => {
  for (const ctx of builtinContextDescriptors()) {
    assert.equal(ctx.readOnlyHint, true, `${ctx.name} should be readOnly`);
    assert.equal(ctx.destructiveHint, false, `${ctx.name} should not be destructive`);
  }
});

test("all builtin contexts have required fields", () => {
  for (const ctx of builtinContextDescriptors()) {
    assert.ok(ctx.name, "name required");
    assert.ok(ctx.title, "title required");
    assert.ok(ctx.description, "description required");
    assert.ok(ctx.category, "category required");
    assert.ok(ctx.inputSchema, "inputSchema required");
    assert.ok(ctx.outputSchema, "outputSchema required");
    assert.ok(ctx.cliCommand, "cliCommand required");
    assert.equal(ctx.bridgeCommand, "context.read", "bridgeCommand should be context.read");
  }
});

test("builtin contexts cover required categories", () => {
  const categories = builtinContextCategories();
  assert.ok(categories.includes("project"), "project category required");
  assert.ok(categories.includes("editor"), "editor category required");
  assert.ok(categories.includes("scene"), "scene category required");
  assert.ok(categories.includes("tests"), "tests category required");
});

test("specific required contexts exist", () => {
  const required = [
    "project.info", "project.tags", "project.layers",
    "editor.state", "editor.selection", "editor.active-tool",
    "editor.windows", "editor.tool-states", "editor.menu-items",
    "prefab.stage", "scene.gameobject", "scene.gameobject.components",
    "scene.gameobject.component", "scene.cameras", "scene.volumes",
    "scene.renderer-features", "scene.rendering-stats", "prefab.info",
    "prefab.hierarchy", "tests.list", "instances.list",
    "tools.groups", "tools.custom",
  ];
  for (const name of required) {
    const ctx = findBuiltinContextByName(name);
    assert.ok(ctx, `Missing required context: ${name}`);
  }
});

test("no duplicate context names", () => {
  const names = builtinContextDescriptors().map((c) => c.name);
  const unique = new Set(names);
  assert.equal(names.length, unique.size, "Duplicate context names found");
});

// ── Category filter ───────────────────────────────────────────────────────

test("filterContextsByCategory returns correct subset", () => {
  const projectContexts = filterContextsByCategory("project");
  assert.equal(projectContexts.length, 3);
  assert.ok(projectContexts.every((c) => c.category === "project"));
});

// ── Search ────────────────────────────────────────────────────────────────

test("searchContexts finds matching contexts", () => {
  const results = searchContexts("camera");
  assert.ok(results.length > 0, "Should find camera-related contexts");
  assert.ok(results.some((c) => c.name === "scene.cameras"));
});

test("searchContexts returns empty for no match", () => {
  const results = searchContexts("nonexistent_context_xyz");
  assert.equal(results.length, 0);
});

// ── context list CLI ──────────────────────────────────────────────────────

test("context list returns all builtin contexts", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("list", [], {}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.list");
  assert.equal(data.count, 25);

  const contexts = data.contexts as Array<Record<string, unknown>>;
  assert.equal(contexts.length, 25);
  assert.ok(contexts[0].name);
  assert.ok(contexts[0].title);
  assert.ok(contexts[0].category);
  assert.ok(contexts[0].source);
});

test("context list with category filter", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("list", [], { category: "editor" }, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  const contexts = data.contexts as Array<Record<string, unknown>>;
  assert.ok(contexts.every((c) => c.category === "editor"));
});

test("context list with text format", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("list", [], { format: "text" }, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.ok(typeof data.text === "string");
  assert.ok(data.text.length > 0);
});

test("context list (no command) defaults to list", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand(undefined, [], {}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.list");
});

// ── context describe CLI ──────────────────────────────────────────────────

test("context describe returns full descriptor for builtin context", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("describe", ["project.info"], {}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as Record<string, unknown>;
  assert.equal(data.command, "context.describe");
  assert.equal(data.name, "project.info");
  assert.equal(data.readOnlyHint, true);
  assert.equal(data.destructiveHint, false);
  assert.ok(data.inputSchema);
  assert.ok(data.outputSchema);
  assert.ok(data.supportStatusLabel);
});

test("context describe without name returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("describe", [], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "CONTEXT_DESCRIBE_NAME_REQUIRED");
});

test("context describe for unknown context returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("describe", ["nonexistent.xyz"], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_NOT_FOUND");
});

// ── context read CLI ──────────────────────────────────────────────────────

test("context read without name returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("read", [], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_NAME_REQUIRED");
});

test("context read for unknown context returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("read", ["nonexistent.xyz"], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_NOT_FOUND");
});

test("context read routes through bridge command", async () => {
  const bridgeExecute = async () => ({
    ok: true,
    data: {
      context: "project.info",
      data: { projectRoot: "/path/to/project", projectName: "TestProject", unityVersion: "2022.3" },
      readAt: new Date().toISOString(),
    },
  });
  const runtime = makeRuntime(bridgeExecute);
  const result = await handleContextCommand("read", ["project.info"], {}, runtime, requestId);
  assert.equal(result.ok, true);
});

// ── unknown subcommand ────────────────────────────────────────────────────

test("unknown context subcommand returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("unknown", [], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_CONTEXT_COMMAND");
});
