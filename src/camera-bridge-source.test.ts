import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const sourceUrl = new URL("../unity-package/Editor/AlpsUnityBridge.cs", import.meta.url);
const readmeUrl = new URL("../README.md", import.meta.url);

test("Unity Bridge exposes documented camera, screenshot, prefab and UI commands", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");
  const bridgeSection = readme.slice(readme.indexOf("## Unity Editor Bridge"), readme.indexOf("## Batch Automation"));

  assert.match(source, /HttpListener/);
  assert.match(source, /POST" \|\| context\.Request\.Url\.AbsolutePath != "\/bridge\/execute"/);
  assert.match(source, /CapabilityJson\("camera", CameraCommands\)/);
  assert.match(source, /CapabilityJson\("prefab", PrefabCommands\)/);
  assert.match(source, /CapabilityJson\("ui", UiCommands\)/);
  assert.match(source, /private static readonly string\[\] CameraCommands = \{ "camera\.list", "camera\.info", "camera\.screenshot" \}/);
  assert.match(source, /private static readonly string\[\] PrefabCommands = \{ "prefab\.info", "prefab\.create", "prefab\.stage-open", "prefab\.stage-save", "prefab\.stage-close", "prefab\.hierarchy" \}/);
  assert.match(source, /private static readonly string\[\] UiCommands = \{ "ui\.canvas\.create", "ui\.text\.create", "ui\.button\.create", "ui\.image\.create", "ui\.document\.info", "ui\.document\.create", "ui\.hierarchy" \}/);
  assert.match(source, /case "camera\.list"/);
  assert.match(source, /case "camera\.info"/);
  assert.match(source, /case "camera\.screenshot"/);
  assert.match(source, /case "scene\.screenshot"/);
  assert.match(source, /case "screenshot\.capture"/);
  assert.match(source, /case "prefab\.info"/);
  assert.match(source, /case "prefab\.create"/);
  assert.match(source, /case "prefab\.stage-open"/);
  assert.match(source, /case "prefab\.stage-save"/);
  assert.match(source, /case "prefab\.stage-close"/);
  assert.match(source, /case "prefab\.hierarchy"/);
  assert.match(source, /case "ui\.canvas\.create"/);
  assert.match(source, /case "ui\.text\.create"/);
  assert.match(source, /case "ui\.button\.create"/);
  assert.match(source, /case "ui\.image\.create"/);
  assert.match(source, /case "ui\.document\.info"/);
  assert.match(source, /case "ui\.document\.create"/);
  assert.match(source, /case "ui\.hierarchy"/);
  assert.match(bridgeSection, /`camera\.list`, `camera\.info`, `camera\.screenshot`, `scene\.screenshot`, `screenshot\.capture`, `prefab\.info`, `prefab\.create`, `prefab\.stage-open`, `prefab\.stage-save`, `prefab\.stage-close`, `prefab\.hierarchy`, `ui\.canvas\.create`, `ui\.text\.create`, `ui\.button\.create`, `ui\.image\.create`, `ui\.document\.info`, `ui\.document\.create` and `ui\.hierarchy`/);
  assert.doesNotMatch(bridgeSection, /live query\/edit\/screenshot/);
  assert.match(bridgeSection, /`scene hierarchy`, `go find`, `component set` and `script edit` are not routed by this package yet/);
});

test("Unity Bridge camera screenshot captures render texture and returns artifact metadata", async () => {
  const source = await readFile(sourceUrl, "utf8");

  assert.match(source, /Camera\.allCameras/);
  assert.match(source, /SceneView\.lastActiveSceneView/);
  assert.match(source, /new RenderTexture\(width, height, 24, RenderTextureFormat\.ARGB32\)/);
  assert.match(source, /texture\.ReadPixels/);
  assert.match(source, /texture\.EncodeToPNG\(\)/);
  assert.match(source, /texture\.EncodeToJPG\(quality\)/);
  assert.match(source, /File\.WriteAllBytes\(outputPath, bytes\)/);
  assert.match(source, /"\\\"screenshotPath\\\":"/);
  assert.match(source, /"\\\"artifactPath\\\":"/);
  assert.match(source, /"\\\"logPath\\\":"/);
});

test("Unity Bridge prefab operations expose structured diagnostics", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /PrefabUtility\.SaveAsPrefabAssetAndConnect/);
  assert.match(source, /PrefabUtility\.LoadPrefabContents/);
  assert.match(source, /PrefabStageUtility/);
  assert.match(source, /E_PREFAB_PATH_REQUIRED/);
  assert.match(source, /E_PREFAB_NOT_FOUND/);
  assert.match(source, /E_ASSET_NOT_PREFAB/);
  assert.match(source, /E_PREFAB_DIRTY_CONFLICT/);
  assert.match(source, /E_PREFAB_STAGE_NOT_OPEN/);
  assert.match(source, /plannedEffects/);
  assert.match(source, /PrefabVariantJson/);
  assert.match(source, /DependencySummaryJson/);
  assert.match(source, /NestedPrefabReferencesJson/);
  assert.match(source, /"\\\"variant\\\":"/);
  assert.match(source, /"\\\"dependencies\\\":"/);
  assert.match(source, /"\\\"nestedPrefabs\\\":"/);
  assert.match(source, /PrefabAssetJson\(path, root, root\)/);
  assert.match(readme, /`prefab info <path>` returns Prefab metadata including GUID, root name, asset type, `variant` base\/source paths and GUIDs, `dependencies\.items`, and `nestedPrefabs\.items` rather than counts only/);
  assert.match(readme, /`prefab hierarchy <path>` pages hierarchy items with `pageInfo`, component summaries, the same Prefab variant\/dependency summary, `nestedPrefabs\.items` and `logPath`/);
  assert.match(readme, /`E_PREFAB_PATH_REQUIRED`, `E_PREFAB_PATH_INVALID`, `E_PREFAB_NOT_FOUND`, `E_ASSET_NOT_PREFAB`, `E_PREFAB_OUTPUT_EXISTS`, `E_PREFAB_DIRTY_CONFLICT`, `E_PREFAB_STAGE_NOT_OPEN`, `E_PREFAB_STAGE_MISMATCH` and `E_UNITY_BUSY`/);
});


