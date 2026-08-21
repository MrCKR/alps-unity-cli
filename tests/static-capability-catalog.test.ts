import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { handleToolsCommand } from "../src/tools/commands.js";
import { staticBridgeToolMetadata } from "../src/tools/catalog.js";
import { staticCapabilityCatalog } from "../src/static-capability-catalog.js";

const OFFLINE_INSTANCE = `unity-${"f".repeat(64)}`;

test("static catalog capture uses a complete internal descriptor snapshot", async () => {
  const source = await readFile(join(process.cwd(), "scripts", "capture-static-capability-catalog.mjs"), "utf8");
  assert.match(source, /dist\/src\/bridge\/index\.js/);
  assert.match(source, /map\(withGeneratedCompletionContract\)[\s\S]*map\(toStaticToolDescriptor\)[\s\S]*map\(stripDynamicToolFields\)/);
  assert.match(source, /Missing generated completion contract for \$\{descriptor\.name\}/);
  assert.match(source, /function toStaticToolDescriptor[\s\S]*\.\.\.metadata/);
  assert.doesNotMatch(source, /publicToolDescriptor/);
  assert.match(source, /"supportsDryRun", "contractProfile", "permissions"/);
  assert.doesNotMatch(source, /contextsResult|publicContextDescriptor|stripDynamicContextFields/);
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
  assert.equal(Object.hasOwn(catalog, "contexts"), false);
  const forbiddenToolFields = ["artifactKinds", "capabilityType", "cliCommand", "completionContract", "defaultTimeoutMs", "descriptorFingerprint", "fingerprint", "handler", "internalDescriptorFingerprint", "maxTimeoutMs", "supportStatus", "supportsJob", "version"];
  for (const tool of catalog.tools) {
    for (const field of forbiddenToolFields) assert.equal(Object.hasOwn(tool, field), false, `${String(tool.name)} must not freeze ${field}`);
    const outputFields = schemaPropertyNames(tool.outputSchema);
    const forbiddenOutputFields = ["command", "execution", "logs", "logPath", "audit", "handlerFingerprint", "descriptorFingerprint", "endpoint", "port", "listenerId", "listenerGeneration", "sourceListenerGeneration", "hwnd", "commandLine"];
    for (const field of forbiddenOutputFields) assert.equal(outputFields.has(field), false, `${String(tool.name)} outputSchema must not expose ${field}`);
    for (const example of (tool.examples ?? []) as Array<Record<string, unknown>>) assert.deepEqual(Object.keys(example).sort(), ["argv", "description"], `${String(tool.name)} static examples must remain argv-only`);
    for (const field of ["permissions", "requiresPackages", "sideEffects"]) if (Object.hasOwn(tool, field)) assert.ok((tool[field] as unknown[]).length > 0, `${String(tool.name)} must omit empty ${field}`);
  }
  assert.doesNotMatch(JSON.stringify(catalog), /bridgeEndpoint|listenerId|actualPort|sourceListenerGeneration/);
});

test("canonical Tools retain every approved duplicate successor", () => {
  const catalog = staticCapabilityCatalog();
  const tools = new Set(catalog.tools.map((tool) => String(tool.name)));
  const successors: Record<string, string> = {
    "prefab.hierarchy": "prefab.hierarchy",
    "prefab.info": "prefab.info",
    "scene.cameras": "camera.list",
    "scene.opened": "scene.list-opened",
    "scene.rendering-stats": "profiler.rendering-stats",
  };
  for (const canonicalTool of Object.values(successors)) {
    assert.equal(tools.has(canonicalTool), true, `${canonicalTool} must remain the canonical Tool`);
  }
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

test("public Tool discovery rejects an offline explicit instance without static fallback", async () => {
  const runtime = { dataDir: "Z:/definitely-missing-unity-cli-data" };
  const flags = { instance: OFFLINE_INSTANCE };
  for (const [command, args] of [["group", []], ["search", ["hierarchy"]], ["describe", ["scene.hierarchy"]]] as const) {
    const result = await handleToolsCommand(command, [...args], flags, runtime, `req_offline_${command}`);
    assert.equal(result.ok, false);
    assert.equal(result.error?.code, "E_INSTANCE_NOT_FOUND");
  }
});

test("all detailed discovery paths expose the complete connected instance catalog", async () => {
  const runtime = {
    projectTools: staticBridgeToolMetadata(),
    runtimeTools: [],
    bridgeExecute: async (payload: { requestId: string }) => ({ ok: true as const, requestId: payload.requestId, data: {} }),
  };
  const described = await handleToolsCommand("describe", ["build.profile.create"], {}, runtime, "req_complete_describe");
  assert.equal(described.ok, true);

  const grouped = await handleToolsCommand("group", ["list", "build"], {}, runtime, "req_complete_group");
  assert.equal(grouped.ok, true);
  const groupProfiles = (grouped.data as { tools: Array<{ name: string }> }).tools.filter((tool) => tool.name.startsWith("build.profile."));
  assert.equal(groupProfiles.length, 4);

  const searched = await handleToolsCommand("search", ["build.profile"], {}, runtime, "req_complete_search");
  assert.equal(searched.ok, true);
  const searchProfiles = (searched.data as { tools: Array<{ name: string }> }).tools;
  assert.equal(searchProfiles.length, 4);
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
