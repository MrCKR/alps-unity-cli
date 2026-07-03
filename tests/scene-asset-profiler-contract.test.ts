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
  const source = await readFile(new URL("../unitypkg/Editor/Commands/AssetCommands.cs", import.meta.url), "utf8");
  assert.match(source, /var filter = request\.StringArg\("filter"\)[\s\S]*AssetDatabase\.FindAssets\(resolvedFilter/, "asset.search should pass native filter syntax to AssetDatabase.FindAssets");
  assert.match(source, /NormalizeAssetSearchFolders[\s\S]*JsonStringArray\(searchInFoldersJson\)/, "asset.search should accept multiple scoped folders");
  assert.match(source, /E_ASSET_SEARCH_SCOPE_INVALID[\s\S]*path\/searchInFolders only for folder scopes/, "invalid folder scopes should be recoverable errors");
});

test("asset get-data/get-components/refresh/preview handlers return bridge success envelopes", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/AssetCommands.cs", import.meta.url), "utf8");
  assert.match(source, /private static string AssetGetData[\s\S]*return SuccessJson\(w\.EndObject\(\)\.ToString\(\)\);/, "asset.get-data must wrap data in SuccessJson");
  assert.match(source, /private static string AssetGetComponents[\s\S]*return SuccessJson\(new UnityCliJsonWriter\(\)\.BeginObject\(\)/, "asset.get-components must wrap data in SuccessJson");
  assert.match(source, /private static string AssetRefresh[\s\S]*AssetDatabase\.Refresh\(options\)/, "asset.refresh should call live AssetDatabase.Refresh");
  assert.match(source, /private static string AssetPreview[\s\S]*return SuccessJson\(w\.EndObject\(\)\.ToString\(\)\);/, "asset.preview must wrap data in SuccessJson");
  assert.doesNotMatch(source, /unity_cli asset/, "asset error guidance must not use removed top-level asset commands");
});

test("camera commands no longer advertise legacy top-level camera CLI", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/CameraCommands.cs", import.meta.url), "utf8");
  assert.doesNotMatch(source, /unity_cli camera/, "camera error guidance must not use removed top-level camera commands");
  const screenshot = findToolByName("camera.screenshot")!;
  const props = screenshot.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.path, "camera.screenshot should document path artifact output");
  assert.ok(props.output, "camera.screenshot should keep output alias documented");
  assert.ok(props.inlineBase64, "camera.screenshot should document optional inlineBase64");
  assert.match(screenshot.description, /inlineBase64/, "camera.screenshot should document explicit inline output");
});

