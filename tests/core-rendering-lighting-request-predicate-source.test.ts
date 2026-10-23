import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editor = new URL("../unitypkg/Editor/", import.meta.url);
const source = (path: string) => readFile(new URL(path, editor), "utf8");

test("core rendering and lighting requests validate immutable plans before one commit and read back effects", async () => {
  const [quality, scene, pipeline, ambient, fog, reflection, properties, bakeSettings, bakeProbe, material, sun, support] = await Promise.all([
    source("Tools/rendering/quality.cs"), source("Tools/rendering/scene-debug.cs"), source("Tools/rendering/pipeline-settings.cs"), source("Tools/lighting/skybox-set-ambient.cs"), source("Tools/lighting/skybox-set-fog.cs"), source("Tools/lighting/skybox-set-reflection.cs"), source("Tools/lighting/skybox-set-properties.cs"), source("Tools/lighting/bake-settings.cs"), source("Tools/lighting/bake-reflection-probe.cs"), source("Tools/lighting/skybox-set-material.cs"), source("Tools/lighting/skybox-set-sun.cs"), source("Tools/rendering/GraphicsToolSupport.cs"),
  ]);
  for (const text of [quality, pipeline, ambient, fog, reflection, properties, bakeSettings]) {
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
  assert.match(pipeline, /ReloadAsset<RenderPipelineAsset>/);
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

test("retained rendering and lighting mutations commit only writes and read back their owning state", async () => {
  const [quality, pipeline, group, reflection] = await Promise.all([
    source("Tools/rendering/quality.cs"),
    source("Tools/rendering/pipeline-settings.cs"),
    source("Tools/lighting/light-probe-group.cs"),
    source("Tools/lighting/reflection-probe.cs"),
  ]);

  assert.match(quality, /var hasWrite = level\.HasValue \|\| request\.VSyncCount\.HasValue[\s\S]*request\.SoftParticles\.HasValue;/);
  assert.match(quality, /if \(hasWrite\) ctx\.CommitEffect\(\)/);
  assert.match(quality, /var observed = GraphicsToolSupport\.BuildQualitySettings\(\)/);
  assert.match(quality, /E_QUALITY_STATE_MISMATCH/);

  const pipelineRead = pipeline.indexOf("if (string.IsNullOrEmpty(r.RenderPipeline))");
  const pipelineCommit = pipeline.indexOf("ctx.CommitEffect()");
  assert.ok(pipelineRead >= 0 && pipelineCommit > pipelineRead, "pipeline pure read must return before commit");
  assert.match(pipeline, /AssetPathToGUID\(pipelinePath\)/);
  assert.match(pipeline, /AssetDatabase\.ImportAsset\(pipelinePath, ImportAssetOptions\.ForceUpdate\)/);
  assert.match(pipeline, /ReloadAsset<RenderPipelineAsset>\(pipelinePath, pipelineGuid\)/);
  assert.match(pipeline, /var observedPipeline = GraphicsSettings\.defaultRenderPipeline/);
  assert.match(pipeline, /E_RENDER_PIPELINE_STATE_MISMATCH/);

  assert.match(group, /var mutates = createdObject \|\| addComponent \|\| positions != null;/);
  assert.match(reflection, /var mutates = createdObject \|\| addComponent \|\| hasMode \|\| r\.Resolution\.HasValue \|\| r\.Bounces\.HasValue \|\| r\.Hdr\.HasValue \|\| boxSize\.HasValue \|\| boxOffset\.HasValue;/);
  for (const text of [group, reflection]) {
    assert.match(text, /if \(mutates\)[\s\S]*ctx\.CommitEffect\(\)/);
    assert.match(text, /UnityCliSceneMutation/);
    assert.match(text, /ResolveUniqueTarget\(go\.GetInstanceID\(\)\.ToString\(CultureInfo\.InvariantCulture\)/);
  }
  assert.match(group, /var observedPositions = observedProbe == null \? null : observedProbe\.probePositions/);
  assert.match(group, /SamePositions\(observedPositions, positions\)/);
  assert.match(group, /E_LIGHT_PROBE_GROUP_STATE_MISMATCH/);

  const reflectionBefore = reflection.slice(0, reflection.indexOf("ctx.CommitEffect()"));
  assert.match(reflectionBefore, /E_REFLECTION_PROBE_RESOLUTION_INVALID/);
  assert.match(reflectionBefore, /E_REFLECTION_PROBE_BOUNCES_INVALID/);
  assert.match(reflection, /var observed = GraphicsToolSupport\.BuildReflectionProbeInfo\(observedProbe\)/);
  assert.match(reflection, /E_REFLECTION_PROBE_STATE_MISMATCH/);
});
