import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

test("weld uses deterministic clique plans and relaxed fresh capture", async () => {
  const [tool, proof, snapshot, reflection] = await Promise.all(["weld-vertices.cs", "ProBuilderVertexObjectCompletionProof.cs", "ProBuilderCanonicalMeshSnapshot.cs", "ProBuilderReflection.cs"].map(name => readFile(new URL(name, root), "utf8")));
  assert.match(proof, /Components\(before, handles, radius\)/);
  assert.match(proof, /component\.Length <= 2 && component\.All\(a => component\.All\(b => a == b \|\| Near/);
  assert.match(proof, /!Finite\(radius \* radius\)/);
  assert.match(proof, /if \(!Finite\(average\)\) return null/);
  assert.match(proof, /ExpectedMeshFinite\(before, expected\)/);
  assert.match(proof, /Finite\(delta\) && Finite\(squared\) && Finite\(radiusSquared\)/);
  assert.match(tool, /at most two distinct shared handles/);
  assert.match(tool, /components of at most two shared handles/);
  assert.match(proof, /sqrMagnitude/);
  assert.match(proof, /CaptureRelaxedAfterWeld/);
  assert.match(snapshot, /CaptureRelaxedAfterWeld[\s\S]*CaptureCore\(go, null, true, false\)/);
  assert.match(reflection, /TryWeldVerticesWithRadius[\s\S]*out int weldedCount/);
  assert.match(proof, /components\.All\(component => component\.Length == 1\)[^\n]+return new WeldPlan/);
  const noOpIndex = proof.indexOf("components.All(component => component.Length == 1)");
  const uv1Index = proof.indexOf("if (HasUv1(before)) return null");
  assert.ok(noOpIndex >= 0 && uv1Index > noOpIndex, "UV1 may only reject an effectful weld");
  assert.match(tool, /effectful weld requires absent UV1/i);
  assert.match(proof, /GlobalObjectId[\s\S]*ScenePath/);
  assert.match(tool, /WeldPlanIsUnambiguous[\s\S]*WeldIsNoOp[\s\S]*ctx\.CommitEffect\(\)[\s\S]*TryWeldVerticesWithRadius[\s\S]*RefreshMeshPreservingVertexPayload/);
  assert.match(tool, /"invoke"[\s\S]*"readback"[\s\S]*"postcondition"/);
});
