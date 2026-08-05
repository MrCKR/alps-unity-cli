import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const directory = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

async function source(name: string) {
  return readFile(new URL(name, directory), "utf8");
}

test("bridge and extrude freeze typed edges before their only effect barrier", async () => {
  const [bridge, extrude, reflection] = await Promise.all([
    source("bridge-edges.cs"), source("extrude-edges.cs"), source("ProBuilderReflection.cs")
  ]);
  for (const text of [bridge, extrude]) {
    assert.equal((text.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
    assert.match(text, /RefreshMeshPreservingVertexPayload\(mesh\)/);
    assert.match(text, /OutcomeUnknown\([^\n]+"(?:invoke|readback|postcondition)"/);
  }
  assert.match(bridge, /var frozenEdgeA = edgeArray\.GetValue\(0\); var frozenEdgeB = edgeArray\.GetValue\(1\);/);
  assert.match(bridge, /TryBridgeFrozenEdges\(mesh, frozenEdgeA, frozenEdgeB/);
  assert.match(extrude, /TryExtrudeFrozenEdges\(mesh, edgeArray, distance, asGroup, out edgeCount\)/);
  assert.doesNotMatch(bridge, /BridgeEdges\(mesh/);
  assert.doesNotMatch(extrude, /ExtrudeEdgesBySelection\(mesh/);
  assert.match(reflection, /TryBridgeFrozenEdges\(object mesh, object frozenEdgeA, object frozenEdgeB/);
  assert.match(reflection, /TryExtrudeFrozenEdges\(object mesh, Array frozenEdges/);
  assert.doesNotMatch(reflection, /ExtrudeEdgesBySelection|public static bool BridgeEdges\(/);
});

test("bridge and extrude terminal proofs consume exact append state", async () => {
  const proof = await source("ProBuilderEdgeCompletionProof.cs");
  for (const token of [
    "IdentityAndTransformEqual", "OldTerminalExactlyPreserved", "OldRawPayloadEquals",
    "NewAppendRawPayloadEquals", "NewFaceExactly", "SharedAppendExactly",
    "SharedExtrudeExactly", "SharedTexturesExactlyPreserved", "PartitionEquals", "AverageNormal"
  ]) assert.match(proof, new RegExp(token));
  assert.match(proof, /new\[\] \{ 2, 1, 0, 2, 3, 1 \}/);
  assert.match(proof, /Projection\.PlanarProject/);
  assert.match(proof, /SegmentsIntersect/);
  assert.match(proof, /denominator = -s2\.x \* s1\.y \+ s1\.x \* s2\.y/);
  assert.match(proof, /s >= 0f && s <= 1f && t >= 0f && t <= 1f/);
  assert.match(proof, /raw\.Uvs\[1\]\.IsPresent/);
  assert.match(proof, /before\.Edges\.Any\(edge => SameEdge/);
  assert.match(proof, /Math\.TriangleArea/);
  assert.match(proof, /CaptureCanonicalSnapshot\(freshMesh\.gameObject, touched\)/);
  assert.doesNotMatch(proof.slice(proof.indexOf("internal static bool BridgeMatches"), proof.indexOf("private static bool IdentityAndTransformEqual")), /HasValueEdge|IsBevelRail|BoundaryReplacedByChain/);
});
