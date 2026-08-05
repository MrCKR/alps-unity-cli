import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
async function source(name: string) { return readFile(new URL(name, root), "utf8"); }

test("flip handlers freeze typed Face selections and prove complete fresh terminal state", async () => {
  const [flip, duplicate, proof] = await Promise.all([
    source("flip-normals.cs"), source("duplicate-and-flip.cs"), source("ProBuilderFlipCompletionProof.cs")
  ]);
  for (const handler of [flip, duplicate]) {
    assert.equal((handler.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
    assert.match(handler, /\.Distinct\(\)\.ToArray\(\)/);
    assert.match(handler, /var frozenFaces = .*typedMesh\.faces\[index\].*ToArray\(\)/);
    assert.match(handler, /ProBuilderFlipCompletionProof\.Capture/);
    assert.match(handler, /every material slot used by at least one face/);
    assert.match(handler, /RefreshMeshPreservingVertexPayload\(typedMesh\)/);
    assert.match(handler, /OutcomeUnknown\([^\n]+"invoke"/);
    assert.match(handler, /OutcomeUnknown\([^\n]+"readback"/);
    assert.match(handler, /OutcomeUnknown\([^\n]+"postcondition"/);
    assert.doesNotMatch(handler, /GetFacesByIndices|ProBuilderFaceCompletionProof\.(?:FlipNormalsMatches|DuplicateAndFlipMatches)|RefreshMesh\(mesh\)/);
  }
  assert.match(flip, /foreach \(var face in plan\.FrozenFaces\) face\.Reverse\(\)/);
  assert.match(duplicate, /AppendElements\.DuplicateAndFlip\(typedMesh, plan\.FrozenFaces\)/);
  for (const token of [
    "CanonicalMeshSnapshot.Capture(target, occurrences)",
    "CanonicalMeshSnapshot.ResolveFreshMesh(target, plan.Before.Identity)",
    "CanonicalMeshSnapshot.Capture(fresh.gameObject, Array.Empty<int>())",
    "DirectedRawTriangleStream.Reverse()",
    "var map = new Dictionary<int, int>()",
    "foreach (var occurrence in occurrences)",
    "ClearUnityMeshUv2(raw)",
    "vertex.Uvs[1] = new CanonicalMeshSnapshot.ChannelValue(false, Vector4.zero)",
    "MaterialSlotsStable(target, before)",
    "used.SetEquals(Enumerable.Range(0, materials.Length))",
    "map.ContainsKey(oldRaw)",
    "shared[before.RawVertices[oldRaw].SharedHandle].Add(newRaw)",
    "shared.Select(x => x.ToArray()), true, before.SharedTextures",
    "RawEquals(after.RawVertices[i], plan.Raw[i])",
    "FaceEquals(after.Faces[i], plan.Faces[i])",
    "PartitionsEqual(after.SharedVertices.Select",
    "PartitionsEqual(after.SharedTextures.Select",
    "CalculateNormalsAndTangents",
    "Vector3.OrthoNormalize"
  ]) assert.match(proof, new RegExp(token.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), token);
  const map = proof.indexOf("var map = new Dictionary<int, int>()");
  const occurrenceLoop = proof.indexOf("foreach (var occurrence in occurrences)");
  assert.ok(map > occurrenceLoop, "DuplicateAndFlip creates a map for each selected face");
  assert.match(proof, /left\.Length == right\.Length/);
  assert.match(proof, /members\.SequenceEqual\(right\[index\]\)/);
  assert.match(proof, /private const int SmoothingNone = 0/);
  assert.match(proof, /private const int SmoothRangeMax = 24/);
  assert.match(proof, /private const int HardRangeMax = 42/);
  assert.match(proof, /Mathf\.Abs\(value\.sqrMagnitude\) < float\.Epsilon \? Vector3\.up : value\.normalized/);
  assert.doesNotMatch(proof, /Math\.EnsureUnitVector|Smoothing\./);
  assert.doesNotMatch(proof, /GetHashCode|semantic existence/i);
});
