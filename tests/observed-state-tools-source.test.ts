import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

test("observed animator and bake tools wait for stable readback", async () => {
  const [play, stop, bakeStart, bakeCancel] = await Promise.all([
    source("../unitypkg/Editor/Tools/animator/play.cs"),
    source("../unitypkg/Editor/Tools/animator/stop.cs"),
    source("../unitypkg/Editor/Tools/lighting/bake/start.cs"),
    source("../unitypkg/Editor/Tools/lighting/bake/cancel.cs")
  ]);

  assert.match(play, /await ctx\.Wait\.UntilStable\([\s\S]*GetCurrentAnimatorStateInfo\(layer\)\.fullPathHash == expectedHash[\s\S]*TimeSpan\.FromMilliseconds\(ctx\.TimeoutMs\)[\s\S]*ct: ct\)/);
  assert.match(play, /E_ANIMATION_STATE_TIMEOUT/);
  assert.match(stop, /animator\.Rebind\(\)/);
  assert.match(stop, /GetCurrentAnimatorStateInfo\(0\)\.fullPathHash == expectedHash/);
  assert.match(stop, /E_ANIMATION_RESET_TIMEOUT/);
  assert.match(bakeStart, /await ctx\.Wait\.UntilStable\(\(\) => Lightmapping\.isRunning, System\.TimeSpan\.FromMilliseconds\(ctx\.TimeoutMs\), ct: ct\)/);
  assert.match(bakeCancel, /await ctx\.Wait\.UntilStable\(\(\) => !Lightmapping\.isRunning, System\.TimeSpan\.FromMilliseconds\(ctx\.TimeoutMs\), ct: ct\)/);
});

test("profiler and frame debugger return only verified session state", async () => {
  const [start, stop, pause, enable, disable, support] = await Promise.all([
    source("../unitypkg/Editor/Tools/profiler/start.cs"),
    source("../unitypkg/Editor/Tools/profiler/stop.cs"),
    source("../unitypkg/Editor/Tools/profiler/pause.cs"),
    source("../unitypkg/Editor/Tools/profiler/frame-debugger/enable.cs"),
    source("../unitypkg/Editor/Tools/profiler/frame-debugger/disable.cs"),
    source("../unitypkg/Editor/Tools/profiler/ProfilerFrameDebuggerSupport.cs")
  ]);

  for (const text of [start, stop, pause]) assert.match(text, /E_PROFILER_STATE_MISMATCH/);
  assert.match(pause, /Paused = !isEnabled/);
  assert.ok(enable.indexOf("E_FRAME_DEBUGGER_REQUIRES_PAUSE") < enable.indexOf("ExecuteMenuItem"));
  assert.match(enable, /var enabled = FrameDebuggerReflection\.GetEnabled\(\)/);
  assert.match(disable, /var enabled = FrameDebuggerReflection\.GetEnabled\(\)/);
  assert.match(support, /EnabledProp/);
  assert.match(support, /GetEnabled\(\)/);
});

