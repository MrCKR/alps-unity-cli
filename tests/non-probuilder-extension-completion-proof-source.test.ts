import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

test("non-ProBuilder extension mutations require observed terminal proof", async () => {
  const [featureSupport, urpSupport, hdrpSupport, urpSettings, hdrpSettings] = await Promise.all([
    source("../extensions/urp/unitypkg/Editor/Urp/UrpFeatureToolSupport.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/UrpToolSupport.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/HdrpToolSupport.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/settings-set.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/settings-set.cs")
  ]);

  assert.match(featureSupport, /TryGetGUIDAndLocalFileIdentifier\(feature, out featureGuid, out featureLocalId\)/);
  assert.match(featureSupport, /FindFeatureIndex\(rendererData, featureGuid, featureLocalId\)/);
  assert.match(featureSupport, /var rendererData = PersistRendererData\(plan\.RendererData\)/);
  assert.match(featureSupport, /var observed = ReadFeature\(rendererData, index\)/);
  assert.match(featureSupport, /!IsExactFeature\(observed, featureGuid, featureLocalId, plan\.FeatureType\)/);
  assert.match(featureSupport, /AssetDatabase\.ImportAsset\(path, ImportAssetOptions\.ForceUpdate\)/);
  assert.match(featureSupport, /ReloadAsset<UnityEngine\.Object>\(path, guid\)/);
  assert.doesNotMatch(featureSupport, /ListRendererFeatures\(\)\.Count - 1/);

  for (const support of [urpSupport, hdrpSupport]) {
    assert.match(support, /ResolveUniqueTarget\(gameObject\.GetInstanceID\(\)\.ToString/);
    assert.match(support, /observedProfile == null \|\| observedProfile\.GetInstanceID\(\) != profile\.GetInstanceID\(\)/);
    assert.match(support, /Created Volume global state did not retain the requested value/);
    assert.match(support, /ReadSerializedPropertyValue\(SerializedProperty property\)/);
  }

  for (const [settings, support, prefix] of [[urpSettings, urpSupport, "URP"], [hdrpSettings, hdrpSupport, "HDRP"]] as const) {
    assert.match(settings, /public string ObservedValue/);
    assert.match(support, new RegExp(`E_${prefix}_SETTING_VERIFY_FAILED`));
    assert.match(settings, /AssetDatabase\.LoadAssetAtPath<UnityEngine\.Object>\(pipelinePath\)/);
    assert.match(settings, /VerifySettingReadback\(plan,/);
    assert.match(support, /using \(var (?:observed|serialized) = new SerializedObject\(/);
    assert.match(settings, /ObservedValue = observedValue/);
  }
});
