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

test("local builtin context descriptors expose TS-owned contexts", () => {
  const contexts = builtinContextDescriptors();
  assert.deepEqual(contexts.map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
  assert.deepEqual(defineContextMetadata().map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
});

test("TS-owned context descriptors keep public descriptor hygiene", () => {
  const forbidden = ["title", "kind", "resourceUri", "destructiveHint", "bridgeCommand"];
  for (const ctx of builtinContextDescriptors()) {
    assert.ok(ctx.name, "name required");
    assert.ok(ctx.summary, `${ctx.name} summary required`);
    assert.ok(ctx.description, `${ctx.name} description required`);
    assert.equal(ctx.source, "built-in", `${ctx.name} source must be built-in`);
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

test("local context categories, filtering, and search reflect TS-owned contexts only", () => {
  assert.deepEqual(builtinContextCategories(), ["tools"]);
  assert.deepEqual(filterContextsByCategory("tools").map((context) => context.name).sort(), ["tools.custom", "tools.groups"]);
  assert.deepEqual(filterContextsByCategory("scene"), []);
  assert.equal(findBuiltinContextByName("scene.cameras"), undefined);
  assert.ok(searchContexts("custom").some((context) => context.name === "tools.custom"));
  assert.equal(searchContexts("camera").length, 0, "TS static metadata contains only local contexts");
});
