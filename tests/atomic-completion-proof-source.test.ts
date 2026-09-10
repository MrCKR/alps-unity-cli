import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function readTool(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

test("atomic mutations return only independently observed completion state", async () => {
  const [bakeClear, moveObject, activeTool, selection, moduleEnable, setActive] = await Promise.all([
    readTool("Tools/rendering/lighting/bake/clear.cs"),
    readTool("Tools/scene/game-object/move-to-scene.cs"),
    readTool("Tools/editor/active-tool/set.cs"),
    readTool("Tools/editor/selection/set.cs"),
    readTool("Tools/profiler/module-enable.cs"),
    readTool("Tools/scene/set-active.cs")
  ]);

  assert.match(bakeClear, /E_GRAPHICS_REALTIME_CLEAR_UNPROVABLE/);
  assert.match(bakeClear, /Lightmapping\.lightingDataAsset == null/);
  assert.match(bakeClear, /E_GRAPHICS_BAKED_CLEAR_STATE_MISMATCH/);
  assert.doesNotMatch(bakeClear, /ClearedRealtimeData = r\.RealtimeData/);

  assert.match(moveObject, /var observed = EditorUtility\.InstanceIDToObject\(gameObjectId\) as GameObject/);
  assert.match(moveObject, /var observedScenePath = observed == null \? null : observed\.scene\.path/);
  assert.match(moveObject, /var observedSceneHandle = observed == null \? 0 : observed\.scene\.handle/);
  assert.match(moveObject, /observedScenePath != destScene\.path \|\| observedSceneHandle != destScene\.handle/);
  assert.match(moveObject, /E_SCENE_MOVE_STATE_MISMATCH/);

  assert.match(activeTool, /var observedTool = UnityEditor\.Tools\.current/);
  assert.match(activeTool, /observedTool != editorTool/);
  assert.match(activeTool, /E_EDITOR_ACTIVE_TOOL_STATE_MISMATCH/);

  assert.match(selection, /var selected = Selection\.activeObject/);
  assert.match(selection, /selected\.GetInstanceID\(\) != obj\.GetInstanceID\(\)/);
  assert.match(selection, /E_EDITOR_SELECTION_STATE_MISMATCH/);

  assert.match(moduleEnable, /setAreaMethod == null \|\| getAreaMethod == null/);
  assert.match(moduleEnable, /var isEnabled = \(bool\)getAreaMethod\.Invoke/);
  assert.match(moduleEnable, /isEnabled != request\.Enabled/);
  assert.match(moduleEnable, /E_PROFILER_MODULE_STATE_MISMATCH/);

  assert.match(setActive, /var observedScene = SceneManager\.GetActiveScene\(\)/);
  assert.match(setActive, /observedScene\.path != scene\.path \|\| observedScene\.handle != scene\.handle/);
  assert.match(setActive, /E_SCENE_ACTIVE_STATE_MISMATCH/);
});
