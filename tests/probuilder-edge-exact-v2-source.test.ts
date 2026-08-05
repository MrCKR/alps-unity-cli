import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const directory = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
const helper = new URL("ProBuilderEdgeCompletionProof.cs", directory);
const reflection = new URL("ProBuilderReflection.cs", directory);
const tools = {
  "append-vertices-to-edge.cs": { capture: /ProBuilderAppendCompletionProof\.TryCreate\(go, mesh as ProBuilderMesh, frozenEdges, count\)/, consume: /AppendVerticesToEdge\(mesh, plan\.FrozenEdges, plan\.Count\)/, matches: /ProBuilderAppendCompletionProof\.Matches\(plan, target\)/ },
  "bevel-edges.cs": { capture: /ProBuilderBevelCompletionProof\.Capture\(go, typedMesh, frozenEdges, amount\)/, consume: /Bevel\.BevelEdges\(typedMesh, plan\.FrozenEdges, amount\)/, matches: /ProBuilderBevelCompletionProof\.Matches\(plan, after\)/ },
  "bridge-edges.cs": { capture: /ProBuilderEdgeCompletionProof\.Capture\(go, mesh, edgeArray\)/, consume: /TryBridgeFrozenEdges\(mesh, frozenEdgeA, frozenEdgeB, allowNonManifold, out bridged\)/, matches: /ProBuilderEdgeCompletionProof\.BridgeMatches\(source, go, freshMesh, allowNonManifold, out var mismatch\)/ },
  "extrude-edges.cs": { capture: /ProBuilderEdgeCompletionProof\.Capture\(go, mesh, edgeArray\)/, consume: /TryExtrudeFrozenEdges\(mesh, edgeArray, distance, asGroup, out edgeCount\)/, matches: /ProBuilderEdgeCompletionProof\.ExtrudeMatches\(source, go, freshMesh, distance, asGroup, edgeCount\)/ }
} as const;

async function source(name: string) {
  return readFile(name === "helper" ? helper : new URL(name, directory), "utf8");
}

test("edge effects capture canonical snapshots and establish a plan before their sole effect barrier", async () => {
  for (const [tool, proof] of Object.entries(tools)) {
    const text = await source(tool);
    assert.equal((text.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1, `${tool} commits once`);
    assert.match(text, proof.capture, `${tool} freezes its exact plan before commit`);
    assert.ok(text.search(proof.capture) < text.indexOf("ctx.CommitEffect()"), `${tool} plans before commit`);
    assert.match(text, proof.consume, `${tool} consumes only its frozen typed selection`);
    assert.match(text, proof.matches, `${tool} proves its dedicated fresh terminal state`);
    assert.match(text, /RefreshMeshPreservingVertexPayload\((?:mesh|typedMesh)\)/, `${tool} refreshes the mutated typed mesh before readback`);
    assert.match(text, /OutcomeUnknown\([^\n]+"(?:invoke|readback|postcondition)"/);
  }
});

test("exact edge algorithms remain in their dedicated completion helpers", async () => {
  const text = await source("helper");
  const [append, bevel] = await Promise.all(["ProBuilderAppendCompletionProof.cs", "ProBuilderBevelCompletionProof.cs"].map(source));
  const reflectionText = await readFile(reflection, "utf8");
  for (const token of [
    "CaptureCanonicalSnapshot", "ExactEquals", "ResolveFreshCanonicalMesh",
    "TryPlanBridge", "TryPlanExtrude", "endpoints.Distinct().Count() != 4",
    "ExpectedOuterA", "NewRawExactly", "SharedAppendExactly", "SharedExtrudeExactly",
    "enableManifoldExtrude: false"
  ]) assert.match(text, new RegExp(token.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), token);
  for (const token of ["FrozenEdges", "TryCreate", "TriangulateExact", "FacesHaveExclusiveRawOwnership", "MaterialSlotsStable", "CalculateDerivedChannels"]) {
    assert.match(append, new RegExp(token), `append owns ${token}`);
  }
  for (const token of ["FrozenEdges", "Capture", "BuildSharedLookup", "ClampExact", "SlideEdgeExact", "GetBridgeFacesExact", "CalculateNormalsAndTangents", "ClearUnityMeshUv1"]) {
    assert.match(bevel, new RegExp(token), `bevel owns ${token}`);
  }
  assert.doesNotMatch(text, /TryPlanAppend|AppendMatches|TryPlanBevel|BevelMatches|EffectiveBevelAmount/);
  assert.match(reflectionText, /method\.Invoke\(null, new object\[\] \{ mesh, frozenEdges, distance, asGroup, false \}\)/,
    "the actual ProBuilder 5.2.4 edge call must disable manifold extrusion");
  assert.match(text, /result\.Any\(value => SameEdge\(value\.SharedA, value\.SharedB, a\.SharedHandle, b\.SharedHandle\)\)/,
    "duplicate requests are rejected by the unordered shared-handle pair inside one frozen snapshot");
  assert.doesNotMatch(text, /ValueKey|EdgeKey|ToString\("R"\)/,
    "canonical edge proof must not use coordinate string keys");
  assert.doesNotMatch(text, /SameBoundary\([^)]*\)\s*\|\|\s*BoundaryContainsChain/);
  assert.doesNotMatch(text, /TransformPoint\(/);
});

test("handlers reject unreadable exact plans before the effect and do not treat API silence as completion", async () => {
  const [append, bevel, bridge, extrude] = await Promise.all(Object.keys(tools).map(source));
  assert.match(append, /count < 1 \|\| count > 512/);
  assert.match(bevel, /if \(plan == null\)/);
  assert.match(bridge, /TryPlanBridge\(source, allowNonManifold\)/);
  assert.match(extrude, /TryPlanExtrude\(source, distance, asGroup\)/);
  for (const text of [append, bevel, bridge, extrude]) assert.match(text, /postcondition/);
});
