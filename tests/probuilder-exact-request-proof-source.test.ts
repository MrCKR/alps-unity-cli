import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";

const contractUrl = new URL("../contracts/tool-completion-contract.json", import.meta.url);
const generatedTsPath = resolve(process.cwd(), "src/tools/.generated-completion-registry.ts");
const generatedCsPath = resolve(process.cwd(), "unitypkg/Editor/Generated/UnityCliToolCompletionRegistry.g.cs");

const requestSpecific = new Map([
  ["probuilder.append-vertices-to-edge", "request-edge-chain-readback"],
  ["probuilder.auto-smooth", "request-smoothing-compatibility-readback"],
  ["probuilder.bevel-edges", "request-bevel-rail-readback"],
  ["probuilder.bridge-edges", "request-bridge-face-readback"],
  ["probuilder.connect-elements", "request-connect-topology-readback"],
  ["probuilder.create-shape", "request-shape-topology-record-readback"],
  ["probuilder.duplicate-and-flip", "request-duplicate-flipped-face-readback"],
  ["probuilder.extrude-edges", "request-edge-extrusion-readback"],
  ["probuilder.extrude-faces", "request-face-extrusion-readback"],
  ["probuilder.flip-normals", "request-face-winding-readback"],
  ["probuilder.insert-vertex", "request-insert-vertex-readback"],
  ["probuilder.merge-objects", "request-world-triangle-material-union-readback"],
  ["probuilder.merge-vertices", "request-shared-vertex-remap-readback"],
  ["probuilder.subdivide", "request-face-child-union-readback"],
  ["probuilder.weld-vertices", "request-weld-cluster-remap-readback"],
]);

const atomicMutations = [
  "probuilder.center-pivot",
  "probuilder.combine-meshes",
  "probuilder.convert-to-probuilder",
  "probuilder.create-poly-shape",
  "probuilder.create-polygon",
  "probuilder.delete-faces",
  "probuilder.detach-faces",
  "probuilder.freeze-transform",
  "probuilder.merge-faces",
  "probuilder.move-vertices",
  "probuilder.repair-mesh",
  "probuilder.set-face-color",
  "probuilder.set-face-material",
  "probuilder.set-face-uvs",
  "probuilder.set-pivot",
  "probuilder.set-smoothing",
  "probuilder.split-vertices",
];

test("completion contract exposes exactly fifteen request-specific ProBuilder tools", async () => {
  const contract = JSON.parse(await readFile(contractUrl, "utf8")) as { tools: Array<Record<string, any>> };
  const probuilder = contract.tools.filter((entry) => typeof entry.toolName === "string" && entry.toolName.startsWith("probuilder."));
  const observed = probuilder.filter((entry) => entry.executionClass === "observed-state");
  assert.equal(requestSpecific.size, 15);
  assert.deepEqual(observed.map((entry) => entry.toolName).sort(), [...requestSpecific.keys()].sort());

  for (const [toolName, predicate] of requestSpecific) {
    const entry = probuilder.find((candidate) => candidate.toolName === toolName);
    assert.ok(entry, `${toolName} has a contract`);
    assert.equal(entry.executionClass, "observed-state");
    assert.equal(entry.completionPredicate.kind, predicate);
    assert.equal(entry.proofSource.kind, "fresh-request-specific-readback");
  }
});

test("the seventeen ordinary ProBuilder mutations remain atomic typed readbacks", async () => {
  const contract = JSON.parse(await readFile(contractUrl, "utf8")) as { tools: Array<Record<string, any>> };
  assert.equal(atomicMutations.length, 17);
  for (const toolName of atomicMutations) {
    const entry = contract.tools.find((candidate) => candidate.toolName === toolName);
    assert.ok(entry, `${toolName} has a contract`);
    assert.equal(entry.executionClass, "atomic");
    assert.equal(entry.completionPredicate.kind, "typed-business-terminal");
    assert.equal(entry.proofSource.kind, "typed-result-and-effect-readback");
  }
});

test("generated registries carry the topology-record predicate for create-shape", async () => {
  const [typescript, csharp] = await Promise.all([readFile(generatedTsPath, "utf8"), readFile(generatedCsPath, "utf8")]);
  const expected = "request-shape-topology-record-readback";
  assert.match(typescript, new RegExp(`"probuilder\\.create-shape"[\\s\\S]{0,500}"completionPredicate": "${expected}"`));
  assert.match(csharp, new RegExp(`"probuilder\\.create-shape"[^\\n]+"${expected}"`));
  assert.doesNotMatch(typescript, /request-shape-fingerprint-readback/);
  assert.doesNotMatch(csharp, /request-shape-fingerprint-readback/);
});
