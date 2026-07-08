import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { readFile, readdir } from "node:fs/promises";

/** 递归列举 .cs 文件。 */
async function listCsFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCsFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

/**
 * Scene / Asset / Prefab / Screenshot / Profiler / Console / Build / Physics
 * 跨大域契约测试：验证大域 4-8 新增工具的 metadata/bridge/contract 一致性。
 */

test("scene domain exposes paged hierarchy and scene data tools", () => {
  for (const name of ["scene.hierarchy", "scene.unload", "scene.set-active", "scene.list-opened", "scene.data", "scene.view-frame"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
  }
  const hierarchy = findToolByName("scene.hierarchy")!;
  const hierarchyProps = hierarchy.inputSchema.properties as Record<string, unknown>;
  assert.equal(hierarchy.playModeSupport, "supported");
  assert.ok(hierarchyProps.parent, "scene.hierarchy should expose parent subtree reads");
  assert.ok(hierarchyProps.maxDepth, "scene.hierarchy should expose maxDepth");
  assert.ok(hierarchyProps.query, "scene.hierarchy should expose query alias");
  assert.ok(hierarchyProps.nameFilter, "scene.hierarchy should expose nameFilter");
  assert.ok(hierarchyProps.componentType, "scene.hierarchy should expose componentType filtering");
  assert.ok(hierarchyProps.includeComponents, "scene.hierarchy should expose component preview control");
  assert.match(hierarchy.description, /paged hierarchy/, "scene.hierarchy should describe paged lightweight hierarchy reads");
  const unload = findToolByName("scene.unload")!;
  assert.equal(unload.destructiveHint, false);
  assert.equal(unload.supportsDryRun, true);
  const data = findToolByName("scene.data")!;
});

test("asset domain exposes paged search/get-data/get-components/refresh/preview with Play Mode metadata", () => {
  for (const name of ["asset.search", "asset.search-built-in", "asset.get-data", "asset.get-components", "asset.refresh", "asset.preview"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
    assert.ok(tool.playModeSupport, `${name} should expose playModeSupport`);
  }
  const searchTool = findToolByName("asset.search")!;
  const searchProps = searchTool.inputSchema.properties as Record<string, unknown>;
  assert.ok(searchProps.pageSize, "asset.search should expose pageSize");
  assert.ok(searchProps.maxResults, "asset.search should expose IvanMurzak-style maxResults alias");
  assert.ok(searchProps.cursor, "asset.search should expose cursor");
  assert.ok(searchProps.searchInFolders, "asset.search should expose multi-folder scope");
  assert.match(searchTool.description, /l:.*t:.*b:.*a:.*glob:/, "asset.search should document Unity AssetDatabase filter syntax");
  const getDataTool = findToolByName("asset.get-data")!;
  const getDataProps = getDataTool.inputSchema.properties as Record<string, unknown>;
  const getDataOutput = getDataTool.outputSchema?.properties as Record<string, unknown>;
  assert.ok(getDataProps.paths, "asset.get-data should expose paths for exact reads");
  assert.ok(getDataProps.viewQuery, "asset.get-data should expose viewQuery for scoped reads");
  assert.ok(getDataProps.maxArrayItems, "asset.get-data should expose maxArrayItems cap");
  assert.ok(getDataOutput.fields, "asset.get-data output should expose SerializedMember fields");
  assert.ok(getDataOutput.path, "asset.get-data output should expose SerializedMember path");
  assert.match(getDataTool.description, /mutually exclusive/, "asset.get-data should warn paths/viewQuery are mutually exclusive");
  const getComponents = findToolByName("asset.get-components")!;
  const getComponentsProps = getComponents.inputSchema.properties as Record<string, unknown>;
  assert.ok(getComponentsProps.query, "asset.get-components should expose query filtering");
  assert.ok(getComponentsProps.componentType, "asset.get-components should expose componentType filtering");
  assert.ok(getComponentsProps.pageSize, "asset.get-components should expose pagination");
  assert.match(getComponents.description, /component summaries/, "asset.get-components should describe lightweight component summaries");
  const refresh = findToolByName("asset.refresh")!;
  assert.equal(refresh.handler, "hybrid");
  assert.equal(refresh.supportStatus, "bridge-job");
  assert.equal(refresh.supportsDryRun, true);
  assert.deepEqual(refresh.sideEffects, ["asset-import", "asset-refresh", "script-compile", "domain-reload"]);
  assert.match(refresh.description, /batchmode job/);
  assert.equal(findToolByName("asset.preview")!.playModeSupport, "session-affecting");
});

test("asset.search C# handler supports native filters and scoped folders", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/asset/search.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/asset/AssetToolSupport.cs", import.meta.url), "utf8");
  assert.match(source, /AssetToolSupport\.NormalizeAssetSearchFolders\(request\.Path, request\.SearchInFolders\)/, "asset.search should normalize single-path and multi-folder scope inputs");
  assert.match(source, /AssetDatabase\.FindAssets\(resolvedFilter, searchInFolders\.Length == 0 \? null : searchInFolders\)/, "asset.search should pass native filter syntax to AssetDatabase.FindAssets");
  assert.match(supportSource, /E_ASSET_SEARCH_SCOPE_INVALID[\s\S]*path\/searchInFolders only for folder scopes/, "invalid folder scopes should be recoverable errors");
});

test("asset get-data/get-components/refresh/preview handlers return bridge success envelopes", async () => {
  const getDataSource = await readFile(new URL("../unitypkg/Editor/Tools/asset/get-data.cs", import.meta.url), "utf8");
  const getComponentsSource = await readFile(new URL("../unitypkg/Editor/Tools/asset/get-components.cs", import.meta.url), "utf8");
  const refreshSource = await readFile(new URL("../unitypkg/Editor/Tools/asset/refresh.cs", import.meta.url), "utf8");
  const previewSource = await readFile(new URL("../unitypkg/Editor/Tools/asset/preview.cs", import.meta.url), "utf8");
  assert.match(getDataSource, /class AssetGetDataTool : IUnityCliTool[\s\S]*Data = GameObjectToolSupport\.ParseJsonToObject\(memberJson\)/, "asset.get-data should return structured object-serializer data through IUnityCliTool");
  assert.match(getComponentsSource, /class AssetGetComponentsTool : IUnityCliTool[\s\S]*PageInfo = new AssetPageInfo/, "asset.get-components should return paged component summaries through IUnityCliTool");
  assert.match(refreshSource, /class AssetRefreshTool : IUnityCliTool[\s\S]*AssetDatabase\.(Refresh|ImportAsset)/, "asset.refresh should call live AssetDatabase.Refresh or ImportAsset depending on scope");
  assert.match(previewSource, /class AssetPreviewTool : IUnityCliTool[\s\S]*AssetPreview\.GetAssetPreview/, "asset.preview should return preview metadata through IUnityCliTool");
  assert.doesNotMatch(`${getDataSource}\n${getComponentsSource}\n${refreshSource}\n${previewSource}`, /unity_cli asset/, "asset error guidance must not use removed top-level asset commands");
});

test("camera commands no longer advertise legacy top-level camera CLI", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/Tools/camera/", import.meta.url));
  const source = (await Promise.all(csFiles.map((file) => readFile(file, "utf8")))).join("\n");
  assert.doesNotMatch(source, /unity_cli camera/, "camera error guidance must not use removed top-level camera commands");
  const screenshot = findToolByName("camera.screenshot")!;
  const props = screenshot.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.path, "camera.screenshot should document path artifact output");
  assert.ok(props.output, "camera.screenshot should keep output alias documented");
  assert.ok(props.inlineBase64, "camera.screenshot should document optional inlineBase64");
  assert.match(screenshot.description, /inlineBase64/, "camera.screenshot should document explicit inline output");
});

