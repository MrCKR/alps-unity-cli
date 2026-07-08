import { readdir, readFile } from "node:fs/promises";
import test from "node:test";
import assert from "node:assert/strict";

const editorUrl = new URL("../../unitypkg/Editor/", import.meta.url);

test("Context commands register context.list, context.describe, context.read", async () => {
  // Registration is in ProjectContextRegistry.cs through the unified handler.
  const source = await readContextCommandsSource();
  const allSources = await readUnityBridgeSources();
  // ContextCommands.cs has the built-in context definitions and read handlers
  // ProjectContextRegistry.cs has the unified RegisterContextCommands
  assert.match(allSources, /RegisterCommand\(registry, "context\.list", "context", false/);
  assert.match(allSources, /RegisterCommand\(registry, "context\.describe", "context", false/);
  assert.match(allSources, /RegisterCommand\(registry, "context\.read", "context", false/);
});

test("Context built-in table contains all expected context names", async () => {
  const source = await readContextCommandsSource();
  const expectedNames: string[] = [];
  for (const name of expectedNames) {
    assert.match(source, new RegExp(`Name = "${escapeRegex(name)}"`), `Built-in context table must include ${name}`);
  }
  assert.match(source, /return Array\.Empty<BuiltInContextDefinition>\(\)/, "legacy built-in table should be empty after Stage 6 context migration");
  const interfaceOwnedContexts = [
    ["project.info", "ProjectInfoContext"],
    ["project.tags", "ProjectTagsContext"],
    ["project.layers", "ProjectLayersContext"],
    ["editor.state", "EditorStateContext"],
    ["editor.selection", "EditorSelectionContext"],
    ["editor.windows", "EditorWindowsContext"],
    ["editor.active-tool", "EditorActiveToolContext"],
    ["editor.tool-states", "EditorToolStatesContext"],
    ["editor.menu-items", "EditorMenuItemsContext"],
    ["prefab.stage", "PrefabStageContext"],
    ["prefab.info", "PrefabInfoContext"],
    ["prefab.hierarchy", "PrefabHierarchyContext"],
    ["scene.opened", "SceneOpenedContext"],
    ["scene.cameras", "SceneCamerasContext"],
    ["scene.gameobject", "SceneGameObjectContext"],
    ["scene.gameobject.components", "SceneGameObjectComponentsContext"],
    ["scene.gameobject.component", "SceneGameObjectComponentContext"],
    ["scene.volumes", "SceneVolumesContext"],
    ["scene.renderer-features", "SceneRendererFeaturesContext"],
    ["scene.rendering-stats", "SceneRenderingStatsContext"],
    ["tests.list", "TestsListContext"],
    ["instances.list", "InstancesListContext"]
  ] as const;
  const allSources = await readUnityBridgeSources();
  for (const [name, className] of interfaceOwnedContexts) {
    assert.doesNotMatch(source, new RegExp(`Name = "${escapeRegex(name)}"`), `${name} is IUnityCliContext-owned and must not stay in the C# built-in table`);
    assert.match(allSources, new RegExp(`class ${className} : IUnityCliContext<${className}\\.Request, ${className}\\.Result>`), `${name} must be implemented as an IUnityCliContext class`);
    assert.match(allSources, new RegExp(`Name\\s*=\\s*"${escapeRegex(name)}"`), `${name} descriptor must keep the public context name`);
  }
  assert.match(allSources, /Source\s*=\s*"built-in"/, "interface-owned built-in contexts must declare source=built-in");
  assert.doesNotMatch(source, /Name = "tools\.groups"/, "tools.groups is TS-owned and must not stay in the C# built-in table");
  assert.doesNotMatch(source, /Name = "tools\.custom"/, "tools.custom is TS-owned and must not stay in the C# built-in table");
});

test("Context built-in descriptor rows keep C# initializer commas", async () => {
  const source = await readContextCommandsSource();
  assert.doesNotMatch(source, /Description = "[^\n]+"\r?\n\s+Category = /, "Description initializer must be followed by a comma before Category");
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
  const allSources = await readUnityBridgeSources();
  assert.match(allSources, /Name\s*=\s*"prefab\.stage"/, "prefab.stage must use the correct name");
  assert.doesNotMatch(allSources, /Name\s*=\s*"editor\.prefab-stage"/, "editor.prefab-stage is the old name and must not appear");
  assert.match(allSources, /Category\s*=\s*"prefab"/, "prefab.stage category must be 'prefab'");
});

test("prefab.info is interface-owned and preserves legacy prefab payload", async () => {
  const source = await readContextCommandsSource();
  const allSources = await readUnityBridgeSources();
  assert.doesNotMatch(source, /ReadPrefabInfoContext/, "prefab.info read handler moved to IUnityCliContext");
  assert.match(allSources, /class PrefabInfoContext : IUnityCliContext<PrefabInfoContext\.Request, PrefabInfoContext\.Result>, IUnityCliBuiltInRawBridgeContext/, "prefab.info must keep raw read support");
  assert.match(allSources, /Name\s*=\s*"prefab\.info"/, "prefab.info descriptor must keep public context name");
  assert.match(allSources, /PrefabInfoPayloadJson[\s\S]*PropRaw\("prefab", PrefabAssetJson\(path, prefab\)\)/, "prefab.info must preserve the legacy prefab payload");
  assert.match(allSources, /E_PREFAB_PATH_REQUIRED/, "prefab.info should keep a stable path-required error code");
  assert.doesNotMatch(allSources, /unity_cli prefab/, "prefab.info path hints must not reference removed legacy prefab CLI");
});

test("prefab.hierarchy is interface-owned and preserves legacy paged hierarchy payload", async () => {
  const source = await readContextCommandsSource();
  const allSources = await readUnityBridgeSources();
  assert.doesNotMatch(source, /ReadPrefabHierarchyContext/, "prefab.hierarchy read handler moved to IUnityCliContext");
  assert.match(allSources, /class PrefabHierarchyContext : IUnityCliContext<PrefabHierarchyContext\.Request, PrefabHierarchyContext\.Result>, IUnityCliBuiltInRawBridgeContext/, "prefab.hierarchy must keep raw read support");
  assert.match(allSources, /Name\s*=\s*"prefab\.hierarchy"/, "prefab.hierarchy descriptor must keep public context name");
  assert.match(allSources, /PrefabHierarchyPayloadJson[\s\S]*PrefabUtility\.LoadPrefabContents[\s\S]*CollectHierarchy[\s\S]*PropRaw\("prefab", PrefabAssetJson\(path, root, root\)\)[\s\S]*PropArray\("items"[\s\S]*PropObject\("pageInfo"/, "prefab.hierarchy must preserve prefab/items/pageInfo payload");
  assert.match(allSources, /PrefabUtility\.UnloadPrefabContents\(root\)/, "prefab.hierarchy must unload temporary Prefab contents");
});

test("scene.volumes is interface-owned and preserves legacy pageInfo output", async () => {
  const source = await readContextCommandsSource();
  const allSources = await readUnityBridgeSources();
  assert.doesNotMatch(source, /ReadSceneVolumes/, "scene.volumes read handler moved to IUnityCliContext");
  assert.match(allSources, /class SceneVolumesContext : IUnityCliContext<SceneVolumesContext\.Request, SceneVolumesContext\.Result>, IUnityCliBuiltInRawBridgeContext/, "scene.volumes must keep raw read support");
  assert.match(allSources, /Name\s*=\s*"scene\.volumes"/, "scene.volumes descriptor must keep public context name");
  assert.match(allSources, /GraphicsVolumesJson\(target\)/, "scene.volumes must reuse graphics Volume inventory helper");
  assert.match(allSources, /SceneVolumesPayloadJson[\s\S]*PropRaw\("volumes"[\s\S]*PropObject\("pageInfo"/, "scene.volumes must preserve volumes/count/pageInfo payload");
});

test("scene.renderer-features has source=built-in", async () => {
  const allSources = await readUnityBridgeSources();
  assert.match(allSources, /Name\s*=\s*"scene\.renderer-features"/, "Must find scene.renderer-features descriptor");
  assert.match(allSources, /Source\s*=\s*"built-in"/, "scene.renderer-features must have source=built-in");
  assert.match(allSources, /com\.unity\.render-pipelines\.universal/, "scene.renderer-features must list URP in requiresPackages");
});

test("C# descriptor includes inputSchema and outputSchema (B2)", async () => {
  const source = await readContextCommandsSource();
  const allSources = await readUnityBridgeSources();
  // Legacy BuiltInContextDefinition still supports schemas while stage cleanup removes the old table.
  assert.match(source, /public string InputSchema;/, "BuiltInContextDefinition must have InputSchema field");
  assert.match(source, /public string OutputSchema;/, "BuiltInContextDefinition must have OutputSchema field");
  assert.match(source, /\.PropRaw\("inputSchema",/, "ContextDescriptorJson must output inputSchema");
  assert.match(source, /\.PropRaw\("outputSchema",/, "ContextDescriptorJson must output outputSchema");
  // Interface-owned contexts derive schemas from DTOs instead of handwritten table rows.
  assert.match(allSources, /\[UnityCliRequired\][\s\S]*public string Path \{ get; set; \}/, "At least one interface-owned context must derive a required path schema");
  assert.match(allSources, /public sealed class PageInfo/, "At least one interface-owned context must expose structured output schema fields");
});

test("editor.tool-states keeps purpose in description without MCP descriptor fields", async () => {
  const allSources = await readUnityBridgeSources();
  const tsIndex = allSources.indexOf("editor.tool-states");
  assert.ok(tsIndex > -1, "Must find editor.tool-states in source");
  const blockStart = allSources.lastIndexOf("public ContextDescriptor Describe()", tsIndex);
  const blockEnd = allSources.indexOf("public Result Read", tsIndex);
  const block = allSources.slice(blockStart, blockEnd > blockStart ? blockEnd : tsIndex + 500);
  assert.match(block, /Description = .*Unity Editor transform tool settings/, "editor.tool-states must explain its Editor tool settings purpose");
  assert.match(allSources, /GridAvailable/, "editor.tool-states output DTO must match runtime field gridAvailable");
  assert.doesNotMatch(allSources, /resourceUri/i, "Context descriptors must not expose resourceUri fields");
});

test("editor context C# descriptors document read-only boundaries", async () => {
  const allSources = await readUnityBridgeSources();
  assert.match(allSources, /class EditorStateContext : IUnityCliContext<EditorStateContext\.Request, EditorStateContext\.Result>/, "editor.state must be interface-owned");
  assert.match(allSources, /Read current Unity Editor play, busy, scene, prefab stage, bridge, and runtime session state\./, "editor.state must keep a routing summary");
  assert.match(allSources, /class EditorSelectionContext : IUnityCliContext<EditorSelectionContext\.Request, EditorSelectionContext\.Result>/, "editor.selection must be interface-owned");
  assert.match(allSources, /Read current Editor selection names and reusable object references without changing selection\./, "editor.selection must keep a routing summary");
  assert.match(allSources, /public sealed class BridgeRequest/, "raw bridge contexts expose BridgeRequest through a public nested type so Unity compilation can satisfy interface signatures");
  assert.match(allSources, /ReadRaw\(UnityCliBridge\.BridgeRequest request, UnityCliProjectToolContext ctx\)/, "raw bridge context interface must qualify the nested BridgeRequest type outside UnityCliBridge partial classes");
  assert.match(allSources, /IUnityCliBuiltInRawBridgeContext/, "editor.state and editor.selection must keep raw bridge read support for legacy control/output semantics");
  assert.match(allSources, /no stable public API for full menu enumeration/i, "editor.menu-items must disclose enumeration limitation");
  assert.match(allSources, /destructive Tool editor\.menu/, "editor.menu-items must distinguish execution tool");
});

test("editor.selection C# context returns reusable detail fields without mutation", async () => {
  const allSources = await readUnityBridgeSources();
  assert.doesNotMatch(await readContextCommandsSource(), /ReadEditorSelection/, "editor.selection read handler moved to IUnityCliContext");
  assert.match(allSources, /PropRaw\("activeObjectDetail", SelectionObjectJson\(activeObject\)\)/, "selection context must return activeObjectDetail");
  assert.match(allSources, /PropArray\("objectDetails"/, "selection context must return objectDetails");
  assert.match(allSources, /PropArray\("gameObjectDetails"/, "selection context must return gameObjectDetails");
  assert.match(allSources, /SelectionObjectJson[\s\S]*Prop\("instanceId"[\s\S]*Prop\("assetPath"[\s\S]*Prop\("assetGUID"[\s\S]*Prop\("hierarchyPath"/, "selection detail must expose reusable object references");
  assert.match(allSources, /SelectionObjectJson[\s\S]*PropRaw\("components", ComponentsJson\(gameObject\)\)/, "selection detail must expose component summaries for GameObjects");
  const readSelectionStart = allSources.indexOf("private static string EditorSelectionPayloadJson");
  const readSelectionEnd = allSources.indexOf("private static string SelectionObjectJson", readSelectionStart);
  const block = allSources.slice(readSelectionStart, readSelectionEnd);
  assert.doesNotMatch(block, /Selection\.activeObject\s*=/, "selection context must not change activeObject");
  assert.doesNotMatch(block, /AssetDatabase\.Refresh/, "selection context must not refresh assets");
});

test("scene.gameobject contexts are interface-owned and preserve raw legacy output", async () => {
  const source = await readContextCommandsSource();
  const allSources = await readUnityBridgeSources();
  assert.doesNotMatch(source, /ReadSceneGameObject/, "scene.gameobject read handlers moved to IUnityCliContext");
  assert.match(allSources, /class SceneGameObjectContext : IUnityCliContext<SceneGameObjectContext\.Request, SceneGameObjectContext\.Result>, IUnityCliBuiltInRawBridgeContext/, "scene.gameobject must keep raw read support");
  assert.match(allSources, /class SceneGameObjectComponentsContext : IUnityCliContext<SceneGameObjectComponentsContext\.Request, SceneGameObjectComponentsContext\.Result>, IUnityCliBuiltInRawBridgeContext/, "scene.gameobject.components must keep raw read support");
  assert.match(allSources, /class SceneGameObjectComponentContext : IUnityCliContext<SceneGameObjectComponentContext\.Request, SceneGameObjectComponentContext\.Result>, IUnityCliBuiltInRawBridgeContext/, "scene.gameobject.component must keep raw read support");
  assert.match(allSources, /ResolveSceneContextGameObject/, "scene.gameobject contexts must share stable target resolution");
  assert.match(allSources, /SceneContextInputStringArg\(request\.RawJsonArg\("input"\), "name"\)/, "scene.gameobject contexts must read business name aliases from raw input, not the context name");
  assert.match(allSources, /SceneContextComponentDetailsJson/, "component list context must preserve readable property preview output");
  assert.match(allSources, /catch \(BridgeCommandException exception\)[\s\S]*BridgeExceptionErrorJson\(exception\)/, "scene.gameobject raw reads must preserve structured target/component resolution errors");
  const gameObjectContextSources = await readFile(new URL("Contexts/scene/gameobject.cs", editorUrl), "utf8")
    + await readFile(new URL("Contexts/scene/gameobject-support.cs", editorUrl), "utf8");
  assert.doesNotMatch(gameObjectContextSources, /unity_cli tools run scene\.hierarchy/, "scene.gameobject context errors must not return legacy command hints");
  assert.doesNotMatch(gameObjectContextSources, /ResolveUniqueTarget\(/, "scene.gameobject contexts must not reuse shared target resolver with legacy tool hints");
  assert.match(allSources, /SceneGameObjectPayloadJson[\s\S]*GameObjectDetailJson/, "scene.gameobject must preserve legacy GameObject detail output");
});

test("Context availability-sensitive handlers return available=false with diagnostics", async () => {
  const allSources = await readUnityBridgeSources();
  // renderer-features, rendering-stats, tests.list should have available=false fallback
  assert.match(allSources, /Prop\("available", false\)|Available\s*=\s*false/, "Availability-sensitive contexts must return available=false when unavailable");
  assert.match(allSources, /Prop\("diagnostics",|Diagnostics\s*=/, "Availability-sensitive contexts must include diagnostics");
  assert.match(allSources, /PropArray\("nextActions"|NextActions\s*=/, "Availability-sensitive contexts must include nextActions");
});

test("tests Context handler exposes stable count fields", async () => {
  const source = await readContextCommandsSource();
  const allSources = await readUnityBridgeSources();
  assert.doesNotMatch(source, /ReadToolsGroupsContext/, "tools.groups read handler moved to TS defineContext");
  assert.doesNotMatch(source, /ReadToolsCustomContext/, "tools.custom read handler moved to TS defineContext");
  assert.match(allSources, /Available\s*=\s*false[\s\S]*Count\s*=\s*0/, "tests.list unavailable branch must still include count");
});

test("Context commands are registered as non-mutating (allowed in Play Mode)", async () => {
  const allSources = await readUnityBridgeSources();
  assert.match(allSources, /RegisterCommand\(registry, "context\.list", "context", false/);
  assert.match(allSources, /RegisterCommand\(registry, "context\.describe", "context", false/);
  assert.match(allSources, /RegisterCommand\(registry, "context\.read", "context", false/);
});

async function readContextCommandsSource(): Promise<string> {
  return readFile(new URL("ProjectContexts/BuiltInContextSupport.cs", editorUrl), "utf8");
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
