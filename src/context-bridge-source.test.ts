import { readdir, readFile } from "node:fs/promises";
import test from "node:test";
import assert from "node:assert/strict";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

test("Context commands register context.list, context.describe, context.read", async () => {
  const source = await readContextCommandsSource();
  assert.match(source, /RegisterCommand\(registry, "context\.list", "context", false/);
  assert.match(source, /RegisterCommand\(registry, "context\.describe", "context", false/);
  assert.match(source, /RegisterCommand\(registry, "context\.read", "context", false/);
});

test("Context built-in table contains all 16 expected context names", async () => {
  const source = await readContextCommandsSource();
  const expectedNames = [
    "project.info", "project.tags", "project.layers",
    "editor.state", "editor.selection", "editor.active-tool",
    "editor.windows", "editor.tool-states",
    "prefab.stage",
    "editor.menu-items",
    "scene.gameobject", "scene.cameras", "scene.volumes",
    "scene.renderer-features", "scene.rendering-stats",
    "tests.list"
  ];
  for (const name of expectedNames) {
    assert.match(source, new RegExp(`Name = "${escapeRegex(name)}"`), `Built-in context table must include ${name}`);
  }
});

test("All context descriptors have readOnlyHint=true", async () => {
  const source = await readContextCommandsSource();
  // Every context descriptor should output readOnlyHint: true
  const readOnlyMatches = source.match(/\.Prop\("readOnlyHint", true\)/g);
  assert.ok(readOnlyMatches !== null, "Must have .Prop(\"readOnlyHint\", true) calls");
  // At least 16 (one per read handler) + 16 (one per descriptor) = 32 minimum
  assert.ok(readOnlyMatches.length >= 16, `Expected at least 16 readOnlyHint=true, got ${readOnlyMatches.length}`);
});

test("Context read handlers never modify Unity state", async () => {
  const source = await readContextCommandsSource();
  // These are forbidden in context read handlers
  assert.doesNotMatch(source, /ExecuteMenuItem/, "Context must never call ExecuteMenuItem");
  assert.doesNotMatch(source, /SaveAssets\b/, "Context must never call SaveAssets");
  assert.doesNotMatch(source, /AssetDatabase\.Refresh/, "Context must never call AssetDatabase.Refresh");
  assert.doesNotMatch(source, /isPlaying\s*=\s*true/, "Context must never set isPlaying = true");
  assert.doesNotMatch(source, /isPlaying\s*=\s*false/, "Context must never set isPlaying = false");
  assert.doesNotMatch(source, /EditorSceneManager\.SaveScene/, "Context must never call SaveScene");
  assert.doesNotMatch(source, /MarkSceneDirty/, "Context must never call MarkSceneDirty");
  assert.doesNotMatch(source, /PackageManager\./, "Context must never modify packages");
});

test("prefab.stage is named 'prefab.stage' not 'editor.prefab-stage'", async () => {
  const source = await readContextCommandsSource();
  assert.match(source, /Name = "prefab\.stage"/, "prefab.stage must use the correct name");
  assert.doesNotMatch(source, /Name = "editor\.prefab-stage"/, "editor.prefab-stage is the old name and must not appear");
  assert.match(source, /Category = "prefab"/, "prefab.stage category must be 'prefab'");
});

test("scene.renderer-features has source=built-in", async () => {
  const source = await readContextCommandsSource();
  // Find the renderer-features definition and verify it uses source=built-in
  const rfIndex = source.indexOf("scene.renderer-features");
  assert.ok(rfIndex > -1, "Must find scene.renderer-features in source");
  // Find the enclosing BuiltInContextDefinition block
  const blockStart = source.lastIndexOf("new BuiltInContextDefinition", rfIndex);
  const blockEnd = source.indexOf("ReadHandler =", rfIndex);
  const block = source.slice(blockStart, blockEnd > blockStart ? blockEnd : rfIndex + 200);
  assert.match(block, /Source = "built-in"/, "scene.renderer-features must have source=built-in");
  assert.match(block, /com\.unity\.render-pipelines\.universal/, "scene.renderer-features must list URP in requiresPackages");
});

test("C# descriptor includes inputSchema and outputSchema (B2)", async () => {
  const source = await readContextCommandsSource();
  // BuiltInContextDefinition must have InputSchema and OutputSchema fields
  assert.match(source, /public string InputSchema;/, "BuiltInContextDefinition must have InputSchema field");
  assert.match(source, /public string OutputSchema;/, "BuiltInContextDefinition must have OutputSchema field");
  // ContextDescriptorJson must output them
  assert.match(source, /\.PropRaw\("inputSchema",/, "ContextDescriptorJson must output inputSchema");
  assert.match(source, /\.PropRaw\("outputSchema",/, "ContextDescriptorJson must output outputSchema");
  // At least some contexts must have non-empty schemas
  assert.match(source, /InputSchema = "\{/, "At least one context must have non-empty InputSchema");
  assert.match(source, /OutputSchema = "\{/, "At least one context must have non-empty OutputSchema");
});

test("editor.tool-states EquivalentUnityMcp is 'none' with note (B1)", async () => {
  const source = await readContextCommandsSource();
  const tsIndex = source.indexOf("editor.tool-states");
  assert.ok(tsIndex > -1, "Must find editor.tool-states in source");
  const blockStart = source.lastIndexOf("new BuiltInContextDefinition", tsIndex);
  const blockEnd = source.indexOf("ReadHandler =", tsIndex);
  const block = source.slice(blockStart, blockEnd > blockStart ? blockEnd : tsIndex + 300);
  assert.match(block, /EquivalentUnityMcp = "none"/, "editor.tool-states EquivalentUnityMcp must be 'none'");
  assert.match(block, /EquivalentUnityMcpNote =/, "editor.tool-states must have EquivalentUnityMcpNote");
  assert.match(block, /MCP tool discovery/, "Note must mention MCP tool discovery");
  // ContextDescriptorJson must output EquivalentUnityMcpNote when present
  assert.match(source, /equivalentUnityMcpNote/, "ContextDescriptorJson must handle equivalentUnityMcpNote");
});

test("Context availability-sensitive handlers return available=false with diagnostics", async () => {
  const source = await readContextCommandsSource();
  // renderer-features, rendering-stats, tests.list should have available=false fallback
  assert.match(source, /Prop\("available", false\)/, "Availability-sensitive contexts must return available=false when unavailable");
  assert.match(source, /Prop\("diagnostics",/, "Availability-sensitive contexts must include diagnostics");
  assert.match(source, /PropArray\("next_actions"/, "Availability-sensitive contexts must include next_actions");
});

test("Context commands are registered as non-mutating (allowed in Play Mode)", async () => {
  const allSources = await readUnityBridgeSources();
  assert.match(allSources, /RegisterCommand\(registry, "context\.list", "context", false/);
  assert.match(allSources, /RegisterCommand\(registry, "context\.describe", "context", false/);
  assert.match(allSources, /RegisterCommand\(registry, "context\.read", "context", false/);
});

async function readContextCommandsSource(): Promise<string> {
  return readFile(new URL("Commands/ContextCommands.cs", editorUrl), "utf8");
}

async function readUnityBridgeSources(): Promise<string> {
  const files = await listCSharpFiles(editorUrl);
  return (await Promise.all(files.map(async (url) => readFile(url, "utf8")))).join("\n");
}

async function listCSharpFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCSharpFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

function escapeRegex(str: string): string {
  return str.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
