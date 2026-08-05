import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const directory = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

const tools: Record<string, { capture: RegExp; consume: RegExp; matches: RegExp }> = {
  "auto-smooth.cs": { capture: /ProBuilderFaceCompletionProof\.CaptureAutoSmooth\(go, faceIndices, angleThreshold\)/, consume: /ApplySmoothingGroups/, matches: /ProBuilderFaceCompletionProof\.AutoSmoothMatches\(plan, after\)/ },
  "duplicate-and-flip.cs": { capture: /ProBuilderFlipCompletionProof\.CaptureDuplicateAndFlip\(go, frozenFaces, requestedFaceIndices\)/, consume: /AppendElements\.DuplicateAndFlip\(typedMesh, plan\.FrozenFaces\)/, matches: /ProBuilderFlipCompletionProof\.DuplicateAndFlipMatches\(plan, target\)/ },
  "extrude-faces.cs": { capture: /ProBuilderFaceExtrudeCompletionProof\.TryCreate\(go, typedMesh, new\[\] \{ 0 \}, method, distance\)/, consume: /ExtrudeElements\.Extrude\(typedMesh, plan\.FrozenFaces, plan\.Method, plan\.Distance\)/, matches: /ProBuilderFaceExtrudeCompletionProof\.Matches\(plan, target\)/ },
  "flip-normals.cs": { capture: /ProBuilderFlipCompletionProof\.CaptureFlip\(go, frozenFaces, faceIndices\)/, consume: /foreach \(var face in plan\.FrozenFaces\) face\.Reverse\(\)/, matches: /ProBuilderFlipCompletionProof\.FlipMatches\(plan, target\)/ },
  "subdivide.cs": { capture: /ProBuilderConnectCompletionProof\.CaptureFace\(go, frozenFaces\)/, consume: /ConnectElements\.Connect\(typedMesh, \(UnityEngine\.ProBuilder\.Face\[\]\)plan\.FrozenFaces\)/, matches: /ProBuilderConnectCompletionProof\.Matches\(plan, after\)/ }
};

test("face completion tools commit once and prove a fresh canonical readback", async () => {
  for (const [entry, proof] of Object.entries(tools)) {
    const source = await readFile(new URL(entry, directory), "utf8");
    assert.match(source, proof.capture, `${entry} freezes its dedicated exact plan`);
    assert.ok(source.search(proof.capture) < source.indexOf("ctx.CommitEffect()"), `${entry} captures before its effect barrier`);
    assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1, `${entry} has exactly one effect barrier`);
    assert.match(source, proof.consume, `${entry} consumes frozen plan fields in its effect`);
    assert.match(source, proof.matches, `${entry} uses its dedicated fresh completion predicate`);
    assert.match(source, /OutcomeUnknown\([^\n]+"invoke"/);
    assert.match(source, /OutcomeUnknown\([^\n]+"readback"/);
    assert.match(source, /OutcomeUnknown\([^\n]+"postcondition"/);
    assert.doesNotMatch(source, /(?:hash|signature|HasObservedMutation|CaptureMeshState|FaceFullAttribute|SharedVertexValue)/i);
  }
});

test("auto smooth freezes the exact 5.2.4 selected-face terminal groups", async () => {
  const source = await readFile(new URL("ProBuilderFaceCompletionProof.cs", directory), "utf8");
  assert.match(source, /AutoSmoothFaceClaim\[\] ExpectedFaces/);
  assert.match(source, /cachedNormalsPresent = before\.RawVertices\.All\(value => value\.LocalNormal\.IsPresent\)/);
  assert.match(source, /cachedNormalsAbsent = before\.RawVertices\.All\(value => !value\.LocalNormal\.IsPresent\)/);
  assert.match(source, /if \(cachedNormalsPresent\)/);
  assert.match(source, /!cachedNormalsAbsent \|\| !before\.TryCalculateNormalsAfterSmoothingReset\(selected, out normals\)/);
  assert.doesNotMatch(source, /anySelectedSmoothed/);
  assert.match(source, /Mathf\.Abs\(Mathf\.Cos\(Mathf\.Clamp\(angleThreshold, 0f, 89\.999f\) \* Mathf\.Deg2Rad\)\)/);
  assert.match(source, /edge\.Incidences\.Where\(value => selected\.Contains\(value\.FaceOccurrenceIndex\)\)/);
  assert.match(source, /Mathf\.Abs\(Vector3\.Dot\(normals\[leftA\]\.normalized, normals\[rightA\]\.normalized\)\) > threshold/);
  assert.match(source, /Mathf\.Abs\(Vector3\.Dot\(normals\[leftB\]\.normalized, normals\[rightB\]\.normalized\)\) > threshold/);
  assert.match(source, /new Queue<int>\(\)/);
  assert.match(source, /component\.Count == 1/);
  assert.match(source, /Where\(face => !selected\.Contains\(face\.OccurrenceIndex\)\)\.Select\(face => face\.SmoothingGroup\)/);
  assert.match(source, /usedGroups\.Add\(0\)/);
  assert.match(source, /NextUnusedSmoothingGroup\(nextGroup, usedGroups\)/);
  assert.match(source, /start > 24 && start < 42/);
  assert.match(source, /before\.Faces\.Any\(face => !CanonicalMeshSnapshot\.FaceEqualsIgnoringSmoothingAndNormals\(before, face, before, face\)\)/);
  assert.match(source, /face\.SmoothingGroup == claim\.ExpectedGroup/);
  assert.match(source, /CanonicalMeshSnapshot\.DerivedChannelsMatch\(plan\.Before\)/);
  assert.match(source, /candidate\.SmoothingGroup == \(expectedGroups\.TryGetValue/);
  assert.match(source, /SharedTexturesEqual\(plan\.Before, after\)/);
  assert.doesNotMatch(source, /SmoothRelation|RelationsMatch/);
});

test("auto smooth no-op and mutation paths obey the durable effect barrier", async () => {
  const source = await readFile(new URL("auto-smooth.cs", directory), "utf8");
  const noOp = source.indexOf("AutoSmoothAlreadyMatches");
  const commit = source.indexOf("ctx.CommitEffect()");
  assert.ok(noOp >= 0 && noOp < commit);
  assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(source, /Mathf\.Clamp\(angleThreshold, 0f, 89\.999f\)/);
  assert.match(source, /Changed = false, NoOpReason = "already-matches-angle-threshold"/);
  assert.match(source, /if \(!ProBuilderReflection\.ApplySmoothingGroups/);
  assert.match(source, /if \(!ProBuilderReflection\.RefreshMesh/);
  assert.match(source, /CanonicalMeshSnapshot\.Capture\(freshMesh\.gameObject, Array\.Empty<int>\(\)\)/);
  assert.match(source, /ExpectedSmoothedFaceCount\(plan\)/);
  assert.match(source, /Changed = true/);
});
