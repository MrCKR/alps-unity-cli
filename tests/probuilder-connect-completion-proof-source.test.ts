import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

test("connect completion plan mirrors the private PB 5.2.4 rebuild overloads and terminal apply/delete layout", async () => {
  const [tool, proof] = await Promise.all([
    readFile(new URL("connect-elements.cs", root), "utf8"),
    readFile(new URL("ProBuilderConnectCompletionProof.cs", root), "utf8")
  ]);

  assert.equal((tool.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(tool, /Capture\(go, faceMode, frozenEdges, frozenFaces\)/);
  assert.match(tool, /RefreshMeshPreservingVertexPayload\(typedMesh\)/);
  assert.match(tool, /catch \(UnityCliBridge\.BridgeCommandException\) \{ throw; \}/);
  assert.match(tool, /"postcondition"/);

  for (const token of [
    "BindingFlags.Static | BindingFlags.NonPublic", "ConnectEdgesInFace", "typeof(List<WingedEdge>)",
    "typeof(WingedEdge)", "typeof(List<Vertex>)", "NormalizeVertex(value, profile)", "new Face(face)",
    "faceRebuildData", "newVertexIndexes", "UnityEngine.ProBuilder.Math.Normal", "child.Reverse()", "textureGroup",
    "SharedVertex", "TryBuildSharedVertices", "TerminalTopologyReadable", "CurrentMeshFormat", "RawOrderMatches", "FaceOrderMatches", "PositionPartitionsMatch", "uvs[1].IsPresent",
    "CalculateNormalsAndTangents", "foreach (var shared in plan.SharedVertices)", "smoothing[index]", "EnsureUnitVector",
    "for (var triangle = 0; triangle < item.Stream.Length; triangle += 3)", "tan1[i1] += sdir", "Vector3.OrthoNormalize",
    "CanonicalMeshSnapshot.ChannelValueEquals(expected[index].Normal, raw.LocalNormal)",
    "!value.HasArrays(MeshArrays.Normal) || Finite(value.normal)",
    "!value.HasArrays(MeshArrays.Tangent) || Finite(value.tangent)",
    "CanonicalMeshSnapshot.ChannelValueEquals(expected[index].Tangent, raw.Tangent)", "SameCapture", "SameRaw", "SameShared", "SameTexture", "SameFace", "SameSharedEdge",
    "CanonicalMeshSnapshot.ChannelValueEquals(left.LocalNormal, right.LocalNormal)", "CanonicalMeshSnapshot.ChannelValueEquals(left.Tangent, right.Tangent)",
    "CanonicalMeshSnapshot.MaterialValueEquals(left.Material, right.Material)", "CanonicalMeshSnapshot.UvValueEquals(left.Uv, right.Uv)", "left.MemberRawIndexes.SequenceEqual(right.MemberRawIndexes)",
    "left.DirectedSharedTriangleStream.SequenceEqual(right.DirectedSharedTriangleStream)", "a.FaceOccurrenceIndex != b.FaceOccurrenceIndex", "MeshArrays.Texture2", "MeshArrays.Texture3", "Finite"
  ]) assert.match(proof, new RegExp(token.replace(/[|\\{}()[\]^$+*?.]/g, "\\$&")));

  for (const token of ["edgesInternal", "indexesInternal", "sharedVertexLookup", "Math.EnsureUnitVector"]) {
    assert.doesNotMatch(proof, new RegExp(token.replace(/[|\\{}()[\]^$+*?.]/g, "\\$&")));
  }
  assert.match(proof, /BuildSharedLookup\(mesh\)/);
  assert.match(proof, /for \(var handle = 0; handle < groups\.Count; handle\+\+\)/);
  assert.match(proof, /lookup\.Add\(index, handle\)/);
  assert.doesNotMatch(proof, /var handle = shared\[0\]/);
  assert.match(proof, /GetSharedVerticesWithPositions\(positions\)/);
  assert.match(proof, /groups\.Select\(value => value == null \? null : value\.ToArray\(\)\)\.ToArray\(\)/);
  assert.match(proof, /expected\.Length == actual\.Length[\s\S]*members\.SequenceEqual\(actual\[index\]\)/);
  const positionPartitions = proof.slice(proof.indexOf("private static bool PositionPartitionsMatch"), proof.indexOf("private static bool Optional"));
  assert.doesNotMatch(positionPartitions, /OrderBy|ExactMultisetEquals/);
  assert.match(proof, /try[\s\S]*GetSharedVerticesWithPositions\(positions\)[\s\S]*catch \{ return false; \}/, "the package quantizer must fail closed before effect");
  assert.match(proof, /var terminal = FinalStreams\(plan\)\.ToArray\(\)/);
  assert.match(proof, /var streams = terminal\.Select\(value => value\.Stream\)\.ToArray\(\)/);
  assert.match(proof, /TerminalTopologyReadable\(positions, streams, sharedVertices\)/);
  assert.match(proof, /RefreshArithmeticFinite\(positions, uv0, streams, terminal\.Select\(value => value\.SmoothingGroup\), true\)/);
  assert.doesNotMatch(proof, /RefreshArithmeticFinite\([^\n]*children\.Select\(child => child\.Face\.indexes/);
  assert.match(proof, /private static Vector3 Normal\(IList<Vertex> vertices, IList<int> indexes\)/);
  assert.match(proof, /for \(var index = 0; index < indexes\.Count; index \+= 3\)/);
  assert.match(proof, /normal \+= UnityEngine\.ProBuilder\.Math\.Normal\(vertices\[indexes\[index\]\]\.position, vertices\[indexes\[index \+ 1\]\]\.position, vertices\[indexes\[index \+ 2\]\]\.position\)/);
  assert.match(proof, /normal \/= indexes\.Count \/ 3f/);
  assert.match(proof, /normal\.Normalize\(\)/);
  assert.match(proof, /private static Vector3 EnsureUnitVector/);
  assert.match(proof, /Mathf\.Abs\(value\.sqrMagnitude\) < float\.Epsilon \? Vector3\.up : value\.normalized/);
  assert.match(tool, /serialized format 2, bind its serialized compiled Mesh to MeshFilter\.sharedMesh/);
  assert.match(tool, /var typedMesh = mesh as UnityEngine\.ProBuilder\.ProBuilderMesh/);
  assert.match(tool, /connectedFaces = UnityEngine\.ProBuilder\.MeshOperations\.ConnectElements\.Connect\(typedMesh, \(UnityEngine\.ProBuilder\.Face\[\]\)plan\.FrozenFaces\)/);
  assert.match(tool, /connectedFaces = UnityEngine\.ProBuilder\.MeshOperations\.ConnectElements\.Connect\(typedMesh, \(UnityEngine\.ProBuilder\.Edge\[\]\)plan\.FrozenEdges\)\.item1/);
  assert.match(tool, /connectedFaces == null \|\| connectedFaces\.Length != plan\.ExpectedFaceCount/);
  assert.match(tool, /ConnectedCount = connectedFaces\.Length/);
  assert.match(proof, /var vertices = CanonicalMeshSnapshot\.BuildManagedVertices\(before\)/);
  assert.match(proof, /var profile = before\.RawVertices\[0\]/);
  assert.match(proof, /new RawPlan\(value, profile\)/);
  assert.match(proof, /if \(profile\.Color\.IsPresent\) result\.color = value\.color/);
  assert.match(proof, /if \(profile\.LocalNormal\.IsPresent\) result\.normal = value\.normal/);
  assert.match(proof, /if \(profile\.Tangent\.IsPresent\) result\.tangent = value\.tangent/);
  assert.doesNotMatch(proof, /mesh\.GetVertices\(\)/, "pre-effect planning must not materialize ProBuilder normal or tangent caches");

  const buildChildren = proof.slice(proof.indexOf("private static FacePlan[] BuildChildren"), proof.indexOf("private static IEnumerable InvokePure"));
  assert.doesNotMatch(buildChildren, /elementGroup/);
  assert.doesNotMatch(buildChildren, /\.SetValue\(/);
  assert.match(proof, /element\.GetValue\(expected\.Face\) is int group\) \|\| group != actual\.ElementGroup/);

  for (const token of ["ExpectedRaw", "WindingMatches", "BoundaryMatches", "IsCenterConnection", "UnselectedFaces", "TryCalculateHardNormals", "source.tangent"]) {
    assert.doesNotMatch(proof, new RegExp(token));
  }
});
