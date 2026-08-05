import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

test("merge-objects freezes and proves the exact deterministic ProBuilder terminal state", async () => {
  const [tool, proof] = await Promise.all(["merge-objects.cs", "ProBuilderVertexObjectCompletionProof.cs"].map(name => readFile(new URL(name, root), "utf8")));
  assert.match(tool, /requires every target to already have a ProBuilderMesh component/);
  assert.match(tool, /probuilder\.convert-to-probuilder/);
  assert.doesNotMatch(tool, /AutoConvert|autoConvert/);
  assert.match(tool, /same loaded scene and non-first sources must not have children/);
  assert.match(tool, /FiniteInvertible/);
  assert.match(tool, /CaptureMergeObjects\(targets, name\)[\s\S]*ctx\.CommitEffect\(\)/);
  assert.match(tool, /"invoke"[\s\S]*"readback"[\s\S]*"postcondition"/);
  assert.match(proof, /CaptureMergeObjects[\s\S]*WorldToLocalMatrix\.MultiplyPoint3x4[\s\S]*MaterialSlots/);
  assert.match(proof, /!source\.HasSharedTextures \|\| HasUv1\(source\)/);
  assert.match(tool, /complete sharedTextures, absent UV1/);
  assert.match(proof, /WithEffectiveSlot\(face\.Material, slot\)/, "contributor faces must expect the remapped target material slot");
  assert.match(proof, /HasTriangleAtOrBelowEpsilon[\s\S]*Math\.TriangleArea/);
  assert.match(proof, /MergeObjectsMatches[\s\S]*ExactExpectedEquals[\s\S]*sharedMaterials/);
  assert.doesNotMatch(proof, /WorldTriangle|CaptureWorldTriangles|TriangleEquals/);
  assert.doesNotMatch(tool, /ConvertToProBuilderMesh\(/);
});
