import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const directory = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

const unprovableSelectionTools = [
  "append-vertices-to-edge.cs",
  "auto-smooth.cs",
  "bevel-edges.cs",
  "bridge-edges.cs",
  "connect-elements.cs",
  "duplicate-and-flip.cs",
  "extrude-edges.cs",
  "extrude-faces.cs",
  "flip-normals.cs",
  "insert-vertex.cs",
  "subdivide.cs"
];

test("unprovable ProBuilder selections are direct-prohibited while frozen handlers remain executable", async () => {
  const contract = JSON.parse(await readFile(new URL("../contracts/tool-completion-contract.json", import.meta.url), "utf8")) as {
    tools: Array<{ toolName: string; directInvocationPolicy?: { kind: string }; frozenDeliveryPolicy?: { kind: string } }>;
  };
  const byName = new Map(contract.tools.map((entry) => [entry.toolName, entry]));
  for (const entry of unprovableSelectionTools) {
    const source = await readFile(new URL(entry, directory), "utf8");
    const toolName = `probuilder.${entry.slice(0, -3)}`;
    assert.equal(byName.get(toolName)?.directInvocationPolicy?.kind, "prohibited-unprovable");
    assert.equal(byName.get(toolName)?.frozenDeliveryPolicy?.kind, "preserved");
    assert.match(source, /ctx\.CommitEffect\(\)/, `${entry} retains frozen batch execution`);
    assert.doesNotMatch(source, /SelectionProofUnavailable|CS0162/);
    assert.match(source, /Checked(?:Int|Float|Vector3)\(/, `${entry} validates narrowing conversions before effect`);
  }
});

test("exact readback paths preserve pivot geometry and do not hide UV reflection failures", async () => {
  const [pivot, uvs, merge, requestProof, operationProof] = await Promise.all([
    readFile(new URL("center-pivot.cs", directory), "utf8"),
    readFile(new URL("set-face-uvs.cs", directory), "utf8"),
    readFile(new URL("merge-objects.cs", directory), "utf8"),
    readFile(new URL("ProBuilderRequestProof.cs", directory), "utf8"),
    readFile(new URL("ProBuilderOperationProof.cs", directory), "utf8")
  ]);

  assert.match(pivot, /CenteredPivotPreservesWorldGeometry/);
  assert.match(pivot, /InverseTransformPoint/);
  assert.match(uvs, /Required ProBuilder 5\.2\.4 UV members are unavailable/);
  assert.match(uvs, /typeof\(IEnumerable<>\)\.MakeGenericType\(faceType\)/);
  assert.match(uvs, /new\[\] \{ refreshUvParameterType \}/);
  assert.match(uvs, /refreshUvMethod\.ReturnType != typeof\(void\)/);
  assert.doesNotMatch(uvs, /catch \(Exception\) \{ \}/);
  assert.match(merge, /!autoConvert && targets\.Any/);
  assert.match(requestProof, /indexes\.Count == requested\.Length && requested\.All\(indexes\.Contains\)/);
  assert.match(operationProof, /before\.Faces\.All\(face => !after\.Faces\.Contains\(face\)\)/);
});

test("ProBuilder request narrowing is range-checked and directional face selection fails closed", async () => {
  const narrowed = [
    "create-polygon.cs", "delete-faces.cs", "detach-faces.cs", "merge-faces.cs", "merge-vertices.cs",
    "set-face-material.cs", "set-smoothing.cs", "split-vertices.cs", "select-faces.cs"
  ];
  for (const entry of narrowed) {
    const source = await readFile(new URL(entry, directory), "utf8");
    assert.match(source, /ProBuilderOperationProof\.CheckedInt\(/, `${entry} range-checks request integers`);
    assert.doesNotMatch(source, /(?:Select|ConvertAll)\([^\n]*=> \(int\)/, `${entry} has no unchecked request narrowing`);
  }

  const selectFaces = await readFile(new URL("select-faces.cs", directory), "utf8");
  assert.match(selectFaces, /using System\.Linq;/);
  assert.match(selectFaces, /normal == Vector3\.zero/);
  assert.match(selectFaces, /Could not read a non-zero normal/);
  for (const entry of ["delete-faces.cs", "detach-faces.cs", "merge-faces.cs", "subdivide.cs"]) {
    const source = await readFile(new URL(entry, directory), "utf8");
    assert.match(source, /using System;/, `${entry} imports System for checked count arithmetic`);
  }
});
