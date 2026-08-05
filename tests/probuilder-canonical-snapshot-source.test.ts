import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const directory = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
const snapshot = new URL("ProBuilderCanonicalMeshSnapshot.cs", directory);

async function source() {
  return readFile(snapshot, "utf8");
}

test("canonical snapshot freezes original object, scene, and component identity for strongly typed fresh resolution", async () => {
  const text = await source();
  for (const field of ["GameObjectInstanceId", "GlobalObjectId", "SceneHandle", "ScenePath", "ProBuilderMeshInstanceId"]) assert.match(text, new RegExp(field));
  assert.match(text, /internal static ProBuilderMesh ResolveFreshMesh\(string target, ObjectIdentityClaim claim\)/);
  assert.match(text, /mesh\.GetInstanceID\(\) != claim\.ProBuilderMeshInstanceId/);
  assert.match(text, /go\.scene\.handle != claim\.SceneHandle/);
  assert.match(text, /go\.scene\.path, claim\.ScenePath/);
  assert.match(text, /GetGlobalObjectIdSlow\(go\).*claim\.GlobalObjectId/);
  assert.doesNotMatch(text, /listenerGeneration|ListenerGeneration/, "the layer must not invent unavailable listener generation");
});

test("capture stores every face and full edge incidence while touched only gates local preconditions", async () => {
  const text = await source();
  assert.match(text, /for \(var occurrence = 0; occurrence < allFaces\.Count; occurrence\+\+\)/, "all face occurrences are captured");
  assert.match(text, /var touchedSet = new HashSet<int>\(touched\)/);
  assert.match(text, /internal int\[\] TouchedOccurrences/);
  assert.match(text, /touchedOccurrences = \(int\[\]\)touched\.Clone\(\)/);
  assert.match(text, /requested == null \? Enumerable\.Range\(0, count\)\.ToArray\(\)/, "null means every face is consumed");
  assert.doesNotMatch(text, /requested\.Length == 0/, "an empty touched set is a valid topology-only capture");
  assert.match(text, /var touchedFace = touchedSet\.Contains\(occurrence\)/);
  assert.match(text, /CaptureBoundary\(triangles, handles, allowCollapsedSharedEdges \|\| !touchedFace\)/, "untouched collapsed faces remain comparable without blocking an unrelated request");
  assert.match(text, /!allowCollapsedSharedEdges && touchedFace && \(HasDegenerateTriangle\(triangles, positions\) \|\| !HasSimpleConnectedBoundary\(boundary\)\)/);
  assert.match(text, /BuildEdges\(captured\)/, "shared edge incidence derives from all captured faces");
  assert.match(text, /incidences\.Add\(new BoundaryIncidence\(face\.OccurrenceIndex, edge\)\)/);
  assert.match(text, /UnselectedFacesExactlyPreserved/);
  assert.match(text, /var remaining = new List<FaceOccurrence>\(after\.faces\)/, "unselected preservation starts with all fresh faces");
  assert.match(text, /remaining\.FindIndex\(candidate => FaceFullAttributeEquals\(before, source, after, candidate\)\)/, "unselected faces are compared by cross-snapshot value, not occurrence number");
  assert.match(text, /remaining\.RemoveAt\(index\)/, "each unselected face consumes exactly one fresh occurrence");
});

