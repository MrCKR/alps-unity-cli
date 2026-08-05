import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

test("canonical proof keeps strict and weld-relaxed capture separate", async () => {
  const [proof, snapshot] = await Promise.all(["ProBuilderVertexObjectCompletionProof.cs", "ProBuilderCanonicalMeshSnapshot.cs"].map(name => readFile(new URL(name, root), "utf8")));
  assert.match(snapshot, /internal static CanonicalMeshSnapshot Capture\([\s\S]*CaptureCore\(go, touchedFaceOccurrences, false, false\)/);
  assert.match(snapshot, /CaptureRelaxedAfterWeld[\s\S]*CaptureCore\(go, null, true, false\)/);
  assert.match(snapshot, /CaptureAfterMergeVertices[\s\S]*CaptureCore\(go, null, false, true\)/);
  assert.match(proof, /class ExpectedMesh/);
  assert.match(proof, /ExactExpectedEquals/);
  assert.match(proof, /PartitionEquals/);
  const createShape = proof.slice(proof.indexOf("internal sealed class CreateShapePlan"), proof.indexOf("internal sealed class ExpectedMesh"));
  assert.match(createShape, /ProBuilderShapeTopologyRecords\.PackageVersion/);
  assert.doesNotMatch(createShape, /Material|GraphicsSettings|Resources|BuiltinMaterials|unityVersion|2022\.3\.61f1|CanonicalMeshSnapshot/);
  const mergeAndWeld = proof.slice(proof.indexOf("internal static MergeObjectsPlan CaptureMergeObjects"));
  assert.doesNotMatch(mergeAndWeld, /GetType\(\)\.Get(?:Property|Method)|Type\.GetType\(/, "merge and weld exact proofs must not use string reflection");
  assert.doesNotMatch(proof, /CultureInfo/);
});
