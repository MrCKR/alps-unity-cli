import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

test("stable target predicate is owned by UnityCliWaiter", async () => {
  const waiter = await source("../unitypkg/Editor/Infrastructure/UnityCliWaiter.cs");

  assert.match(waiter, /UntilStable\(Func<bool> predicate, TimeSpan timeout, int minimumFrames = 2, TimeSpan minimumDuration = default, CancellationToken ct = default\)/);
  assert.match(waiter, /class StablePredicateWait/);
  assert.match(waiter, /consecutiveFrames >= minimumFrames/);
  assert.match(waiter, /utcNow - stableSinceUtc\.Value >= minimumDuration/);
  assert.match(waiter, /cancellationToken\.Register/);
});

test("observed-state tools wait stably and construct from final readback", async () => {
  const [animationPlay, animationStop, bakeStart, bakeCancel, editorPause, editorPlay, editorStop, profilerStart, profilerStop, profilerPause, frameEnable, frameDisable] = await Promise.all([
    source("../unitypkg/Editor/Tools/animation/play.cs"),
    source("../unitypkg/Editor/Tools/animation/stop.cs"),
    source("../unitypkg/Editor/Tools/graphics/bake-start.cs"),
    source("../unitypkg/Editor/Tools/graphics/bake-cancel.cs"),
    source("../unitypkg/Editor/Tools/editor/pause.cs"),
    source("../unitypkg/Editor/Tools/editor/play.cs"),
    source("../unitypkg/Editor/Tools/editor/stop.cs"),
    source("../unitypkg/Editor/Tools/profiler/start.cs"),
    source("../unitypkg/Editor/Tools/profiler/stop.cs"),
    source("../unitypkg/Editor/Tools/profiler/pause.cs"),
    source("../unitypkg/Editor/Tools/profiler/frame-debugger/enable.cs"),
    source("../unitypkg/Editor/Tools/profiler/frame-debugger/disable.cs")
  ]);

  for (const text of [animationPlay, animationStop, bakeStart, bakeCancel, editorPause, editorPlay, editorStop, profilerStart, profilerStop, profilerPause, frameEnable, frameDisable]) {
    assert.match(text, /Wait\.UntilStable\(/);
  }
  assert.match(animationPlay, /var finalAnimator = AnimationToolSupport\.BuildAnimatorInfo/);
  assert.match(animationStop, /var finalAnimator = AnimationToolSupport\.BuildAnimatorInfo/);
  assert.match(bakeStart, /var baking = Lightmapping\.isRunning/);
  assert.match(bakeCancel, /var baking = Lightmapping\.isRunning/);
  assert.match(profilerStart, /var observed = ProfilerToolSupport\.BuildProfilerState\(\)/);
  assert.match(profilerStop, /var observed = ProfilerToolSupport\.BuildProfilerState\(\)/);
  assert.match(profilerPause, /var observed = ProfilerToolSupport\.BuildProfilerState\(\)/);
  assert.match(frameEnable, /var eventCount = FrameDebuggerReflection\.GetEventCount\(\)/);
});

test("VFX actions use stable observable state and profiler artifacts retain external proof", async () => {
  const [vfxSupport, controlPause, controlPlay, controlReinitialize, controlStop, particlePause, particlePlay, particleStop, capture, exportTool, load, assetRefresh, menu] = await Promise.all([
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/VfxActionToolSupport.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-pause.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-play.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-reinitialize.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/control-stop.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-pause.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-play.cs"),
    source("../extensions/vfx-graph/unitypkg/Editor/Vfx/particle-stop.cs"),
    source("../unitypkg/Editor/Tools/profiler/capture.cs"),
    source("../unitypkg/Editor/Tools/profiler/export.cs"),
    source("../unitypkg/Editor/Tools/profiler/load.cs"),
    source("../unitypkg/Editor/Tools/asset/refresh.cs"),
    source("../unitypkg/Editor/Tools/editor/menu.cs")
  ]);

  assert.match(vfxSupport, /context\.Wait\.UntilStable\(/);
  assert.match(controlPause, /await VfxActionToolSupport\.RequirePauseState/);
  assert.match(controlPlay, /await VfxActionToolSupport\.RequirePauseState/);
  for (const text of [controlReinitialize, controlStop, particlePause, particlePlay, particleStop]) assert.match(text, /await VfxActionToolSupport\.Require/);
  assert.match(capture, /WriteAndVerifyJsonProfilerArtifact/);
  assert.match(exportTool, /WriteAndVerifyJsonProfilerArtifact/);
  assert.match(load, /InputSha256/);
  assert.match(load, /ProfilerToolSupport\.FileSha256/);
  assert.match(load, /Wait\.UntilStable/);
  assert.match(assetRefresh, /asset\.refresh requires one asset path/);
  assert.match(assetRefresh, /Wait\.UntilStable/);
  assert.doesNotMatch(assetRefresh, /AssetDatabase\.Refresh\(/);
  assert.match(menu, /if \(!saveScene\)/);
  assert.match(menu, /Wait\.UntilStable/);
});
