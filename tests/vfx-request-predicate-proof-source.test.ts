import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

function afterCommit(sourceText: string): string {
  return sourceText.slice(sourceText.indexOf("ctx.CommitEffect()"));
}

test("VFX request predicates are prepared before commit and proved from fresh observed state", async () => {
  const [support, actionSupport, actions, parameter, clear, particleControls] = await Promise.all([
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/VfxToolSupport.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/VfxActionToolSupport.cs"),
    Promise.all(["trail-create", "trail-set", "line-create", "line-positions", "particle-create", "particle-main", "particle-emission", "particle-shape", "effect-add", "effect-assign", "control-set-rate", "control-set-seed"].map((name) => source(`../extensions/vfx-graph/unitypkg/Editor/Vfx/${name}.cs`))),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/parameter.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-clear.cs"),
    Promise.all(["particle-clear", "particle-pause", "particle-play", "particle-stop"].map((name) => source(`../extensions/vfx-graph/unitypkg/Editor/Vfx/${name}.cs`)))
  ]);

  assert.match(support, /ResolveObservedComponent<T>/);
  assert.match(support, /RequireFinite/);
  assert.match(support, /VerifyLineRendererFields/);
  assert.match(support, /VerifyTrailRendererFields/);
  assert.match(actionSupport, /rootId.*GetInstanceID\(\)/s);
  assert.match(actionSupport, /FindGameObjectByTarget\(rootId\.ToString/s);

  for (const action of actions) {
    const commit = action.indexOf("ctx.CommitEffect()");
    assert.ok(commit > 0, "action must commit");
    assert.match(afterCommit(action), /GetInstanceID\(\)|ResolveObservedComponent|FindGameObjectByTarget/);
  }

  const trailCreate = actions[0];
  assert.ok(trailCreate.indexOf("PrepareTrailRendererFields") < trailCreate.indexOf("ctx.CommitEffect()"));
  assert.ok(trailCreate.indexOf("ctx.CommitEffect()") < trailCreate.indexOf("CreateSceneObject"));

  assert.ok(parameter.indexOf("PrepareParameterApi") < parameter.indexOf("ctx.CommitEffect()"));
  assert.match(parameter, /Has.*Get.*Set/s);
  assert.match(parameter, /ResolveObserved|observedObject/);
  assert.match(clear, /RequireParticleState/);
  for (const action of particleControls) assert.match(action, /using UnityEngine;/);
});

test("VFX Burst, renderer, parameter, and action proofs use bounded fresh observed values", async () => {
  const [support, emission, parameter, actionSupport, controlPause, controlPlay, controlStop, controlReinitialize, trailSet] = await Promise.all([
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/VfxToolSupport.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-emission.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/parameter.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/VfxActionToolSupport.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-pause.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-play.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-stop.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-reinitialize.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/trail-set.cs")
  ]);

  assert.match(support, /value\.Value < -float\.MaxValue/);
  assert.match(support, /PrepareParticleBursts/);
  assert.match(support, /burst\.Count\.Value < 0/);
  assert.match(support, /key\.Time <= previousTime\.Value/);
  assert.match(support, /stop\.Time <= previousTime\.Value/);
  assert.match(support, /colorKeys\.Count < 2/);
  assert.match(support, /UnityEditor\.VisualEffectAssetEditorUtility/);
  assert.doesNotMatch(support, /WriteMinimalVfxGraphYaml|VisualEffectResource|guid: [0-9a-f]{32}/);
  assert.match(support, /emission\.GetBursts\(actual\)/);
  assert.match(support, /CurveEquals/);
  assert.match(support, /GradientEquals/);
  assert.match(support, /Enum\.IsDefined/);
  assert.match(emission, /new ParticleSystem\.Burst\[burstCount\]/);
  assert.equal((emission.match(/emission\.SetBursts\(/g) ?? []).length, 1);
  assert.match(emission, /VerifyParticleEmissionFields\(observed.*request\.Bursts\)/s);
  assert.match(parameter, /GetMethod\("Set" \+ suffix, new\[\] \{ typeof\(string\), valueType \}\)/);
  assert.match(actionSupport, /ResolveObservedEffect/);
  assert.match(actionSupport, /PrepareGraphAssignment/);
  assert.match(actionSupport, /PrepareControlMethod/);
  assert.match(actionSupport, /PrepareWritableBoolProperty/);
  assert.equal((actionSupport.match(/ct: ct\)/g) ?? []).length, 3);
  assert.doesNotMatch(controlPause, /return new System\.Threading\.Tasks\.ValueTask<VfxControlActionResult>/);
  for (const action of [controlPause, controlPlay, controlStop, controlReinitialize]) {
    assert.ok(action.indexOf("Prepare") < action.indexOf("ctx.CommitEffect()"));
    assert.match(afterCommit(action), /ResolveObservedEffect/);
  }
  assert.match(trailSet, /WidthCurve\?\.Count > 0/);
  assert.match(trailSet, /ColorGradient\?\.Count > 0/);
});
