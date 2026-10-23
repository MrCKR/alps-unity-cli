import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editor = new URL("../unitypkg/Editor/", import.meta.url);
const source = (path: string) => readFile(new URL(path, editor), "utf8");

test("content mutations validate before commit and prove fresh bytes or pixels", async () => {
  const [textureCreate, textureModify, sprite, assetCreate, assetModify, shaderCreate, shaderUpdate] = await Promise.all([
    source("Tools/texture/create.cs"), source("Tools/texture/modify.cs"), source("Tools/texture/sprite.cs"),
    source("Tools/asset/create.cs"), source("Tools/asset/write-text.cs"), source("Tools/shader/create.cs"), source("Tools/shader/update.cs")
  ]);
  assert.ok(textureCreate.indexOf("ValidateTextureSettings") < textureCreate.indexOf("ctx.CommitEffect()"));
  assert.match(textureModify, /VerifyPixels\(path, x, y, width, height, color\)/);
  assert.match(textureModify, /ImageConversion\.LoadImage\(observed, File\.ReadAllBytes/);
  assert.ok(sprite.indexOf("ValidateSpriteRequest") < sprite.indexOf("ctx.CommitEffect()"));
  for (const text of [assetCreate, assetModify, shaderCreate, shaderUpdate]) {
    assert.match(text, /File\.ReadAllText\(AssetToolSupport\.AssetFullPath\(path\)\)/);
    assert.ok(text.indexOf("ctx.CommitEffect()") < text.indexOf("File.ReadAllText"));
  }
});

test("serialized-object patches are validate-only with fresh readback", async () => {
  const [patch, objectModify, componentModify] = await Promise.all([
    source("Infrastructure/UnityCliObjectPatch.cs"), source("Tools/serialized-object/patch.cs"), source("Tools/component/modify.cs")
  ]);
  assert.match(patch, /ValidateOnly\(UnityEngine\.Object target, IList pathPatches, object mergePatch\)/);
  assert.match(patch, /E_OBJECT_PATCH_UNSUPPORTED/);
  assert.match(patch, /ApplyVerifiedSerializedPatches/);
  assert.match(patch, /VerifyFreshSerializedProperties/);
  for (const text of [objectModify, componentModify]) {
    assert.ok(text.indexOf("ObjectPatch.ValidateOnly") < text.indexOf("ctx.CommitEffect()"));
    assert.match(text, /VerifyFreshSerializedProperties/);
  }
});

test("uGUI creation fresh-resolves requested parent, rect, and components", async () => {
  const [canvas, button, image, text, panel] = await Promise.all([
    source("Tools/ui/canvas-create.cs"), source("Tools/ui/button-create.cs"), source("Tools/ui/image-create.cs"), source("Tools/ui/text-create.cs"), source("Tools/ui/update-panel-settings.cs")
  ]);
  for (const item of [canvas, button, image, text]) {
    assert.match(item, /EditorUtility\.InstanceIDToObject/);
    assert.match(item, /READBACK_FAILED/);
  }
  for (const item of [button, image, text]) {
    assert.match(item, /observedGo\.transform\.parent != parent\.transform/);
    assert.match(item, /observedRect/);
  }
  assert.ok(panel.indexOf("E_UI_PANEL_SETTINGS_SCALE_MODE_INVALID") < panel.indexOf("ctx.CommitEffect()"));
});

test("UI Toolkit mutations commit only for real writes and prove from frozen identities", async () => {
  const [modify, create, attach, detach, panel] = await Promise.all([
    source("Tools/ui/modify-visual-element.cs"), source("Tools/ui/document-create.cs"), source("Tools/ui/attach-document.cs"), source("Tools/ui/detach-document.cs"), source("Tools/ui/update-panel-settings.cs")
  ]);
  assert.match(modify, /var hasRequestedMutation =/);
  assert.match(modify, /if \(!dryRun && hasRequestedMutation\)\s*\{\s*ctx\.CommitEffect\(\)/);
  assert.match(modify, /else if \(!hasRequestedMutation\)[\s\S]*EditorUtility\.InstanceIDToObject\(targetId\)/);
  for (const text of [create, attach, detach]) {
    assert.match(text, /var \w+Id = \w+\.GetInstanceID\(\)/);
    assert.match(text, /EditorUtility\.InstanceIDToObject\(/);
    assert.equal((text.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  }
  assert.ok(attach.indexOf("E_UI_DOCUMENT_SOURCE_NOT_FOUND") < attach.indexOf("ctx.CommitEffect()"));
  assert.match(panel, /var nextScaleMode =/);
  assert.match(panel, /observed\.scaleMode != nextScaleMode/);
  assert.match(panel, /observed\.referenceResolution != nextResolution/);
  assert.match(panel, /observed\.sortingOrder != nextSortingOrder/);
  assert.doesNotMatch(panel, /observed\.scaleMode != panel\.scaleMode/);
});
