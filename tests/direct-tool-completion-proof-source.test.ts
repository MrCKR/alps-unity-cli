import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function readTool(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

test("direct tool completion proof waits for terminal preview and deletion state", async () => {
  const preview = await readTool("Tools/asset/preview.cs");
  assert.match(preview, /AssetPreview\.GetAssetPreview\(asset\)[\s\S]*ctx\.Wait\.Until\([\s\S]*!AssetPreview\.IsLoadingAssetPreview\(instanceId\)[\s\S]*AssetPreview\.GetAssetPreview\(asset\)/);
  assert.match(preview, /IsTransient = false/);
  assert.match(preview, /E_ASSET_PREVIEW_TIMEOUT/);

  for (const path of ["Tools/asset/delete.cs", "Tools/shader/delete.cs", "Tools/texture/delete.cs"]) {
    const source = await readTool(path);
    assert.match(source, /ctx\.Wait\.Until\([\s\S]*!File\.Exists\(fullPath\)[\s\S]*!Directory\.Exists\(fullPath\)[\s\S]*!AssetDatabase\.IsValidFolder\(path\)[\s\S]*LoadMainAssetAtPath\(path\) == null/);
    // Unity 可能保留 deleted path 的 stale GUID，不能把 GUID 空值当删除终态。
    assert.doesNotMatch(source, /AssetPathToGUID\(path\)/);
    assert.match(source, /DELETE_PROOF_MISSING/);
  }
});

test("camera captures and texture generation prove their observed artifacts", async () => {
  const camera = await readTool("Tools/camera/CameraToolSupport.cs");
  const multiview = await readTool("Tools/camera/multiview.cs");
  const generate = await readTool("Tools/texture/generate.cs");

  assert.match(camera, /ScreenshotToolSupport\.WriteScreenshotBytes\(outputPath, bytes, budget\)/);
  assert.match(camera, /E_SCREENSHOT_ARTIFACT_EMPTY/);
  assert.match(camera, /E_SCREENSHOT_ARTIFACT_MISSING/);
  assert.match(camera, /E_SCREENSHOT_ARTIFACT_INVALID/);
  assert.match(multiview, /CameraToolSupport\.NormalizeFormat\(request\.Format, Path\.Combine\(outputDir, "capture\.png"\)\)/);
  assert.match(multiview, /plans\.Add\(new KeyValuePair<int, string>\(camera\.GetInstanceID\(\), outputPath\)\)/);
  assert.match(multiview, /EditorUtility\.InstanceIDToObject\(plan\.Key\) as Camera/);
  assert.match(multiview, /CaptureCamera\(camera, width, height, false, 95, plan\.Value, format/);
  assert.match(generate, /!File\.Exists\(fullPath\) \|\| new FileInfo\(fullPath\)\.Length <= 0/);
  assert.match(generate, /AssetDatabase\.ImportAsset\(path,[\s\S]*LoadAssetAtPath<Texture2D>\(path\)/);
  assert.match(generate, /SizeBytes = sizeBytes/);
  assert.match(generate, /E_TEXTURE_GENERATE_FAILED/);
});
