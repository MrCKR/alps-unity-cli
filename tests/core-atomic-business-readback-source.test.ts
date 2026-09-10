import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function source(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

test("core atomic business mutations return readback state rather than requested values", async () => {
  const [enabled, speed, parameter, assign, weight, material, rigidbody, settings, sprite] = await Promise.all([
    source("Tools/animator/set-enabled.cs"),
    source("Tools/animator/set-speed.cs"),
    source("Tools/animator/set-parameter.cs"),
    source("Tools/animator-controller/assign.cs"),
    source("Tools/animator-controller/set-layer-weight.cs"),
    source("Tools/material/assign.cs"),
    source("Tools/physics/rigidbody.cs"),
    source("Tools/physics/settings.cs"),
    source("Tools/texture/sprite.cs")
  ]);

  assert.match(enabled, /var observedEnabled = observedAnimator != null && observedAnimator\.enabled/);
  assert.match(enabled, /E_ANIMATOR_ENABLED_STATE_MISMATCH/);
  assert.match(enabled, /Enabled = observedEnabled/);

  assert.match(speed, /var observedTarget = EditorUtility\.InstanceIDToObject\(go\.GetInstanceID\(\)\) as GameObject/);
  assert.match(speed, /var observedAnimator = observedTarget == null \? null : observedTarget\.GetComponent<Animator>\(\)/);
  assert.match(speed, /var observedSpeed = observedAnimator == null \? 0f : observedAnimator\.speed/);
  assert.match(speed, /Mathf\.Approximately\(observedSpeed, speed\)/);
  assert.match(speed, /Speed = observedSpeed/);

  assert.match(parameter, /var observedController = AnimationToolSupport\.ReloadControllerAsset\(AssetDatabase\.GetAssetPath\(ac\)\)/);
  assert.match(parameter, /foreach \(var candidate in observedController\.parameters\)/);
  assert.match(parameter, /DefaultValueMatches\(observedParameter, r\.Value\)/);
  assert.match(parameter, /E_ANIMATOR_PARAMETER_STATE_MISMATCH/);

  assert.match(assign, /var observedTarget = EditorUtility\.InstanceIDToObject\(go\.GetInstanceID\(\)\) as GameObject/);
  assert.match(assign, /var observedAnimator = observedTarget == null \? null : observedTarget\.GetComponent<Animator>\(\)/);
  assert.match(assign, /var observedController = observedAnimator == null \? null : observedAnimator\.runtimeAnimatorController/);
  assert.match(assign, /observedController != controller \|\| observedPath != path/);
  assert.match(assign, /ControllerPath = observedPath/);

  assert.match(weight, /var observedController = AnimationToolSupport\.ReloadControllerAsset\(path, controllerGuid\)/);
  assert.match(weight, /E_LAYER_WEIGHT_INVALID/);
  assert.ok(weight.indexOf("E_LAYER_WEIGHT_INVALID") < weight.indexOf("ctx.CommitEffect()"));
  assert.match(weight, /var observedLayers = observedController == null \? null : observedController\.layers/);
  assert.match(weight, /Mathf\.Approximately\(observedLayers\[idx\]\.defaultWeight, weight\)/);
  assert.match(weight, /Weight = observedLayers\[idx\]\.defaultWeight/);

  assert.match(material, /var observedRenderer = observedTarget == null \? null : observedTarget\.GetComponent<Renderer>\(\)/);
  assert.match(material, /var observedMaterials = observedRenderer == null \? null : observedRenderer\.sharedMaterials/);
  assert.match(material, /AssetDatabase\.AssetPathToGUID\(AssetDatabase\.GetAssetPath\(observedMaterials\[slot\]\)\) != materialGuid/);
  assert.match(material, /observedProperty\.GetValue\(observedGraphic, null\) as Material/);
  assert.match(material, /E_MATERIAL_ASSIGN_STATE_MISMATCH/);

  assert.match(rigidbody, /var observedTarget = EditorUtility\.InstanceIDToObject\(targetId\) as GameObject/);
  assert.match(rigidbody, /var observedRigidbody = observedTarget == null \? null : observedTarget\.GetComponent<Rigidbody>\(\)/);
  assert.match(rigidbody, /E_PHYSICS_RIGIDBODY_STATE_MISMATCH/);
  assert.match(rigidbody, /Rigidbody = PhysicsToolSupport\.BuildRigidbodyInfo\(observedRigidbody\)/);

  assert.match(settings, /var immutablePlans = plans\.AsReadOnly\(\)/);
  assert.match(settings, /foreach \(var plan in immutablePlans\) plan\.Apply\(\)/);
  assert.match(settings, /if \(!plan\.ReadbackMatches\(\)\)/);
  assert.match(settings, /\(\) => Physics\.gravity == value/);
  assert.match(settings, /E_PHYSICS_SETTINGS_STATE_MISMATCH/);

  assert.match(sprite, /var observedImporter = TextureToolSupport\.RequireTextureImporter\(path\)/);
  assert.match(sprite, /var observedSlices = observedImporter\.spritesheet/);
  assert.match(sprite, /SliceCount = observedSlices\.Length/);
  assert.match(sprite, /E_TEXTURE_SPRITE_STATE_MISMATCH/);
});

test("UI and ScriptableObject asset mutations commit only after validation and return reload readback", async () => {
  const [documentCreate, visualElement, scriptableCreate, scriptableModify, button, image, text] = await Promise.all([
    source("Tools/ui/document/create.cs"),
    source("Tools/ui/modify-visual-element.cs"),
    source("Tools/scriptable-object/create.cs"),
    source("Tools/scriptable-object/modify.cs"),
    source("Tools/ui/button/create.cs"),
    source("Tools/ui/image/create.cs"),
    source("Tools/ui/text/create.cs"),
  ]);

  assert.ok(documentCreate.indexOf("ValidateTextAssetCreation") < documentCreate.indexOf("ctx.CommitEffect()"));
  assert.ok(documentCreate.indexOf("ctx.CommitEffect()") < documentCreate.indexOf("EnsureTextAsset(uxmlPath"));
  assert.ok(visualElement.indexOf("E_UI_DISPLAY_INVALID") < visualElement.indexOf("ctx.CommitEffect()"));
  assert.ok(visualElement.indexOf("E_UI_ELEMENT_TEXT_UNSUPPORTED") < visualElement.indexOf("ctx.CommitEffect()"));

  assert.match(scriptableCreate, /AssetDatabase\.ImportAsset\(path, ImportAssetOptions\.ForceUpdate\);[\s\S]*LoadAssetAtPath<ScriptableObject>\(path\)/);
  assert.match(scriptableModify, /PrepareObjectMemberWrite\(asset, property, request\.Value\)[\s\S]*ctx\.CommitEffect\(\)/);
  assert.match(scriptableModify, /AssetDatabase\.ImportAsset\(path, ImportAssetOptions\.ForceUpdate\);[\s\S]*AssetPathToGUID\(path\)[\s\S]*GUIDToAssetPath\(assetGuid\)[\s\S]*ObjectMemberValueMatches/);
  assert.match(scriptableModify, /observedSerialized\.Update\(\);[\s\S]*VerifyObservedPatches\(observedSerialized, patchItems\)/);
  assert.match(scriptableModify, /E_SCRIPTABLE_OBJECT_PATCH_READBACK_FAILED/);
  for (const sourceText of [button, image, text]) {
    assert.match(sourceText, /ctx\.CommitEffect\(\);[\s\S]*GetComponent/);
    assert.match(sourceText, /READBACK_FAILED/);
  }
});

test("adjacent core tools expose observed business objects after mutation", async () => {
  const [buildSettings, importSettings, objectModify, componentSet, gameObjectModify] = await Promise.all([
    source("Tools/build/settings/set.cs"),
    source("Tools/texture/import-settings.cs"),
    source("Tools/serialized-object/patch.cs"),
    source("Tools/component/set.cs"),
    source("Tools/game-object/modify.cs")
  ]);

  assert.match(buildSettings, /var result = BuildSettingsToolSupport\.Read\(\)/);
  assert.match(importSettings, /TextureToolSupport\.LoadTextureAsset\(path\)/);
  assert.match(importSettings, /BuildTextureImportSettings\(TextureToolSupport\.RequireTextureImporter\(path\)\)/);
  assert.match(objectModify, /ObjectSerializer\.Serialize\(observedTarget, options, readBudget\)/);
  assert.match(componentSet, /GameObjectToolSupport\.BuildComponentSummary\(observedComponent\)/);
  assert.match(gameObjectModify, /GameObjectToolSupport\.BuildGameObjectDetail\(observed\)/);
  assert.match(gameObjectModify, /GameObject = details\[0\], ModifiedCount = 1/);
  assert.match(gameObjectModify, /ModifiedCount = details\.Count/);
});