test("scene.hierarchy C# handler supports parent/depth/filter/component preview controls", async () => {
  // scene.hierarchy 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/scene/hierarchy.cs)。
  const source = await readFile(new URL("../unitypkg/Editor/Tools/scene/hierarchy.cs", import.meta.url), "utf8");
  assert.match(source, /class SceneHierarchyTool : IUnityCliTool</, "scene.hierarchy should be an IUnityCliTool");
  assert.match(source, /public string Parent[\s\S]*public string Query[\s\S]*public string ComponentType[\s\S]*IncludeComponents/, "scene.hierarchy should parse parent/query/componentType/includeComponents");
  assert.match(source, /CollectHierarchy\(Transform transform, List<HierarchyItem> items, int depth, int maxDepth, string query, bool includeInactive, bool includeComponents, string componentType\)/, "hierarchy collection should be bounded and filterable");
  const goSource = await readFile(new URL("../unitypkg/Editor/Commands/GameObjectCommands.cs", import.meta.url), "utf8");
  assert.match(goSource, /CollectHierarchy\(Transform transform, List<string> items, int depth, int maxDepth, string nameFilter, bool includeInactive, bool includeComponents\)/, "go/prefab hierarchy collection should stay bounded and filterable");
  assert.match(goSource, /HierarchyItemJson\(transform, depth, includeComponents\)/, "go/prefab hierarchy items should optionally include components");
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

  const source = await readFile(new URL("../unitypkg/Editor/Commands/GameObjectCommands.cs", import.meta.url), "utf8");
  const findSource = await readFile(new URL("../unitypkg/Editor/Tools/go/find.cs", import.meta.url), "utf8");
  const createSource = await readFile(new URL("../unitypkg/Editor/Tools/go/create.cs", import.meta.url), "utf8");
  const modifySource = await readFile(new URL("../unitypkg/Editor/Tools/go/modify.cs", import.meta.url), "utf8");
  const lookAtSource = await readFile(new URL("../unitypkg/Editor/Tools/go/look-at.cs", import.meta.url), "utf8");
  assert.match(findSource, /SearchGameObjects\(searchMethod, searchTerm, includeInactive\)[\s\S]*PageInfo/, "go.find should search with paging");
  assert.match(source, /private static List<GameObject> SearchGameObjects[\s\S]*by_component[\s\S]*ResolveComponentType/, "go.find should support component search");
  assert.match(source, /ResolveUniqueTarget[\s\S]*FindObjectsOfType<GameObject>\(true\)/, "go tools should resolve inactive targets after active=false changes");
  assert.match(createSource, /ResolveUniqueTarget\(request\.Parent[\s\S]*ParsePrimitiveType\(request\.Primitive\)[\s\S]*ValidateMutationInputs\(request\.TagValue, request\.LayerValue\)[\s\S]*new GameObject/, "go.create should validate parent/primitive/tag/layer before mutating");
  assert.match(modifySource, /ValidateMutationInputs[\s\S]*ResolveModifyTargets\(request\)[\s\S]*Undo\.RecordObject/, "go.modify should validate tag/layer and resolve all targets before recording changes");
  assert.match(source, /ResolveModifyTargets[\s\S]*RawJsonArg\("targets"\)[\s\S]*JsonStringArray[\s\S]*ResolveUniqueTarget/, "go.modify should support targets[] batch resolution");
  assert.match(source, /GameObjectBatchMutationJson[\s\S]*modifiedCount[\s\S]*gameObjects/, "go.modify batch output should expose modifiedCount and gameObjects");
  assert.match(source, /ParsePrimitiveType[\s\S]*E_PRIMITIVE_TYPE_INVALID/, "invalid primitive types should be recoverable errors");
  assert.doesNotMatch(source, /unity_cli scene hierarchy/, "go errors must not use removed top-level scene commands");
  assert.match(lookAtSource, /GameObject look-at requires a lookAt target[\s\S]*tools run/, "go.look-at missing target guidance should use current tools run input shape");
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
  const source = await readFile(new URL("../unitypkg/Editor/Commands/EditorCommands.cs", import.meta.url), "utf8");
  assert.doesNotMatch(source, /unity_cli editor menu/, "editor.menu error guidance must not use removed top-level editor commands");
  assert.match(source, /E_EDITOR_CONSOLE_CLEAR_UNSUPPORTED[\s\S]*console\.clear --dry-run[\s\S]*console\.clear --yes/, "editor.console must reject legacy clear and point to console.clear safety flow");
});

test("console.get C# handler returns bounded bridge envelope", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  assert.match(allSource, /private static string ConsoleGet[\s\S]*return SuccessJson\(w\.EndObject\(\)\.ToString\(\)\);/, "console.get should return a bridge success envelope");
  assert.match(allSource, /private static int CountHierarchy\(Transform transform\)/, "prefab/context hierarchy callers should keep CountHierarchy compatibility");
  assert.match(allSource, /private static void CollectHierarchy\(Transform transform, List<string> items, int cursor, int pageSize, Counter counter\)/, "prefab/context hierarchy callers should keep paged CollectHierarchy compatibility");
});

test("build read handlers return bridge success envelopes", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/BuildCommands.cs", import.meta.url), "utf8");
  assert.match(source, /private static string BuildScenes[\s\S]*if \(action == "get"\)[\s\S]*return SuccessJson\(w\.EndObject\(\)\.ToString\(\)\);/, "build.scenes get should return a bridge success envelope");
  assert.match(source, /private static string BuildSettings[\s\S]*return SuccessJson\(w\.EndObject\(\)\.ToString\(\)\);/, "build.settings should return a bridge success envelope");
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
  const source = await readFile(new URL("../unitypkg/Editor/Commands/AnimationCommands.cs", import.meta.url), "utf8");
  assert.doesNotMatch(source, /RegisterCommand\(registry, "animation\.clip", "animation", false, AnimationClipCommand\)/, "animation.clip should be read-only at bridge registration");
  assert.doesNotMatch(source, /RegisterCommand\(registry, "animation\.clip\.create", "animation", true, AnimationClipCreate\)/, "animation.clip.create should be mutating at bridge registration");
  assert.doesNotMatch(source, /RegisterCommand\(registry, "animation\.clip\.modify", "animation", true, AnimationClipModify\)/, "animation.clip.modify should be mutating at bridge registration");
  assert.match(source, /AnimationUtility\.GetAnimationEvents\(clip\)/, "clip info should expose AnimationEvents");
  assert.match(source, /AnimationUtility\.GetObjectReferenceCurveBindings\(clip\)/, "clip info should expose object-reference curves");
  assert.match(source, /AnimationUtility\.GetAnimationClipSettings\(clip\)/, "clip info should expose AnimationClipSettings");
  assert.match(source, /AnimationUtility\.SetAnimationClipSettings\(clip, settings\)/, "clip create and modify should apply AnimationClipSettings");
  assert.match(source, /AnimationClipCreate[\s\S]*ApplyAnimationClipSettings\(clip, request, true\)/, "clip create should apply documented defaults");
  assert.match(source, /AnimationClipModify[\s\S]*ApplyAnimationClipSettings\(clip, request, false\)/, "clip modify should only write explicit fields");
  assert.match(source, /AnimationClipModify[\s\S]*SetSerializedAnimationClipWrapMode\(clip, requestedWrapMode\.Value\)/, "clip modify should persist legacy wrapMode");
});

