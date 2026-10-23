import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const readPath = "unitypkg/Editor/Tools/asset/import-settings.cs";
const setPath = "unitypkg/Editor/Tools/asset/import-settings-set.cs";
const supportPath = "unitypkg/Editor/Tools/asset/AssetImportSettingsToolSupport.cs";

test("asset importer settings expose one read Tool and one atomic write Tool", async () => {
  const [read, set] = await Promise.all([readFile(readPath, "utf8"), readFile(setPath, "utf8")]);
  assert.match(read, /Name = "asset\.import-settings"/);
  assert.match(read, /DestructiveHint = false/);
  assert.match(read, /SideEffects = new string\[0\]/);
  assert.match(read, /PlayModeSupport = "editor-only"/);
  assert.doesNotMatch(read, /CommitEffect|SaveAndReimport|AssetDatabase\.Refresh|AssetDatabase\.ImportAsset/);

  assert.match(set, /Name = "asset\.import-settings-set"/);
  assert.match(set, /SideEffects = new\[\] \{ "asset-meta-write", "asset-import" \}/);
  assert.match(set, /SupportsDryRun = true/);
  assert.match(set, /Permissions = new\[\] \{ "project-asset-write" \}/);
  assert.equal((set.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.ok(set.indexOf("AssetImportSettingsToolSupport.Prepare") < set.indexOf("ctx.CommitEffect()"));
  assert.ok(set.indexOf("ctx.CommitEffect()") < set.indexOf("AssetImportSettingsToolSupport.Apply"));
  assert.ok(set.indexOf("AssetImportSettingsToolSupport.Apply") < set.indexOf("AssetImportSettingsToolSupport.Prove"));
});

test("importer setting support prevalidates all keys and proves fresh GUID and values", async () => {
  const source = await readFile(supportPath, "utf8");
  assert.match(source, /settings\.Count == 0/);
  assert.match(source, /GroupBy\(key => key, StringComparer\.OrdinalIgnoreCase\)/);
  assert.match(source, /E_IMPORT_SETTINGS_CONFLICT/);
  assert.match(source, /Enum\.IsDefined/);
  assert.match(source, /FlagsAttribute/);
  assert.match(source, /IsJsonNumber/);
  assert.match(source, /decimal\.Truncate/);
  assert.doesNotMatch(source, /UnityCliSerializer\.(?:Serialize|Deserialize)/);
  assert.match(source, /issues\.Count > 0/);
  assert.match(source, /no importer state was changed/);
  assert.match(source, /SaveAndReimport\(\)/);
  assert.match(source, /AssetDatabase\.AssetPathToGUID/);
  assert.match(source, /RequireImporter\(plan\.Path\)/);
  assert.match(source, /EnsureExpected/);
  assert.match(source, /E_IMPORT_SETTINGS_PROOF_FAILED/);
  assert.match(source, /ReloadRiskImporterTypes/);
  assert.doesNotMatch(source, /AssetDatabase\.Refresh|AssetDatabase\.ImportAsset/);
});

test("importer settings cover typed default and Texture or Audio platform owners", async () => {
  const source = await readFile(supportPath, "utf8");
  for (const type of ["TextureImporter", "ModelImporter", "AudioImporter", "TextureImporterPlatformSettings", "AudioImporterSampleSettings"])
    assert.match(source, new RegExp(`\\b${type}\\b`));
  for (const platform of ["Default", "Standalone", "iOS", "Android", "WebGL", "tvOS"])
    assert.match(source, new RegExp(`"${platform}"`));
  assert.match(source, /ModelImporter has no per-platform import override/);
  assert.match(source, /overridden=false cannot be combined/);
  assert.match(source, /Dictionary<string, object> Settings/);
  assert.match(source, /Dictionary<string, object> PlatformOverride/);
});
