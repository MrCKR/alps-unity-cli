import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function readTool(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

function assertBefore(source: string, first: string, second: string, label: string): void {
  assert.ok(source.indexOf(first) >= 0, `${label}: missing ${first}`);
  assert.ok(source.indexOf(second) >= 0, `${label}: missing ${second}`);
  assert.ok(source.indexOf(first) < source.indexOf(second), `${label}: ${first} must precede ${second}`);
}

test("critical asset and create handlers commit immediately before their first effect", async () => {
  const [assetCreate, assetModify, assetImport, controller, goCreate, camera, material] = await Promise.all([
    readTool("Tools/asset/create.cs"),
    readTool("Tools/asset/write-text.cs"),
    readTool("Tools/asset/import.cs"),
    readTool("Tools/animator-controller/create.cs"),
    readTool("Tools/game-object/create.cs"),
    readTool("Tools/camera/create.cs"),
    readTool("Tools/material/create.cs")
  ]);

  assertBefore(assetCreate, "ctx.CommitEffect();\n                AssetToolSupport.CreateAssetFolder", "AssetToolSupport.CreateAssetFolder", "asset.create folder");
  assertBefore(assetCreate, "ctx.CommitEffect();\n                AssetToolSupport.WriteTextAsset", "AssetToolSupport.WriteTextAsset", "asset.create file");
  assertBefore(assetModify, "ctx.CommitEffect();", "AssetToolSupport.WriteTextAsset", "asset.write-text");
  assertBefore(assetImport, "ctx.CommitEffect();\n                AssetToolSupport.CreateAssetDirectory", "File.Copy", "asset.import source");
  assertBefore(assetImport, "ctx.CommitEffect();\n                AssetToolSupport.WriteTextAsset", "AssetToolSupport.WriteTextAsset", "asset.import content");
  assertBefore(controller, "ctx.CommitEffect();", "SceneToolSupport.CreateAssetDirectory", "animator-controller.create");
  assertBefore(goCreate, "ctx.CommitEffect();", "new GameObject", "game-object.create");
  assertBefore(camera, "var position =", "ctx.CommitEffect();", "camera.create vector validation");
  assertBefore(camera, "ctx.CommitEffect();", "new GameObject", "camera.create");
  assertBefore(material, "ctx.CommitEffect();", "AssetToolSupport.CreateAssetDirectory", "material.create directory");
  assertBefore(material, "ctx.CommitEffect();", "AssetDatabase.CreateAsset", "material.create asset");
});

test("lighting and prefab validate before committing and profile proof does not replay", async () => {
  const [lightProbe, reflectionProbe, prefab, profile] = await Promise.all([
    readTool("Tools/lighting/light-probe-group.cs"),
    readTool("Tools/lighting/reflection-probe.cs"),
    readTool("Tools/prefab/modify.cs"),
    readTool("Tools/build/profile-switch.cs")
  ]);

  assertBefore(lightProbe, "GraphicsToolSupport.ParseVector3Array", "ctx.CommitEffect();", "light probe vector validation");
  assertBefore(lightProbe, "ctx.CommitEffect();", "new GameObject", "light probe object creation");
  assertBefore(reflectionProbe, "Enum.TryParse<UnityEngine.Rendering.ReflectionProbeMode>", "ctx.CommitEffect();", "reflection probe enum validation");
  assertBefore(reflectionProbe, "GameObjectToolSupport.ParseVector3", "ctx.CommitEffect();", "reflection probe vector validation");
  assertBefore(reflectionProbe, "ctx.CommitEffect();", "new GameObject", "reflection probe object creation");
  const prefabExecute = prefab.slice(prefab.indexOf("ExecuteAsync"));
  assertBefore(prefabExecute, "var existingAddChild", "ctx.CommitEffect();", "prefab child validation");
  assertBefore(prefabExecute, "E_PREFAB_MODIFY_NOOP", "ctx.CommitEffect();", "prefab no-op validation");
  assertBefore(prefabExecute, "ctx.CommitEffect();", "Object.DestroyImmediate", "prefab destroy");
  assertBefore(prefabExecute, "ctx.CommitEffect();", "new GameObject", "prefab child creation");
  assertBefore(prefabExecute, "ctx.CommitEffect();", "PrefabUtility.SaveAsPrefabAsset", "prefab save");
  assertBefore(profile, "ctx.CommitEffect();", "BuildToolSupport.SetActiveBuildProfile", "profile switch commit before effect");
});