test("authoritative optional channels preserve absence and reject malformed values without lazy mesh access", async () => {
  const text = await source();
  for (const token of ["ChannelValue LocalNormal, Tangent, Color", "bool IsPresent", "ReadOptional(mesh.normals", "ReadOptional(mesh.tangents", "ReadOptional(mesh.colors", "mesh.GetUVs(channel, values)"]) assert.match(text, new RegExp(token.replace(/[()]/g, "\\$&")));
  assert.match(text, /mesh\.textures\?\.Select/);
  assert.match(text, /GetComponent<MeshFilter>\(\)\?\.sharedMesh/);
  assert.match(text, /values == null \|\| values\.Count == 0/, "empty cached channels are absent");
  assert.match(text, /values\.Count != count \|\| values\.Any\(value => !Finite\(value\)\)/, "partial or non-finite cached channels fail closed");
  assert.match(text, /values\.Length != 0 && values\.Length != count \|\| values\.Any\(value => !Finite\(value\)\)/, "partial or non-finite UV channels fail closed");
  assert.match(text, /TryReadSharedTextures/);
  assert.match(text, /TryCalculateHardNormals/);
  assert.match(text, /var cross = UnityEngine\.ProBuilder\.Math\.Normal\(rawVertices\[a\]\.LocalPosition, rawVertices\[b\]\.LocalPosition, rawVertices\[c\]\.LocalPosition\);\s*cross\.Normalize\(\)/,
    "hard-normal planning mirrors both 5.2.4 normalization steps");
  assert.doesNotMatch(text, /\.GetNormals\(\)/, "hard-normal planning must not populate the ProBuilder cache");
  assert.match(text, /GetProperty\("sharedTextures", BindingFlags\.Instance \| BindingFlags\.NonPublic\)/);
  assert.match(text, /new ChannelValue\(false, Vector4\.zero\)/, "absent values retain an explicit false presence bit");
  assert.doesNotMatch(text, /mesh\.GetUVs\(0|mesh\.GetUVs\(1/);
});

test("normal and tangent preflight mirror full refresh arithmetic", async () => {
  const text = await source();
  assert.match(text, /internal static bool NormalArithmeticFinite/);
  assert.match(text, /UnityEngine\.ProBuilder\.Math\.Normal\(positions\[stream\[index\]\], positions\[stream\[index \+ 1\]\], positions\[stream\[index \+ 2\]\]\)/);
  assert.match(text, /internal static bool TangentArithmeticFinite/);
  assert.match(text, /var r = 1\.0f \/ \(s1 \* t2 - s2 \* t1\)/);
  assert.match(text, /!IsFinite\(r\) \|\| !Finite\(sdir\) \|\| !Finite\(tdir\)/);
  assert.match(text, /var next1 = tan1\[raw\] \+ sdir; var next2 = tan2\[raw\] \+ tdir/);
  assert.match(text, /!Finite\(next1\) \|\| !Finite\(next2\)/);
});

test("canonical capture pins the current serialized format and compiled mesh binding", async () => {
  const text = await source();
  assert.match(text, /internal static bool CurrentMeshFormat\(ProBuilderMesh mesh\)/);
  assert.match(text, /GetField\("m_MeshFormatVersion", BindingFlags\.Instance \| BindingFlags\.NonPublic\)/);
  assert.match(text, /version == 2/);
  assert.match(text, /CompiledMeshBindingMatches\(mesh, compiledMesh\)/);
  assert.match(text, /GetField\("m_Mesh", BindingFlags\.Instance \| BindingFlags\.NonPublic\)/);
  assert.match(text, /field\.FieldType == typeof\(Mesh\)/);
  assert.match(text, /ReferenceEquals\(field\.GetValue\(mesh\), compiledMesh\)/);
});

test("predicted terminal topology must remain readable by strict canonical capture", async () => {
  const text = await source();
  assert.match(text, /internal static bool TerminalTopologyReadable/);
  assert.match(text, /handles = Enumerable\.Repeat\(-1, positions\.Count\)\.ToArray\(\)/);
  assert.match(text, /!ExactEquals\(positions\[raw\], canonical\)/, "approximate shared merging must fail before effect");
  assert.match(text, /HasDegenerateTriangle\(stream, positions\)/);
  assert.match(text, /CaptureBoundary\(stream, handles\)/);
  assert.match(text, /!HasSimpleConnectedBoundary\(boundary\)/);
  assert.match(text, /handles\.Any\(handle => handle < 0\)/, "every predicted raw must belong to exactly one shared group");
});

test("approved pre-effect topology planners clone managed vertices without cache materialization", async () => {
  const [snapshotText, connect, insert] = await Promise.all([
    source(),
    readFile(new URL("ProBuilderConnectCompletionProof.cs", directory), "utf8"),
    readFile(new URL("ProBuilderInsertCompletionProof.cs", directory), "utf8"),
  ]);
  assert.match(snapshotText, /internal static List<Vertex> BuildManagedVertices\(CanonicalMeshSnapshot snapshot\)/);
  assert.match(snapshotText, /new Vertex \{ position = raw\.LocalPosition \}/);
  for (const proof of [connect, insert]) {
    assert.match(proof, /CanonicalMeshSnapshot\.BuildManagedVertices\(/);
    assert.doesNotMatch(proof, /mesh\.GetVertices\(\)/);
  }
});

test("fresh canonical captures include every compiled Mesh field consumed by dedicated completion proofs", async () => {
  const text = await source();
  for (const token of ["CompiledMeshInstanceId", "IndexFormat", "positions, normals", "tangents", "colors", "uvs", "CompiledSubmeshSnapshot", "MeshTopology", "indexes", "Bounds = mesh.bounds", "mesh.GetUVs(channel, values)", "mesh.subMeshCount", "CaptureCompiledMesh(compiledMesh"]) {
    assert.match(text, new RegExp(token.replace(/[()]/g, "\\$&")));
  }
  assert.match(text, /new CanonicalMeshSnapshot\([^\n]+compiled\)/);
  assert.match(text, /CaptureCompiledMesh\(compiledMesh, renderer, rawVertices, captured\.ToArray\(\), allowEmptyMesh\)/);
  assert.match(text, /raw\.Length > ushort\.MaxValue \? UnityEngine\.Rendering\.IndexFormat\.UInt32 : UnityEngine\.Rendering\.IndexFormat\.UInt16/);
  assert.match(text, /mesh\.indexFormat != expectedIndexFormat/);
});

test("snapshot captures raw streams, face occurrences, and shared partitions without prescribing operation comparison algorithms", async () => {
  const text = await source();
  for (const token of ["RawVertex", "FaceOccurrence", "DirectedRawTriangleStream", "DirectedSharedTriangleStream", "sharedVertices", "sharedTextures"]) {
    assert.match(text, new RegExp(token));
  }
  assert.match(text, /new FaceOccurrence\(/);
  assert.match(text, /new RawVertex\(/);
  assert.match(text, /CaptureBoundary\(triangles, handles, allowCollapsedSharedEdges \|\| !touchedFace\)/);
  assert.doesNotMatch(text, /CommitEffect|Undo\.|\.Refresh\(/, "snapshot capture remains read-only");
});

test("boundary derives from directed triangle half-edges and material comparison uses clamped effective slots", async () => {
  const text = await source();
  assert.match(text, /CaptureBoundary\(triangles, handles, allowCollapsedSharedEdges \|\| !touchedFace\)/);
  assert.match(text, /triangle half-edge/);
  assert.match(text, /values\[0\]\.RawA != values\[1\]\.RawB \|\| values\[0\]\.RawB != values\[1\]\.RawA/);
  assert.match(text, /outgoing\.ContainsKey\(edge\.SharedA\) \|\| incoming\.ContainsKey\(edge\.SharedB\)/);
  assert.match(text, /visited\.Count == outgoing\.Count/);
  assert.doesNotMatch(text, /face\.edges/);
  for (const state of ["MaterialState.RendererMissing", "MaterialState.NoSlots", "MaterialState.Null", "MaterialState.Object"]) assert.match(text, new RegExp(state.replace(".", "\\.")));
  assert.match(text, /Mathf\.Clamp\(rawSubmeshIndex, 0, materials\.Length - 1\)/);
  assert.match(text, /GetField\("m_Material", BindingFlags\.Instance \| BindingFlags\.NonPublic\)/);
  assert.match(text, /SerializedFaceMaterial/);
  assert.match(text, /WithSerializedFaceMaterial\(CaptureMaterial\(renderer, materials, face\.submeshIndex\)/);
  assert.match(text, /MaterialValueEquals[\s\S]*SerializedFaceMaterial/);
  assert.match(text, /AssetDatabase\.TryGetGUIDAndLocalFileIdentifier/);
  assert.match(text, /left\.HasAssetIdentity \|\| right\.HasAssetIdentity/);
  assert.match(text, /left\.AssetGuid == right\.AssetGuid/);
  assert.match(text, /return left\.InstanceId == right\.InstanceId/);
});

test("snapshot records finite transform matrices and leaves their equality to operation semantics", async () => {
  const text = await source();
  assert.match(text, /Matrix4x4 LocalToWorldMatrix, WorldToLocalMatrix/);
  assert.match(text, /!Finite\(go\.transform\.localToWorldMatrix\) \|\| !Finite\(go\.transform\.worldToLocalMatrix\)/);
  assert.match(text, /private static bool Finite\(Matrix4x4 value\)/);
  assert.match(text, /TransformMatricesEqual\(CanonicalMeshSnapshot left, CanonicalMeshSnapshot right\)/);
  assert.match(text, /ExactEquals\(left\.LocalToWorldMatrix, right\.LocalToWorldMatrix\)/);
  assert.match(text, /ExactEquals\(float left, float right\) => IsFinite\(left\) && IsFinite\(right\) && left == right/);
  assert.doesNotMatch(text, /GeometryEpsilon|PlanarityEpsilon|RelativeGeometryEpsilon|Mathf\.Min\(PlanarityEpsilon|Mathf\.Abs\(left - right\)/);
  assert.doesNotMatch(text, /CommitEffect|Undo\.|\.Refresh\(/, "the snapshot layer must not mutate or refresh Unity state");
});

test("operation proof exposes typed canonical delegates only", async () => {
  const text = await readFile(new URL("ProBuilderOperationProof.cs", directory), "utf8");
  assert.match(text, /CaptureCanonicalSnapshot\(GameObject go, int\[\] touchedFaceOccurrences = null\)/);
  assert.match(text, /return CanonicalMeshSnapshot\.Capture\(go, touchedFaceOccurrences\)/);
  assert.match(text, /UnityEngine\.ProBuilder\.ProBuilderMesh ResolveFreshCanonicalMesh\(string target, CanonicalMeshSnapshot\.ObjectIdentityClaim claim\)/);
  assert.match(text, /return CanonicalMeshSnapshot\.ResolveFreshMesh\(target, claim\)/);
});

test("face extrusion maps only the exact current ProBuilder 5.2.4 method names", async () => {
  const [tool, proof, reflection] = await Promise.all([
    readFile(new URL("extrude-faces.cs", directory), "utf8"),
    readFile(new URL("ProBuilderFaceExtrudeCompletionProof.cs", directory), "utf8"),
    readFile(new URL("ProBuilderReflection.cs", directory), "utf8")
  ]);
  assert.match(tool, /request\.Method \?\? "vertexNormal"/);
  assert.match(tool, /method != "individual" && method != "vertexNormal" && method != "faceNormal"/);
  assert.match(proof, /value == "individual"[\s\S]*ExtrudeMethod\.IndividualFaces/);
  assert.match(proof, /value == "vertexNormal"[\s\S]*ExtrudeMethod\.VertexNormal/);
  assert.match(proof, /value == "faceNormal"[\s\S]*ExtrudeMethod\.FaceNormal/);
  assert.doesNotMatch(reflection, /public static bool Extrude\(object mesh, Array faces/);
  assert.doesNotMatch(tool, /method must be vertex or faceNormal/);
});
