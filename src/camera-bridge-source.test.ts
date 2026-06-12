import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const sourceUrl = new URL("../unity-package/Editor/AlpsUnityBridge.cs", import.meta.url);
const readmeUrl = new URL("../README.md", import.meta.url);

test("Unity Bridge exposes documented camera, screenshot and prefab commands", async () => {
  const source = await readFile(sourceUrl, "utf8");
  const readme = await readFile(readmeUrl, "utf8");
  const bridgeSection = readme.slice(readme.indexOf("## Unity Editor Bridge"), readme.indexOf("## Batch Automation"));

  assert.match(source, /HttpListener/);
  assert.match(source, /POST" \|\| context\.Request\.Url\.AbsolutePath != "\/bridge\/execute"/);
  assert.match(source, /CapabilityJson\("camera", CameraCommands\)/);
  assert.match(source, /CapabilityJson\("prefab", PrefabCommands\)/);
  assert.match(source, /private static readonly string\[\] CameraCommands = \{ "camera\.list", "camera\.info", "camera\.screenshot" \}/);
  assert.match(source, /private static readonly string\[\] PrefabCommands = \{ "prefab\.info", "prefab\.create", "prefab\.stage-open", "prefab\.stage-save", "prefab\.stage-close", "prefab\.hierarchy" \}/);
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
  assert.match(bridgeSection, /`camera\.list`, `camera\.info`, `camera\.screenshot`, `scene\.screenshot`, `screenshot\.capture`, `prefab\.info`, `prefab\.create`, `prefab\.stage-open`, `prefab\.stage-save`, `prefab\.stage-close` and `prefab\.hierarchy`/);
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
  assert.match(readme, /`prefab hierarchy <path>` pages hierarchy items with `pageInfo`/);
  assert.match(readme, /`E_PREFAB_PATH_REQUIRED`, `E_PREFAB_PATH_INVALID`, `E_PREFAB_NOT_FOUND`, `E_ASSET_NOT_PREFAB`, `E_PREFAB_OUTPUT_EXISTS`, `E_PREFAB_DIRTY_CONFLICT`, `E_PREFAB_STAGE_NOT_OPEN`, `E_PREFAB_STAGE_MISMATCH` and `E_UNITY_BUSY`/);
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
  assert.match(readme, /C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG\/JPG artifacts, Prefab Stage open\/save\/close, Prefab dirty conflicts and nested\/variant Prefab summaries must still be proven by real Windows \+ Unity Editor validation/);
});
