import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";

const directory = new URL(`${pathToFileURL(join(process.cwd(), "extensions", "probuilder", "unitypkg", "Editor", "ProBuilder")).href}/`);

const predicates: Record<string, string> = {
  "append-vertices-to-edge.cs": "HasInsertedVertices",
  "auto-smooth.cs": "HasChangedSmoothing",
  "bevel-edges.cs": "HasTopologyExpansion",
  "bridge-edges.cs": "HasTopologyExpansion",
  "connect-elements.cs": "HasTopologyExpansion",
  "delete-faces.cs": "HasRemovedRequestedFaces",
  "detach-faces.cs": "HasRemovedRequestedFaces",
  "duplicate-and-flip.cs": "HasTopologyExpansion",
  "extrude-edges.cs": "HasTopologyExpansion",
  "extrude-faces.cs": "HasInsertedVertices",
  "flip-normals.cs": "HasFlippedRequestedFaces",
  "insert-vertex.cs": "HasInsertedVertices",
  "merge-faces.cs": "HasMergedRequestedFaces",
  "merge-vertices.cs": "HasMergedVertices",
  "split-vertices.cs": "HasSplitVertices",
  "subdivide.cs": "HasSubdividedRequestedFaces"
};

test("targeted ProBuilder topology tools use request-specific completion predicates", async () => {
  for (const [entry, predicate] of Object.entries(predicates)) {
    const source = await readFile(new URL(entry, directory), "utf8");
    assert.match(source, /ProBuilderOperationProof\.Validate(?:Edges|FaceIndices|VertexIndices)|GetFacesByIndices|vertexIndices\.Any/, `${entry} validates its requested selection before commit`);
    assert.match(source, /ProBuilderOperationProof\.Capture\(/, `${entry} captures stable requested geometry`);
    assert.match(source, /ctx\.CommitEffect\(\)/, `${entry} commits only after validation`);
    assert.ok(
      /ProBuilderOperationProof\.ResolveFreshMesh\(request\.Target, go\)/.test(source) || /EditorUtility\.InstanceIDToObject\(sourceInstanceId\) as GameObject/.test(source),
      `${entry} resolves a fresh target mesh after commit`
    );
    assert.match(source, new RegExp(`ProBuilderOperationProof\\.${predicate}\\(`), `${entry} uses its operation-specific completion predicate`);
    assert.doesNotMatch(source, /HasObservedMutation|CaptureMeshState/, `${entry} must not use generic mutation observation`);
  }
});

test("ProBuilder operation proof captures stable geometry and selection fields", async () => {
  const source = await readFile(new URL("ProBuilderOperationProof.cs", directory), "utf8");
  for (const field of ["VertexCount", "FaceCount", "EdgeCount", "Faces", "Edges", "Vertices", "Smoothing", "SharedVertices"]) {
    assert.match(source, new RegExp(`internal .* ${field}`));
  }
  for (const method of ["FaceSignature", "EdgeSignature", "VertexSignature", "ResolveFreshMesh", "ValidateFaceIndices", "ValidateVertexIndices", "ValidateEdges"]) {
    assert.match(source, new RegExp(`${method}\\(`));
  }
  assert.doesNotMatch(source, /HasObservedMutation|CaptureMeshState/);
});

test("detach-faces commits before isolating Undo and proves the detached instance from fresh objects", async () => {
  const source = await readFile(new URL("detach-faces.cs", directory), "utf8");
  const commit = source.indexOf("ctx.CommitEffect()");
  const increment = source.indexOf("Undo.IncrementCurrentGroup()");
  const name = source.indexOf('Undo.SetCurrentGroupName("ProBuilder Detach Faces")');

  assert.ok(commit >= 0 && increment > commit && name > increment, "detach-faces must create and name its Undo group after CommitEffect");
  assert.match(source, /var undoGroup = Undo\.GetCurrentGroup\(\);/);
  assert.match(source, /Undo\.RevertAllDownToGroup\(undoGroup\);/);
  assert.ok(source.indexOf("try") < source.indexOf('Undo.RecordObject(go, "ProBuilder Detach Faces")'), "detach-faces must roll back every effect in its dedicated Undo group");
  assert.doesNotMatch(source, /var undoGroup = Undo\.GetCurrentGroup\(\);[\s\S]{0,200}ctx\.CommitEffect\(\)/);
  const sourceMeshUndo = source.indexOf("var sourceProBuilderMesh = mesh as UnityEngine.Object;");
  const detachedMeshUndo = source.indexOf("var detachedProBuilderMesh = detachedMesh as UnityEngine.Object;");
  const completeUndo = source.indexOf('Undo.RegisterCompleteObjectUndo(undoTargets, "ProBuilder Detach Faces");');
  const firstDeleteFaces = source.indexOf("ProBuilderReflection.DeleteFaces");

  assert.ok(sourceMeshUndo > name && detachedMeshUndo > sourceMeshUndo, "detach-faces must snapshot both actual ProBuilderMesh components in its dedicated group");
  assert.match(source, /go\.GetComponent<MeshFilter>\(\)\?\.sharedMesh/);
  assert.match(source, /detached\.GetComponent<MeshFilter>\(\)\?\.sharedMesh/);
  assert.ok(completeUndo > detachedMeshUndo && completeUndo < firstDeleteFaces, "detach-faces must register complete topology and Unity Mesh snapshots before its first DeleteFaces call");
  assert.ok(source.indexOf("Undo.RevertAllDownToGroup(undoGroup)") > completeUndo, "detach-faces rollback must target only the dedicated Undo group after all recorded effects");
  assert.match(source, /var detachedInstanceId = detached\.GetInstanceID\(\);/);
  assert.match(source, /EditorUtility\.InstanceIDToObject\(sourceInstanceId\) as GameObject/);
  assert.match(source, /EditorUtility\.InstanceIDToObject\(detachedInstanceId\) as GameObject/);
  assert.match(source, /ProBuilderReflection\.GetProBuilderMesh\(freshDetached\)/);
  assert.match(source, /DetachedObject = new ObjectRef \{ Id = freshDetached\.GetInstanceID\(\), Name = freshDetached\.name, Path = HierarchyPath\(freshDetached\) \}/);
});

const requestProofs: Record<string, string> = {
  "center-pivot.cs": "CenteredPivotPreservesWorldGeometry",
  "combine-meshes.cs": "SourcesWereRemovedAndGeometryIncluded",
  "convert-to-probuilder.cs": "ConvertedGeometryMatchesSource",
  "create-poly-shape.cs": "PolyShapeMatchesRequest",
  "create-polygon.cs": "PolygonWasCreatedFromRequestedVertices",
  "create-shape.cs": "CreatedShapeMatchesRequest",
  "freeze-transform.cs": "TransformWasBaked",
  "move-vertices.cs": "VerticesMovedByRequestedOffset",
  "repair-mesh.cs": "RepairReducedInvalidGeometry",
  "set-face-color.cs": "RequestedFacesHaveColor",
  "set-face-material.cs": "RequestedFacesUseMaterial",
  "set-face-uvs.cs": "RequestedFacesHaveUv",
  "set-pivot.cs": "TransformMatchesRequestedPivot",
  "set-smoothing.cs": "RequestedFacesHaveSmoothingGroup"
};

test("targeted ProBuilder tools use request-specific fresh readback instead of generic mutation snapshots", async () => {
  for (const [entry, proof] of Object.entries(requestProofs)) {
    const source = await readFile(new URL(entry, directory), "utf8");
    assert.doesNotMatch(source, /HasObserved(?:Object)?Mutation\(/, `${entry} must not use a generic mutation predicate`);
    assert.match(source, /ProBuilderInput\.ResolveTarget\(/, `${entry} must fresh-resolve the target after commit`);
    assert.match(source, new RegExp(`ProBuilderRequestProof\\.${proof}`), `${entry} must prove its own request fields`);
  }
});
