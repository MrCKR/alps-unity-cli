import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function source(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

test("animation, component, and controller mutations prove completion through concrete readback", async () => {
  const [clipCreate, clipModify, enabled, parameter, speed, componentAdd, componentModify, componentRemove, componentSet, controllerCreate, addLayer, addParameter, addState, addTransition, assign, blend1d, blend2d, blendChild, removeLayer, layerWeight] = await Promise.all([
    source("Tools/animation/clip/create.cs"), source("Tools/animation/clip/modify.cs"), source("Tools/animation/animator/set-enabled.cs"), source("Tools/animation/animator/set-parameter.cs"), source("Tools/animation/animator/set-speed.cs"), source("Tools/component/add.cs"), source("Tools/component/modify.cs"), source("Tools/component/remove.cs"), source("Tools/component/set.cs"), source("Tools/controller/create.cs"), source("Tools/controller/add-layer.cs"), source("Tools/controller/add-parameter.cs"), source("Tools/controller/add-state.cs"), source("Tools/controller/add-transition.cs"), source("Tools/controller/assign.cs"), source("Tools/controller/create-blend-tree-1d.cs"), source("Tools/controller/create-blend-tree-2d.cs"), source("Tools/controller/add-blend-tree-child.cs"), source("Tools/controller/remove-layer.cs"), source("Tools/controller/set-layer-weight.cs")
  ]);

  for (const text of [clipCreate, clipModify]) {
    assert.match(text, /ReloadAnimationClipAsset\(path\)/);
    assert.match(text, /E_ANIMATION_CLIP_.*_STATE_MISMATCH/);
  }
  for (const text of [enabled, speed]) {
    assert.match(text, /EditorUtility\.InstanceIDToObject/);
    assert.match(text, /GetComponent<Animator>\(\)/);
    assert.match(text, /STATE_MISMATCH/);
  }
  assert.match(parameter, /var expectedFloat = 0f/);
  assert.match(parameter, /ReloadControllerAsset\(AssetDatabase\.GetAssetPath\(ac\)\)/);
  assert.match(parameter, /DefaultValueMatches\(observedParameter, r\.Value\)/);

  assert.match(componentAdd, /VerifyComponentReadback/);
  assert.match(componentAdd, /new SerializedObject\(observedComponent\)/);
  assert.match(componentSet, /VerifyComponentReadback/);
  assert.match(componentModify, /observedSerializedObject\.Update\(\)/);
  assert.match(componentModify, /E_COMPONENT_MODIFY_STATE_MISMATCH/);
  assert.match(componentRemove, /E_COMPONENT_REMOVE_STATE_MISMATCH/);
  assert.match(componentRemove, /ctx\.CommitEffect\(\);\n                foreach/);

  for (const text of [controllerCreate, addLayer, addParameter, addState, addTransition, blend1d, blend2d]) {
    assert.match(text, /ReloadControllerAsset\(path\)/);
    assert.match(text, /STATE_MISMATCH/);
  }
  for (const text of [removeLayer, layerWeight]) assert.match(text, /STATE_MISMATCH/);
  assert.match(addState, /FindStateInLayer\(observedController\.layers\[layerIndex\]\.stateMachine, r\.StateName\)/);
  assert.match(addLayer, /E_LAYER_WEIGHT_INVALID/);
  assert.ok(addLayer.indexOf("E_LAYER_WEIGHT_INVALID") < addLayer.indexOf("ctx.CommitEffect()"));
  assert.match(addParameter, /E_PARAMETER_DEFAULT_INVALID/);
  assert.ok(addParameter.indexOf("E_PARAMETER_DEFAULT_INVALID") < addParameter.indexOf("ctx.CommitEffect()"));
  assert.match(addTransition, /var fromState = isAnyState \? null : AnimationToolSupport\.FindStateInLayer\(sm, r\.FromState\)/);
  assert.ok(addTransition.indexOf("if (!isAnyState && fromState == null)") < addTransition.indexOf("ctx.CommitEffect()"));
  assert.match(removeLayer, /ReloadControllerAsset\(path, controllerGuid\)/);
  assert.match(removeLayer, /CaptureLayerIdentity\(controller\.layers\[removeIndex\]\)/);
  assert.match(removeLayer, /SameLayerIdentity\(observedIdentity, expectedRemaining\[index\]\)/);
  assert.match(layerWeight, /E_LAYER_WEIGHT_INVALID/);
  assert.ok(layerWeight.indexOf("E_LAYER_WEIGHT_INVALID") < layerWeight.indexOf("ctx.CommitEffect()"));
  assert.match(layerWeight, /ReloadControllerAsset\(path, controllerGuid\)/);
  assert.match(blendChild, /ReloadControllerAsset\(path, controllerGuid\)/);
  assert.match(blendChild, /observedChildren\.Length != beforeChildren\.Length \+ 1/);
  assert.match(blendChild, /observedMatches != beforeMatches \+ 1/);
  assert.match(blendChild, /blendTree\.useAutomaticThresholds = false/);
  assert.ok(blendChild.indexOf("E_BLEND_TREE_CHILD_INPUT_CONFLICT") < blendChild.indexOf("ctx.CommitEffect()"));
  assert.match(assign, /observedTarget = EditorUtility\.InstanceIDToObject/);
  const crossfade = await source("Tools/animation/animator/crossfade.cs");
  assert.match(crossfade, /AnimatorCrossfadeTool/);
  assert.match(crossfade, /FindStateInLayer/);
  assert.match(crossfade, /Animator\.StringToHash\(controllerLayers\[targetLayer\]\.name/);
  assert.match(crossfade, /GetCurrentAnimatorStateInfo/);
  assert.match(crossfade, /GetNextAnimatorStateInfo/);
  assert.match(crossfade, /IsInTransition/);
  assert.match(crossfade, /normalizedTime/);
  assert.match(crossfade, /ctx\.Wait\.UntilStable/);
  assert.match(crossfade, /E_EXECUTION_TIMEOUT/);
  assert.match(crossfade, /ctx\.CommitEffect\(\)/);
});
