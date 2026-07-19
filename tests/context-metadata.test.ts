import test from "node:test";
import assert from "node:assert/strict";
import { access } from "node:fs/promises";
import { constants } from "node:fs";
import { builtinContextCategories, builtinContextDescriptors, findBuiltinContextByName, filterContextsByCategory, searchContexts } from "../src/contexts/registry.js";
import { defineContextMetadata } from "../src/contexts/registry.js";
import { validateSchema } from "../src/schema/validator.js";

async function exists(path: string): Promise<boolean> {
  try {
    await access(path, constants.F_OK);
    return true;
  } catch {
    return false;
  }
}

test("TS context metadata has one authoritative plural source", async () => {
  assert.equal(await exists(["src", "context"].join("/")), false, "singular Context source directory must not exist");
  assert.equal(await exists("src/contexts/catalog.ts"), true, "plural Context catalog must exist");
  assert.equal(await exists("src/contexts/dispatcher.ts"), true, "plural Context dispatcher must exist");
});

test("offline context catalog merges TS-owned and packaged C# descriptors", () => {
  const contexts = builtinContextDescriptors();
  assert.equal(contexts.length, 25);
  assert.ok(contexts.some((context) => context.name === "editor.state" && context.source === "built-in"));
  assert.ok(contexts.some((context) => context.name === "probuilder.scene" && context.source === "extension"));
  assert.deepEqual(defineContextMetadata().map((context) => context.name).sort(), ["editor.state", "tools.custom", "tools.groups"]);
  assert.equal(findBuiltinContextByName("editor.state")?.supportStatus, "hybrid");
});

test("offline context descriptors keep public descriptor hygiene", () => {
  const forbidden = ["title", "kind", "resourceUri", "destructiveHint", "bridgeCommand"];
  for (const ctx of builtinContextDescriptors()) {
    assert.ok(ctx.name, "name required");
    assert.ok(ctx.summary, `${ctx.name} summary required`);
    assert.ok(ctx.description, `${ctx.name} description required`);
    assert.ok(ctx.source === "built-in" || ctx.source === "extension", `${ctx.name} source must be static`);
    assert.ok(ctx.inputSchema, `${ctx.name} inputSchema required`);
    assert.ok(ctx.outputSchema, `${ctx.name} outputSchema required`);
    assert.ok(ctx.examples?.length, `${ctx.name} must expose at least one example`);
    for (const example of ctx.examples ?? []) {
      assert.deepEqual(example.argv.slice(0, 3), ["context", "read", ctx.name], `${ctx.name} example argv`);
      assert.deepEqual(validateSchema(ctx.inputSchema, example.input ?? {}), [], `${ctx.name} example input must satisfy inputSchema`);
    }
    assert.ok(ctx.cliCommand.startsWith("context read "), `${ctx.name} cliCommand must be context read form`);
    for (const field of forbidden) {
      assert.equal(field in ctx, false, `${ctx.name} must not expose ${field}`);
    }
  }
});

test("tools.groups remains offline and tools.custom remains live bridge backed", () => {
  const toolsGroups = findBuiltinContextByName("tools.groups");
  assert.ok(toolsGroups, "tools.groups must exist");
  assert.equal(toolsGroups.supportStatus, "cli-only");
  assert.equal(toolsGroups.availabilityScope, "always");
  assert.ok((toolsGroups.outputSchema.properties as Record<string, unknown>).groups, "tools.groups must expose groups array");

  const toolsCustom = findBuiltinContextByName("tools.custom");
  assert.ok(toolsCustom, "tools.custom must exist");
  assert.equal(toolsCustom.supportStatus, "live-bridge");
  assert.equal(toolsCustom.refreshBehavior, "on-demand");
  assert.ok((toolsCustom.outputSchema.properties as Record<string, unknown>).tools, "tools.custom must expose tools array");
});

test("offline context categories, filtering, and search include packaged C# metadata", () => {
  assert.deepEqual(builtinContextCategories(), ["editor", "instances", "prefab", "probuilder", "project", "scene", "tests", "tools"]);
  assert.deepEqual(filterContextsByCategory("tools").map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
  assert.ok(filterContextsByCategory("scene").some((context) => context.name === "scene.cameras"));
  assert.equal(findBuiltinContextByName("scene.cameras")?.source, "built-in");
  assert.ok(searchContexts("custom").some((context) => context.name === "tools.custom"));
  assert.ok(searchContexts("camera").some((context) => context.name === "scene.cameras"));
});
