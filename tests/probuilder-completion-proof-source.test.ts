import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { constants } from "node:fs";
import test from "node:test";

const directory = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

const dedicated: Record<string, { capture: RegExp; matches: RegExp }> = {
  "append-vertices-to-edge.cs": { capture: /ProBuilderAppendCompletionProof\.TryCreate\(/, matches: /ProBuilderAppendCompletionProof\.Matches\(/ },
  "auto-smooth.cs": { capture: /ProBuilderFaceCompletionProof\.CaptureAutoSmooth\(/, matches: /ProBuilderFaceCompletionProof\.AutoSmoothMatches\(/ },
  "bevel-edges.cs": { capture: /ProBuilderBevelCompletionProof\.Capture\(/, matches: /ProBuilderBevelCompletionProof\.Matches\(/ },
  "bridge-edges.cs": { capture: /ProBuilderEdgeCompletionProof\.Capture\(/, matches: /ProBuilderEdgeCompletionProof\.BridgeMatches\(/ },
  "connect-elements.cs": { capture: /ProBuilderConnectCompletionProof\.Capture\(/, matches: /ProBuilderConnectCompletionProof\.Matches\(/ },
  "create-shape.cs": { capture: /CaptureCreateShapePlan\(/, matches: /CreatedShapeMismatch\(plan/ },
  "duplicate-and-flip.cs": { capture: /ProBuilderFlipCompletionProof\.CaptureDuplicateAndFlip\(/, matches: /ProBuilderFlipCompletionProof\.DuplicateAndFlipMatches\(/ },
  "extrude-edges.cs": { capture: /ProBuilderEdgeCompletionProof\.Capture\(/, matches: /ProBuilderEdgeCompletionProof\.ExtrudeMatches\(/ },
  "extrude-faces.cs": { capture: /ProBuilderFaceExtrudeCompletionProof\.TryCreate\(/, matches: /ProBuilderFaceExtrudeCompletionProof\.Matches\(/ },
  "flip-normals.cs": { capture: /ProBuilderFlipCompletionProof\.CaptureFlip\(/, matches: /ProBuilderFlipCompletionProof\.FlipMatches\(/ },
  "insert-vertex.cs": { capture: /ProBuilderInsertCompletionProof\.Capture\(/, matches: /ProBuilderInsertCompletionProof\.Matches\(/ },
  "merge-objects.cs": { capture: /ProBuilderVertexObjectCompletionProof\.CaptureMergeObjects\(/, matches: /ProBuilderVertexObjectCompletionProof\.MergeObjectsMatches\(/ },
  "merge-vertices.cs": { capture: /ProBuilderVertexObjectCompletionProof\.CaptureMergeVertices\(/, matches: /ProBuilderVertexObjectCompletionProof\.MergeVerticesMatches\(/ },
  "subdivide.cs": { capture: /ProBuilderConnectCompletionProof\.CaptureFace\(/, matches: /ProBuilderConnectCompletionProof\.Matches\(/ },
  "weld-vertices.cs": { capture: /ProBuilderVertexObjectCompletionProof\.CaptureWeld\(/, matches: /ProBuilderVertexObjectCompletionProof\.WeldMatches\(/ },
};

const atomic = [
  "center-pivot.cs",
  "combine-meshes.cs",
  "convert-to-probuilder.cs",
  "create-poly-shape.cs",
  "create-polygon.cs",
  "delete-faces.cs",
  "detach-faces.cs",
  "freeze-transform.cs",
  "merge-faces.cs",
  "move-vertices.cs",
  "repair-mesh.cs",
  "set-face-color.cs",
  "set-face-material.cs",
  "set-face-uvs.cs",
  "set-pivot.cs",
  "set-smoothing.cs",
  "split-vertices.cs",
];

const removedOwners = /ProBuilder(?:AttributeTransform|ComplexTopology|LocalTopology)CompletionProof|ProBuilderRequestProof/;

test("only the approved fifteen ProBuilder tools use request-specific completion proof", async () => {
  assert.equal(Object.keys(dedicated).length, 15);
  for (const [entry, proof] of Object.entries(dedicated)) {
    const source = await readFile(new URL(entry, directory), "utf8");
    const commit = source.indexOf("ctx.CommitEffect()");
    assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1, `${entry} has one effect barrier`);
    assert.ok(source.search(proof.capture) >= 0 && source.search(proof.capture) < commit, `${entry} freezes its request-specific proof before the barrier`);
    assert.match(source, proof.matches, `${entry} consumes its dedicated fresh readback`);
    assert.match(source, /ProBuilderErrors\.OutcomeUnknown\(/, `${entry} fails closed after effect uncertainty`);
    assert.doesNotMatch(source.slice(commit), /\brequest\.[A-Z]/, `${entry} does not read mutable request data after commit`);
  }
});

test("the remaining seventeen ProBuilder mutations use only minimal atomic readback", async () => {
  assert.equal(atomic.length, 17);
  for (const entry of atomic) {
    const source = await readFile(new URL(entry, directory), "utf8");
    const commit = source.indexOf("ctx.CommitEffect()");
    assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1, `${entry} has one effect barrier in source`);
    assert.match(source, /ProBuilderAtomicReadback\./, `${entry} uses the shared minimal typed readback owner`);
    assert.match(source, /ProBuilderErrors\.OutcomeUnknown\(/, `${entry} fails closed after effect uncertainty`);
    assert.doesNotMatch(source, removedOwners, `${entry} does not retain a removed strict proof owner`);
    assert.doesNotMatch(source, /CanonicalMeshSnapshot/, `${entry} does not build a full canonical plan`);
    assert.doesNotMatch(source.slice(commit), /\brequest\.[A-Z]/, `${entry} does not read mutable request data after commit`);
  }
});

test("removed ProBuilder proof owners and full-state shape template stay absent", async () => {
  for (const name of [
    "ProBuilderAttributeTransformCompletionProof.cs",
    "ProBuilderComplexTopologyCompletionProof.cs",
    "ProBuilderLocalTopologyCompletionProof.cs",
    "ProBuilderRequestProof.cs",
    "ProBuilderShapeTemplates.g.cs",
  ]) await assert.rejects(access(new URL(name, directory), constants.F_OK), `${name} must remain absent`);
});

test("detach-faces keeps one Undo group without rolling back uncertain effects", async () => {
  const source = await readFile(new URL("detach-faces.cs", directory), "utf8");
  const commit = source.indexOf("ctx.CommitEffect()");
  assert.ok(source.indexOf("Undo.IncrementCurrentGroup()") > commit);
  assert.match(source, /Undo\.GetCurrentGroup\(\)/);
  assert.match(source, /ProBuilderAtomicReadback\.RegisterMeshUndo\(go, mesh/);
  assert.match(source, /Undo\.RegisterCreatedObjectUndo\(detached/);
  assert.match(source, /Undo\.CollapseUndoOperations\(undoGroup\)/);
  assert.doesNotMatch(source, /RevertAllDownToGroup|PerformUndo/);
  assert.match(source, /EditorUtility\.InstanceIDToObject\(sourceId\) as GameObject/);
  assert.match(source, /EditorUtility\.InstanceIDToObject\(detachedId\) as GameObject/);
});
