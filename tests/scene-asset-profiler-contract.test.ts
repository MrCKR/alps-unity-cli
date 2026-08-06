import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { contractToolNames, listCsFiles, readCsSources } from "./helpers/csharp-test-helpers.js";

const ONE_CAPABILITY_DOMAINS = ["scene", "asset", "build", "animation", "package", "graphics", "physics", "profiler"];

async function oneCapabilityToolNames(domain: string): Promise<string[]> {
  const files = await listCsFiles(new URL(`../unitypkg/Editor/Tools/${domain}/`, import.meta.url));
  const names: string[] = [];
  for (const file of files) {
    const source = await readFile(file, "utf8");
    if (!/:\s*IUnityCliTool\s*</.test(source)) continue;
    assert.equal((source.match(/:\s*IUnityCliTool\s*</g) ?? []).length, 1, `${file.pathname} must implement exactly one Tool`);
    const name = /ToolDescriptor\s+Describe\(\)\s*=>\s*new\(\)\s*\{\s*Name\s*=\s*"([a-z0-9][a-z0-9.-]*)"/.exec(source)?.[1];
    assert.ok(name, `${file.pathname} must expose a Tool descriptor name`);
    if (name.split(".")[0] === domain) names.push(name);
  }
  return names.sort();
}

/**
 * 跨域契约测试收敛为两类：
 * 1. 新增命令仍在 contract / bridge spec 中存在。
 * 2. 跨域共享的 C# 关键实现与错误提示没有回退到旧 CLI 形态。
 */

test("cross-domain new tools keep contract entries with present C# handlers", () => {
  const newTools = [
    "scene.unload", "scene.set-active", "scene.list-opened", "scene.data", "scene.view-frame",
    "go.set-parent", "prefab.instantiate", "prefab.unpack",
    "asset.search-built-in", "asset.get-data", "asset.get-components", "asset.preview",
    "shader.list", "shader.get-data", "texture.generate",
    "console.get", "console.clear", "editor.selection.set", "editor.active-tool.set",
    "build.scene.list", "build.scene.add", "build.scene.remove", "build.scene.enable", "build.scene.disable",
    "build.platform.get", "build.platform.set", "build.profile.list", "build.profile.create", "build.profile.switch", "build.profile.modify", "build.settings.get", "build.settings.set",
    "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate", "physics.assign-material"
  ];
  for (const name of newTools) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("Build Profile Tools stay registered on every supported Unity version", async () => {
  for (const name of ["create", "modify", "switch"]) {
    const source = await readFile(new URL(`../unitypkg/Editor/Tools/build/profile/${name}.cs`, import.meta.url), "utf8");
    assert.match(source, /^#if UNITY_EDITOR\b/);
    assert.doesNotMatch(source, /UNITY_6000_0_OR_NEWER/);
    assert.match(source, /RequireBuildProfileType/);
  }
  const registry = await readFile(new URL("../unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs", import.meta.url), "utf8");
  assert.match(registry, /BuildProfileApiRequired[\s\S]*BuildToolSupport\.BuildProfileType\(\) == null/);
  assert.match(registry, /E_BUILD_PROFILE_API_UNAVAILABLE/);
  assert.match(registry, /E_TOOL_PLAYMODE_UNAVAILABLE: Exit Play Mode before executing this editor-only Tool/);
  assert.doesNotMatch(registry, /E_UNITY_PLAY_MODE: Exit Play Mode before executing this editor-only Tool/);
});

test("asset/search scene/hierarchy go/find and editor/console sources keep current implementation", async () => {
  const assetSource = await readCsSources(new URL("../unitypkg/Editor/Tools/asset/", import.meta.url));
  const assetSupportSource = await readFile(new URL("../unitypkg/Editor/Tools/asset/AssetToolSupport.cs", import.meta.url), "utf8");
  const sceneSource = await readCsSources(new URL("../unitypkg/Editor/Tools/scene/", import.meta.url));
  const goSource = await readCsSources(new URL("../unitypkg/Editor/Tools/go/", import.meta.url));
  const consoleSource = await readFile(new URL("../unitypkg/Editor/Tools/console/get.cs", import.meta.url), "utf8");
  const menuSource = await readFile(new URL("../unitypkg/Editor/Tools/editor/menu.cs", import.meta.url), "utf8");

  assert.match(assetSource, /AssetToolSupport\.NormalizeAssetSearchFolders\(request\.Path, request\.SearchInFolders\)/);
  assert.match(assetSource, /AssetDatabase\.FindAssets\(resolvedFilter, searchInFolders\.Length == 0 \? null : searchInFolders\)/);
  assert.match(assetSource, /class AssetGetDataTool : IUnityCliTool/);
  assert.match(assetSource, /class AssetGetComponentsTool : IUnityCliTool[\s\S]*PageInfo = new AssetPageInfo/);
  // asset.refresh 已迁移为协调器路线（internal.asset.refresh.start/.status），不再是 asset/search 域的 IUnityCliTool。
  assert.doesNotMatch(assetSource, /class AssetRefreshTool/);
  assert.match(assetSource, /class AssetPreviewTool : IUnityCliTool[\s\S]*AssetPreview\.GetAssetPreview/);
  const assetExists = assetSupportSource.slice(assetSupportSource.indexOf("public static bool AssetExists"), assetSupportSource.indexOf("public static void CreateAssetDirectory"));
  assert.match(assetExists, /File\.Exists\(fullPath\)/);
  assert.match(assetExists, /AssetDatabase\.LoadMainAssetAtPath\(assetPath\) != null/);
  assert.doesNotMatch(assetExists, /AssetPathToGUID/);
  assert.doesNotMatch(assetSource, /unity_cli asset/);

  assert.match(sceneSource, /class SceneHierarchyTool : IUnityCliTool</);
  assert.match(sceneSource, /public string Parent[\s\S]*public string Query[\s\S]*public string ComponentType[\s\S]*IncludeComponents/);
  assert.match(sceneSource, /CollectHierarchy\(Transform transform, List<HierarchyItem> items, int depth, int maxDepth, string query, bool includeInactive, bool includeComponents, string componentType\)/);
  assert.match(sceneSource, /BuildPageInfo\(pageSize, cursor, total, page\.Count\)/);
  assert.doesNotMatch(sceneSource, /unity_cli scene/);

  assert.match(goSource, /SearchGameObjects\(searchMethod, searchTerm, includeInactive\)[\s\S]*PageInfo = new GoPageInfo/);
  assert.match(goSource, /ResolveUniqueTarget[\s\S]*FindObjectsOfType<GameObject>\(true\)/);
  assert.match(goSource, /ResolveComponentType\(string componentName\)/);
  assert.match(goSource, /ValidateMutationInputs[\s\S]*ResolveModifyTargets\(request\)[\s\S]*UnityCliSceneMutation\.RecordObject/);
  assert.match(goSource, /GameObject look-at requires a lookAt target[\s\S]*go\.look-at --input/);
  assert.doesNotMatch(goSource, /unity_cli scene hierarchy/);

  assert.match(consoleSource, /class ConsoleGetTool : IUnityCliTool/);
  assert.match(consoleSource, /Skip\(cursor\)\.Take\(count\)/);
  assert.match(menuSource, /class EditorMenuTool : IUnityCliTool[\s\S]*ExecuteMenuItem\(request\.Path\)/);
  assert.match(menuSource, /E_MENU_COMPLETION_UNSUPPORTED[\s\S]*Use a dedicated Tool, or use File\/Save/);
  assert.match(menuSource, /string\.Equals\(request\.Path, "File\/Save"/);
  assert.match(menuSource, /ctx\.Wait\.UntilStable\([\s\S]*!activeScene\.isDirty/);
  assert.match(menuSource, /scene\.isDirty/);
  assert.doesNotMatch(menuSource, /unity_cli editor menu/);
});

test("scene.view-frame synchronizes the hidden SceneView camera before frustum proof", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/scene/view-frame.cs", import.meta.url), "utf8");
  const commitIndex = source.indexOf("ctx.CommitEffect()");
  const frameIndex = source.indexOf("sceneView.Frame(bounds, true)");
  const synchronizeIndex = source.indexOf("SynchronizeSceneCamera(sceneView)");
  const stableIndex = source.indexOf("ctx.Wait.UntilStable");

  assert.ok(commitIndex >= 0 && commitIndex < frameIndex);
  assert.ok(frameIndex < synchronizeIndex && synchronizeIndex < stableIndex);
  assert.match(source, /camera\.transform\.position = sceneView\.pivot \+ rotation \* new Vector3\(0f, 0f, -distance\)/);
  assert.match(source, /camera\.fieldOfView = verticalHalfFov \* 2f \* Mathf\.Rad2Deg/);
  assert.match(source, /camera\.ResetWorldToCameraMatrix\(\)[\s\S]*camera\.ResetProjectionMatrix\(\)/);
});

test("asset preview and delete expose only terminal observed state", async () => {
  const previewSource = await readFile(new URL("../unitypkg/Editor/Tools/asset/preview.cs", import.meta.url), "utf8");
  const deleteSource = await readFile(new URL("../unitypkg/Editor/Tools/asset/delete.cs", import.meta.url), "utf8");

  assert.match(previewSource, /AssetPreview\.GetAssetPreview\(asset\)[\s\S]*ctx\.Wait\.Until\([\s\S]*!AssetPreview\.IsLoadingAssetPreview\(instanceId\)[\s\S]*AssetPreview\.GetAssetPreview\(asset\)/);
  assert.match(previewSource, /IsTransient = false/);
  assert.match(previewSource, /E_ASSET_PREVIEW_TIMEOUT/);
  assert.match(deleteSource, /ctx\.Wait\.Until\([\s\S]*!File\.Exists\(fullPath\)[\s\S]*!Directory\.Exists\(fullPath\)[\s\S]*!AssetDatabase\.IsValidFolder\(path\)[\s\S]*LoadMainAssetAtPath\(path\) == null/);
  assert.doesNotMatch(deleteSource, /AssetPathToGUID\(path\)/);
  assert.match(deleteSource, /E_ASSET_DELETE_PROOF_MISSING/);
});

test("build/animation/package sources keep current typed-result flows", async () => {
  const buildSource = await readCsSources(new URL("../unitypkg/Editor/Tools/build/", import.meta.url));
  const animationSource = await readCsSources(new URL("../unitypkg/Editor/Tools/animation/", import.meta.url));
  const controllerSource = await readCsSources(new URL("../unitypkg/Editor/Tools/controller/", import.meta.url));
  const packageTool = await readFile(new URL("../src/tools/package/embed.js", import.meta.url), "utf8");
  const packageSource = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliPackageEmbedCoordinator.cs", import.meta.url), "utf8");

  assert.match(buildSource, /class BuildSceneListTool : IUnityCliTool/);
  assert.match(buildSource, /Name = "build\.scene\.add"/);
  assert.match(buildSource, /Name = "build\.profile\.create"/);
  assert.match(buildSource, /class BuildSettingsGetTool : IUnityCliTool/);
  assert.match(buildSource, /Name = "build\.settings\.set"/);
  assert.doesNotMatch(buildSource, /public string Action/);

  assert.match(animationSource, /AnimationUtility\.GetCurveBindings\(clip\)/);
  assert.match(animationSource, /AnimationUtility\.GetAnimationEvents\(clip\)/);
  assert.match(animationSource, /AnimationUtility\.GetAnimationClipSettings\(clip\)/);
  assert.match(animationSource, /AnimationUtility\.SetAnimationClipSettings\(clip, settings\)/);
  assert.match(animationSource, /if \(r\.LoopTime\.HasValue\) settings\.loopTime = r\.LoopTime\.Value/);
  assert.match(animationSource, /var wm = AnimationToolSupport\.ValidateClipSettings\(r\.FrameRate, r\.WrapMode, r\.StartTime, r\.StopTime, r\.Length\)/);
  assert.match(animationSource, /if \(!string\.IsNullOrEmpty\(r\.WrapMode\)\) clip\.wrapMode = wm/);
  assert.match(controllerSource, /ControllerCreateBlendTree1DTool[\s\S]*E_STATE_EXISTS/);
  assert.match(controllerSource, /ControllerCreateBlendTree2DTool[\s\S]*E_STATE_EXISTS/);
  assert.match(controllerSource, /ControllerGetInfoTool[\s\S]*Parameters = parameters/);
  assert.doesNotMatch(controllerSource, /controller get-info <path>/);
  assert.doesNotMatch(controllerSource, /animation\.clip action=create/);

  assert.match(packageTool, /internal\.package\.embed\.start/);
  assert.match(packageTool, /internal\.package\.embed\.status/);
  assert.match(packageSource, /UnityCliPackageEmbedCoordinator/);
  assert.match(packageSource, /PackageClient\.Embed is not available/);
  assert.match(packageSource, /new List<EditorPlannedEffect> \{ new EditorPlannedEffect \{ Type = "package\.embed"/);
});

test("one-capability source, contract, and bridge spec sets are bidirectionally aligned", async () => {
  for (const domain of ONE_CAPABILITY_DOMAINS) {
    const sourceNames = await oneCapabilityToolNames(domain);
    const contractNames = contractToolNames(domain)
      .filter((name) => findCommandContract(name)?.csharp_handler === "present");
    const specNames = [...new Set(bridgeCommandRegistry.specs()
      .filter((spec) => spec.group === domain && spec.toolName)
      .map((spec) => spec.toolName!))]
      .sort();
    assert.deepEqual(contractNames, sourceNames, `${domain} contract must enumerate current one-capability files`);
    assert.deepEqual(specNames, sourceNames, `${domain} bridge spec must enumerate current one-capability files`);
  }
  assert.equal(contractToolNames("build").filter((name) => findCommandContract(name)?.csharp_handler === "present").length, 13);
  assert.equal(contractToolNames("graphics").filter((name) => findCommandContract(name)?.csharp_handler === "present").length, 26);
  assert.equal(contractToolNames("physics").filter((name) => findCommandContract(name)?.csharp_handler === "present").length, 15);
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

    ["shader", "list", "project-tool.run"],
    ["texture", "generate", "project-tool.run"],
    ["console", "get", "project-tool.run", "console.get"],

    ["editor", "selection-set", "project-tool.run", "editor.selection.set"],
    ["build", "scene-list", "project-tool.run", "build.scene.list"],
    ["build", "profile-list", "project-tool.run", "build.profile.list"],
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
