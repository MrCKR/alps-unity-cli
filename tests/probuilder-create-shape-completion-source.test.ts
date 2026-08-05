import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { constants } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
const shapes = ["Cube", "Stair", "CurvedStair", "Prism", "Cylinder", "Plane", "Door", "Pipe", "Cone", "Sprite", "Arch", "Sphere", "Torus"];

test("create-shape freezes a 5.2.4 topology record before its effect boundary", async () => {
  const [tool, proof, records, generator] = await Promise.all([
    readFile(new URL("create-shape.cs", root), "utf8"),
    readFile(new URL("ProBuilderVertexObjectCompletionProof.cs", root), "utf8"),
    readFile(new URL("ProBuilderShapeTopologyRecords.g.cs", root), "utf8"),
    readFile(resolve(process.cwd(), "scripts/probuilder/ProBuilderShapeTopologyRecordCapture.cs"), "utf8")
  ]);
  await assert.rejects(access(new URL("ProBuilderShapeTemplates.g.cs", root), constants.F_OK));
  await assert.rejects(access(resolve(process.cwd(), "scripts/probuilder/ProBuilderShapeTemplateCapture.cs"), constants.F_OK));

  assert.match(tool, /ProBuilder 5\.2\.4 topology record/);
  assert.match(tool, /CaptureCreateShapePlan[\s\S]*ctx\.CommitEffect\(\)[\s\S]*CreateShape[\s\S]*localScale = plan\.LocalScale[\s\S]*CreatedShapeMismatch\(plan/);
  assert.match(tool, /freshGo\.GetInstanceID\(\)[\s\S]*freshGo\.name[\s\S]*HierarchyPath\(freshGo\)/);
  assert.match(tool, /Plane and Sprite have a zero Y normal axis/);

  assert.match(proof, /ProBuilderReflection\.InstalledPackageVersion != ProBuilderShapeTopologyRecords\.PackageVersion/);
  assert.match(proof, /record\.DefaultLocalBounds\.center != Vector3\.zero/);
  assert.match(proof, /BoundsFromPositions\(pb\.positions, out var localBounds\)[\s\S]*Approximately\(localBounds\.center, Vector3\.zero\)/);
  assert.match(proof, /Approximately\(renderer\.bounds\.center, plan\.ExpectedWorldBoundsCenter\)/);
  assert.match(proof, /Approximately\(renderer\.bounds\.size, plan\.RequestedWorldSize\)/);
  assert.match(proof, /return plan\.Record\.Mismatch\(mesh\)/);

  assert.match(records, /internal const string PackageVersion = "5\.2\.4"/);
  assert.match(records, /internal sealed class Record[\s\S]*RawVertexCount[\s\S]*faceIndexes[\s\S]*sharedVertices/);
  assert.match(records, /internal string Mismatch\(object mesh\)[\s\S]*pb\.positions\.Count[\s\S]*pb\.faces[\s\S]*pb\.sharedVertices/);
  assert.doesNotMatch(records, /FaceIndexes\s*=>|SharedVertices\s*=>/);
  assert.equal((records.match(/new Record\(/g) ?? []).length, 13);
  for (const shape of shapes) assert.match(records, new RegExp(`Create${shape}\\(\\)`));

  assert.match(generator, /class ProBuilderShapeTopologyRecordCapture/);
  assert.match(generator, /ShapeGenerator\.CreateShape[\s\S]*PivotLocation\.Center/);
  assert.match(generator, /BoundsFromPositions\(positions, out var bounds\)[\s\S]*bounds\.center != Vector3\.zero/);
  assert.match(generator, /pb\.positions\.Count[\s\S]*pb\.faces[\s\S]*pb\.sharedVertices/);
  assert.match(generator, /private static string Header\(\)[\s\S]*class ProBuilderShapeTopologyRecords[\s\S]*internal sealed class Record/);
  assert.match(generator, /Count\(source, "new Record\("\) != Shapes\.Length[\s\S]*WriteAtomically\(outputPath, source\)/);
  assert.match(generator, /File\.WriteAllText\(temporaryPath[\s\S]*File\.Replace\(temporaryPath, fullPath, null\)[\s\S]*File\.Move\(temporaryPath, fullPath\)/);

  const createShapeProof = proof.slice(proof.indexOf("internal sealed class CreateShapePlan"), proof.indexOf("internal sealed class ExpectedMesh"));
  const forbidden = /RawTemplate|FaceTemplate|ChannelTemplate|CompiledTemplate|CanonicalMeshSnapshot|unityVersion|2022\.3\.61f1|Material|GraphicsSettings|MeshFilter|sharedMesh|normal|tangent|color|uv|sharedTextures|unwrap|submesh|selectable|userCollisions|selected|ToMesh|Refresh/;
  for (const [name, source] of [["records", records], ["generator", generator], ["create proof", createShapeProof]]) assert.doesNotMatch(source, forbidden, name);
});
