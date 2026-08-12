import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const applyPath = "unitypkg/Editor/Tools/prefab/overrides/apply.cs";
const revertPath = "unitypkg/Editor/Tools/prefab/overrides/revert.cs";
const variantPath = "unitypkg/Editor/Tools/prefab/variant/create.cs";
const supportPath = "unitypkg/Editor/Tools/prefab/PrefabOverrideToolSupport.cs";

test("Prefab override actions have independent atomic Tool owners", async () => {
  const [apply, revert] = await Promise.all([readFile(applyPath, "utf8"), readFile(revertPath, "utf8")]);
  assert.match(apply, /Name = "prefab\.overrides\.apply"/);
  assert.match(revert, /Name = "prefab\.overrides\.revert"/);
  for (const [source, action] of [[apply, "ApplyOverrides"], [revert, "RevertOverrides"]] as const) {
    assert.match(source, /SupportsDryRun = true/);
    assert.match(source, /PlayModeSupport = "editor-only"/);
    assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
    assert.ok(source.indexOf("PrepareOverride") < source.indexOf("ctx.CommitEffect()"));
    assert.ok(source.indexOf("ctx.CommitEffect()") < source.indexOf(action));
    assert.ok(source.indexOf(action) < source.indexOf("ProveOverrides"));
  }
});

test("Prefab apply and revert prove source hash and clean connected instance", async () => {
  const source = await readFile(supportPath, "utf8");
  assert.match(source, /GetOutermostPrefabInstanceRoot/);
  assert.match(source, /GetPrefabAssetPathOfNearestInstanceRoot/);
  assert.match(source, /GetCorrespondingObjectFromSource/);
  assert.match(source, /GetPrefabInstanceStatus\(root\) != PrefabInstanceStatus\.Connected/);
  assert.match(source, /HasPrefabInstanceAnyOverrides\(root, false\)/);
  assert.match(source, /Applying overrides did not change the serialized source prefab/);
  assert.match(source, /Reverting scene overrides unexpectedly changed the source prefab/);
  assert.match(source, /Undo\.RegisterFullObjectHierarchyUndo/);
  assert.match(source, /AssetDatabase\.SaveAssets/);
});

test("Prefab variant uses a cleaned preview scene and proves base identity", async () => {
  const [tool, support] = await Promise.all([readFile(variantPath, "utf8"), readFile(supportPath, "utf8")]);
  assert.match(tool, /Name = "prefab\.variant\.create"/);
  assert.match(tool, /SupportsDryRun = true/);
  assert.equal((tool.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.ok(tool.indexOf("PrepareVariant") < tool.indexOf("ctx.CommitEffect()"));
  assert.ok(tool.indexOf("ctx.CommitEffect()") < tool.indexOf("CreateVariant"));
  assert.ok(tool.indexOf("CreateVariant") < tool.indexOf("ProveVariant"));
  assert.match(support, /EditorSceneManager\.NewPreviewScene/);
  assert.match(support, /finally[\s\S]*?DestroyImmediate[\s\S]*?ClosePreviewScene/);
  assert.match(support, /File\.Exists\(outputFullPath \+ "\.meta"\)/);
  assert.match(support, /E_PREFAB_OUTPUT_META_EXISTS/);
  assert.match(support, /PrefabAssetType\.Variant/);
  assert.match(support, /string\.Equals\(outputGuid, plan\.BaseGuid/);
  assert.match(support, /BaseGuid/);
  assert.match(support, /FileSha256\(plan\.BasePath\)/);
  assert.match(support, /E_PREFAB_VARIANT_PROOF_FAILED/);
});

test("Prefab hash read failures are converted to a stable public error", async () => {
  const support = await readFile(supportPath, "utf8");
  assert.match(support, /catch \(Exception\)[\s\S]*?E_PREFAB_SOURCE_PROOF_UNAVAILABLE/);
  assert.match(support, /The serialized prefab hash could not be read\./);
  assert.doesNotMatch(support, /catch \(Exception exception\)[\s\S]*?exception\.Message/);
});
