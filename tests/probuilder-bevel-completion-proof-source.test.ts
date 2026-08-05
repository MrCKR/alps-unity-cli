import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const directory = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
async function source(name: string) { return readFile(new URL(name, directory), "utf8"); }

test("bevel accepts only the closed one-edge two-triangle domain and replays its exact terminal state", async () => {
  const [tool, proof, reflection] = await Promise.all([
    source("bevel-edges.cs"),
    source("ProBuilderBevelCompletionProof.cs"),
    source("ProBuilderReflection.cs")
  ]);

  assert.equal((tool.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(tool, /var frozenEdges = edgeArray as Edge\[\]/);
  assert.match(tool, /Bevel\.BevelEdges\(typedMesh, plan\.FrozenEdges, amount\)/);
  assert.doesNotMatch(tool, /Bevel\.BevelEdges\(typedMesh, frozenEdges, amount\)/);
  assert.match(tool, /current ProBuilder 5\.2\.4 serialized mesh format with initialized sharedTextures/);
  assert.match(tool, /nonempty sharedMaterials/);
  assert.match(tool, /every material slot must be used/);
  assert.match(tool, /RefreshMeshPreservingVertexPayload/);
  assert.match(reflection, /RefreshMeshPreservingVertexPayload\(object mesh\) => RefreshMesh\(mesh, "Normals, Tangents, Collisions, Bounds", true\)/);
  assert.match(reflection, /GetMethod\("GetVertices", new\[\] \{ typeof\(IList<int>\) \}\)/);
  assert.match(reflection, /GetMethod\("SetVertices", new\[\] \{ vertexListType, typeof\(bool\) \}\)/);
  assert.match(reflection, /refresh\.Invoke\(mesh, new\[\] \{ refreshMask \}\)[\s\S]*getVertices\.Invoke\(mesh, new object\[\] \{ null \}\)[\s\S]*setVertices\.Invoke\(mesh, new\[\] \{ vertices, \(object\)true \}\)/);
  assert.doesNotMatch(reflection, /RefreshMeshPreservingVertexPayload\(object mesh\) => RefreshMesh\(mesh, "[^"]*UV/);
  assert.match(tool, /try\s*\{[\s\S]*CanonicalMeshSnapshot\.Capture\(freshMesh\.gameObject\)[\s\S]*\}\s*catch\s*\{/);
  assert.match(tool, /OutcomeUnknown\("bevel-edges", "postcondition"/);

  for (const token of [
    "frozenEdges.Length != 1", "!before.HasSharedTextures", "CanonicalMeshSnapshot.CurrentMeshFormat(typedMesh)", "before.Faces.Length != 2", "DirectedRawTriangleStream.Length != 3",
    "WingedEdge.GetWingedEdges(typedMesh)", "EdgeLookup.GetEdgeLookup", "FirstOrDefault(x => x.edge.Equals(lup))",
    "ClampExact", "amount != requestedAmount", "amount < .001f",
    "SlideEdgeExact", "Manual", "GetBridgeFacesExact", "appendFaces", "holeFaces",
    "SharedVertex.GetSharedVerticesWithPositions", "TerminalTopologyReadable", "sharedTextures", "CalculateNormalsAndTangents",
    "MaterialSlotsStable", "sharedMaterials", "used.SetEquals",
    "ClearUnityMeshUv1", "Uvs[1]", "Vector4.zero",
    "SmoothingGroupNone = 0", "SmoothRangeMax = 24", "HardRangeMax = 42",
    "BuildSharedLookup", "sharedVertices[group]", "face.indexes.ToArray()",
    "private static Vector3 Normal(IList<Vertex> vertices, IList<int> indexes = null)",
    "nrm += UnityEngine.ProBuilder.Math.Normal(vertices[indexes[i]].position, vertices[indexes[i + 1]].position, vertices[indexes[i + 2]].position)",
    "nrm /= (len / 3f)", "return Mathf.Abs(value.sqrMagnitude) < float.Epsilon ? Vector3.up : value.normalized",
    "EnsureUnitVector(sums[group] / groupCounts[group])", "Vector3.OrthoNormalize", "tan1", "tan2", "Finite(raw)",
    "x.LocalNormal",
    "RawEquals", "FaceEquals", "OrderedSharedPartitionsEqual", "MemberRawIndexes.SequenceEqual(expected[i])", "HasSharedTextures", "FrozenEdges",
  ]) assert.match(proof, new RegExp(token.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));

  for (const removed of ["TrySlide", "UntouchedRawPayloadPreserved", "SharedVertexPartitionPreserved", "FindEdge", "ExactlyOneStrip", "RailOnFacePlane", "Rail", "ConformBridgeExact", "ConformOppositeNormal", "GetCommonEdgeInWindingOrder", "ExactEquals(amount, requestedAmount)", "sharedVertexLookup", "indexesInternal", "Math.EnsureUnitVector", "Smoothing.smoothingGroupNone", "Smoothing.smoothRangeMax", "Smoothing.hardRangeMax"])
    assert.doesNotMatch(proof, new RegExp(removed));
  for (const token of [
    "Vector2 w1 = textures[i1]", "float x1 = v2.x - v1.x", "float s1 = w2.x - w1.x",
    "float r = 1.0f / (s1 * t2 - s2 * t1)", "Vector3 sdir", "Vector3 tdir",
    "tan1[i1] += sdir", "tan2[i3] += tdir", "tangent.w = Vector3.Dot"
  ]) assert.match(proof, new RegExp(token.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  assert.doesNotMatch(proof, /uv3\.y - uv1\.y|p2\.x - p1\.x/);
  assert.doesNotMatch(proof, /ExactMultisetEquals\(after\.SharedVertices|MemberRawIndexes\.OrderBy/);
  assert.match(proof, /Smoothing = -1, Texture = -1, Element = -1, Manual = false/);
  assert.doesNotMatch(proof, /Set\(v, x\.Normal, MeshArrays\.Normal/);
});
