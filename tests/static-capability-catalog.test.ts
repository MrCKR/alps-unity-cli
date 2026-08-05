import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { handleContextCommand } from "../src/contexts/commands.js";
import { handleToolsCommand } from "../src/tools/commands.js";
import { staticCapabilityCatalog } from "../src/static-capability-catalog.js";

const OFFLINE_INSTANCE = `unity-${"f".repeat(64)}`;

test("static catalog capture uses compiled public descriptor projections", async () => {
  const source = await readFile(join(process.cwd(), "scripts", "capture-static-capability-catalog.mjs"), "utf8");
  assert.match(source, /dist\/src\/bridge\/index\.js/);
  assert.match(source, /map\(withGeneratedCompletionContract\)[\s\S]*map\(toPublicToolDescriptor\)[\s\S]*map\(stripDynamicToolFields\)/);
  assert.match(source, /Missing generated completion contract for \$\{descriptor\.name\}/);
  assert.match(source, /map\(toPublicContextDescriptor\)[\s\S]*map\(stripDynamicContextFields\)/);
  assert.match(source, /publicToolDescriptor\(metadata\)/);
  assert.match(source, /publicContextDescriptor\(parsed\)/);
  assert.doesNotMatch(source, /\.\.\/dist\/bridge\/index\.js/);
});

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
  const forbiddenToolFields = ["artifactKinds", "capabilityType", "cliCommand", "completionContract", "defaultTimeoutMs", "descriptorFingerprint", "fingerprint", "handler", "internalDescriptorFingerprint", "maxTimeoutMs", "supportStatus", "supportsJob", "version"];
  const forbiddenContextFields = ["capabilityType", "cliCommand", "descriptorFingerprint", "fingerprint", "handler", "refreshBehavior", "supportStatus", "version"];
  for (const tool of catalog.tools) {
    for (const field of forbiddenToolFields) assert.equal(Object.hasOwn(tool, field), false, `${String(tool.name)} must not freeze ${field}`);
    const outputFields = schemaPropertyNames(tool.outputSchema);
    const forbiddenOutputFields = ["command", "execution", "logs", "logPath", "audit", "handlerFingerprint", "descriptorFingerprint", "endpoint", "port", "listenerId", "listenerGeneration", "sourceListenerGeneration", "hwnd", "commandLine"];
    for (const field of forbiddenOutputFields) assert.equal(outputFields.has(field), false, `${String(tool.name)} outputSchema must not expose ${field}`);
    for (const example of (tool.examples ?? []) as Array<Record<string, unknown>>) assert.deepEqual(Object.keys(example).sort(), ["argv", "description"], `${String(tool.name)} examples must be public argv only`);
    for (const field of ["permissions", "requiresPackages", "sideEffects"]) if (Object.hasOwn(tool, field)) assert.ok((tool[field] as unknown[]).length > 0, `${String(tool.name)} must omit empty ${field}`);
  }
  for (const context of catalog.contexts) {
    for (const field of forbiddenContextFields) assert.equal(Object.hasOwn(context, field), false, `${String(context.name)} must not freeze ${field}`);
    for (const field of ["command", "execution", "logs", "logPath", "audit", "handlerFingerprint", "descriptorFingerprint", "endpoint", "port", "listenerId", "listenerGeneration", "sourceListenerGeneration", "hwnd", "commandLine"]) {
      assert.equal(schemaPropertyNames(context.outputSchema).has(field), false, `${String(context.name)} outputSchema must not expose ${field}`);
    }
    for (const example of (context.examples ?? []) as Array<Record<string, unknown>>) assert.deepEqual(Object.keys(example).sort(), ["argv", "description"], `${String(context.name)} examples must be public argv only`);
    if (Object.hasOwn(context, "requiresPackages")) assert.ok((context.requiresPackages as unknown[]).length > 0, `${String(context.name)} must omit empty requiresPackages`);
  }
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

test("all detailed discovery paths mark conditional Tools unavailable when the selected generation omits them", async () => {
  const runtime = { projectTools: [] };
  const described = await handleToolsCommand("describe", ["build.profile.create"], {}, runtime, "req_conditional_describe");
  assert.equal(described.ok, true);
  const descriptor = described.data as { available: boolean; unavailableReason?: string };
  assert.equal(descriptor.available, false);
  assert.match(String(descriptor.unavailableReason), /^E_TOOL_UNAVAILABLE:/);

  const grouped = await handleToolsCommand("group", ["list", "build"], {}, runtime, "req_conditional_group");
  assert.equal(grouped.ok, true);
  const groupProfiles = (grouped.data as { tools: Array<{ name: string; available: boolean; unavailableReason?: string }> }).tools.filter((tool) => tool.name.startsWith("build.profile."));
  assert.equal(groupProfiles.length, 4);
  assert.ok(groupProfiles.every((tool) => tool.available === false && /^E_TOOL_UNAVAILABLE:/.test(String(tool.unavailableReason))));

  const searched = await handleToolsCommand("search", ["build.profile"], {}, runtime, "req_conditional_search");
  assert.equal(searched.ok, true);
  const searchProfiles = (searched.data as { tools: Array<{ name: string; available: boolean; unavailableReason?: string }> }).tools;
  assert.equal(searchProfiles.length, 4);
  assert.ok(searchProfiles.every((tool) => tool.available === false && /^E_TOOL_UNAVAILABLE:/.test(String(tool.unavailableReason))));
});

function schemaPropertyNames(value: unknown, result = new Set<string>()): Set<string> {
  if (Array.isArray(value)) {
    for (const item of value) schemaPropertyNames(item, result);
    return result;
  }
  if (!value || typeof value !== "object") return result;
  for (const [key, nested] of Object.entries(value as Record<string, unknown>)) {
    if (key === "properties" && nested && typeof nested === "object" && !Array.isArray(nested)) {
      for (const property of Object.keys(nested as Record<string, unknown>)) result.add(property);
    }
    schemaPropertyNames(nested, result);
  }
  return result;
}

test("static Context discovery stays available with an offline explicit instance", async () => {
  const runtime = { dataDir: "Z:/definitely-missing-unity-cli-data" };
  const flags = { instance: OFFLINE_INSTANCE };
  const listed = await handleContextCommand("list", [], flags, runtime, "req_static_context_list");
  assert.equal(listed.ok, true);
  assert.equal((listed.data as { count: number }).count, 26);

  const searched = await handleContextCommand("search", ["editor.state"], flags, runtime, "req_static_context_search");
  assert.equal(searched.ok, true);
  assert.ok((searched.data as { contexts: Array<{ name: string }> }).contexts.some((context) => context.name === "editor.state"));

  const described = await handleContextCommand("describe", ["editor.state"], flags, runtime, "req_static_context_describe");
  assert.equal(described.ok, true);
  assert.equal((described.data as { source: string }).source, "built-in");
});
