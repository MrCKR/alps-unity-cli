import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function source(path: string): Promise<string> {
  return readFile(new URL(path, import.meta.url), "utf8");
}

test("targeted observed-state tools use stable fresh readback before returning", async () => {
  const paths = [
    "../unitypkg/Editor/Tools/animation/play.cs",
    "../unitypkg/Editor/Tools/animation/stop.cs",
    "../unitypkg/Editor/Tools/editor/pause.cs",
    "../unitypkg/Editor/Tools/graphics/bake-cancel.cs",
    "../unitypkg/Editor/Tools/graphics/bake-start.cs",
    "../unitypkg/Editor/Tools/profiler/frame-debugger/enable.cs",
    "../unitypkg/Editor/Tools/profiler/frame-debugger/disable.cs",
    "../unitypkg/Editor/Tools/profiler/pause.cs",
    "../unitypkg/Editor/Tools/profiler/start.cs",
    "../unitypkg/Editor/Tools/profiler/stop.cs"
  ];
  for (const path of paths) assert.match(await source(path), /ctx\.Wait\.UntilStable\(/, path);

  const [play, stop, pause] = await Promise.all(paths.slice(0, 3).map(source));
  assert.match(play, /var finalAnimator = AnimationToolSupport\.BuildAnimatorInfo/);
  assert.match(stop, /ctx\.CommitEffect\(\)[\s\S]*UnityCliSceneMutation\.MarkSceneObjectDirty/);
  assert.match(pause, /var observedPaused = EditorApplication\.isPaused/);
});

test("artifact writers reread bytes and return size digest and format proof", async () => {
  const [screenshots, camera, reflection, ui, orbit, surround] = await Promise.all([
    source("../unitypkg/Editor/Tools/screenshot/ScreenshotToolSupport.cs"),
    source("../unitypkg/Editor/Tools/camera/CameraToolSupport.cs"),
    source("../unitypkg/Editor/Tools/graphics/bake-reflection-probe.cs"),
    source("../unitypkg/Editor/Tools/ui/render-preview.cs"),
    source("../unitypkg/Editor/Tools/screenshot/orbit.cs"),
    source("../unitypkg/Editor/Tools/screenshot/surround.cs")
  ]);

  for (const text of [screenshots, camera]) {
    assert.match(text, /File\.ReadAllBytes\(outputPath\)/);
    assert.match(text, /SHA256\.Create\(\)/);
    assert.match(text, /bytes\[0\] == 0x89/);
    assert.match(text, /bytes\[0\] == 0xff/);
  }
  assert.match(screenshots, /class ScreenshotArtifactProof/);
  assert.match(camera, /class CameraArtifactProof/);
  assert.match(reflection, /var bytes = File\.ReadAllBytes\(outputPath\)/);
  assert.match(reflection, /bytes\[0\] == 0x76 && bytes\[1\] == 0x2f && bytes\[2\] == 0x31 && bytes\[3\] == 0x01/);
  assert.match(reflection, /ArtifactPath = outputPath[\s\S]*SizeBytes = bytes\.LongLength[\s\S]*Sha256 = digest/);
  assert.match(ui, /VerifyScreenshotArtifact\(outputPath, "png"\)/);
  assert.match(orbit, /File\.Exists\(candidate\) && !request\.Overwrite/);
  assert.match(orbit, /SizeBytes = proof\.SizeBytes, Sha256 = proof\.Sha256/);
  assert.match(surround, /VerifyScreenshotArtifact\(contactSheetPath, "png"\)/);
});

test("recovery continuations preserve proof-only and never replay a committed mutation", async () => {
  const [coordinator, asset, platform, profile, packageTool, menu] = await Promise.all([
    source("../unitypkg/Editor/Infrastructure/UnityCliOperationCoordinator.cs"),
    source("../unitypkg/Editor/Tools/asset/import.cs"),
    source("../unitypkg/Editor/Tools/build/platform/set.cs"),
    source("../unitypkg/Editor/Tools/build/profile/switch.cs"),
    source("../unitypkg/Editor/Tools/package/embed.cs"),
    source("../unitypkg/Editor/Tools/editor/menu.cs")
  ]);

  assert.match(coordinator, /if \(IsProofRecoveryRequest\(request\.RequestId\)\)[\s\S]*Proof recovery cannot commit the mutation again/);
  assert.match(coordinator, /external-reobserve-v1/);
  assert.match(coordinator, /ReobserveGenericExternalContinuation\(decision\.Continuation\)[\s\S]*PersistOutcomeUnknown/);
  assert.match(coordinator, /File\.ReadAllBytes\(path\)/);
  assert.match(coordinator, /return PersistOutcomeUnknown\(request\.RequestId, true/);
  for (const text of [asset, platform, profile, packageTool]) {
    assert.match(text, /ctx\.ProofOnly/);
    assert.match(text, /RequireProofContinuation\(/);
  }
  assert.match(asset, /FileSha256\(fullPath\)/);
  assert.match(packageTool, /info\.source != UnityEditor\.PackageManager\.PackageSource\.Embedded/);
  assert.match(menu, /if \(!saveScene\)[\s\S]*E_MENU_COMPLETION_UNSUPPORTED/);
  assert.match(menu, /if \(!ctx\.ProofOnly\)[\s\S]*ctx\.CommitEffect\(\)/);
});
