import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const editorUrl = new URL("../unitypkg/Editor/", import.meta.url);

async function source(path: string): Promise<string> {
  return readFile(new URL(path, editorUrl), "utf8");
}

function body(text: string): string {
  return text.slice(text.indexOf("ExecuteAsync"));
}

test("supported dry-runs bypass direct proof refusal while graphics reads remain journal-free", async () => {
  const coordinator = await source("Infrastructure/UnityCliOperationCoordinator.cs");
  assert.match(coordinator, /contract\.DeliveryMode, "direct-terminal"/);
  assert.match(coordinator, /RequestRequiresMutation[\s\S]*request\.Execution\.DryRun == true\)\) return false/);
  assert.match(coordinator, /name == "lighting.reflection-probe"/);
  assert.match(coordinator, /name == "lighting.light-probe-group"/);
  assert.match(coordinator, /name == "physics\.rigidbody"/);
  assert.doesNotMatch(coordinator, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN/);
});

test("core request predicates cross one barrier before their first mutation", async () => {
  const paths = [
    "Tools/game-object/duplicate.cs", "Tools/game-object/modify.cs", "Tools/material/assign.cs", "Tools/material/set.cs",
    "Tools/camera/target.cs", "Tools/camera/lens.cs", "Tools/animator/stop.cs", "Tools/asset/duplicate.cs",
    "Tools/asset/move.cs", "Tools/prefab/create.cs"
  ];
  const texts = await Promise.all(paths.map(source));
  for (const text of texts.filter(text => !text.includes("MaterialAssignTool"))) {
    const execute = body(text);
    assert.equal((execute.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1, "exactly one commit barrier: " + text);
  }

  const [duplicate, modify, assign, set, target, lens, stop, assetDuplicate, move, prefab] = texts.map(body);
  assert.ok(duplicate.indexOf("ctx.CommitEffect()") < duplicate.indexOf("Instantiate"));
  assert.ok(modify.indexOf("ResolveModifyTargets") < modify.indexOf("ctx.CommitEffect()"));
  assert.match(modify, /EditorUtility\.InstanceIDToObject\(plan\.Id\)/);
  assert.ok(assign.indexOf("ctx.CommitEffect()") < assign.indexOf("RecordObject"));
  assert.ok(set.indexOf("ctx.CommitEffect()") < set.indexOf("ApplyMaterialValue(material"));
  assert.ok(target.indexOf("ParseVector3") < target.indexOf("ctx.CommitEffect()"));
  assert.ok(lens.indexOf("ParseCameraClearFlags") < lens.indexOf("ctx.CommitEffect()"));
  assert.ok(stop.indexOf("ctx.CommitEffect()") < stop.indexOf("animator.Rebind"));
  assert.ok(assetDuplicate.indexOf("ctx.CommitEffect()") < assetDuplicate.indexOf("CreateAssetDirectory"));
  assert.ok(move.indexOf("ctx.CommitEffect()") < move.indexOf("CreateAssetDirectory"));
  assert.ok(prefab.indexOf("ResolveGameObjectFirstOrDefault") < prefab.indexOf("ctx.CommitEffect()"));
  assert.match(assetDuplicate, /GUIDToAssetPath\(destinationGuid\)/);
  assert.match(move, /GUIDToAssetPath\(sourceGuid\)/);
  assert.match(prefab, /GUIDToAssetPath\(outputGuid\)/);
});
