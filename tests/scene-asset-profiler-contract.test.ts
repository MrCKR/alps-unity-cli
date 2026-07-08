import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { contractToolNames, readCsSources } from "./helpers/csharp-test-helpers.js";

/**
 * 跨域契约测试收敛为两类：
 * 1. 新增命令仍在 contract / bridge spec 中存在。
 * 2. 跨域共享的 C# 关键实现与错误提示没有回退到旧 CLI 形态。
 */

test("cross-domain new tools keep contract entries with present C# handlers", () => {
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
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("asset/search scene/hierarchy go/find and editor/console sources keep current implementation", async () => {
  const assetSource = await readCsSources(new URL("../unitypkg/Editor/Tools/asset/", import.meta.url));
  const sceneSource = await readCsSources(new URL("../unitypkg/Editor/Tools/scene/", import.meta.url));
  const goSource = await readCsSources(new URL("../unitypkg/Editor/Tools/go/", import.meta.url));
  const editorSource = await readFile(new URL("../unitypkg/Editor/Tools/editor/action-tools.cs", import.meta.url), "utf8");

  assert.match(assetSource, /AssetToolSupport\.NormalizeAssetSearchFolders\(request\.Path, request\.SearchInFolders\)/);
  assert.match(assetSource, /AssetDatabase\.FindAssets\(resolvedFilter, searchInFolders\.Length == 0 \? null : searchInFolders\)/);
  assert.match(assetSource, /class AssetGetDataTool : IUnityCliTool[\s\S]*Data = GameObjectToolSupport\.ParseJsonToObject\(memberJson\)/);
  assert.match(assetSource, /class AssetGetComponentsTool : IUnityCliTool[\s\S]*PageInfo = new AssetPageInfo/);
  assert.match(assetSource, /class AssetRefreshTool : IUnityCliTool[\s\S]*AssetDatabase\.(Refresh|ImportAsset)/);
  assert.match(assetSource, /class AssetPreviewTool : IUnityCliTool[\s\S]*AssetPreview\.GetAssetPreview/);
  assert.doesNotMatch(assetSource, /unity_cli asset/);

  assert.match(sceneSource, /class SceneHierarchyTool : IUnityCliTool</);
  assert.match(sceneSource, /public string Parent[\s\S]*public string Query[\s\S]*public string ComponentType[\s\S]*IncludeComponents/);
  assert.match(sceneSource, /CollectHierarchy\(Transform transform, List<HierarchyItem> items, int depth, int maxDepth, string query, bool includeInactive, bool includeComponents, string componentType\)/);
  assert.match(sceneSource, /BuildPageInfo\(pageSize, cursor, total, page\.Count\)/);
  assert.doesNotMatch(sceneSource, /unity_cli scene/);

  assert.match(goSource, /SearchGameObjects\(searchMethod, searchTerm, includeInactive\)[\s\S]*PageInfo = new GoPageInfo/);
  assert.match(goSource, /ResolveUniqueTarget[\s\S]*FindObjectsOfType<GameObject>\(true\)/);
  assert.match(goSource, /ResolveComponentType\(string componentName\)/);
  assert.match(goSource, /ValidateMutationInputs[\s\S]*ResolveModifyTargets\(request\)[\s\S]*Undo\.RecordObject/);
  assert.match(goSource, /GameObject look-at requires a lookAt target[\s\S]*go\.look-at --input/);
  assert.doesNotMatch(goSource, /unity_cli scene hierarchy/);

  assert.match(editorSource, /class EditorConsoleTool : IUnityCliTool[\s\S]*Use console\.clear to clear\./);
  assert.match(editorSource, /class EditorMenuTool : IUnityCliTool[\s\S]*ExecuteMenuItem\(r\.Path\)/);
  assert.match(editorSource, /class ConsoleGetTool : IUnityCliTool</);
  assert.match(editorSource, /Skip\(cursor\)\.Take\(count\)/);
  assert.doesNotMatch(editorSource, /unity_cli editor menu/);
});

test("build/animation/package sources keep current typed-result flows", async () => {
  const scenesSource = await readFile(new URL("../unitypkg/Editor/Tools/build/scenes.cs", import.meta.url), "utf8");
  const settingsSource = await readFile(new URL("../unitypkg/Editor/Tools/build/settings.cs", import.meta.url), "utf8");
  const clipSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/clip-tools.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/AnimationToolSupport.cs", import.meta.url), "utf8");
  const runtimeSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/animator-runtime.cs", import.meta.url), "utf8");
  const controllerSource = await readFile(new URL("../unitypkg/Editor/Tools/animation/controller-tools.cs", import.meta.url), "utf8");
  const packageBridgeSpec = await readFile(new URL("../src/bridge/domains/package.js", import.meta.url), "utf8");
  const packageSource = await readFile(new URL("../unitypkg/Editor/Tools/editor/action-tools.cs", import.meta.url), "utf8");
  const animationSource = `${clipSource}\n${supportSource}\n${runtimeSource}\n${controllerSource}`;

  assert.match(scenesSource, /class BuildScenesTool : IUnityCliTool[\s\S]*return new Result \{ Command = "build\.scenes"/);
  assert.match(settingsSource, /class BuildSettingsTool : IUnityCliTool[\s\S]*return BuildResult\(\)/);

  assert.match(animationSource, /AnimationUtility\.GetCurveBindings\(clip\)/);
  assert.match(animationSource, /AnimationUtility\.GetAnimationEvents\(clip\)/);
  assert.match(animationSource, /AnimationUtility\.GetAnimationClipSettings\(clip\)/);
  assert.match(animationSource, /AnimationUtility\.SetAnimationClipSettings\(clip, settings\)/);
  assert.match(animationSource, /if \(r\.LoopTime\.HasValue\) settings\.loopTime = r\.LoopTime\.Value/);
  assert.match(animationSource, /if \(!string\.IsNullOrEmpty\(r\.WrapMode\) && System\.Enum\.TryParse<WrapMode>\(r\.WrapMode, true, out var wm\)\) clip\.wrapMode = wm/);
  assert.match(animationSource, /public static AnimatorControllerParameter\[\] AnimatorParameterDefinitions\(Animator animator\)/);
  assert.match(animationSource, /return controller == null \|\| controller\.parameters == null \? new AnimatorControllerParameter\[0\] : controller\.parameters;/);
  assert.match(animationSource, /var hasRuntime = animator\.parameterCount > 0;/);
  assert.match(animationSource, /var effectiveLayerCount = animator\.layerCount > 0 \? animator\.layerCount : controllerLayers\.Length;/);
  assert.match(animationSource, /ControllerCreateBlendTree1DTool[\s\S]*E_STATE_EXISTS/);
  assert.match(animationSource, /ControllerCreateBlendTree2DTool[\s\S]*E_STATE_EXISTS/);
  assert.match(animationSource, /ControllerGetInfoTool[\s\S]*Parameters = parameters/);
  assert.doesNotMatch(controllerSource, /controller get-info <path>/);
  assert.doesNotMatch(controllerSource, /animation\.clip action=create/);

  assert.match(packageBridgeSpec, /packageEmbedArgs[\s\S]*packageId/);
  assert.match(packageBridgeSpec, /dryRun: boolFlag\(flags, "dry-run"\) \?\? boolField\(flags, "dryRun"\)/);
  assert.match(packageSource, /class PackageEmbedTool : IUnityCliTool/);
  assert.match(packageSource, /PackageClient\.Embed is not available/);
  assert.match(packageSource, /PlannedEffects = new List<EditorPlannedEffect> \{ new EditorPlannedEffect \{ Type = "package\.embed"/);
});

test("group counts for build/graphics/physics stay aligned with contract discovery", () => {
  assert.ok(contractToolNames("build").includes("build.scenes"));
  assert.ok(contractToolNames("build").includes("build.platform"));
  assert.ok(contractToolNames("build").includes("build.profiles"));
  assert.ok(contractToolNames("build").includes("build.settings"));
  assert.equal(contractToolNames("graphics").length, 28);
  assert.ok(contractToolNames("physics").includes("physics.raycast-all"));
  assert.ok(contractToolNames("physics").includes("physics.linecast"));
  assert.ok(contractToolNames("physics").includes("physics.shapecast"));
  assert.ok(contractToolNames("physics").includes("physics.validate"));
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
    assert.equal(spec?.bridgeCommand, bridgeCommand);
    if (toolName) {
      assert.equal(spec?.toolName, toolName, `${group} ${command} toolName should match`);
    }
  }
});
