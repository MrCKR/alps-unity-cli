import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { join, relative } from "node:path";
import test from "node:test";

const root = new URL("../../", import.meta.url);
const rootPath = fileURLToPath(root);

async function source(path: string): Promise<string> {
  return readFile(new URL(path, root), "utf8");
}

// 递归收集 C# source，确保审计覆盖后续新增的 capability 文件。
async function csFiles(directory: string): Promise<string[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async (entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return csFiles(path);
    return entry.isFile() && entry.name.endsWith(".cs") ? [path] : [];
  }));
  return nested.flat();
}

test("scene mutation helper separates Edit Mode persistence from Play Mode runtime changes", async () => {
  const helper = await source("unitypkg/Editor/Infrastructure/UnityCliSceneMutation.cs");

  assert.match(helper, /EditorApplication\.isPlaying/);
  assert.match(helper, /if \(!IsPlaying && target != null\) Undo\.RecordObject/);
  assert.match(helper, /IsPlaying \? target\.AddComponent/);
  assert.match(helper, /if \(IsPlaying\) UnityEngine\.Object\.DestroyImmediate\(target\)/);
  assert.match(helper, /else Undo\.DestroyObjectImmediate\(target\)/);
  assert.doesNotMatch(helper, /UnityEngine\.Object\.Destroy\(target\)/);
  assert.match(helper, /if \(!IsPlaying && scene\.IsValid\(\)\) EditorSceneManager\.MarkSceneDirty/);
  assert.match(helper, /if \(!IsPlaying && target != null\) EditorUtility\.SetDirty/);
});

test("game-object.delete verifies synchronous PlayMode destruction before reporting success", async () => {
  const deleteSource = await source("unitypkg/Editor/Tools/game-object/delete.cs");
  const destroy = deleteSource.indexOf("UnityCliSceneMutation.DestroyObject(gameObject)");
  const verify = deleteSource.indexOf("if (gameObject != null)");
  const success = deleteSource.indexOf("Destroyed = true");

  assert.match(deleteSource, /public System\.Threading\.Tasks\.ValueTask<Result> ExecuteAsync/);
  assert.doesNotMatch(deleteSource, /async System\.Threading\.Tasks\.ValueTask|ctx\.Wait/);
  assert.ok(destroy >= 0 && verify > destroy && success > verify);
  assert.match(deleteSource, /E_GAMEOBJECT_DELETE_FAILED/);
  assert.match(deleteSource, /Play Mode scene instances are destroyed synchronously/);
  assert.match(deleteSource, /Dry-run returns target with destroyed=false/);
});

test("PlayMode scene tools use the mutation helper instead of unsafe Editor APIs", async () => {
  const paths = [
    "unitypkg/Editor/Tools/camera/create.cs",
    "unitypkg/Editor/Tools/component/add.cs",
    "unitypkg/Editor/Tools/component/modify.cs",
    "unitypkg/Editor/Tools/component/remove.cs",
    "unitypkg/Editor/Tools/component/set.cs",
    "unitypkg/Editor/Tools/game-object/create.cs",
    "unitypkg/Editor/Tools/game-object/delete.cs",
    "unitypkg/Editor/Tools/game-object/duplicate.cs",
    "unitypkg/Editor/Tools/game-object/look-at.cs",
    "unitypkg/Editor/Tools/game-object/modify.cs",
    "unitypkg/Editor/Tools/game-object/move-relative.cs",
    "unitypkg/Editor/Tools/game-object/set-parent.cs",
    "unitypkg/Editor/Tools/material/set-renderer-color.cs",
    "unitypkg/Editor/Tools/material/assign.cs",
    "unitypkg/Editor/Tools/animator-controller/assign.cs",
    "unitypkg/Editor/Tools/rendering/lighting/light-probe-group.cs",
    "unitypkg/Editor/Tools/rendering/lighting/reflection-probe.cs",
    "unitypkg/Editor/Tools/rendering/lighting/skybox/set-material.cs",
    "unitypkg/Editor/Tools/physics/assign-material.cs",
    "unitypkg/Editor/Tools/physics/force.cs",
    "unitypkg/Editor/Tools/physics/joint/add.cs",
    "unitypkg/Editor/Tools/physics/joint/remove.cs"
  ];

  for (const path of paths) {
    const value = await source(path);
    assert.match(value, /UnityCliSceneMutation/, path);
    assert.doesNotMatch(value, /Undo\.|EditorSceneManager\.MarkSceneDirty|EditorUtility\.SetDirty/, path);
  }

  const prefab = await source("unitypkg/Editor/Tools/prefab/instantiate.cs");
  // PlayMode 也必须经 PrefabUtility.InstantiatePrefab 建立 prefab 连接，
  // 否则终态 readback 的 GetCorrespondingObjectFromSource 断言必然把合法调用判成 outcome-unknown。
  assert.match(prefab, /PrefabUtility\.InstantiatePrefab/);
  assert.match(prefab, /GetCorrespondingObjectFromSource/);
  assert.doesNotMatch(prefab, /UnityEngine\.Object\.Instantiate\(prefab\)/);

  const objectModify = await source("unitypkg/Editor/Tools/serialized-object/patch.cs");
  assert.match(objectModify, /E_TOOL_PLAYMODE_UNAVAILABLE/);
  assert.match(objectModify, /EditorUtility\.IsPersistent\(target\)/);
  assert.match(objectModify, /UnityCliSceneMutation\.MarkSceneObjectDirty\(target\)/);

  const componentModify = await source("unitypkg/Editor/Tools/component/modify.cs");
  const persistentGuard = componentModify.indexOf("EditorUtility.IsPersistent(component)");
  const firstMutation = componentModify.indexOf("ObjectPatch.ApplyVerifiedSerializedPatches");
  assert.ok(persistentGuard >= 0 && firstMutation > persistentGuard, "persistent target must be rejected before applying patches");
});

