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

test("all builtin contexts have kind=context and readOnlyHint=true", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.equal(ctx.kind, "context", `${ctx.name} kind must be "context"`);
    assert.equal(ctx.readOnlyHint, true, `${ctx.name} readOnlyHint must be true`);
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

test("all builtin contexts have handler=bridge", () => {
  for (const ctx of builtinContextMetadata()) {
    assert.equal(ctx.handler, "bridge", `${ctx.name} handler must be "bridge"`);
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

test("tests.list has mode input parameter (not platform)", () => {
  const tests = findContextByName("tests.list");
  assert.ok(tests, "tests.list must exist");
  const schema = tests.inputSchema as { type: string; properties: Record<string, unknown> };
  assert.equal(schema.type, "object");
  assert.ok(schema.properties.mode, "tests.list must accept mode (not platform)");
  assert.ok(!schema.properties.platform, "tests.list must NOT accept platform parameter");
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
    assert.ok(!hasDestructive, `${ctx.name} must not have destructiveHint — contexts are always read-only`);
  }
});

test("no context name duplicates", () => {
  const names = builtinContextMetadata().map((c) => c.name);
  const unique = new Set(names);
  assert.equal(names.length, unique.size, "All context names must be unique");
});
