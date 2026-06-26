import test from "node:test";
import assert from "node:assert/strict";
import {
  builtinContextMetadata,
  builtinContextCategories,
  findContextByName,
  filterContextsByCategory,
  searchContexts,
  type ContextMetadata
} from "../src/context/metadata/index.js";

test("builtin context metadata contains all required entries", () => {
  const contexts = builtinContextMetadata();
  assert.equal(contexts.length, 23);
});

test("all builtin contexts have kind=context", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.equal(ctx.kind, "context", `${ctx.name} kind must be "context"`);
  }
});

test("all builtin contexts have resourceUri starting with unity-cli://", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.ok(ctx.resourceUri, `${ctx.name} must have resourceUri`);
    assert.ok(ctx.resourceUri.startsWith("unity-cli://"), `${ctx.name} resourceUri must start with unity-cli://`);
  }
});

test("all builtin contexts have equivalentUnityMcp (or 'none' with note)", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.ok(ctx.equivalentUnityMcp, `${ctx.name} must have equivalentUnityMcp`);
    if (ctx.equivalentUnityMcp === "none") {
      assert.ok(ctx.equivalentUnityMcpNote, `${ctx.name} has equivalentUnityMcp='none' and must explain the semantic difference`);
    } else {
      assert.ok(ctx.equivalentUnityMcp.startsWith("mcpforunity://"), `${ctx.name} equivalentUnityMcp must start with mcpforunity://`);
    }
  }
});

test("all builtin contexts have outputSchema", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.ok(ctx.outputSchema, `${ctx.name} must have outputSchema`);
    assert.equal(typeof ctx.outputSchema, "object", `${ctx.name} outputSchema must be an object`);
  }
});

test("all builtin contexts expose playModeSupport", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.ok(["supported", "runtime-only", "session-affecting", "editor-only", "unknown"].includes(ctx.playModeSupport ?? ""), `${ctx.name} must have playModeSupport`);
  }
});

test("all builtin contexts declare an explicit handler", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.ok(["bridge", "bridge-runtime", "offline"].includes(ctx.handler ?? ""), `${ctx.name} handler must be explicit`);
  }
});

test("all builtin contexts have source=built-in", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.equal(ctx.source, "built-in", `${ctx.name} source must be "built-in"`);
  }
});

test("all builtin contexts have valid dotted names", () => {
  const namePattern = /^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*)+$/;
  for (const ctx of builtinContextMetadata()) {
    assert.ok(namePattern.test(ctx.name), `${ctx.name} must match dotted name pattern`);
  }
});

test("all builtin contexts have cliCommand", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.ok(ctx.cliCommand, `${ctx.name} must have cliCommand`);
    assert.ok(ctx.cliCommand.startsWith("context read "), `${ctx.name} cliCommand must start with "context read "`);
  }
});

test("context names match the expected built-in contexts", () => {
  const expectedNames = [
    "project.info",
    "project.tags",
    "project.layers",
    "editor.state",
    "editor.selection",
    "editor.active-tool",
    "editor.windows",
    "editor.tool-states",
    "prefab.stage",
    "editor.menu-items",
    "scene.gameobject",
    "scene.gameobject.components",
    "scene.gameobject.component",
    "scene.opened",
    "scene.cameras",
    "scene.volumes",
    "scene.renderer-features",
    "scene.rendering-stats",
    "prefab.info",
    "prefab.hierarchy",
    "tests.list",
    "tools.groups",
    "tools.custom"
  ];
  const actualNames = builtinContextMetadata().map((c) => c.name);
  assert.deepEqual(actualNames.sort(), expectedNames.sort());
});

test("prefab.stage is categorized as prefab (not editor)", () => {
  const prefabStage = findContextByName("prefab.stage");
  assert.ok(prefabStage, "prefab.stage must exist");
  assert.equal(prefabStage.category, "prefab");
  assert.equal(prefabStage.equivalentUnityMcp, "mcpforunity://editor/prefab-stage");
});

test("editor.tool-states has equivalentUnityMcp='none' with semantic difference note", () => {
  const toolStates = findContextByName("editor.tool-states");
  assert.ok(toolStates, "editor.tool-states must exist");
  assert.equal(toolStates.equivalentUnityMcp, "none");
  assert.ok(toolStates.equivalentUnityMcpNote, "editor.tool-states must have equivalentUnityMcpNote");
  assert.ok(toolStates.equivalentUnityMcpNote!.includes("MCP tool discovery"), "Note must explain that unity-mcp tool-states is about MCP tool discovery");
  assert.ok(toolStates.equivalentUnityMcpNote!.includes("Unity Editor tool settings"), "Note must explain that this context is about Unity Editor tool settings");
  const schema = toolStates.outputSchema as { properties: Record<string, unknown> };
  assert.ok(schema.properties.transform, "editor.tool-states must expose transform settings");
  assert.ok(schema.properties.snap, "editor.tool-states must expose snap settings");
  assert.ok(schema.properties.gridAvailable, "editor.tool-states must expose grid availability");
});

