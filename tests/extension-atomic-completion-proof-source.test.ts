import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

test("extension atomic mutations return observed state or fail closed", async () => {
  const [urpSupport, urpAdd, urpSet, hdrpSupport, hdrpAdd, hdrpSet, particleClear, bevel] = await Promise.all([
    source("../extensions/urp/unitypkg/Editor/Urp/UrpToolSupport.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/volume-add-effect.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/volume-set.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/HdrpToolSupport.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/volume-add-effect.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/volume-set.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-clear.cs"),
    source("../extensions/probuilder/unitypkg/Editor/ProBuilder/bevel-edges.cs")
  ]);

  for (const [support, addTool, setTool] of [[urpSupport, urpAdd, urpSet], [hdrpSupport, hdrpAdd, hdrpSet]] as const) {
    assert.match(support, /(?:VolumeProfile\)\?\.components\.FirstOrDefault|observedProfile\?\.components\.FirstOrDefault)/);
    assert.match(support, /ObservedEffectType = observed\.GetType\(\)\.FullName/);
    assert.match(support, /(?:ObservedEffectType = effect\.GetType\(\)\.FullName|ObservedEffectType = observedEffect\.GetType\(\)\.FullName)/);
    assert.match(support, /_VOLUME_VERIFY_FAILED/);
    assert.match(addTool, /EffectType = added\.ObservedEffectType/);
    assert.match(setTool, /EffectType = set\.ObservedEffectType/);
    assert.doesNotMatch(addTool, /EffectType = request\.EffectType/);
    assert.doesNotMatch(setTool, /EffectType = request\.EffectType/);
  }

  assert.match(particleClear, /var withChildren = request\.WithChildren \?\? true/);
  assert.match(particleClear, /particleSystem\.Clear\(withChildren\)/);
  assert.match(particleClear, /await VfxActionToolSupport\.RequireParticleState\(particleSystem, withChildren, system => system\.particleCount == 0/);

  assert.match(bevel, /beforeVertexCount = ProBuilderReflection\.GetVertexCount\(mesh\)/);
  assert.match(bevel, /freshMesh = ProBuilderOperationProof\.ResolveFreshMesh/);
  assert.match(bevel, /afterVertexCount = ProBuilderReflection\.GetVertexCount\(freshMesh\)/);
  assert.match(bevel, /afterFaceCount = ProBuilderReflection\.GetFaceCount\(freshMesh\)/);
  assert.match(bevel, /afterEdgeCount = ProBuilderReflection\.GetEdgeCount\(freshMesh\)/);
  assert.match(bevel, /Bevel did not produce observable mesh topology changes/);
});