test("animator parameter handlers fall back to AnimatorController definitions", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  assert.match(allSource, /private static AnimatorControllerParameter\[\] AnimatorParameterDefinitions/, "animator tools should share parameter definition fallback");
  assert.match(allSource, /return controller == null \|\| controller\.parameters == null \? new AnimatorControllerParameter\[0\] : controller\.parameters;/, "animator tools should read Controller asset parameters when runtime cache is empty");
  assert.match(allSource, /var hasRuntimeValue = AnimatorHasRuntimeParameter\(animator, parameterName\);/, "get-parameter should distinguish runtime values from Controller defaults");
  assert.match(allSource, /value = hasRuntimeValue \? animator\.GetFloat\(parameterName\) : param\.defaultFloat;/, "get-parameter should return Controller defaultFloat fallback");
  assert.match(allSource, /var effectiveLayerCount = runtimeLayerCount > 0 \? runtimeLayerCount : controllerLayers\.Length;/, "crossfade should use Controller layers when runtime layer cache is empty");
  assert.match(allSource, /先校验可选 clip[\s\S]*return ErrorJson\("E_CLIP_NOT_FOUND"[\s\S]*var state = rootStateMachine\.AddState\(stateName\);/, "add-state should validate clipPath before mutating the controller");
  assert.match(allSource, /ControllerCreateBlendTree1D[\s\S]*FindStateInLayer\(stateMachine, stateName\)[\s\S]*E_STATE_EXISTS[\s\S]*stateMachine\.AddState\(stateName\)/, "1D BlendTree creation should reject duplicate states before mutating");
  assert.match(allSource, /ControllerCreateBlendTree2D[\s\S]*FindStateInLayer\(stateMachine, stateName\)[\s\S]*E_STATE_EXISTS[\s\S]*stateMachine\.AddState\(stateName\)/, "2D BlendTree creation should reject duplicate states before mutating");
  assert.match(allSource, /WriteControllerTransitions[\s\S]*destinationStateName[\s\S]*conditionCount[\s\S]*conditions/, "get-info should expose transition destinations and condition details");
  assert.match(allSource, /BlendTreeJson[\s\S]*blendParameter[\s\S]*childCount[\s\S]*threshold[\s\S]*position/, "get-info should expose BlendTree parameters and child motion details");
});

test("controller commands do not advertise removed top-level syntax", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/AnimationCommands.cs", import.meta.url), "utf8");
  const controllerSection = source.slice(source.indexOf("// ── controller.* AnimatorController editing tools ────────"));
  assert.doesNotMatch(controllerSection, /controller get-info <path>/);
  assert.doesNotMatch(controllerSection, /animation\.clip action=create/);
  assert.doesNotMatch(controllerSection, /controller (assign|add-layer|remove-layer|set-layer-weight) requires --/);
  assert.doesNotMatch(controllerSection, /controller (create-blend-tree-[12]d|add-blend-tree-child) requires --/);
  assert.doesNotMatch(controllerSection, /controller\.(add-state|add-transition|add-parameter|create-blend-tree-[12]d|add-blend-tree-child) requires --/);
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
  const source = await readFile(new URL("../unitypkg/Editor/Commands/GraphicsCommands.cs", import.meta.url), "utf8");
  assert.match(source, /private static int ResolveQualityLevel[\s\S]*QualitySettings\.names[\s\S]*E_QUALITY_LEVEL_INVALID/, "graphics quality should resolve index or quality level name");
  assert.match(source, /GraphicsPipelineSettings[\s\S]*renderPipeline[\s\S]*GraphicsSettings\.defaultRenderPipeline = null/, "pipeline settings should support built-in/null pipeline assignment");
  assert.match(source, /GraphicsPipelineInfo[\s\S]*GraphicsSummaryJson\(\)[\s\S]*QualitySettingsJson\(\)/, "pipeline info should return graphics and quality summaries");
  assert.match(source, /GraphicsBakeSettings[\s\S]*HasBakeSettingsWriteArgs[\s\S]*new LightingSettings\(\)/, "bake settings writes should create LightingSettings when the scene lacks one");
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