test("editor contexts describe read-only runtime-safe observations", () => {
  const expected = ["editor.state", "editor.selection", "editor.active-tool", "editor.windows", "editor.tool-states", "editor.menu-items"];
  for (const name of expected) {
    const ctx = findContextByName(name);
    assert.ok(ctx, `${name} must exist`);
    assert.equal(ctx.category, "editor", `${name} must stay in editor category`);
    assert.equal(ctx.playModeSupport ?? "supported", "supported", `${name} must be readable in Play Mode`);
    assert.equal(ctx.handler, "bridge", `${name} must route through bridge`);
  }

  const editorState = findContextByName("editor.state")!;
  assert.match(editorState.description, /busy/i, "editor.state must explain busy state");
  const stateSchema = editorState.outputSchema as { properties: Record<string, unknown> };
  assert.ok(stateSchema.properties.state, "editor.state must expose wrapped state payload");

  const menuItems = findContextByName("editor.menu-items")!;
  assert.match(menuItems.description, /no stable public API/i, "editor.menu-items must disclose Unity menu enumeration limitation");
  assert.match(menuItems.description, /editor\.menu/, "editor.menu-items must distinguish read context from menu execution tool");
  const menuSchema = menuItems.outputSchema as { properties: Record<string, unknown> };
  assert.ok(menuSchema.properties.diagnostics, "editor.menu-items must expose diagnostics");
});

test("scene.renderer-features has source=built-in (not extension)", () => {
  const rendererFeatures = findContextByName("scene.renderer-features");
  assert.ok(rendererFeatures, "scene.renderer-features must exist");
  assert.equal(rendererFeatures.source, "built-in");
  assert.deepEqual(rendererFeatures.requiresPackages, ["com.unity.render-pipelines.universal"]);
  assert.equal(rendererFeatures.equivalentUnityMcp, "mcpforunity://pipeline/renderer-features");
});

test("scene.gameobject has id/path/target input parameters", () => {
  const go = findContextByName("scene.gameobject");
  assert.ok(go, "scene.gameobject must exist");
  const schema = go.inputSchema as { type: string; properties: Record<string, unknown> };
  assert.equal(schema.type, "object");
  assert.ok(schema.properties.target, "scene.gameobject must accept target");
  assert.ok(schema.properties.id, "scene.gameobject must accept id");
  assert.ok(schema.properties.path, "scene.gameobject must accept path");
});

test("tests and tools contexts expose runtime-accurate schemas", () => {
  const tests = findContextByName("tests.list");
  assert.ok(tests, "tests.list must exist");
  const schema = tests.inputSchema as { type: string; properties: Record<string, unknown> };
  assert.equal(schema.type, "object");
  assert.ok(schema.properties.mode, "tests.list must accept mode (not platform)");
  assert.ok(!schema.properties.platform, "tests.list must NOT accept platform parameter");
  const testsOutput = tests.outputSchema as { properties: Record<string, unknown> };
  assert.ok(testsOutput.properties.tests, "tests.list must expose tests array");
  assert.ok(testsOutput.properties.count, "tests.list must expose count");

  const toolsGroups = findContextByName("tools.groups");
  assert.ok(toolsGroups, "tools.groups must exist");
  assert.equal(toolsGroups.handler, "offline", "tools.groups must disclose that it reads the local CLI registry");
  assert.equal(toolsGroups.availabilityScope, "always", "tools.groups must be available without a Unity session");

  const toolsCustom = findContextByName("tools.custom");
  assert.ok(toolsCustom, "tools.custom must exist");
  const customOutput = toolsCustom.outputSchema as { properties: Record<string, unknown> };
  assert.ok(customOutput.properties.tools, "tools.custom must expose tools array");
  assert.ok(customOutput.properties.count, "tools.custom must expose count");
});

test("builtinContextCategories returns expected categories", () => {
  const categories = builtinContextCategories();
  assert.ok(categories.includes("project"));
  assert.ok(categories.includes("editor"));
  assert.ok(categories.includes("prefab"));
  assert.ok(categories.includes("scene"));
  assert.ok(categories.includes("tests"));
});

test("findContextByName returns undefined for unknown context", () => {
  assert.equal(findContextByName("unknown.context"), undefined);
  assert.equal(findContextByName(""), undefined);
});

test("filterContextsByCategory returns correct contexts", () => {
  const project = filterContextsByCategory("project");
  assert.equal(project.length, 3);
  assert.ok(project.every((c) => c.category === "project"));

  const scene = filterContextsByCategory("scene");
  assert.equal(scene.length, 8);
  assert.ok(scene.every((c) => c.category === "scene"));

  const prefab = filterContextsByCategory("prefab");
  assert.equal(prefab.length, 3);
  assert.ok(prefab.some((c) => c.name === "prefab.stage"));
});

test("searchContexts finds contexts by keyword", () => {
  const camera = searchContexts("camera");
  assert.ok(camera.length >= 1);
  assert.ok(camera.some((c) => c.name === "scene.cameras"));

  const prefab = searchContexts("prefab");
  assert.ok(prefab.length >= 1);
  assert.ok(prefab.some((c) => c.name === "prefab.stage"));
});

test("all contexts have destructiveHint absent or undefined", () => {
  for (const ctx of builtinContextMetadata()) {
    const hasDestructive = "destructiveHint" in ctx && (ctx as Record<string, unknown>).destructiveHint !== undefined;
    assert.ok(!hasDestructive, `${ctx.name} must not have destructiveHint — contexts do not expose destructive metadata`);
  }
});

test("no context name duplicates", () => {
  const names = builtinContextMetadata().map((c) => c.name);
  const unique = new Set(names);
  assert.equal(names.length, unique.size, "All context names must be unique");
});