test("scene.hierarchy C# handler supports parent/depth/filter/component preview controls", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/scene/hierarchy.cs", import.meta.url), "utf8");
  assert.match(source, /class SceneHierarchyTool : IUnityCliTool</, "scene.hierarchy should be an IUnityCliTool");
  assert.match(source, /public string Parent[\s\S]*public string Query[\s\S]*public string ComponentType[\s\S]*IncludeComponents/, "scene.hierarchy should parse parent/query/componentType/includeComponents");
  assert.match(source, /CollectHierarchy\(Transform transform, List<HierarchyItem> items, int depth, int maxDepth, string query, bool includeInactive, bool includeComponents, string componentType\)/, "hierarchy collection should be bounded and filterable");
  assert.match(source, /BuildPageInfo\(pageSize, cursor, total, page\.Count\)/, "scene.hierarchy should return bounded page info in the new shape");
});

test("go domain exposes paged find and prevalidated mutations", async () => {
  const find = findToolByName("go.find")!;
  const props = find.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.searchMethod, "go.find should expose searchMethod");
  assert.ok(props.pageSize, "go.find should expose pageSize");
  assert.ok(props.cursor, "go.find should expose cursor");
  assert.equal(find.playModeSupport, "supported");
  for (const name of ["go.create", "go.modify", "go.duplicate", "go.move-relative", "go.look-at", "go.delete", "go.set-parent"]) {
    assert.equal(findToolByName(name)?.playModeSupport, "session-affecting", `${name} should expose scene session side effects`);
  }

  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/go/GameObjectToolSupport.cs", import.meta.url), "utf8");
  const findSource = await readFile(new URL("../unitypkg/Editor/Tools/go/find.cs", import.meta.url), "utf8");
  const createSource = await readFile(new URL("../unitypkg/Editor/Tools/go/create.cs", import.meta.url), "utf8");
  const modifySource = await readFile(new URL("../unitypkg/Editor/Tools/go/modify.cs", import.meta.url), "utf8");
  const lookAtSource = await readFile(new URL("../unitypkg/Editor/Tools/go/look-at.cs", import.meta.url), "utf8");
  assert.match(findSource, /SearchGameObjects\(searchMethod, searchTerm, includeInactive\)[\s\S]*PageInfo = new GoPageInfo/, "go.find should search with paging");
  assert.match(supportSource, /ResolveUniqueTarget[\s\S]*FindObjectsOfType<GameObject>\(true\)/, "go tools should resolve inactive targets after active=false changes");
  assert.match(supportSource, /ResolveComponentType\(string componentName\)/, "go.find should support component search through shared type resolution");
  assert.match(createSource, /ResolveUniqueTarget\(request\.Parent[\s\S]*ParsePrimitiveType\(request\.Primitive\)[\s\S]*ValidateMutationInputs\(request\.TagValue, request\.LayerValue\)[\s\S]*new GameObject/, "go.create should validate parent/primitive/tag/layer before mutating");
  assert.match(modifySource, /ValidateMutationInputs[\s\S]*ResolveModifyTargets\(request\)[\s\S]*Undo\.RecordObject/, "go.modify should validate tag/layer and resolve all targets before recording changes");
  assert.match(modifySource, /public List<string> Targets \{ get; set; \}[\s\S]*ResolveModifyTargets\(request\)[\s\S]*ResolveUniqueTarget/, "go.modify should support targets[] batch resolution");
  assert.match(supportSource, /ParsePrimitiveType[\s\S]*E_PRIMITIVE_TYPE_INVALID/, "invalid primitive types should be recoverable errors");
  assert.doesNotMatch(supportSource, /unity_cli scene hierarchy/, "go errors must not use removed top-level scene commands");
  assert.match(lookAtSource, /GameObject look-at requires a lookAt target[\s\S]*go\.look-at --input/, "go.look-at missing target guidance should use the current input shape");
});