test("editor mutations prove terminal state before returning", async () => {
  const [pause, undo, redo] = await Promise.all([
    source("../unitypkg/Editor/Tools/editor/pause.cs"),
    source("../unitypkg/Editor/Tools/editor/undo.cs"),
    source("../unitypkg/Editor/Tools/editor/redo.cs")
  ]);

  assert.match(pause, /var expectedPaused = !before/);
  assert.match(pause, /await ctx\.Wait\.UntilStable\([\s\S]*EditorApplication\.isPaused == expectedPaused/);
  assert.match(pause, /var observedPaused = EditorApplication\.isPaused/);
  assert.match(pause, /E_PLAYMODE_PAUSE_STATE_MISMATCH/);
  assert.match(pause, /IsPaused = observedPaused/);
  for (const [name, text] of [["Undo", undo], ["Redo", redo]] as const) {
    assert.match(text, new RegExp(`Undo\\.undoRedoPerformed \\+= callback[\\s\\S]*ctx\\.RunCompletionProbe\\([\\s\\S]*Undo\\.Perform${name}\\(\\)[\\s\\S]*finally[\\s\\S]*Undo\\.undoRedoPerformed -= callback`));
    assert.match(text, new RegExp(`E_${name.toUpperCase()}_NOT_PERFORMED`));
    assert.match(text, /Completion = completion/);
    assert.doesNotMatch(text, /BUSINESS_PROOF_UNAVAILABLE|Wait\.NextFrame/);
  }
});

test("console, profiler, build settings, and Prefab Stage mutations use readback", async () => {
  const [consoleClear, profilerClear, buildState, buildAdd, buildRemove, stageOpen, stageSave, stageClose] = await Promise.all([
    source("../unitypkg/Editor/Tools/console/clear.cs"),
    source("../unitypkg/Editor/Tools/profiler/clear.cs"),
    source("../unitypkg/Editor/Tools/build/BuildSceneStateTool.cs"),
    source("../unitypkg/Editor/Tools/build/scene/add.cs"),
    source("../unitypkg/Editor/Tools/build/scene/remove.cs"),
    source("../unitypkg/Editor/Tools/prefab/stage-open.cs"),
    source("../unitypkg/Editor/Tools/prefab/stage-save.cs"),
    source("../unitypkg/Editor/Tools/prefab/stage-close.cs")
  ]);

  assert.match(consoleClear, /clearMethod == null \|\| getCountMethod == null/);
  assert.match(consoleClear, /clearMethod\.Invoke\(null, null\)[\s\S]*getCountMethod\.Invoke\(null, null\)/);
  assert.match(consoleClear, /E_CONSOLE_CLEAR_STATE_MISMATCH/);
  assert.match(profilerClear, /BindingFlags\.Static \| BindingFlags\.Public \| BindingFlags\.NonPublic/);
  assert.match(profilerClear, /firstFrameIndex[\s\S]*lastFrameIndex/);
  assert.match(profilerClear, /E_PROFILER_CLEAR_STATE_MISMATCH/);
  assert.doesNotMatch(profilerClear, /try \{ clearMethod\.Invoke\(null, null\); \} catch/);
  assert.match(buildState, /EditorBuildSettings\.scenes = scenes[\s\S]*scenes = ReadScenes\(\)/);
  assert.match(buildState, /FindIndex\(scene => scene\.path == path\)[\s\S]*scenes\[index\]\.enabled != enabled/);
  assert.match(buildAdd, /EditorBuildSettings\.scenes =[\s\S]*scenes = BuildSceneStateTool\.ReadScenes\(\)[\s\S]*scene\.path == path && scene\.enabled/);
  assert.match(buildRemove, /EditorBuildSettings\.scenes =[\s\S]*scenes = BuildSceneStateTool\.ReadScenes\(\)[\s\S]*if \(scenes\.Any\(scene => scene\.path == path\)\)/);
  for (const text of [stageOpen, stageSave, stageClose]) assert.match(text, /PrefabToolSupport\.CurrentPrefabStage\(\)/);
  assert.match(stageOpen, /E_PREFAB_STAGE_OPEN_MISMATCH/);
  assert.match(stageSave, /E_PREFAB_STAGE_SAVE_MISMATCH/);
  assert.match(stageClose, /E_PREFAB_STAGE_CLOSE_MISMATCH/);
});

test("oracle core mutations prevalidate inputs and return fresh proof", async () => {
  const [multiview, create, lens, goSupport, goCreate, goModify, relative, parent, color, instantiate, move, clipCreate, clipModify, animationSupport, buildAdd] = await Promise.all([
    source("../unitypkg/Editor/Tools/camera/multiview.cs"),
    source("../unitypkg/Editor/Tools/camera/create.cs"),
    source("../unitypkg/Editor/Tools/camera/lens.cs"),
    source("../unitypkg/Editor/Tools/game-object/GameObjectToolSupport.cs"),
    source("../unitypkg/Editor/Tools/game-object/create.cs"),
    source("../unitypkg/Editor/Tools/game-object/modify.cs"),
    source("../unitypkg/Editor/Tools/game-object/move-relative.cs"),
    source("../unitypkg/Editor/Tools/game-object/set-parent.cs"),
    source("../unitypkg/Editor/Tools/material/set-renderer-color.cs"),
    source("../unitypkg/Editor/Tools/prefab/instantiate.cs"),
    source("../unitypkg/Editor/Tools/game-object/move-to-scene.cs"),
    source("../unitypkg/Editor/Tools/animation-clip/create.cs"),
    source("../unitypkg/Editor/Tools/animation-clip/update.cs"),
    source("../unitypkg/Editor/Tools/animation-clip/AnimationToolSupport.cs"),
    source("../unitypkg/Editor/Tools/build/scene/add.cs")
  ]);

  assert.match(multiview, /var plans = new List<KeyValuePair<int, string>>\(\)[\s\S]*ctx\.CommitEffect\(\)[\s\S]*E_MULTIVIEW_ARTIFACT_MISSING/);
  assert.match(create, /ValidateFloatRange\(request\.FieldOfView[\s\S]*EditorUtility\.InstanceIDToObject[\s\S]*E_CAMERA_CREATE_STATE_MISMATCH/);
  assert.match(lens, /ValidateFloatRange\(request\.FarClipPlane[\s\S]*float\.MaxValue/);
  assert.match(goSupport, /ValidateTransformMutationValues[\s\S]*parsed >= 0 && parsed <= 31/);
  for (const text of [goCreate, goModify, relative]) assert.match(text, /ValidateTransformMutationValues/);
  assert.match(relative, /var expectedPosition = gameObject\.transform\.position \+ offset[\s\S]*EditorUtility\.InstanceIDToObject\(gameObjectId\)[\s\S]*expectedPosition\.x/);
  assert.match(parent, /newParent == gameObject\.transform \|\| \(newParent != null && newParent\.IsChildOf\(gameObject\.transform\)\)[\s\S]*EditorUtility\.InstanceIDToObject/);
  assert.match(color, /E_MATERIAL_SLOT_INVALID[\s\S]*EditorUtility\.InstanceIDToObject[\s\S]*GetPropertyBlock/);
  assert.match(instantiate, /ValidateTransformMutationValues[\s\S]*AssetDatabase\.AssetPathToGUID\(path\)[\s\S]*GUIDToAssetPath\(sourceGuid\)/);
  assert.match(move, /var gameObjectId = go\.GetInstanceID\(\)[\s\S]*EditorUtility\.InstanceIDToObject\(gameObjectId\)/);
  for (const text of [clipCreate, clipModify]) assert.match(text, /AnimationToolSupport\.ValidateClipSettings/);
  assert.match(animationSupport, /E_ANIMATION_CLIP_TIME_CONFLICT/);
  assert.match(animationSupport, /E_ANIMATION_WRAP_MODE_INVALID/);
  assert.match(buildAdd, /StartsWith\("Assets\/"[\s\S]*path\.Contains\("\.\."\)[\s\S]*LoadAssetAtPath<SceneAsset>/);
});

test("VFX actions use observable terminal state or fail closed", async () => {
  const [actions, pause, play, reinitialize, stop, particlePause, particlePlay, particleStop] = await Promise.all([
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/VfxActionToolSupport.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-pause.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-play.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-reinitialize.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-stop.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-pause.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-play.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-stop.cs")
  ]);

  assert.match(actions, /RequirePauseState/);
  assert.match(actions, /RequireAliveState/);
  assert.match(actions, /aliveParticleCount/);
  assert.match(actions, /await context\.Wait\.UntilStable\([\s\S]*TimeSpan\.FromMilliseconds\(context\.TimeoutMs\)[\s\S]*ct: ct\)/);
  assert.match(pause, /RequirePauseState\(effect, expected/);
  assert.match(play, /InvokeRequired\(effect, "Play"\)/);
  assert.match(play, /count => count > 0/);
  assert.match(stop, /InvokeRequired\(effect, "Stop"\)/);
  assert.match(stop, /count => count == 0/);
  assert.match(reinitialize, /InvokeRequired\(effect, "Reinit"\)/);
  assert.match(reinitialize, /count => count == 0/);
  for (const text of [particlePause, particlePlay, particleStop]) {
    assert.match(text, /await VfxActionToolSupport\.RequireParticleState/);
  }
  assert.match(particlePause, /system => system\.isPaused/);
  assert.match(particlePlay, /system => system\.isPlaying && !system\.isPaused/);
  assert.match(particleStop, /system => !system\.isPlaying && !system\.isEmitting && !system\.isPaused/);
});
