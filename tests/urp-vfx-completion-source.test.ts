import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

test("URP and VFX mutations validate inputs before commit and return observed state", async () => {
  const [settings, volumes, linePositions, graphCreate, templateCreate, vfxSupport, trailCreate, trailSet, particleCreate, particleMain, particleEmission, particleShape] = await Promise.all([
    source("../extensions/urp/unitypkg/Editor/Urp/settings-set.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/UrpToolSupport.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/line-positions.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/graph-create.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/template-create.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/VfxToolSupport.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/trail-create.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/trail-set.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-create.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-main.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-emission.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-shape.cs")
  ]);

  assert.ok(settings.indexOf("ValidateSetting") < settings.indexOf("ctx.CommitEffect()"));
  assert.match(volumes, /ValidateAddEffect/);
  assert.match(volumes, /ValidateSet/);
  assert.equal((linePositions.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.ok(linePositions.indexOf("ctx.CommitEffect()") < linePositions.indexOf("line.positionCount = positions.Length"));
  assert.ok(linePositions.indexOf("ctx.CommitEffect()") < linePositions.indexOf("line.SetPosition"));

  for (const sourceText of [graphCreate, templateCreate]) {
    assert.match(sourceText, /AssetDatabase\.LoadAssetAtPath\([^,]+, graphType\)/);
    assert.match(sourceText, /created == null \|\| !graphType\.IsInstanceOfType\(created\)/);
  }
  assert.match(vfxSupport, /SHA256\.Create\(\)/);
  assert.match(vfxSupport, /public string Hash/);
  assert.match(vfxSupport, /VerifyParticleMainFields/);
  assert.match(vfxSupport, /VerifyParticleEmissionFields/);
  assert.match(vfxSupport, /VerifyParticleShapeFields/);
  assert.match(trailCreate, /UnityCliProjectToolContext ctx/);
  assert.match(trailSet, /UnityCliProjectToolContext ctx/);
  assert.doesNotMatch(trailCreate, /\bcontext\./);
  assert.doesNotMatch(trailSet, /\bcontext\./);
  assert.match(particleCreate, /VerifyParticleMainFields\(observed, request\)/);
  assert.match(particleMain, /VerifyParticleMainFields\(observed, request\)/);
  assert.match(particleEmission, /VerifyParticleEmissionFields/);
  assert.match(particleShape, /VerifyParticleShapeFields/);
});

test("URP Volume plans inspect only non-mutating profiles and mutate a fresh runtime profile", async () => {
  const [support, set, create] = await Promise.all([
    source("../extensions/urp/unitypkg/Editor/Urp/UrpToolSupport.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/volume-set.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/volume-create.cs"),
  ]);
  const validate = support.slice(support.indexOf("ValidateSet(string target"), support.indexOf("Set(VolumeSetMutationPlan plan"));
  const mutate = support.slice(support.indexOf("Set(VolumeSetMutationPlan plan"));
  assert.match(validate, /GetProfileForPlanning\(volume\)/);
  assert.doesNotMatch(validate, /GetVolumeProfile\(/);
  assert.match(support, /m_InternalProfile/);
  assert.match(support, /PlanningProfileId/);
  assert.match(support, /SharedProfileId/);
  assert.doesNotMatch(support, /public static SetVolumeResult Set\(string target/);
  assert.match(mutate, /var mutationProfile = UrpToolSupport\.GetVolumeProfile/);
  assert.match(mutate, /mutationEffect.*component\.GetType\(\) == plan\.EffectType/);
  assert.match(mutate, /mutationProperty.*plan\.PropertyType/);
  assert.match(mutate, /observedProfile\.GetInstanceID\(\) != mutationProfile\.GetInstanceID\(\)/);
  assert.match(create, /var plan = UrpVolumeOperations\.ValidateCreate/);
  assert.match(create, /Create\(request\.Name, request\.IsGlobal, plan/);
  assert.match(support, /Convert\.ToSingle\(observedPriority.*plan\.Priority\.Value/);
  assert.match(support, /Convert\.ToSingle\(observedWeight.*plan\.Weight\.Value/);
});

test("URP feature handlers preflight index, type, and property before committing", async () => {
  const [add, toggle, configure, remove, support] = await Promise.all([
    source("../extensions/urp/unitypkg/Editor/Urp/feature-add.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/feature-toggle.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/feature-configure.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/feature-remove.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/UrpFeatureToolSupport.cs")
  ]);
  for (const sourceText of [add, toggle, configure, remove]) {
    assert.equal((sourceText.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
    assert.ok(sourceText.indexOf("Validate") < sourceText.indexOf("ctx.CommitEffect()"));
  }
  assert.match(add, /var plan = UrpFeatureToolSupport\.ValidateAdd/);
  assert.match(add, /UrpFeatureToolSupport\.Add\(plan,/);
  assert.match(toggle, /var plan = UrpFeatureToolSupport\.ValidateToggle/);
  assert.match(toggle, /UrpFeatureToolSupport\.Toggle\(plan,/);
  assert.match(support, /ValidateFeatureCollectionForAdd\(serialized\)/);
  assert.ok(support.indexOf("ValidateFeatureCollectionForAdd(serialized)") < support.indexOf("AssetDatabase.AddObjectToAsset"));
  assert.match(support, /m_RendererFeatures is not an editable serialized feature collection/);
  assert.match(support, /m_RendererFeatureMap is not an editable long serialized collection/);
  assert.match(support, /GetMethod\("SetActive", BindingFlags\.Instance \| BindingFlags\.Public, null, new\[\] \{ typeof\(bool\) \}, null\)/);
  assert.match(support, /active\.propertyType != SerializedPropertyType\.Boolean \|\| !active\.editable/);
  assert.match(support, /IsExactFeature\(observed, featureGuid, featureLocalId, plan\.FeatureType\)/);
  assert.match(support, /IsExactFeature\(observed, plan\.FeatureGuid, plan\.FeatureLocalId, plan\.FeatureType\)/);
});