test("prefab domain exposes instantiate/unpack", () => {
  const instantiate = findToolByName("prefab.instantiate")!;
  assert.ok(instantiate);
  assert.equal(instantiate.destructiveHint, false);
  const unpack = findToolByName("prefab.unpack")!;
  assert.ok(unpack);
  assert.equal(unpack.destructiveHint, false);
  assert.equal(unpack.supportsDryRun, true);
});

// screenshot 与 profiler 域详细契约测试已拆分至 screenshot-contract.test.ts 与 profiler-contract.test.ts

test("console domain exposes get/clear; editor exposes selection.set/active-tool.set", () => {
  const get = findToolByName("console.get")!;
  const props = get.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.cursor, "console.get should expose cursor pagination");
  assert.ok(get.outputSchema, "console.get should describe paged log output");
  assert.equal(get.playModeSupport, "supported");
  const consoleClear = findToolByName("console.clear")!;
  assert.equal(consoleClear.destructiveHint, false);
  assert.equal(consoleClear.supportsDryRun, true);
  assert.equal(consoleClear.playModeSupport, "session-affecting");
  assert.deepEqual(consoleClear.sideEffects, ["console-clear"]);
  assert.ok(consoleClear.outputSchema);
  assert.equal(findToolByName("editor.selection.set")?.destructiveHint, false);
  assert.equal(findToolByName("editor.active-tool.set")?.destructiveHint, false);
});

