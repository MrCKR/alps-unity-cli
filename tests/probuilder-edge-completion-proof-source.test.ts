import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const directory = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
const helper = new URL("ProBuilderEdgeCompletionProof.cs", directory);

const tools = {
  "append-vertices-to-edge.cs": { owner: "ProBuilderAppendCompletionProof", predicate: "Matches", capture: "TryCreate", frozen: "plan.FrozenEdges", invariants: ["FrozenEdges", "SharedTextures"] },
  "bevel-edges.cs": { owner: "ProBuilderBevelCompletionProof", predicate: "Matches", capture: "Capture", frozen: "plan.FrozenEdges", invariants: ["FrozenEdges", "MaterialSlotsStable"] },
  "bridge-edges.cs": { owner: "ProBuilderEdgeCompletionProof", predicate: "BridgeMatches", capture: "Capture", frozen: "frozenEdgeA", invariants: ["BridgeCrossed", "TriangleArea"] },
  "connect-elements.cs": { owner: "ProBuilderConnectCompletionProof", predicate: "Matches", capture: "Capture", frozen: "plan.Frozen", invariants: ["FaceMode", "FrozenEdges", "FrozenFaces"] },
  "extrude-edges.cs": { owner: "ProBuilderEdgeCompletionProof", predicate: "ExtrudeMatches", capture: "Capture", frozen: "edgeArray", invariants: ["ExpectedOuterA", "asGroup"] }
} as const;

test("each requested edge operation has a separate fresh request predicate", async () => {
  for (const [file, operation] of Object.entries(tools)) {
    const source = await readFile(new URL(file, directory), "utf8");
    const helperName = `${operation.owner}.cs`;
    const proof = await readFile(new URL(helperName, directory), "utf8");
    assert.match(source, new RegExp(`${operation.owner}\\.${operation.predicate}\\(`), `${file} uses its own dedicated predicate`);
    assert.match(source, new RegExp(`${operation.owner}\\.${operation.capture}\\(`), `${file} captures an exact pre-effect plan`);
    assert.ok(source.indexOf(`${operation.owner}.${operation.capture}`) < source.indexOf("ctx.CommitEffect()"), `${file} plans before commit`);
    assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1, `${file} has one committed effect`);
    assert.match(source, new RegExp(operation.frozen.replace(".", "\\.")), `${file} consumes frozen plan data`);
    assert.match(source, /OutcomeUnknown\([^\n]+"(?:invoke|readback|postcondition)"/, `${file} reports post-effect uncertainty`);
    for (const invariant of operation.invariants) assert.match(proof, new RegExp(invariant.replace(/[+.]/g, "\\$&")), `${file} proves ${invariant}`);
  }
});

test("bridge and edge extrusion publish their exact accepted domain and result counts", async () => {
  const [bridge, extrude, operationProof, edgeProof] = await Promise.all([
    readFile(new URL("bridge-edges.cs", directory), "utf8"),
    readFile(new URL("extrude-edges.cs", directory), "utf8"),
    readFile(new URL("ProBuilderOperationProof.cs", directory), "utf8"),
    readFile(new URL("ProBuilderEdgeCompletionProof.cs", directory), "utf8"),
  ]);
  assert.match(bridge, /four distinct shared endpoints/);
  assert.match(bridge, /must not require the ProBuilder triangle-bridge branch/);
  assert.match(bridge, /NewFaceCount = 1/);
  assert.doesNotMatch(bridge, /NewFaceCount = newFaceCount/);
  assert.match(extrude, /Every selected edge must have exactly one readable face incidence/);
  assert.match(extrude, /cached finite normals, UV1 must be absent/);
  assert.match(bridge, /ApplyDeterministicQuadUvs\(mesh, source\[0\]\.Before\.Faces\.Length, 1\)/);
  assert.match(extrude, /ApplyDeterministicQuadUvs\(mesh, source\[0\]\.Before\.Faces\.Length, edgeCount\)/);
  assert.match(operationProof, /internal static bool ApplyDeterministicQuadUvs/);
  assert.match(operationProof, /new\[\] \{ new Vector2\(0f, 0f\), new Vector2\(1f, 0f\), new Vector2\(0f, 1f\), new Vector2\(1f, 1f\) \}/);
  assert.match(operationProof, /manualUV = true/);
  assert.match(operationProof, /var edgeOrBridge = new\[\] \{ raw\[0\] \+ 2, raw\[0\] \+ 1, raw\[0\], raw\[0\] \+ 2, raw\[0\] \+ 3, raw\[0\] \+ 1 \}/);
  assert.match(operationProof, /var faceExtrude = new\[\] \{ raw\[0\], raw\[0\] \+ 1, raw\[0\] \+ 2, raw\[0\] \+ 1, raw\[0\] \+ 3, raw\[0\] \+ 2 \}/);
  assert.match(edgeProof, /actual\.ManualUv/);
  assert.match(edgeProof, /TangentInputsFinite\(before\)/);
  assert.match(edgeProof, /CanonicalMeshSnapshot\.RefreshArithmeticFinite/);
  assert.match(edgeProof, /QuadTangentInputsFinite/);
  assert.match(edgeProof, /SideUv\(quadRawIndex\)/);
  assert.match(edgeProof, /BridgeAttributeFace/);
  assert.match(edgeProof, /FirstValidateEdgeFace\(before, a\) \?\? FirstValidateEdgeFace\(before, b\)/);
  assert.match(edgeProof, /DirectedSharedTriangleStream\.Contains\(source\.SharedA\).*DirectedSharedTriangleStream\.Contains\(source\.SharedB\)/);
});

test("edge completion readback binds boundary topology to the original object identity", async () => {
  const proof = await readFile(helper, "utf8");
  for (const token of ["ObjectIdentityClaim", "ResolveFresh", "CanonicalMeshSnapshot", "Boundary", "BoundaryIncidence", "CaptureCanonicalSnapshot"]) {
    assert.match(proof, new RegExp(token.replace(/[().]/g, "\\$&")));
  }
  assert.match(proof, /CaptureCanonicalSnapshot\(go, Array\.Empty<int>\(\)\)/, "the first pass must tolerate unrelated collapsed faces");
  assert.match(proof, /CaptureCanonicalSnapshot\(go, touched\.OrderBy\(value => value\)\.ToArray\(\)\)/, "incident faces are the only strict pre-effect occurrences");
  assert.match(proof, /CaptureCanonicalSnapshot\(freshMesh\.gameObject, touched\)/, "fresh readback must retain the same strict occurrence scope");
  assert.match(proof, /!Finite\(source\.ExpectedOuterA\) \|\| !Finite\(source\.ExpectedOuterB\)[\s\S]*LocalToWorldMatrix\.MultiplyPoint3x4\(source\.ExpectedOuterA\)[\s\S]*LocalToWorldMatrix\.MultiplyPoint3x4\(source\.ExpectedOuterB\)[\s\S]*!FiniteExtrudeSide\(source\)/, "predicted edge extrusion local/world positions and side normals must remain finite before commit");
  assert.match(proof, /Math\.Normal\(source\.ExpectedOuterA, source\.LocalB, source\.LocalA\)[\s\S]*Math\.Normal\(source\.ExpectedOuterA, source\.ExpectedOuterB, source\.LocalB\)/);
  assert.doesNotMatch(proof, /(?:Mesh|Topology)(?:Hash|Signature)/i);
});