test("Unity Bridge UI operations expose uGUI and UI Toolkit diagnostics", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /using UiCanvasScaler = UnityEngine\.UI\.CanvasScaler;/);
  assert.match(source, /using UiGraphicRaycaster = UnityEngine\.UI\.GraphicRaycaster;/);
  assert.match(source, /new GameObject\(name, typeof\(RectTransform\), typeof\(Canvas\), typeof\(UiCanvasScaler\), typeof\(UiGraphicRaycaster\)\)/);
  assert.match(source, /GetComponent<UiCanvasScaler>\(\)/);
  assert.match(source, /UiCanvasScaler\.ScaleMode\.ScaleWithScreenSize/);
  assert.match(source, /AddComponent<UiText>\(\)/);
  assert.match(source, /AddComponent<UiButton>\(\)/);
  assert.match(source, /AddComponent<UiImage>\(\)/);
  assert.match(source, /UIDocument/);
  assert.match(source, /VisualTreeAsset/);
  assert.match(source, /StyleSheet/);
  assert.match(source, /PanelSettings/);
  assert.match(source, /E_UI_PARENT_NOT_FOUND/);
  assert.match(source, /E_TARGET_AMBIGUOUS/);
  assert.match(source, /TargetAmbiguousErrorJson\("UI parent"/);
  assert.match(source, /TargetAmbiguousErrorJson\("UIDocument"/);
  assert.match(source, /TargetAmbiguousErrorJson\("UI hierarchy"/);
  assert.match(source, /TargetMatchesJson/);
  assert.match(source, /matchCount/);
  assert.match(source, /ResolveGameObjectTarget/);
  assert.match(source, /private sealed class TargetResolution/);
  assert.match(source, /E_UI_PANEL_SETTINGS_NOT_FOUND/);
  assert.match(source, /E_UI_UXML_NOT_FOUND/);
  assert.match(source, /E_UI_USS_NOT_FOUND/);
  assert.match(source, /CollectUiHierarchy/);
  assert.match(readme, /`ui canvas\/create`, `ui text\/create`, `ui button\/create` and `ui image\/create` create uGUI objects/);
  assert.match(readme, /`ui document\/info` supports live UIDocument inspection and offline `.uxml`\/`.uss` file reads/);
  assert.match(readme, /`E_UI_PARENT_NOT_FOUND`, `E_UI_SPRITE_NOT_FOUND`, `E_UI_UXML_NOT_FOUND`, `E_UI_USS_NOT_FOUND`, `E_UI_PANEL_SETTINGS_NOT_FOUND`, `E_TARGET_NOT_FOUND`, `E_TARGET_AMBIGUOUS` and `E_UNITY_BUSY`/);
});

test("Unity Bridge HTTP timeout prevents queued side effects from stale requests", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /CancelBeforeStart/);
  assert.match(source, /TryStart/);
  assert.match(source, /MarkTimedOut/);
  assert.match(source, /bridge_request_timeout_cancelled/);
  assert.match(source, /bridge_request_timeout_started/);
  assert.match(source, /bridge_request_skipped_expired/);
  assert.match(source, /bridge_request_completed_after_http_timeout/);
  assert.match(source, /sideEffectSemantics/);
  assert.match(source, /started-before-http-timeout/);
  assert.match(source, /IsMutatingCommand/);
  assert.match(readme, /the bridge cancels that queued work before execution and returns `E_UNITY_BUSY` with no side effects applied/);
  assert.match(readme, /returns a success envelope with `status: "running"`, `sideEffectSemantics: "started-before-http-timeout"`, the original `requestId` and `logPath`/);
});

test("Unity Bridge screenshot validates path, format, overwrite and busy diagnostics", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");

  assert.match(source, /E_UNITY_BUSY/);
  assert.match(source, /E_CAMERA_NOT_FOUND/);
  assert.match(source, /E_TARGET_NOT_FOUND/);
  assert.match(source, /E_SCENE_VIEW_NOT_AVAILABLE/);
  assert.match(source, /E_OUTPUT_PATH_REQUIRED/);
  assert.match(source, /E_OUTPUT_EXISTS/);
  assert.match(source, /E_SCREENSHOT_FORMAT_UNSUPPORTED/);
  assert.match(source, /E_IO_ERROR/);
  assert.match(source, /string\.IsNullOrEmpty\(target\)/);
  assert.match(source, /File\.Exists\(outputPath\) && !overwrite/);
  assert.match(source, /CreateScreenshotDirectory\(outputPath\)/);
  assert.match(source, /WriteScreenshotBytes\(outputPath, bytes\)/);
  assert.match(source, /Path\.GetFullPath/);
  assert.match(readme, /`E_CAMERA_NOT_FOUND` for `camera info` when a target Camera cannot be resolved/);
  assert.match(readme, /`E_TARGET_NOT_FOUND` for `camera screenshot` when the target is empty or cannot be resolved/);
  assert.match(readme, /C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG\/JPG artifacts, Prefab Stage open\/save\/close, Prefab dirty conflicts, nested\/variant Prefab summaries, uGUI object creation, UIDocument\/PanelSettings linking and UI hierarchy summaries must still be proven by real Windows \+ Unity Editor validation/);
});
