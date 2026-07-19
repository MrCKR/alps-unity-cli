import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

function beforeCommit(sourceText: string): string {
  const commit = sourceText.indexOf("ctx.CommitEffect()");
  assert.ok(commit > 0, "missing commit barrier");
  return sourceText.slice(0, commit);
}

test("Cinemachine stage and priority predicates precede commit", async () => {
  const [support, body, aim, priority] = await Promise.all([
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/CinemachineToolSupport.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/body.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/aim.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/priority.cs"),
  ]);

  assert.match(support, /Unity\.Cinemachine\.CinemachineCamera/);
  assert.match(support, /Cinemachine\.CinemachineVirtualCamera/);
  assert.match(support, /E_CINEMACHINE_COMPONENT_STAGE_MISMATCH/);
  assert.match(support, /CinemachineTransposer/);
  assert.match(support, /CinemachineComposer/);
  assert.match(beforeCommit(body), /RequirePipelineComponentStage\(bodyType, "Body"\)/);
  assert.match(beforeCommit(aim), /RequirePipelineComponentStage\(aimType, "Aim"\)/);
  assert.match(priority, /RequirePriorityEnabledWritable/);
  assert.match(priority, /GetInstanceID\(\)\.ToString\(CultureInfo\.InvariantCulture\)/);
});

test("URP and HDRP volume mutations prove request predicates and fresh IDs", async () => {
  const [urp, hdrp, urpCreate, hdrpCreate, urpSettings] = await Promise.all([
    source("../extensions/urp/unitypkg/Editor/Urp/UrpToolSupport.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/HdrpToolSupport.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/volume-create.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/volume-create.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/settings-set.cs"),
  ]);

  for (const support of [urp, hdrp]) {
    assert.match(support, /typeof\(VolumeComponent\)\.IsAssignableFrom\(effectType\)/);
    assert.match(support, /GetMethod\("Add", new\[\] \{ typeof\(Type\), typeof\(bool\) \}\)/);
    assert.match(support, /_VOLUME_EFFECT_EXISTS/);
    assert.match(support, /countAfter != countBefore \+ 1/);
    assert.match(support, /gameObject\.GetInstanceID\(\)\.ToString\(CultureInfo\.InvariantCulture\)/);
  }
  assert.match(beforeCommit(urpCreate), /ValidateCreate\(request\.Priority, request\.Weight, "E_URP"\)/);
  assert.match(beforeCommit(hdrpCreate), /ValidateCreate\(request\.Priority, request\.Weight, "E_HDRP"\)/);
  assert.match(urp, /observedProfile\.GetInstanceID\(\) != profile\.GetInstanceID\(\)/);
  assert.match(hdrp, /observedProfile\.GetInstanceID\(\) != profile\.GetInstanceID\(\)/);
  assert.match(urpSettings, /AssetDatabase\.LoadAssetAtPath<UnityEngine\.Object>\(pipelinePath\)/);
  assert.match(urpSettings, /VerifySettingReadback\(plan, reloadedPipeline\)/);
  assert.match(urp, /using \(var observed = new SerializedObject\(reloadedPipeline\)\)/);
  assert.match(urp, /UrpVolumeOperations\.ReadMember/);
  assert.match(hdrp, /HdrpVolumeOperations\.ReadMember/);
  assert.doesNotMatch(urp, /UrpVolumeToolSupport/);
  assert.doesNotMatch(hdrp, /HdrpVolumeToolSupport/);
});