test("editor console/menu handlers use current safe CLI guidance", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/editor/action-tools.cs", import.meta.url), "utf8");
  assert.doesNotMatch(source, /unity_cli editor menu/, "editor.menu error guidance must not use removed top-level editor commands");
  assert.match(source, /class EditorConsoleTool : IUnityCliTool[\s\S]*Use console\.clear to clear\./, "editor.console should route destructive console clearing to console.clear");
  assert.match(source, /class EditorMenuTool : IUnityCliTool[\s\S]*ExecuteMenuItem\(r\.Path\)/, "editor.menu should execute exact Unity menu paths in the new tool shape");
});

test("console.get C# handler returns bounded bridge envelope", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/editor/action-tools.cs", import.meta.url), "utf8");
  assert.match(source, /class ConsoleGetTool : IUnityCliTool</, "console.get should be implemented as an IUnityCliTool");
  assert.match(source, /Skip\(cursor\)\.Take\(count\)/, "console.get should page results with cursor and count");
  assert.match(source, /PageInfo = new PageInfo \{ PageSize = count, Cursor = cursor, Total = items\.Count, NextCursor = cursor \+ paged\.Count < items\.Count \? \(cursor \+ paged\.Count\)\.ToString\(\) : null \}/, "console.get should return bounded page info");
});

test("build read handlers return bridge success envelopes", async () => {
  const scenesSource = await readFile(new URL("../unitypkg/Editor/Tools/build/scenes.cs", import.meta.url), "utf8");
  const settingsSource = await readFile(new URL("../unitypkg/Editor/Tools/build/settings.cs", import.meta.url), "utf8");
  assert.match(scenesSource, /class BuildScenesTool : IUnityCliTool[\s\S]*if \(action == "get"\)[\s\S]*return new Result \{ Command = "build\.scenes", Action = "get", Count = scenes\.Length, Scenes = entries \}/, "build.scenes get should return a structured result through IUnityCliTool");
  assert.match(settingsSource, /class BuildSettingsTool : IUnityCliTool[\s\S]*if \(action == "get"\)[\s\S]*return BuildResult\(\)/, "build.settings should return a structured get result through IUnityCliTool");
});

test("animation.clip exposes full clip properties and create settings", async () => {
  const clip = findToolByName("animation.clip")!;
  const create = findToolByName("animation.clip.create")!;
  const modify = findToolByName("animation.clip.modify")!;
  const clipProps = clip.inputSchema.properties as Record<string, unknown>;
  const createProps = create.inputSchema.properties as Record<string, unknown>;
  const modifyProps = modify.inputSchema.properties as Record<string, unknown>;
  assert.ok(clipProps.maxCurveKeys, "animation.clip should bound curve key summaries");
  assert.equal(clip.destructiveHint, false);
  assert.equal(create.destructiveHint, true);
  assert.equal(create.supportsDryRun, true);
  assert.equal(modify.destructiveHint, true);
  assert.equal(modify.supportsDryRun, true);
  assert.ok(createProps.wrapMode, "animation.clip.create should expose wrapMode");
  assert.ok(createProps.loopTime, "animation.clip.create should expose loopTime");
  assert.ok(createProps.length, "animation.clip.create should expose length");
  assert.ok(modifyProps.wrapMode, "animation.clip.modify should expose wrapMode");
  assert.ok(modifyProps.loopTime, "animation.clip.modify should expose loopTime");
  const clipSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/clip-tools.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/AnimationToolSupport.cs", import.meta.url), "utf8");
  const source = `${clipSource}\n${supportSource}`;
  assert.match(source, /class AnimationClipTool : IUnityCliTool</, "animation.clip should be implemented as an IUnityCliTool");
  assert.match(source, /AnimationUtility\.GetCurveBindings\(clip\)/, "clip info should expose curve bindings");
  assert.match(source, /AnimationUtility\.GetAnimationEvents\(clip\)/, "clip info should expose AnimationEvents");
  assert.match(source, /AnimationUtility\.GetAnimationClipSettings\(clip\)/, "clip info should expose AnimationClipSettings");
  assert.match(source, /AnimationUtility\.SetAnimationClipSettings\(clip, settings\)/, "clip create and modify should apply AnimationClipSettings");
  assert.match(source, /if \(r\.LoopTime\.HasValue\) settings\.loopTime = r\.LoopTime\.Value/, "clip create/modify should persist loopTime");
  assert.match(source, /if \(!string\.IsNullOrEmpty\(r\.WrapMode\) && System\.Enum\.TryParse<WrapMode>\(r\.WrapMode, true, out var wm\)\) clip\.wrapMode = wm/, "clip create/modify should persist wrapMode");
});