test("supported PlayMode descriptors contain no direct unsafe Editor mutation calls", async () => {
  const files = [
    ...await csFiles(join(rootPath, "unitypkg", "Editor")),
    ...await csFiles(join(rootPath, "extensions"))
  ];
  const unsafe = /Undo\.|EditorSceneManager\.MarkSceneDirty|EditorUtility\.SetDirty|AssetDatabase\.(?:SaveAssets|CreateAsset|DeleteAsset|MoveAsset|ImportAsset|Refresh)/;

  for (const file of files) {
    const value = await readFile(file, "utf8");
    if (!/PlayModeSupport = "supported"/.test(value)) continue;
    assert.doesNotMatch(value, unsafe, relative(rootPath, file));
  }
});

test("retained graphics replacements declare their final PlayMode behavior", async () => {
  const [quality, pipeline, group, reflection] = await Promise.all([
    source("unitypkg/Editor/Tools/rendering/quality.cs"),
    source("unitypkg/Editor/Tools/rendering/pipeline/settings.cs"),
    source("unitypkg/Editor/Tools/rendering/lighting/light-probe-group.cs"),
    source("unitypkg/Editor/Tools/rendering/lighting/reflection-probe.cs"),
  ]);

  for (const value of [quality, pipeline]) {
    assert.match(value, /PlayModeSupport = "session-affecting"/);
  }
  for (const value of [group, reflection]) {
    assert.match(value, /PlayModeSupport = "supported"/);
    assert.match(value, /UnityCliSceneMutation/);
  }
});

test("temporary EditMode cleanup descriptors avoid shared Undo probing", async () => {
  const deleteSource = await source("unitypkg/Editor/Tools/game-object/delete.cs");
  assert.match(deleteSource, /EditMode stays dirty, and final PlayMode proof requires reading editor\.state/);
  assert.match(deleteSource, /call scene\.load once with the exact active scene path/);
  assert.match(deleteSource, /do not probe or repeat editor\.undo/);
  assert.doesNotMatch(deleteSource, /effects\.playModeChanged/);
  assert.match(deleteSource, /read the editor\.state Context after cleanup/);

  const undoSource = await source("unitypkg/Editor/Tools/editor/undo.cs");
  assert.match(undoSource, /Call Undo\.PerformUndo\(\) exactly once/);
  assert.match(undoSource, /completionProbe must identify an eligible read Tool/);
  assert.match(undoSource, /Undo\.undoRedoPerformed \+= callback[\s\S]*ctx\.RunCompletionProbe\([\s\S]*Undo\.PerformUndo\(\)[\s\S]*if \(!performed\)[\s\S]*Undo\.undoRedoPerformed -= callback/);
  assert.match(undoSource, /SideEffects = new\[\] \{ "undo-redo-replay", "undo-stack" \}/);
  assert.doesNotMatch(undoSource, /performed=true proves|business success without attribution/);
});

test("persistent writes are explicitly editor-only", async () => {
  const paths = [
    "unitypkg/Editor/Tools/editor/undo.cs", "unitypkg/Editor/Tools/editor/redo.cs",
    "unitypkg/Editor/Tools/asset/create.cs", "unitypkg/Editor/Tools/texture/create.cs", "unitypkg/Editor/Tools/shader/create.cs",
    "unitypkg/Editor/Tools/scriptable-object/create.cs", "unitypkg/Editor/Tools/material/create.cs", "unitypkg/Editor/Tools/physics/material.cs",
    "unitypkg/Editor/Tools/animation-clip/create.cs", "unitypkg/Editor/Tools/animation-clip/animator/set-parameter.cs", "unitypkg/Editor/Tools/animator-controller/create.cs",
    "unitypkg/Editor/Tools/rendering/lighting/skybox/set-properties.cs",
    "unitypkg/Editor/Tools/prefab/create.cs", "unitypkg/Editor/Tools/scene/save.cs",
    "unitypkg/Editor/Tools/build/settings/set.cs", "unitypkg/Editor/Tools/project-settings/layer/add.cs",
    "extensions/urp/unitypkg/Editor/Urp/settings-set.cs", "extensions/hdrp/unitypkg/Editor/Hdrp/settings-set.cs"
  ];

  for (const path of paths) {
    assert.match(await source(path), /PlayModeSupport = "editor-only"/, path);
  }
});
