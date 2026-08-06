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

test("audit-only Context snapshot merges CLI aggregates and packaged descriptors", () => {
  const contexts = builtinContextDescriptors();
  assert.equal(contexts.length, 24);
  assert.ok(contexts.some((context) => context.name === "editor.state" && context.source === "built-in"));
  assert.ok(contexts.some((context) => context.name === "probuilder.scene" && context.source === "extension"));
  assert.deepEqual(defineContextMetadata().map((context) => context.name).sort(), ["editor.dialogs", "editor.state"]);
});

test("audit-only Context descriptors keep public descriptor hygiene", () => {
  const forbidden = ["title", "kind", "resourceUri", "destructiveHint", "bridgeCommand", "cliCommand", "supportStatus", "refreshBehavior", "version", "fingerprint"];
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
      const inputIndex = example.argv.indexOf("--input");
      const input = inputIndex >= 0 ? JSON.parse(example.argv[inputIndex + 1]) : {};
      assert.deepEqual(validateSchema(ctx.inputSchema, input), [], `${ctx.name} example input must satisfy inputSchema`);
    }
    for (const field of forbidden) {
      assert.equal(field in ctx, false, `${ctx.name} must not expose ${field}`);
    }
  }
});

test("duplicate Tool discovery Contexts are absent", () => {
  assert.equal(findBuiltinContextByName("tools.groups"), undefined);
  assert.equal(findBuiltinContextByName("tools.custom"), undefined);
});

test("audit-only Context categories, filtering, and search include packaged C# metadata", () => {
  assert.deepEqual(builtinContextCategories(), ["editor", "instances", "prefab", "probuilder", "project", "scene", "tests"]);
  assert.deepEqual(filterContextsByCategory("tools"), []);
  assert.ok(filterContextsByCategory("scene").some((context) => context.name === "scene.cameras"));
  assert.equal(findBuiltinContextByName("scene.cameras")?.source, "built-in");
  assert.equal(searchContexts("custom").some((context) => context.name === "tools.custom"), false);
  assert.ok(searchContexts("camera").some((context) => context.name === "scene.cameras"));
});
