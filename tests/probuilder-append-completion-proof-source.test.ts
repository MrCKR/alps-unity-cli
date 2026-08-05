import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
const append = new URL("append-vertices-to-edge.cs", root);
const proof = new URL("ProBuilderAppendCompletionProof.cs", root);

async function source(url: URL) {
  return readFile(url, "utf8");
}

test("append vertices uses a frozen request-specific terminal-state plan", async () => {
  const [handler, planner] = await Promise.all([source(append), source(proof)]);

  assert.equal((handler.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(handler, /var frozenEdges = edgeArray\.Cast<Edge>\(\)\.Select\(edge => new Edge\(edge\.a, edge\.b\)\)\.ToArray\(\)/);
  assert.match(handler, /ProBuilderAppendCompletionProof\.TryCreate\(go, mesh as ProBuilderMesh, frozenEdges, count\)/);
  assert.ok(handler.indexOf("TryCreate") < handler.indexOf("ctx.CommitEffect()"));
  assert.match(handler, /AppendVerticesToEdge\(mesh, plan\.FrozenEdges, plan\.Count\)/);
  assert.match(handler, /var target = request\.Target/);
  assert.match(handler, /ProBuilderAppendCompletionProof\.Matches\(plan, target\)/);
  assert.doesNotMatch(handler.slice(handler.indexOf("ctx.CommitEffect()")), /\brequest\./);
  assert.match(handler, /OutcomeUnknown\("append-vertices-to-edge", "postcondition"/);
  assert.doesNotMatch(handler, /CountRequestedInsertedVertices|AppendMatches\(source/);

  for (const token of [
    "CanonicalMeshSnapshot.Capture(target)",
    "!before.HasSharedTextures",
    "FrozenEdges",
    "Vertex.Mix",
    "WingedEdge",
    "SortEdgesByAdjacency",
    "Triangulation",
    "TriangulateVertices",
    "typeof(IList<Vertex>)",
    "typeof(List<int>).MakeByRefType()",
    "new object[] { vertices.Select(vertex => new Vertex(vertex)).ToList(), null, false, false }",
    "FreezeFirstRaw(mesh, before)",
    "groups[handle][0]",
    "MixedVertices(before, firstRaw, edge.Item1, edge.Item2, count)",
    "FiniteForProfile(vertex, profile)",
    "new RawPlan(vertex, profile)",
    "MaterialSlotsStable(before, renderer)",
    "materials != null && materials.Length > 0",
    "face.SubmeshIndex >= 0 && face.SubmeshIndex < materials.Length",
    "Enumerable.Range(0, materials.Length).All(slot => before.Faces.Any(face => face.SubmeshIndex == slot))",
    "CanonicalMeshSnapshot.ResolveFreshMesh(target, plan.Before.Identity)",
    "CanonicalMeshSnapshot.Capture(freshMesh.gameObject)",
    "IdentityAndTransformEqual",
    "RawBaseEquals",
    "FaceEquals",
    "OrderedPartitionsEqual",
    "SharedTextures",
    "FacesHaveExclusiveRawOwnership",
    "BuiltinMaterials.defaultMaterial",
    "TerminalTopologyReadable",
    "InsertedCount",
    "face.DirectedRawTriangleStream.Distinct().ToArray()",
    "foreach (var face in affected)",
    "before.RawVertices.Length + selected.ToList().IndexOf(edge) * count",
    "shared.Where(group => group.Count > 0)",
    "after.HasSharedTextures != true",
    "Rebuilt",
    "AutoUnwrapSettings.tile",
    "CalculateDerivedChannels",
    "CalculateHardNormals",
    "private const int SmoothingGroupNone = 0",
    "private const int SmoothRangeMax = 24",
    "private const int HardRangeMin = 42",
    "smoothGroupMax = SmoothRangeMax",
    "group <= SmoothingGroupNone || (group > SmoothRangeMax && group < HardRangeMin)",
    "Mathf.Abs(v.sqrMagnitude) < float.Epsilon ? Vector3.up : v.normalized",
    "EnsureUnitVector(sums[group] / groupCounts[group])",
    "EnsureUnitVector(tan1[index])",
    "1.0f/(s1*t2-s2*t1)",
    "Vector3.OrthoNormalize",
    "RawBaseEquals",
    "LocalNormal",
    "Tangent"
  ]) assert.match(planner, new RegExp(token.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), token);

  assert.doesNotMatch(planner, /Destroy\(|CreateInstance|ScriptableObject/);
  assert.doesNotMatch(planner, /face\.DirectedRawTriangleStream\.Distinct\(\)\.Count\(\) == face\.DirectedRawTriangleStream\.Length/);
  assert.match(planner, /UnityEngine\.ProBuilder\.Math\.Normal\(raw\[a\]\.Position, raw\[b\]\.Position, raw\[c\]\.Position\)/);
  assert.match(planner, /var insertedCount = \(long\)frozenEdges\.Length \* count/);
  assert.match(planner, /var workingRawCount = \(long\)before\.RawVertices\.Length \+ affected\.Sum/);
  assert.match(planner, /insertedCount > int\.MaxValue - \(long\)before\.RawVertices\.Length \|\| workingRawCount > int\.MaxValue/);
  assert.match(planner, /return Tuple\.Create\(a, b\)/, "native common-edge direction must be preserved");
  assert.doesNotMatch(planner, /a < b \? Tuple\.Create\(a, b\) : Tuple\.Create\(b, a\)/);
  assert.match(planner, /before\.Faces\.Where\(face => !occurrences\.Contains\(face\.OccurrenceIndex\)\)\.All\(face => face\.DirectedRawTriangleStream\.All\(raw => !deleted\.Contains\(raw\)\)\)/);
  assert.match(planner, /new FacePlan\(face, relative\.Select\(index => offset \+ index\), true, rebuiltMaterial\)/);
  assert.match(planner, /WithSerializedFaceMaterial\(source\.Material, rebuiltMaterial\)/);
  assert.match(planner, /CanonicalMeshSnapshot\.RefreshArithmeticFinite\(before\)/, "effect 前仍必须拒绝不可安全刷新的输入网格");
  const terminalPlan = planner.slice(planner.indexOf("var positions = raw.Select"), planner.indexOf("return new Plan"));
  assert.match(terminalPlan, /TerminalTopologyReadable\(positions, streams, terminalShared\)/);
  assert.match(terminalPlan, /CalculateDerivedChannels\(raw, orderedFaces, terminalShared\)/);
  assert.doesNotMatch(terminalPlan, /RefreshArithmeticFinite/, "专用最终通道重放不得再被通用 tangent 中间有限性重复拒绝");
  assert.match(planner, /OrderedPartitionsEqual\(after\.SharedVertices\.Select\(group => group\.MemberRawIndexes\), plan\.Shared\)/);
  assert.match(planner, /OrderedPartitionsEqual\(after\.SharedTextures\.Select\(group => group\.MemberRawIndexes\), plan\.SharedTextures\)/);
  assert.match(planner, /group\.SequenceEqual\(right\[index\]\)/);
  assert.doesNotMatch(planner, /private static bool PartitionEquals/);
  assert.match(planner, /GetType\("UnityEngine\.ProBuilder\.MeshOperations\.Triangulation"\)/);
  assert.doesNotMatch(planner, /GetType\("UnityEngine\.ProBuilder\.Triangulation"\)/);
  assert.match(planner, /new\[\] \{ typeof\(IList<Vertex>\), typeof\(List<int>\)\.MakeByRefType\(\), typeof\(bool\), typeof\(bool\) \}/);
  assert.doesNotMatch(planner, /GetMethod\("TriangulateVertices", BindingFlags\.Static \| BindingFlags\.Public \| BindingFlags\.NonPublic\);/);
  assert.match(planner, /Set\(vertex, raw\.LocalNormal, MeshArrays\.Normal/);
  assert.match(planner, /Channel\(profile\.LocalNormal\.IsPresent, vertex\.normal\)/);
  assert.match(planner, /Channel\(profile\.Tangent\.IsPresent, vertex\.tangent\)/);
  assert.match(planner, /Channel\(profile\.Color\.IsPresent, \(Vector4\)vertex\.color\)/);
  assert.doesNotMatch(planner, /raw\.Normal/);
  assert.doesNotMatch(planner, /Smoothing\.(?:smoothingGroupNone|smoothRangeMax|hardRangeMax)/);
  assert.doesNotMatch(planner, /UnityEngine\.ProBuilder\.Math\.EnsureUnitVector/);
  assert.match(planner, /CanonicalMeshSnapshot\.ChannelValueEquals\(after\.RawVertices\[index\]\.LocalNormal, expected\.Normal\)/);
  assert.match(planner, /foreach \(var vertex in raw\) vertex\.Uvs\[1\] = new CanonicalMeshSnapshot\.ChannelValue\(false, Vector4\.zero\);/);
  assert.ok(planner.indexOf("vertex.Uvs[1] = new CanonicalMeshSnapshot.ChannelValue(false, Vector4.zero)") < planner.indexOf("CalculateDerivedChannels(raw, orderedFaces, terminalShared)"));
  assert.match(handler, /RefreshMeshPreservingVertexPayload\(mesh\)/);
  assert.doesNotMatch(handler, /RefreshMesh\(mesh\)/);
  assert.match(handler, /InsertedCount = plan\.InsertedCount/);
  assert.doesNotMatch(handler, /InsertedCount = count \* plan\.FrozenEdges\.Length/);
  assert.match(handler, /initialized sharedTextures/);
  assert.match(handler, /Selected edges must not rebuild the same face/);
  assert.match(handler, /not referenced by any retained face/);
  assert.match(handler, /complete material slot coverage restriction/);
  assert.match(handler, /catch \(System\.Exception\)/);
});
