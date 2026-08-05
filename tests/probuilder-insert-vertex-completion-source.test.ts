import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

test("insert-vertex rejects exterior points before effect and proves mode-specific topology", async () => {
  const [tool, proof] = await Promise.all([
    readFile(new URL("insert-vertex.cs", root), "utf8"),
    readFile(new URL("ProBuilderInsertCompletionProof.cs", root), "utf8"),
  ]);
  assert.match(tool, /HasFaceInsertionSharedTextureConflict\(go, requestedFaces\[0\]\)[\s\S]*face insertion cannot preserve this mesh's shared texture groups/);
  assert.match(tool, /ProBuilderInsertCompletionProof\.Capture\(go,[^;]+out var captureFailure\)[\s\S]*Requested canonical insertion geometry is not readable: " \+ captureFailure[\s\S]*PointIsStrictlyInterior\(before, point\)[\s\S]*ctx\.CommitEffect\(\)/);
  assert.match(tool, /RestoreEdgeFaceAttributes\(mesh, before\)[\s\S]*if \(!ProBuilderReflection\.RefreshMesh\(mesh\)\)[\s\S]*OutcomeUnknown\("insert-vertex", "invoke"/);
  assert.match(tool, /CanonicalMeshSnapshot\.ResolveFreshMesh\(target, before\.Before\.Identity\)[\s\S]*CanonicalMeshSnapshot\.Capture\(freshMesh\.gameObject, Array\.Empty<int>\(\)\)[\s\S]*ProBuilderInsertCompletionProof\.Matches\(before, after, point, out var proofFailure\)[\s\S]*proofFailure/);

  for (const invariant of [
    "AffectedOccurrences",
    "PointInFaceInterior",
    "PointInPolygon",
    "OnSegmentInterior",
    "SharedLayoutMatches",
    "PreservedFaceEquals",
    "EdgeReplacementMatches",
    "FaceChildMatches",
    "IsCompleteTriangulation",
    "ExactMultisetEquals",
    "RawValueEquals",
    "PointMemberMatches",
    "SourceNormals",
    "SharedTextureLayoutMatches",
    "ExactBijection",
    "RestoreEdgeFaceAttributes",
    "AffectedTextureCoverageIsEmpty",
    "TextureGroupValueEquals",
    "FaceContainsValue",
    "SharedTextureDiagnostic",
    "PointPayloadClaim",
    "FreezePointPayload",
  ]) assert.match(proof, new RegExp(invariant));
  assert.match(proof, /\(edge == null\) != \(face >= 0\)/);
  assert.match(proof, /source\.Boundary\.Length \* 2/);
  assert.match(proof, /remaining\.Count != face\.Boundary\.Length/);
  assert.match(proof, /if \(HasValueEdge\(after, request\.LocalA, request\.LocalB\)\)/);
  assert.match(proof, /!initial\.HasSharedTextures \|\| initial\.RawVertices\.Any\(raw => raw\.Uvs\[1\]\.IsPresent\)/);
  assert.match(tool, /initialized sharedTextures and absent UV1/);
  assert.match(tool, /raw vertices exclusively owned by that face/);
  assert.match(proof, /HasSharedTextureGroups\(initial\)[^\n]+face insertion cannot preserve existing shared texture groups/);
  assert.match(proof, /before\.SharedTextures\.Length == 0 && after\.HasSharedTextures/);
  assert.match(proof, /after\.SharedTextures\.Length == boundaryCount \* 2 \+ 1/);
  assert.match(proof, /pointGroups\.Length == 1 && pointGroups\[0\]\.MemberRawIndexes\.Length == boundaryCount/);
  assert.match(proof, /endpointGroups\.Length == boundaryCount \* 2 && endpointGroups\.All\(group => group\.MemberRawIndexes\.Length == 1\)/);
  assert.match(proof, /AffectedTextureCoverageIsEmpty\(before, affected\)/);
  assert.match(proof, /FacesHaveExclusiveRawOwnership\(initial, new\[\] \{ face \}\)/);
  assert.match(proof, /FacesHaveExclusiveRawOwnership\(before, affected\)/);
  assert.match(proof, /ownerCounts\.TryGetValue\(raw, out var count\) && count == 1/);
  assert.match(proof, /affected edge face has shared texture mappings whose 5\.2\.4 boundary reorder cannot be proven/);
  assert.match(proof, /DirectedRawTriangleStream\.Distinct\(\)\.All\(raw => !mapped\.Contains\(raw\)\)/);
  assert.match(proof, /ExactMultisetEquals\(before\.SharedTextures, after\.SharedTextures/);
  assert.match(proof, /face\.uv = new AutoUnwrapSettings\(source\.Uv\)/);
  assert.match(proof, /elementGroup\.SetValue\(face, source\.ElementGroup\)/);
  assert.match(proof, /candidate counts=" \+ string\.Join\(",", candidateCounts\)/);
  assert.match(proof, /Vector3\.Magnitude\(a\) \* Mathf\.Cos\(Vector3\.Angle\(b, a\) \* Mathf\.Deg2Rad\) \/ Vector3\.Magnitude\(b\)/);
  assert.match(proof, /var mixed = Vertex\.Mix\(vertices\[request\.RawA\], vertices\[request\.RawB\], weight\)/);
  assert.match(proof, /request\.ExpectedPoint = expected/);
  assert.match(proof, /request\.ExpectedPoint\.Position/);
  assert.match(proof, /CanonicalMeshSnapshot\.ChannelValueEquals\(actual\.Color, request\.ExpectedPoint\.Color\)/);
  assert.match(proof, /actualUvs\.Zip\(expectedUvs, CanonicalMeshSnapshot\.ChannelValueEquals\)/);
  assert.match(proof, /new CanonicalMeshSnapshot\.ChannelValue\(false, Vector4\.zero\)/);
  const pointMember = proof.slice(proof.indexOf("private static bool PointMemberMatches"), proof.indexOf("private static bool ExactBijection"));
  assert.doesNotMatch(pointMember, /Vector3\.Dot|Vector4\.Lerp/);
  assert.doesNotMatch(proof, /return edgeMode \?|after\.Faces\.Count\([^\n]+> request\.Before\.Faces\.Length/);
});
