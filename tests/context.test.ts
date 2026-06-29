/**
 * Unit tests for context command family: context list, context describe, context read.
 */

import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
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
  assert.equal(contexts.length, 23, `Expected 23 built-in contexts, got ${contexts.length}`);
});

test("all builtin contexts have required fields", () => {
  for (const ctx of builtinContextDescriptors()) {
    assert.ok(ctx.name, "name required");
    assert.ok(ctx.description, "description required");
    assert.equal("title" in ctx, false, `${ctx.name} should not expose title`);
    assert.equal("summary" in ctx, false, `${ctx.name} should not expose summary`);
    assert.ok(ctx.category, "category required");
    assert.ok(ctx.inputSchema, "inputSchema required");
    assert.ok(ctx.outputSchema, "outputSchema required");
    assert.ok(ctx.cliCommand, "cliCommand required");
    assert.equal("bridgeCommand" in ctx, false, `${ctx.name} should not expose bridgeCommand`);
    assert.equal("destructiveHint" in ctx, false, `${ctx.name} should not expose destructiveHint`);
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
    "prefab.hierarchy", "tests.list",
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
  assert.equal(data.count, 23);

  const contexts = data.contexts as Array<Record<string, unknown>>;
  assert.equal(contexts.length, 23);
  assert.ok(contexts[0].name);
  assert.ok(contexts[0].description);
  assert.equal("title" in contexts[0], false);
  assert.equal("summary" in contexts[0], false);
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
  assert.equal("destructiveHint" in data, false);
  assert.equal("bridgeCommand" in data, false);
  assert.equal("title" in data, false);
  assert.equal("summary" in data, false);
  assert.ok(data.inputSchema);
  assert.ok(data.outputSchema);
});

test("context describe without name returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("describe", [], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "CONTEXT_DESCRIBE_NAME_REQUIRED");
});

test("context describe editor.selection documents detail fields", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("describe", ["editor.selection"], {}, runtime, requestId);
  assert.equal(result.ok, true);

  const data = result.data as { outputSchema: { properties: Record<string, unknown> }; description: string };
  assert.match(data.description, /reusable object details/i);
  assert.ok(data.outputSchema.properties.activeObjectDetail);
  assert.ok(data.outputSchema.properties.objectDetails);
  assert.ok(data.outputSchema.properties.gameObjectDetails);
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

test("context read tools.groups uses offline CLI registry", async () => {
  const payloads: unknown[] = [];
  const runtime = makeRuntime(async (payload) => {
    payloads.push(payload);
    return { ok: true, data: {} };
  });

  const result = await handleContextCommand("read", ["tools.groups"], {}, runtime, requestId);

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.context, "tools.groups");
  assert.ok(Array.isArray(data.groups), "tools.groups must return groups array");
  assert.equal(typeof data.count, "number", "tools.groups must return count");
  assert.deepEqual(payloads, [], "tools.groups must not call the Unity bridge");
});

test("context read forwards structured input and recovers PowerShell escaped JSON", async () => {
  const payloads: Array<{ args: Record<string, unknown> }> = [];
  const bridgeExecute = async (payload: { args: Record<string, unknown> }) => {
    payloads.push(payload);
    return { ok: true, data: { context: "scene.gameobject", gameObject: { name: "Main Camera" } } };
  };
  const runtime = makeRuntime(bridgeExecute);

  const regular = await handleContextCommand("read", ["scene.gameobject"], { input: JSON.stringify({ target: "Main Camera", includeComponents: false }) }, runtime, requestId);
  const pwshEscaped = await handleContextCommand("read", ["scene.gameobject"], { input: "{\\ target\\:\\Main Camera\\,\\includeComponents\\:false}" }, runtime, requestId);
  const backslashQuoted = await handleContextCommand("read", ["scene.gameobject"], { input: '{\\"target\\":\\"Main Camera\\",\\"includeComponents\\":false}' }, runtime, requestId);

  assert.equal(regular.ok, true);
  assert.equal(pwshEscaped.ok, true);
  assert.equal(backslashQuoted.ok, true);
  assert.deepEqual(payloads[0].args, { name: "scene.gameobject", input: { target: "Main Camera", includeComponents: false }, target: "Main Camera", includeComponents: false });
  assert.deepEqual(payloads[1].args, { name: "scene.gameobject", input: { target: "Main Camera", includeComponents: false }, target: "Main Camera", includeComponents: false });
  assert.deepEqual(payloads[2].args, { name: "scene.gameobject", input: { target: "Main Camera", includeComponents: false }, target: "Main Camera", includeComponents: false });
});

test("context read rejects invalid --input JSON before bridge execution", async () => {
  const payloads: unknown[] = [];
  const runtime = makeRuntime(async (payload) => {
    payloads.push(payload);
    return { ok: true, data: {} };
  });

  const result = await handleContextCommand("read", ["scene.gameobject"], { input: "{not-json" }, runtime, requestId);

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_CONTEXT_INPUT_JSON_INVALID");
  assert.deepEqual(payloads, []);
});

test("context read supports --input-file", async () => {
  const tempDir = await mkdtemp(join(tmpdir(), "unity-cli-context-"));
  const inputPath = join(tempDir, "input.json");
  await writeFile(inputPath, JSON.stringify({ search: "File" }), "utf8");
  const payloads: Array<{ args: Record<string, unknown> }> = [];
  const runtime = makeRuntime(async (payload: { args: Record<string, unknown> }) => {
    payloads.push(payload);
    return { ok: true, data: { items: [], count: 0 } };
  });

  const result = await handleContextCommand("read", ["editor.menu-items"], { "input-file": inputPath }, runtime, requestId);

  assert.equal(result.ok, true);
  assert.deepEqual(payloads[0].args, { name: "editor.menu-items", input: { search: "File" }, search: "File" });
});

// ── unknown subcommand ────────────────────────────────────────────────────

test("unknown context subcommand returns error", async () => {
  const runtime = makeRuntime();
  const result = await handleContextCommand("unknown", [], {}, runtime, requestId);
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "UNKNOWN_CONTEXT_COMMAND");
});
