import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { handleContextCommand } from "../src/contexts/commands.js";
import { handleToolsCommand } from "../src/tools/commands.js";
import { staticCapabilityCatalog } from "../src/static-capability-catalog.js";

const OFFLINE_INSTANCE = `unity-${"f".repeat(64)}`;

test("packaged static catalog exactly covers C# built-in and Extension Tool inventory", async () => {
  const catalog = staticCapabilityCatalog();
  const completion = JSON.parse(await readFile(new URL("../contracts/tool-completion-contract.json", import.meta.url), "utf8")) as {
    tools: Array<{ toolName: string; descriptorOwner: string }>;
  };
  const expected = completion.tools
    .filter((tool) => tool.descriptorOwner === "built-in" || tool.descriptorOwner.startsWith("extension:"))
    .map((tool) => tool.toolName)
    .sort();
  const actual = catalog.tools.map((tool) => String(tool.name)).sort();
  assert.deepEqual(actual, expected);
  assert.equal(new Set(actual).size, actual.length);
  assert.ok(catalog.tools.every((tool) => tool.inputSchema && tool.outputSchema));
  assert.ok(catalog.tools.every((tool) => tool.source === "built-in" || tool.source === "extension"));
  assert.ok(catalog.contexts.every((context) => context.source === "built-in" || context.source === "extension"));
  assert.ok(catalog.contexts.every((context) => /^[a-f0-9]{64}$/.test(String(context.descriptorFingerprint))));
  assert.doesNotMatch(JSON.stringify(catalog), /bridgeEndpoint|listenerId|actualPort|sourceListenerGeneration/);
});

test("session-mutating read-shaped Tools declare explicit side effects", () => {
  const tools = new Map(staticCapabilityCatalog().tools.map((tool) => [String(tool.name), tool]));
  const expected: Record<string, string[]> = {
    "asset.preview": ["asset-preview-generation", "editor-cache-write"],
    "console.clear": ["editor-console-state"],
    "editor.active-tool.set": ["editor-tool-state"],
    "editor.redo": ["undo-redo-replay", "undo-stack"],
    "editor.selection.set": ["editor-selection"],
    "editor.undo": ["undo-redo-replay", "undo-stack"],
    "graphics.bake-cancel": ["lightmapping-bake-cancel"],
    "graphics.scene-debug": ["scene-view-state"],
    "profiler.clear": ["profiler-data-clear"],
    "profiler.deep": ["profiler-session-state"],
    "profiler.frame": ["frame-timing-capture"],
    "profiler.frame-debugger.disable": ["frame-debugger-state"],
    "profiler.frame-debugger.enable": ["frame-debugger-state", "editor-window-state"],
    "profiler.load": ["profiler-loaded-data-session", "profiler-recording-stop"],
    "profiler.module-enable": ["profiler-area-state"],
    "profiler.pause": ["profiler-session-state"],
    "profiler.start": ["profiler-session-state"],
    "profiler.stop": ["profiler-session-state"],
    "scene.move-object": ["scene-modify"],
    "scene.view-frame": ["editor-selection", "scene-view-state"],
    "ui.modify-visual-element": ["ui-document-session-state"],
  };
  for (const [name, sideEffects] of Object.entries(expected)) {
    assert.deepEqual(tools.get(name)?.sideEffects, sideEffects, `${name} must be classified as mutation`);
  }
});

test("static Tool discovery stays available with an offline explicit instance and performs no live call", async () => {
  const runtime = { dataDir: "Z:/definitely-missing-unity-cli-data" };
  const flags = { instance: OFFLINE_INSTANCE };
  const group = await handleToolsCommand("group", [], flags, runtime, "req_static_group");
  assert.equal(group.ok, true);
  assert.ok((group.data as { groups: Array<{ name: string }> }).groups.some((entry) => entry.name === "scene"));

  const search = await handleToolsCommand("search", ["hierarchy"], flags, runtime, "req_static_search");
  assert.equal(search.ok, true);
  assert.ok((search.data as { tools: Array<{ name: string }> }).tools.some((tool) => tool.name === "scene.hierarchy"));

  const describe = await handleToolsCommand("describe", ["scene.hierarchy"], flags, runtime, "req_static_describe");
  assert.equal(describe.ok, true);
  assert.equal((describe.data as { source: string }).source, "built-in");
});

test("static Context discovery stays available with an offline explicit instance", async () => {
  const runtime = { dataDir: "Z:/definitely-missing-unity-cli-data" };
  const flags = { instance: OFFLINE_INSTANCE };
  const listed = await handleContextCommand("list", [], flags, runtime, "req_static_context_list");
  assert.equal(listed.ok, true);
  assert.equal((listed.data as { count: number }).count, 25);

  const searched = await handleContextCommand("search", ["editor.state"], flags, runtime, "req_static_context_search");
  assert.equal(searched.ok, true);
  assert.ok((searched.data as { contexts: Array<{ name: string }> }).contexts.some((context) => context.name === "editor.state"));

  const described = await handleContextCommand("describe", ["editor.state"], flags, runtime, "req_static_context_describe");
  assert.equal(described.ok, true);
  assert.equal((described.data as { source: string }).source, "built-in");
});
