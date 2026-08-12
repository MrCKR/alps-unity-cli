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
  assert.match(force, /rigidbody\.Sleep\(\)/);
  assert.match(force, /if \(!rigidbody\.IsSleeping\(\)\)/);
  assert.match(force, /if \(observedRigidbody\.IsSleeping\(\)\)/);
  assert.doesNotMatch(force, /Physics\.Simulate/);
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
  assert.match(simulation, /\[UnityCliRequired\] public UnityCliCompletionProbe CompletionProbe/);
  assert.match(simulation, /completion = ctx\.RunCompletionProbe\(request\.CompletionProbe/);
  assert.doesNotMatch(simulation, /Physics\.Simulate\(deltaTime\)[\s\S]*ctx\.CommitEffect\(\)/);

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
  assert.match(frame, /ReadTargetBounds/);
  assert.match(frame, /ReadAllBounds/);
  assert.match(frame, /GeometryUtility\.CalculateFrustumPlanes/);
  assert.match(frame, /BoundsContained/);
  assert.match(frame, /ctx\.Wait\.NextFrame/);
  assert.match(frame, /ctx\.Wait\.UntilStable/);
  assert.match(frame, /SameBounds/);
  assert.match(frame, /target-bounds-already-contained/);
  assert.match(frame, /E_EXECUTION_TIMEOUT/);
  assert.match(resolution, /var observedWidth = PlayerSettings\.defaultScreenWidth/);
  assert.match(resolution, /E_EDITOR_RESOLUTION_STATE_MISMATCH/);

  assert.doesNotMatch(preflight, /ctx\.CommitEffect\(\)/);
  assert.doesNotMatch(preflight, /DiscoverProjectTools\(\)/);
  assert.doesNotMatch(preflight, /DiscoverProjectContexts\(\)/);
  assert.doesNotMatch(preflight, /SideEffects\s*=/);
});

test("shared undo and redo require request-specific read Tool proof", async () => {
  const [undo, redo, bridge] = await Promise.all([
    source("Tools/editor/undo.cs"),
    source("Tools/editor/redo.cs"),
    readFile(new URL("../src/bridge/domains/editor.js", import.meta.url), "utf8"),
  ]);

  for (const [name, tool] of [["UNDO", undo], ["REDO", redo]] as const) {
    assert.match(tool, /\[UnityCliRequired\] public UnityCliCompletionProbe CompletionProbe/);
    assert.match(tool, /ctx\.RunCompletionProbe\(/);
    assert.match(tool, new RegExp(`E_${name}_NOT_PERFORMED`));
    assert.match(tool, /Completion = completion/);
    assert.doesNotMatch(tool, /BUSINESS_PROOF_UNAVAILABLE/);
  }
  assert.match(bridge, /command === "editor\.undo" \|\| command === "editor\.redo"/);
  assert.match(bridge, /completionProbe: structuredInput\(flags\)\.completionProbe/);
});
