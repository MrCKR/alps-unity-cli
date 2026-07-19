import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function source(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

test("named core atomic tools prevalidate, commit, and return independently observed state", async () => {
  const [assign, collision, force, material, jointAdd, simulation, prefabCreate, instantiate, unpack, create, load, save, unload, frame, resolution, preflight] = await Promise.all([
    source("Tools/physics/assign-material.cs"),
    source("Tools/physics/collision-matrix.cs"),
    source("Tools/physics/force.cs"),
    source("Tools/physics/material.cs"),
    source("Tools/physics/joint/add.cs"),
    source("Tools/physics/simulation.cs"),
    source("Tools/prefab/create.cs"),
    source("Tools/prefab/instantiate.cs"),
    source("Tools/prefab/unpack.cs"),
    source("Tools/scene/create.cs"),
    source("Tools/scene/load.cs"),
    source("Tools/scene/save.cs"),
    source("Tools/scene/unload.cs"),
    source("Tools/scene/view-frame.cs"),
    source("Tools/editor/resolution.cs"),
    source("Tools/diagnose/preflight.cs"),
  ]);

  assert.match(assign, /EditorUtility\.InstanceIDToObject\(colliderId\) as Collider/);
  assert.match(assign, /var observedMaterial = observedCollider == null \? null : observedCollider\.sharedMaterial/);
  assert.match(assign, /E_PHYSICS_ASSIGN_MATERIAL_STATE_MISMATCH/);
  assert.match(collision, /Physics\.GetIgnoreLayerCollision\(layerA, layerB\) != request\.Ignore\.Value/);
  assert.match(collision, /E_PHYSICS_COLLISION_MATRIX_STATE_MISMATCH/);
  assert.match(force, /var beforeVelocity = rigidbody\.velocity/);
  assert.match(force, /var rigidbodyId = rigidbody\.GetInstanceID\(\)/);
  assert.match(force, /candidate\.GetInstanceID\(\) == rigidbodyId/);
  assert.match(force, /var observedVelocity = observedRigidbody\.velocity/);
  assert.match(force, /E_PHYSICS_FORCE_STATE_UNPROVABLE/);
  assert.doesNotMatch(force, /Framed = true/);
  assert.match(material, /var observedMaterial = AssetDatabase\.LoadAssetAtPath<PhysicMaterial>\(path\)/);
  assert.match(material, /E_PHYSICS_MATERIAL_STATE_MISMATCH/);
  assert.match(jointAdd, /EditorUtility\.InstanceIDToObject\(targetId\) as GameObject/);
  assert.match(jointAdd, /var observedJoint = observedTarget == null \? null : observedTarget\.GetComponent\(type\) as Joint/);
  assert.match(jointAdd, /E_PHYSICS_JOINT_ADD_STATE_MISMATCH/);
  assert.match(simulation, /E_PHYSICS_SIMULATION_PROOF_UNAVAILABLE/);

  assert.match(prefabCreate, /var observedCopy = string\.IsNullOrEmpty\(observedPath\) \? null : PrefabToolSupport\.LoadPrefabAsset\(observedPath\)/);
  assert.match(prefabCreate, /E_PREFAB_CREATE_PROOF_MISSING/);
  assert.match(instantiate, /var observedInstance = EditorUtility\.InstanceIDToObject/);
  assert.match(instantiate, /E_PREFAB_INSTANTIATE_STATE_MISMATCH/);
  assert.match(unpack, /PrefabUtility\.IsPartOfPrefabInstance\(gameObject\)/);
  assert.match(unpack, /E_PREFAB_UNPACK_STATE_MISMATCH/);

  assert.match(create, /var observedScene = SceneManager\.GetActiveScene\(\)/);
  assert.match(create, /E_SCENE_CREATE_STATE_MISMATCH/);
  assert.match(load, /E_SCENE_LOAD_STATE_MISMATCH/);
  assert.match(save, /E_SCENE_SAVE_STATE_MISMATCH/);
  assert.match(unload, /E_SCENE_UNLOAD_STATE_MISMATCH/);
  assert.match(frame, /E_SCENE_VIEW_FRAME_STATE_MISMATCH/);
  assert.match(frame, /E_SCENE_VIEW_FRAME_PROOF_UNAVAILABLE/);
  assert.match(resolution, /var observedWidth = PlayerSettings\.defaultScreenWidth/);
  assert.match(resolution, /E_EDITOR_RESOLUTION_STATE_MISMATCH/);

  assert.doesNotMatch(preflight, /ctx\.CommitEffect\(\)/);
  assert.doesNotMatch(preflight, /DiscoverProjectTools\(\)/);
  assert.doesNotMatch(preflight, /DiscoverProjectContexts\(\)/);
  assert.doesNotMatch(preflight, /SideEffects\s*=/);
});

test("shared undo and redo do not report direct business success without attribution", async () => {
  const [undo, redo] = await Promise.all([
    source("Tools/editor/undo.cs"),
    source("Tools/editor/redo.cs"),
  ]);

  assert.match(undo, /E_UNDO_BUSINESS_PROOF_UNAVAILABLE/);
  assert.match(redo, /E_REDO_BUSINESS_PROOF_UNAVAILABLE/);
  assert.doesNotMatch(undo, /return new Result \{ Performed = true/);
  assert.doesNotMatch(redo, /return new Result \{ Performed = true/);
});
