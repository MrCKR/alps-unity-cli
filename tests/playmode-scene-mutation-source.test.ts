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

test("go.delete verifies synchronous PlayMode destruction before reporting success", async () => {
  const deleteSource = await source("unitypkg/Editor/Tools/go/delete.cs");
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
    "unitypkg/Editor/Tools/go/create.cs",
    "unitypkg/Editor/Tools/go/delete.cs",
    "unitypkg/Editor/Tools/go/duplicate.cs",
    "unitypkg/Editor/Tools/go/look-at.cs",
    "unitypkg/Editor/Tools/go/modify.cs",
    "unitypkg/Editor/Tools/go/move-relative.cs",
    "unitypkg/Editor/Tools/go/set-parent.cs",
    "unitypkg/Editor/Tools/material/set-renderer-color.cs",
    "unitypkg/Editor/Tools/material/assign.cs",
    "unitypkg/Editor/Tools/controller/assign.cs",
    "unitypkg/Editor/Tools/graphics/probe-positions.cs",
    "unitypkg/Editor/Tools/graphics/light-probe-group.cs",
    "unitypkg/Editor/Tools/graphics/reflection-probe.cs",
    "unitypkg/Editor/Tools/graphics/skybox-set-material.cs",
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
  assert.match(prefab, /EditorApplication\.isPlaying/);
  assert.match(prefab, /UnityEngine\.Object\.Instantiate\(prefab\)/);
  assert.match(prefab, /PrefabUtility\.InstantiatePrefab/);

  const objectModify = await source("unitypkg/Editor/Tools/object/modify.cs");
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
    if (!/PlayModeSupport = "(?:supported|session-affecting)"/.test(value)) continue;
    assert.doesNotMatch(value, unsafe, relative(rootPath, file));
  }
});

test("temporary EditMode cleanup descriptors avoid shared Undo probing", async () => {
  const deleteSource = await source("unitypkg/Editor/Tools/go/delete.cs");
  assert.match(deleteSource, /EditMode stays dirty, and final PlayMode proof requires reading editor\.state/);
  assert.match(deleteSource, /call scene\.load once with the exact active scene path/);
  assert.match(deleteSource, /do not probe or repeat editor\.undo/);
  assert.match(deleteSource, /effects\.playModeChanged is a per-call side-effect delta, not the current mode/);
  assert.match(deleteSource, /read the editor\.state Context after cleanup/);

  const undoSource = await source("unitypkg/Editor/Tools/editor/undo.cs");
  assert.match(undoSource, /exactly one entry from the shared Unity Editor Undo stack/);
  assert.match(undoSource, /performed=true proves Unity raised undoRedoPerformed for this operation/);
  assert.match(undoSource, /Undo\.undoRedoPerformed \+= callback[\s\S]*Undo\.PerformUndo\(\)[\s\S]*Undo\.undoRedoPerformed -= callback[\s\S]*if \(!performed\)/);
  assert.match(undoSource, /SideEffects = new\[\] \{ "undo-redo-replay", "undo-stack" \}/);
  assert.match(undoSource, /use scene\.load once with the exact active scene path instead/);
});

test("persistent writes are explicitly editor-only", async () => {
  const paths = [
    "unitypkg/Editor/Tools/editor/undo.cs", "unitypkg/Editor/Tools/editor/redo.cs",
    "unitypkg/Editor/Tools/asset/create.cs", "unitypkg/Editor/Tools/texture/create.cs", "unitypkg/Editor/Tools/shader/create.cs",
    "unitypkg/Editor/Tools/scriptable-object/create.cs", "unitypkg/Editor/Tools/material/create.cs", "unitypkg/Editor/Tools/physics/material.cs",
    "unitypkg/Editor/Tools/animation/clip/create.cs", "unitypkg/Editor/Tools/animation/animator/set-parameter.cs", "unitypkg/Editor/Tools/controller/create.cs",
    "unitypkg/Editor/Tools/graphics/settings.cs", "unitypkg/Editor/Tools/graphics/quality.cs", "unitypkg/Editor/Tools/graphics/pipeline-quality.cs", "unitypkg/Editor/Tools/graphics/skybox-set-properties.cs",
    "unitypkg/Editor/Tools/prefab/create.cs", "unitypkg/Editor/Tools/scene/save.cs",
    "unitypkg/Editor/Tools/build/settings/set.cs", "unitypkg/Editor/Tools/project/layer/add.cs",
    "extensions/urp/unitypkg/Editor/Urp/settings-set.cs", "extensions/hdrp/unitypkg/Editor/Hdrp/settings-set.cs"
  ];

  for (const path of paths) {
    assert.match(await source(path), /PlayModeSupport = "editor-only"/, path);
  }
});
