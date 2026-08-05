import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

async function source(name: string) {
  return readFile(new URL(name, root), "utf8");
}

test("extrude-faces freezes a single typed face and proves the complete terminal state", async () => {
  const [tool, proof] = await Promise.all([
    source("extrude-faces.cs"), source("ProBuilderFaceExtrudeCompletionProof.cs")
  ]);
  assert.equal((tool.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(tool, /ProBuilderFaceExtrudeCompletionProof\.TryCreate\(go, typedMesh, new\[\] \{ 0 \}, method, distance\)/);
  assert.match(tool, /ExtrudeElements\.Extrude\(typedMesh, plan\.FrozenFaces, plan\.Method, plan\.Distance\)/);
  assert.match(tool, /ApplyDeterministicQuadUvs\(typedMesh, plan\.Before\.Faces\.Length, created\.Length\)/);
  assert.match(tool, /RefreshMeshPreservingVertexPayload\(typedMesh\)/);
  assert.match(tool, /ProBuilderFaceExtrudeCompletionProof\.Matches\(plan, target\)/);
  assert.doesNotMatch(tool, /GetFacesByIndices|ProBuilderFaceCompletionProof\.ExtrudeMatches|ProBuilderReflection\.Extrude/);
  assert.match(proof, /before\.Faces\.Length != 1 \|\| before\.RawVertices\.Length != 3/);
  assert.match(proof, /BoundaryExactlyMatchesTriangle/);
  assert.match(proof, /extrudeMethod != ExtrudeMethod\.IndividualFaces/);
  assert.match(proof, /var direction = normal;\s*direction\.Normalize\(\)/);
  assert.match(proof, /extrudeMethod == ExtrudeMethod\.FaceNormal[\s\S]*Math\.Secant\(Vector3\.Angle\(direction, normal\) \* Mathf\.Deg2Rad\)/);
  assert.match(proof, /FiniteExtrudedGeometry\(before, boundary, delta\)/);
  assert.match(proof, /CanonicalMeshSnapshot\.RefreshArithmeticFinite/);
  assert.match(proof, /new\[\] \{ 0, 1, 2, 1, 3, 2 \}/);
  assert.match(proof, /before\.RawVertices\.Any\(raw => !Finite\(raw\.LocalPosition \+ delta\)[\s\S]*LocalToWorldMatrix\.MultiplyPoint3x4\(raw\.LocalPosition \+ delta\)/);
  assert.match(proof, /Math\.Normal\(a, b, c\)[\s\S]*Math\.Normal\(b, d, c\)/);
  assert.match(proof, /var typedFaces = mesh\.faces/);
  assert.match(proof, /typedFace\.indexes\.ToArray\(\)/);
  assert.match(proof, /typedFace\.edges\.ToArray\(\)/);
  assert.doesNotMatch(proof, /facesInternal|indexesInternal|edgesInternal/);
  assert.match(proof, /private const int SmoothingNone = 0/);
  assert.match(proof, /private const int SmoothRangeMax = 24/);
  assert.match(proof, /private const int HardRangeMax = 42/);
  assert.match(proof, /Mathf\.Abs\(value\.sqrMagnitude\) < float\.Epsilon \? Vector3\.up : value\.normalized/);
  assert.doesNotMatch(proof, /Math\.EnsureUnitVector|Smoothing\./);
  assert.match(proof, /new\[\] \{ offset, offset \+ 1, offset \+ 2, offset \+ 1, offset \+ 3, offset \+ 2 \}/);
  assert.match(tool, /exactly one occupied renderer material slot at submesh 0/);
  assert.match(proof, /materials\.Length != 1 \|\| materials\[0\] == null \|\| face\.SubmeshIndex != 0/);
  assert.match(proof, /RawPositionsAndPayloadExactly/);
  assert.match(proof, /SharedPartitionExactly/);
  assert.match(proof, /foreach \(var sourceGroup in plan\.Before\.SharedVertices\)/);
  assert.match(proof, /outerByRaw\.Add\(raw, outer\); expected\.Add\(outer\)/);
  assert.match(proof, /oldByRaw\.Add\(x, oldX\); expected\.Add\(oldX\)/);
  assert.match(proof, /OrderedPartitionEquals\(expected, after\.SharedVertices/);
  assert.match(proof, /members\.SequenceEqual\(right\[index\]\)/);
  assert.doesNotMatch(proof, /return PartitionEquals\(expected, after\.SharedVertices/);
  assert.match(proof, /SharedTexturesExactly/);
  assert.match(proof, /SidePayloadEquals/);
  assert.match(proof, /SideUv\(quadRawIndex\)/);
  assert.match(proof, /0, -1, -1, true, beforeFace\.Uv/);
  assert.match(proof, /!actualUvs\[1\]\.IsPresent/);
  assert.match(proof, /new\[\] \{ 0, 2, 3 \}/);
  assert.match(proof, /DerivedNormalsAndTangentsMatch/);
  assert.match(proof, /UnityEngine\.ProBuilder\.Math\.Normal\(raw\[a\]\.LocalPosition, raw\[b\]\.LocalPosition, raw\[c\]\.LocalPosition\)/);
  assert.match(proof, /float r = 1\.0f \/ \(s1 \* t2 - s2 \* t1\)/);
  assert.match(proof, /TangentsMatch/);
  const tangentReplay = proof.slice(proof.indexOf("private static bool TangentsMatch"), proof.indexOf("private static bool HasFiniteNonDegenerateUv"));
  assert.doesNotMatch(tangentReplay, /Finite\(denominator\)|Mathf\.Approximately\(denominator/);
  assert.match(proof, /CanonicalMeshSnapshot\.ResolveFreshMesh/);
});
