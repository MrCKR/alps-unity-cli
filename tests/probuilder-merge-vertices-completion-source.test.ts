import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

test("merge-vertices replays the 5.2.4 terminal state and treats -1 as a valid result", async () => {
  const [tool, proof, reflection] = await Promise.all(["merge-vertices.cs", "ProBuilderVertexObjectCompletionProof.cs", "ProBuilderReflection.cs"].map(name => readFile(new URL(name, root), "utf8")));
  assert.match(reflection, /TryMergeVerticesWithCollapse[\s\S]*out int mergedIndex[\s\S]*return true/);
  assert.match(tool, /before\.IsNoOp[\s\S]*ctx\.CommitEffect\(\)[\s\S]*TryMergeVerticesWithCollapse[\s\S]*RefreshMeshPreservingVertexPayload[\s\S]*MergeVerticesMatches\(before, freshMesh, mergedToIndex\)/);
  assert.match(tool, /"invoke"[\s\S]*"readback"[\s\S]*"postcondition"/);
  assert.match(proof, /AverageRaw\(before\.RawVertices, requested\)/);
  assert.match(proof, /AverageChannel\(selected\.Select\(value => value\.LocalNormal\)\)/);
  assert.match(proof, /AverageChannel\(selected\.Select\(value => value\.Tangent\)\)/);
  assert.match(proof, /authoritative\.LocalNormal, authoritative\.Tangent, authoritative\.Color/);
  assert.match(proof, /SplitRequestedTexturePartitions/);
  assert.match(proof, /RemoveDegenerateAndReindex/);
  assert.match(proof, /CaptureAfterMergeVertices/);
  assert.match(proof, /RawTerminalFinite\(before, center\)/);
  assert.match(proof, /ExpectedMeshFinite\(before, expected\.Mesh\)/);
  assert.match(proof, /LocalToWorldMatrix\.MultiplyPoint3x4\(raw\.LocalPosition\)/);
  assert.match(proof, /CanonicalMeshSnapshot\.RefreshArithmeticFinite/);
  assert.match(proof, /var isNoOp = ExactExpectedEquals\(before, expected\.Mesh\)/);
  assert.match(proof, /new ExpectedMesh\(compact\.ToArray\(\), compactFaces, PartitionsFromHandles\(compactHandles\), before\.HasSharedTextures, compactTextures\)/);
  assert.match(proof, /if \(!isNoOp && \(!before\.HasSharedTextures \|\| HasUv1\(before\)\)\) return null/);
  assert.match(tool, /effectful merge requires initialized sharedTextures, absent UV1/i);
  assert.match(proof, /var sharedGroups = mesh\.sharedVertices/);
  assert.match(proof, /requestedHandles\.OrderBy\(handle => handle\)\.SelectMany\(handle => sharedGroups\[handle\]\.ToArray\(\)\)/);
  assert.doesNotMatch(proof, /Shared\(before, handle\)\.MemberRawIndexes\.OrderBy\(raw => raw\)/, "returned index must retain the original SharedVertex member sequence");
  assert.match(proof, /mergedMemberOrder\.Where\(rawIndex => expected\.OldToNew\.ContainsKey\(rawIndex\)\)[\s\S]*survivingMembers\[survivingMembers\.Length - 1\]/);
  assert.doesNotMatch(proof, /LastOrDefault\(-1\)/);
  assert.match(proof, /Math\.TriangleArea[\s\S]*area > Mathf\.Epsilon/);
  assert.match(proof, /ExpectedReturnedIndex/);
});
