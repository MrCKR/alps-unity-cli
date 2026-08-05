import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editor = new URL("../unitypkg/Editor/", import.meta.url);
const source = (path: string) => readFile(new URL(path, editor), "utf8");

test("core graphics requests validate immutable plans before one commit and read back effects", async () => {
  const [quality, scene, settings, pipeline, ambient, fog, reflection, properties, bakeSettings, bakeProbe, material, sun, support] = await Promise.all([
    source("Tools/graphics/quality.cs"), source("Tools/graphics/scene-debug.cs"), source("Tools/graphics/settings.cs"), source("Tools/graphics/pipeline-quality.cs"), source("Tools/graphics/skybox-set-ambient.cs"), source("Tools/graphics/skybox-set-fog.cs"), source("Tools/graphics/skybox-set-reflection.cs"), source("Tools/graphics/skybox-set-properties.cs"), source("Tools/graphics/bake-settings.cs"), source("Tools/graphics/bake-reflection-probe.cs"), source("Tools/graphics/skybox-set-material.cs"), source("Tools/graphics/skybox-set-sun.cs"), source("Tools/graphics/GraphicsToolSupport.cs"),
  ]);
  for (const text of [quality, settings, pipeline, ambient, fog, reflection, properties, bakeSettings]) {
    assert.match(text, /ctx\.CommitEffect\(\)/);
    assert.match(text, /STATE_MISMATCH|readback/i);
  }
  for (const text of [bakeProbe, pipeline, scene, ambient, fog, reflection]) {
    assert.match(text, /Examples = new\[\] \{ new ToolExample/);
  }
  assert.match(support, /ParseEnum<T>/);
  assert.match(support, /ParseFinite/);
  assert.match(support, /NormalizeAssetPathRequired/);
  assert.match(support, /ReloadAsset<T>/);
  assert.match(scene, /#if UNITY_2023_2_OR_NEWER[\s\S]*ctx\.CommitEffect\(\)[\s\S]*#else[\s\S]*ctx\.CommitEffect\(\)/);
  assert.match(scene, /observed\.cameraMode\.drawMode != expected/);
  assert.match(reflection, /public string Resolution/);
  assert.match(reflection, /int\.TryParse\(r\.Resolution[\s\S]*parsed < 16 \|\| parsed > 8192/);
  assert.doesNotMatch(reflection, /DefaultReflectionResolution/);
  assert.match(quality, /MasterTextureLimit/);
  assert.match(quality, /RealtimeReflectionProbes/);
  assert.match(settings, /ReloadAsset<RenderPipelineAsset>/);
  assert.match(settings, /GenericSettingMatches/);
  assert.match(properties, /E_SKYBOX_PROPERTY_NOT_FOUND/);
  assert.match(properties, /E_SKYBOX_PROPERTY_TYPE_UNSUPPORTED/);
  assert.match(properties, /ReloadAsset<Material>/);
  assert.match(bakeSettings, /E_BAKE_SCENE_NOT_FOUND/);
  assert.match(bakeSettings, /EditorUtility\.SetDirty\(ls\)/);
  assert.match(bakeSettings, /LoadAllAssetsAtPath/);
  assert.match(material, /SKYBOX_MATERIAL_STATE_MISMATCH/);
  assert.match(material, /AssetPathToGUID/);
  assert.match(sun, /LightType\.Directional/);
  assert.match(sun, /GetInstanceID\(\) != expectedId/);
  assert.match(bakeProbe, /The parent Assets folder must already exist/);
  assert.match(bakeProbe, /E_GRAPHICS_ASSET_DIRECTORY_NOT_FOUND/);
  assert.ok(bakeProbe.indexOf("E_GRAPHICS_ASSET_DIRECTORY_NOT_FOUND") < bakeProbe.indexOf("ctx.CommitEffect()"));
  assert.match(bakeProbe, /oldExists/);
  assert.match(bakeProbe, /oldDigest/);
  assert.match(bakeProbe, /File\.ReadAllBytes/);
  assert.match(bakeProbe, /E_REFLECTION_PROBE_BAKE_ARTIFACT_UNPROVEN/);
  assert.match(bakeProbe, /bytes\[0\] == 0x76/);
});

test("graphics probe and pipeline writes validate before commit and read fresh objects", async () => {
  const [reflection, group, positions, pipeline] = await Promise.all([
    source("Tools/graphics/reflection-probe.cs"),
    source("Tools/graphics/light-probe-group.cs"),
    source("Tools/graphics/probe-positions.cs"),
    source("Tools/graphics/pipeline-settings.cs"),
  ]);
  const reflectionBefore = reflection.slice(0, reflection.indexOf("ctx.CommitEffect()"));
  assert.match(reflectionBefore, /E_REFLECTION_PROBE_RESOLUTION_INVALID/);
  assert.match(reflectionBefore, /E_REFLECTION_PROBE_BOUNCES_INVALID/);
  assert.match(reflection, /ResolveUniqueTarget\(go\.GetInstanceID\(\)\.ToString\(CultureInfo\.InvariantCulture\)/);
  assert.match(reflection, /E_REFLECTION_PROBE_STATE_MISMATCH/);
  assert.match(group, /SamePositions\(observedPositions, positions\)/);
  assert.match(group, /GetInstanceID\(\)\.ToString\(CultureInfo\.InvariantCulture\)/);
  assert.match(positions, /E_PROBE_TYPE_INVALID/);
  assert.match(positions, /E_PROBE_POSITIONS_INVALID/);
  assert.match(positions, /E_PROBE_STATE_MISMATCH/);
  assert.match(pipeline, /AssetPathToGUID\(pipelinePath\)/);
  assert.match(pipeline, /AssetDatabase\.ImportAsset\(pipelinePath, ImportAssetOptions\.ForceUpdate\)/);
  assert.match(pipeline, /ReloadAsset<RenderPipelineAsset>\(pipelinePath, pipelineGuid\)/);
});
