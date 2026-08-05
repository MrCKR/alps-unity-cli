import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

function section(source: string, start: string, end: string): string {
  const first = source.indexOf(start);
  const last = source.indexOf(end, first);
  assert.ok(first >= 0 && last > first, `missing source section ${start}`);
  return source.slice(first, last);
}

test("Cinemachine lens validates every request field before commit and reads each field from a re-resolved camera", async () => {
  const lens = await source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/lens.cs");
  const beforeCommit = section(lens, "if (!request.FieldOfView.HasValue", "ctx.CommitEffect()");
  for (const field of ["FieldOfView", "NearClipPlane", "FarClipPlane", "OrthographicSize", "Dutch"]) {
    assert.match(beforeCommit, new RegExp(`request\\.${field}`));
  }
  assert.match(beforeCommit, /E_CINEMACHINE_LENS_RANGE_INVALID/);
  const afterCommit = lens.slice(lens.indexOf("ctx.CommitEffect()"));
  assert.match(afterCommit, /ResolveRequiredTarget\(gameObject\.GetInstanceID\(\)\.ToString/);
  for (const field of ["FieldOfView", "NearClipPlane", "FarClipPlane", "OrthographicSize", "Dutch"]) {
    assert.match(afterCommit, new RegExp(`VerifyLensFloat\\(observed, "${field}"`));
  }
});

test("Cinemachine priority and blend parse request values before commit and read back each requested field", async () => {
  const [priority, blend] = await Promise.all([
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/priority.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/blend.cs"),
  ]);
  const priorityBefore = section(priority, "var effects", "ctx.CommitEffect()");
  assert.match(priorityBefore, /E_CINEMACHINE_PRIORITY_INPUT_REQUIRED/);
  assert.match(priorityBefore, /request\.Priority\.HasValue/);
  assert.match(priorityBefore, /request\.Enabled\.HasValue/);
  const priorityAfter = priority.slice(priority.indexOf("ctx.CommitEffect()"));
  assert.match(priorityAfter, /ResolveRequiredTarget\(gameObject\.GetInstanceID\(\)\.ToString/);
  assert.match(priorityAfter, /value\.intValue != request\.Priority\.Value/);
  assert.match(priorityAfter, /enabled\.boolValue != request\.Enabled\.Value/);

  const blendBefore = section(blend, "var effects", "ctx.CommitEffect()");
  assert.match(blendBefore, /E_CINEMACHINE_BLEND_INPUT_REQUIRED/);
  assert.match(blendBefore, /E_CINEMACHINE_BLEND_STYLE_INVALID/);
  assert.match(blendBefore, /E_CINEMACHINE_BLEND_RANGE_INVALID/);
  const blendAfter = blend.slice(blend.indexOf("ctx.CommitEffect()"));
  assert.match(blendAfter, /ResolveRequiredTarget\(brainOwner\.GetInstanceID\(\)\.ToString/);
  assert.match(blendAfter, /style\.enumValueIndex != requestedStyle\.Value/);
  assert.match(blendAfter, /Mathf\.Approximately\(time\.floatValue/);
});

test("Cinemachine brain ensure proves the component from a newly resolved owner", async () => {
  const brain = await source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/brain-ensure.cs");
  const afterCommit = brain.slice(brain.indexOf("ctx.CommitEffect()"));
  assert.match(afterCommit, /ResolveRequiredTarget\(owner\.GetInstanceID\(\)\.ToString/);
  assert.match(afterCommit, /RequireBrainComponent\(observedOwner\)/);
  assert.match(afterCommit, /BuildBrainInfo\(observedBrain\)/);
});

test("Cinemachine body, aim, noise, and create prevalidate typed requests and prove fresh terminal state", async () => {
  const [support, body, aim, noise, create] = await Promise.all([
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/CinemachineToolSupport.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/body.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/aim.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/noise.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/create.cs"),
  ]);

  for (const [tool, stage] of [[body, "Body"], [aim, "Aim"]] as const) {
    const before = tool.slice(0, tool.indexOf("ctx.CommitEffect()"));
    const after = tool.slice(tool.indexOf("ctx.CommitEffect()"));
    assert.match(before, new RegExp(`PrepareComponentMember\\(${stage.toLowerCase()}Type`));
    assert.match(after, /ResolveRequiredTarget\(gameObject\.GetInstanceID\(\)\.ToString\(\)/);
    assert.match(after, new RegExp(`GetPipelineComponent\\(observedCamera, "${stage}"\\)`));
    assert.match(after, /VerifyComponentMember\(observed/);
    assert.match(after, /_VERIFY_FAILED/);
  }

  const noiseBefore = noise.slice(0, noise.indexOf("ctx.CommitEffect()"));
  const noiseAfter = noise.slice(noise.indexOf("ctx.CommitEffect()"));
  assert.match(noiseBefore, /GetPipelineComponent\(camera, "Noise"\)/);
  assert.match(noiseBefore, /E_CINEMACHINE_NOISE_REMOVE_CONFLICT/);
  assert.match(noiseBefore, /E_CINEMACHINE_NOISE_PROFILE_NOT_FOUND/);
  assert.match(noiseBefore, /PrepareFirstComponentMember\(noiseType/);
  assert.match(noiseAfter, /SwapPipelineComponent\(gameObject, camera, "Noise", noiseType\)/);
  assert.match(noiseAfter, /GetPipelineComponent\(observedCamera, "Noise"\)/);
  assert.match(noiseAfter, /VerifyComponentMember\(observedNoise/);

  const createBefore = create.slice(0, create.indexOf("ctx.CommitEffect()"));
  const createAfter = create.slice(create.indexOf("ctx.CommitEffect()"));
  assert.match(createBefore, /RequirePipelineComponentStage\(bodyType, "Body"\)/);
  assert.match(createBefore, /RequirePipelineComponentStage\(aimType, "Aim"\)/);
  assert.match(createBefore, /PrepareFirstComponentObjectMember\(CinemachineToolSupport\.CameraType, follow\.transform/);
  assert.match(createBefore, /RequirePriorityWritable\(CinemachineToolSupport\.CameraType\)/);
  assert.match(createAfter, /ResolveRequiredTarget\(gameObject\.GetInstanceID\(\)\.ToString/);
  assert.match(createAfter, /observedObject\.transform\.parent/);
  assert.match(createAfter, /observedObject\.transform\.position - expectedPosition/);
  assert.match(createAfter, /VerifyComponentMember\(observedCamera, followPlan/);
  assert.match(createAfter, /actualPriority\.Value != request\.Priority\.Value/);
  assert.match(createAfter, /GetPipelineComponent\(observedCamera, "Body"\)/);
  assert.match(createAfter, /GetPipelineComponent\(observedCamera, "Aim"\)/);

  assert.match(support, /Enum\.IsDefined\(stageEnumType, stage\)/);
  assert.match(support, /Enum\.Parse\(stageEnumType, stage\)/);
  assert.doesNotMatch(support, /string\.Equals\(stage, "Body"[\s\S]{0,80}"Aim"/);
});

test("Cinemachine target and extensions plan writable members and verify fresh IDs", async () => {
  const [target, add, remove, support] = await Promise.all([
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/target.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/extension-add.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/extension-remove.cs"),
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/CinemachineToolSupport.cs"),
  ]);
  const targetBefore = target.slice(0, target.indexOf("ctx.CommitEffect()"));
  assert.match(targetBefore, /PrepareComponentObjectMember\(camera\.GetType\(\), "Follow"/);
  assert.match(targetBefore, /PrepareComponentObjectMember\(camera\.GetType\(\), "LookAt"/);
  assert.match(target, /VerifyComponentMember\(observed, followPlan/);
  assert.match(target, /GetInstanceID\(\)\.ToString\(System\.Globalization\.CultureInfo\.InvariantCulture\)/);
  for (const text of [add, remove]) {
    assert.match(text, /var targetId = target\.GetInstanceID\(\)/);
    assert.match(text, /Contains\(targetId, type, out var observedTarget\)/);
  }
  assert.match(support, /PrepareComponentObjectMember/);
  assert.match(support, /"m_" \+ memberName/);
  assert.match(support, /value < -float\.MaxValue \|\| value > float\.MaxValue/);
});

test("Cinemachine, URP, and HDRP scalar conversions reject non-finite floats and undefined enums", async () => {
  const [cinemachine, urp, hdrp] = await Promise.all([
    source("../extensions/cinemachine/unitypkg/Editor/Cinemachine/CinemachineToolSupport.cs"),
    source("../extensions/urp/unitypkg/Editor/Urp/UrpToolSupport.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/HdrpToolSupport.cs"),
  ]);

  assert.match(cinemachine, /RequireFinite\(parsed, "component"\)/);
  assert.match(cinemachine, /Enum\.IsDefined\(targetType, parsed\)/);
  for (const support of [urp, hdrp]) {
    assert.match(support, /ParseFiniteSingle\(value, ".* setting"\)/);
    assert.match(support, /double\.IsNaN\(parsed\) \|\| double\.IsInfinity\(parsed\) \|\| parsed < -float\.MaxValue \|\| parsed > float\.MaxValue/);
    assert.match(support, /Enum\.IsDefined\(enumType, parsed\)/);
    assert.match(support, /ParseSerializedEnumIndex\(property, value,/);
    assert.match(support, /property\.floatValue = ParseFiniteSingle/);
    assert.match(support, /property\.enumValueIndex = ParseSerializedEnumIndex/);
  }
});

test("HDRP settings and volume mutations use independently acquired pipeline, profile, effect, and serialized readback", async () => {
  const [settings, support, create] = await Promise.all([
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/settings-set.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/HdrpToolSupport.cs"),
    source("../extensions/hdrp/unitypkg/Editor/Hdrp/volume-create.cs"),
  ]);
  assert.match(settings, /PrepareSetting\(pipeline, request\.Property, request\.Value\)/);
  assert.match(settings, /ApplySetting\(plan\)/);
  assert.match(settings, /VerifySettingReadback\(plan/);
  assert.doesNotMatch(settings.slice(settings.indexOf("ctx.CommitEffect()")), /ConvertValue\(request\.Value/);
  assert.match(settings, /AssetDatabase\.LoadAssetAtPath<UnityEngine\.Object>\(pipelinePath\)/);
  assert.match(support, /VerifySettingReadback\(SettingMutationPlan plan, UnityEngine\.Object pipeline\)/);
  assert.match(support, /using \(var serialized = new SerializedObject\(pipeline\)\) \{ serialized\.Update\(\);/);
  assert.match(support, /ResolveUniqueTarget\(gameObject\.GetInstanceID\(\)\.ToString/);
  assert.match(support, /var observedProfile = observedVolume == null \? null : HdrpToolSupport\.GetVolumeProfile\(observedVolume\)/);
  assert.match(support, /var observedProfile = HdrpToolSupport\.GetVolumeProfile\(observedObject\.GetComponent/);
  assert.match(support, /using \(var observed = new SerializedObject\(observedEffect\)\)/);
  assert.match(support, /VolumeSetMutationPlan/);
  assert.match(support, /Set\(VolumeSetMutationPlan plan/);
  assert.match(support, /ValidateSet\(string target/);
  assert.match(support, /plan\.ExpectedReadback/);
  const set = support.slice(support.indexOf("Set(VolumeSetMutationPlan plan"));
  assert.doesNotMatch(support, /public static SetVolumeResult Set\(string target/);
  assert.match(support, /GetProfileForPlanning\(volumeComponent\)/);
  assert.match(support, /m_InternalProfile/);
  // sharedProfile 是 Core RP 14 的 public 字段：规划读回必须字段优先，否则 PlayMode 重载后永远命中不了。
  assert.match(support, /GetField\("sharedProfile", BindingFlags\.Instance \| BindingFlags\.Public \| BindingFlags\.NonPublic\)/);
  assert.match(support, /var sharedProperty = s_volumeType\.GetProperty\("sharedProfile"/);
  assert.match(set, /var mutationProfile = HdrpToolSupport\.GetVolumeProfile/);
  assert.match(set, /mutationProfile\.components\.FirstOrDefault/);
  assert.match(set, /plan\.SharedProfileId/);
  assert.match(set, /mutationSerialized\.FindProperty\(plan\.PropertyPath\)/);
  assert.match(set, /observed\.FindProperty\(plan\.PropertyPath\)/);
  assert.match(create, /var plan = HdrpVolumeOperations\.ValidateCreate/);
  assert.match(create, /Create\(request\.Name, request\.IsGlobal, plan/);
  assert.match(support, /Convert\.ToSingle\(observedPriority.*plan\.Priority\.Value/);
  assert.match(support, /Convert\.ToSingle\(observedWeight.*plan\.Weight\.Value/);
});