test("animator parameter handlers fall back to AnimatorController definitions", async () => {
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/AnimationToolSupport.cs", import.meta.url), "utf8");
  const runtimeSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/animator-runtime.cs", import.meta.url), "utf8");
  const controllerSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/controller-tools.cs", import.meta.url), "utf8");
  const allSource = `${supportSource}\n${runtimeSource}\n${controllerSource}`;
  assert.match(allSource, /public static AnimatorControllerParameter\[\] AnimatorParameterDefinitions\(Animator animator\)/, "animator tools should share parameter definition fallback");
  assert.match(allSource, /return controller == null \|\| controller\.parameters == null \? new AnimatorControllerParameter\[0\] : controller\.parameters;/, "animator tools should read Controller asset parameters when runtime cache is empty");
  assert.match(allSource, /var hasRuntime = animator\.parameterCount > 0;/, "get-parameter should distinguish runtime values from Controller defaults");
  assert.match(allSource, /case AnimatorControllerParameterType\.Float: value = hasRuntime \? animator\.GetFloat\(r\.ParameterName\) : \(object\)param\.defaultFloat;/, "get-parameter should return Controller defaultFloat fallback");
  assert.match(allSource, /var effectiveLayerCount = animator\.layerCount > 0 \? animator\.layerCount : controllerLayers\.Length;/, "crossfade should use Controller layers when runtime layer cache is empty");
  assert.match(allSource, /if \(!string\.IsNullOrEmpty\(r\.ClipPath\)\) \{ clip = AssetDatabase\.LoadAssetAtPath<AnimationClip>\(SceneToolSupport\.NormalizeAssetPath\(r\.ClipPath\)\); if \(clip == null\) throw new UnityCliBridge\.BridgeCommandException\("E_CLIP_NOT_FOUND"/, "add-state should validate clipPath before mutating the controller");
  assert.match(allSource, /ControllerCreateBlendTree1DTool[\s\S]*FindStateInLayer\(sm, r\.StateName\)[\s\S]*E_STATE_EXISTS[\s\S]*sm\.AddState\(r\.StateName\)/, "1D BlendTree creation should reject duplicate states before mutating");
  assert.match(allSource, /ControllerCreateBlendTree2DTool[\s\S]*FindStateInLayer\(sm, r\.StateName\)[\s\S]*E_STATE_EXISTS[\s\S]*sm\.AddState\(r\.StateName\)/, "2D BlendTree creation should reject duplicate states before mutating");
  assert.match(allSource, /ControllerGetInfoTool[\s\S]*Parameters = parameters/, "get-info should expose parameter definitions in the new shape");
});

test("controller commands do not advertise removed top-level syntax", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/animation/controller-tools.cs", import.meta.url), "utf8");
  assert.doesNotMatch(source, /controller get-info <path>/);
  assert.doesNotMatch(source, /animation\.clip action=create/);
  assert.doesNotMatch(source, /controller (assign|add-layer|remove-layer|set-layer-weight) requires --/);
  assert.doesNotMatch(source, /controller (create-blend-tree-[12]d|add-blend-tree-child) requires --/);
  assert.doesNotMatch(source, /controller\.(add-state|add-transition|add-parameter|create-blend-tree-[12]d|add-blend-tree-child) requires --/);
});

test("package domain exposes embed", () => {
  const embed = findToolByName("package.embed")!;
  assert.ok(embed);
  assert.equal(embed.destructiveHint, true);
  assert.equal(embed.supportsDryRun, true);
  assert.equal(embed.riskLevel, "high");
});

test("build domain exposes scenes/platform/profiles/settings alongside run/status/cancel", () => {
  const buildTools = filterToolsByGroup("build").map((t) => t.name);
  assert.ok(buildTools.includes("build.scenes"));
  assert.ok(buildTools.includes("build.platform"));
  assert.ok(buildTools.includes("build.profiles"));
  assert.ok(buildTools.includes("build.settings"));
  assert.ok(buildTools.includes("build.run"));
  assert.ok(buildTools.includes("build.status"));
  assert.ok(buildTools.includes("build.cancel"));
});

test("graphics tools expose complete metadata and current CLI guidance", async () => {
  const graphicsTools = filterToolsByGroup("graphics");
  assert.equal(graphicsTools.length, 28, "graphics group should keep 28 tools in the current contract");
  for (const tool of graphicsTools) {
    assert.equal(tool.supportStatus, "live-bridge", `${tool.name} should disclose live bridge support`);
    assert.ok(tool.outputSchema, `${tool.name} should expose outputSchema`);
    assert.ok(tool.examples?.some((example) => example.argv.includes("--input")), `${tool.name} should include tools run --input examples`);
    assert.ok(tool.playModeSupport, `${tool.name} should declare Play Mode semantics`);
    assert.ok(tool.sideEffects, `${tool.name} should explicitly declare side effects, even when empty`);
  }
  for (const name of ["graphics.quality", "graphics.settings", "graphics.pipeline-quality", "graphics.pipeline-settings"]) {
    assert.equal(findToolByName(name)?.playModeSupport, "session-affecting", `${name} should disclose graphics settings writes`);
  }
  for (const name of ["graphics.pipeline", "graphics.pipeline-info", "graphics.stats", "graphics.skybox-get", "graphics.bake-status"]) {
    assert.equal(findToolByName(name)?.playModeSupport, "supported", `${name} should be read-only`);
  }
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/Tools/graphics/", import.meta.url));
  const source = (await Promise.all(csFiles.map((file) => readFile(file, "utf8")))).join("\n");
  assert.match(source, /ResolveQualityLevel[\s\S]*QualitySettings\.names[\s\S]*E_QUALITY_LEVEL_INVALID/, "graphics quality should resolve index or quality level name");
  assert.match(source, /GraphicsSettings\.defaultRenderPipeline = null/, "pipeline settings should support built-in/null pipeline assignment");
  assert.match(source, /BuildGraphicsSummary\(\)[\s\S]*BuildQualitySettings\(\)/, "pipeline info should return graphics and quality summaries through shared helpers");
  assert.match(source, /new LightingSettings\(\)/, "bake settings writes should create LightingSettings when the scene lacks one");
  assert.match(source, /builtin:Default-Skybox[\s\S]*AssetDatabase\.GetBuiltinExtraResource<Material>\("Default-Skybox\.mat"\)/, "skybox material restore should support Unity built-in default skybox");
  assert.doesNotMatch(source, /unity_cli graphics /, "graphics error guidance must use tools run graphics.* shape");
});

test("physics domain exposes raycast-all/linecast/shapecast/validate", () => {
  for (const name of ["physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate"]) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name} should exist`);
  }
});

test("bridge specs resolve for all new domain 4-8 tools", () => {
  const checks = [
    ["scene", "unload", "project-tool.run"],
    ["scene", "data", "project-tool.run"],
    ["go", "set-parent", "project-tool.run"],
    ["prefab", "instantiate", "project-tool.run"],
    ["prefab", "unpack", "project-tool.run"],
    ["asset", "get-data", "project-tool.run"],
    ["asset", "get-components", "project-tool.run"],
    ["asset", "refresh", "project-tool.run"],
    ["shader", "list", "project-tool.run"],
    ["texture", "generate", "project-tool.run"],
    ["console", "get", "project-tool.run", "console.get"],
    ["package", "embed", "package.embed"],
    ["editor", "selection-set", "project-tool.run", "editor.selection.set"],
    ["build", "scenes", "project-tool.run"],
    ["build", "profiles", "project-tool.run"],
    ["physics", "validate", "project-tool.run", "physics.validate"],
    ["physics", "assign-material", "project-tool.run", "physics.assign-material"]
  ] as const;
  for (const [group, command, bridgeCommand, toolName] of checks) {
    const spec = bridgeCommandRegistry.lookup(group, command);
    assert.ok(spec, `${group} ${command} should resolve in bridge registry`);
    assert.equal(spec.bridgeCommand, bridgeCommand);
    if (toolName) {
      assert.equal(spec.toolName, toolName, `${group} ${command} toolName should match`);
    }
  }
});

test("all new tools have contract entries with present C# handler", () => {
  // screenshot/profiler/object/component/reflection 专属契约测试已拆分至分域文件
  const newTools = [
    "scene.unload", "scene.set-active", "scene.list-opened", "scene.data", "scene.view-frame",
    "go.set-parent", "prefab.instantiate", "prefab.unpack",
    "asset.search-built-in", "asset.get-data", "asset.get-components", "asset.preview", "asset.refresh",
    "shader.list", "shader.get-data", "texture.generate",
    "console.get", "console.clear", "editor.selection.set", "editor.active-tool.set", "package.embed",
    "build.scenes", "build.platform", "build.profiles", "build.settings",
    "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate", "physics.assign-material"
  ];
  for (const name of newTools) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});
