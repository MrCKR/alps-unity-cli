import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";

const toolPath = "unitypkg/Editor/Tools/scene/save.cs";
const bridgePath = "src/bridge/domains/scene.ts";

test("scene.save owns the explicit all-open-dirty-scenes mode", async () => {
  const source = await readFile(toolPath, "utf8");
  assert.match(source, /public bool All \{ get; set; \}/);
  assert.match(source, /Name = "scene\.save"/);
  assert.match(source, /PlayModeSupport = "editor-only"/);
  assert.match(source, /SupportsDryRun = true/);
  assert.doesNotMatch(source, /Name = "scene\.save-all"/);
  assert.match(source, /if \(request\.All\)[\s\S]*?SaveAll\(request, ctx, ct\)/);
});

test("scene.save all freezes only loaded dirty saved scenes before one effect", async () => {
  const source = await readFile(toolPath, "utf8");
  assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1, "both exclusive modes share one barrier owner");
  assert.equal((source.match(/CommitEffect\(ctx\);/g) ?? []).length, 2, "single and all branches each call the shared barrier once");
  assert.match(source, /!scene\.IsValid\(\) \|\| !scene\.isLoaded \|\| !scene\.isDirty/);
  assert.match(source, /E_SCENE_SAVE_ALL_UNTITLED/);
  assert.match(source, /request\.Path != null \|\| request\.Name != null/);
  assert.match(source, /E_SCENE_SAVE_MODE_CONFLICT/);
  assert.ok(source.indexOf("var plans = CaptureDirtyScenes()") < source.indexOf("EditorSceneManager.SaveOpenScenes()"));
  assert.ok(source.indexOf("if (plans.Count == 0)") < source.indexOf("CommitEffect(ctx);", source.indexOf("private static Result SaveAll")));
  assert.ok(source.indexOf("CommitEffect(ctx);", source.indexOf("private static Result SaveAll")) < source.indexOf("EditorSceneManager.SaveOpenScenes()"));
});

test("scene.save all proves every frozen identity and terminal clean state", async () => {
  const source = await readFile(toolPath, "utf8");
  assert.match(source, /FindSceneByHandle\(plan\.Handle\)/);
  assert.match(source, /private static Scene FindSceneByHandle[\s\S]*?SceneManager\.GetSceneAt\(index\)/);
  assert.match(source, /scene\.handle != plan\.Handle/);
  assert.match(source, /!string\.Equals\(path, plan\.Path, StringComparison\.Ordinal\)/);
  assert.match(source, /scene\.isDirty/);
  assert.match(source, /AssetDatabase\.AssetPathToGUID\(path\)/);
  assert.match(source, /AssetDatabase\.GUIDToAssetPath\(guid\)/);
  assert.match(source, /EnsureAllLoadedScenesClean\(\)/);
  assert.match(source, /SavedCount = observed\.Count/);
  assert.match(source, /Scenes = observed/);
});

test("scene.save bridge forwards all only on the save route", async () => {
  const source = await readFile(bridgePath, "utf8");
  assert.match(source, /command: "save"[\s\S]*?buildArgs: sceneSaveArgs/);
  assert.match(source, /all: boolFlag\(context\.flags, "all"\) \?\? boolValue\(structured\.all\)/);
  const spec = bridgeCommandRegistry.lookup("scene", "save");
  assert.ok(spec);
  const built = await spec.buildArgs({ command: "scene.save", flags: { __structuredInput: '{"all":true}' } });
  assert.deepEqual(built, { all: true });
});
