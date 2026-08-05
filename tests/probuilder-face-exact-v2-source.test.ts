import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
const tools: Record<string, RegExp> = {
  "auto-smooth.cs": /ProBuilderFaceCompletionProof\.CaptureAutoSmooth\(go,/,
  "connect-elements.cs": /ProBuilderConnectCompletionProof\.Capture\(go,/,
  "duplicate-and-flip.cs": /ProBuilderFlipCompletionProof\.CaptureDuplicateAndFlip\(go,/,
  "extrude-faces.cs": /ProBuilderFaceExtrudeCompletionProof\.TryCreate\(go,/,
  "flip-normals.cs": /ProBuilderFlipCompletionProof\.CaptureFlip\(go,/,
  "insert-vertex.cs": /ProBuilderInsertCompletionProof\.Capture\(go,/,
  "subdivide.cs": /ProBuilderConnectCompletionProof\.CaptureFace\(go,/
};

test("face/topology tools freeze a dedicated plan before one effect barrier", async () => {
  for (const [name, capture] of Object.entries(tools)) {
    const source = await readFile(new URL(name, root), "utf8");
    assert.match(source, capture);
    assert.ok(source.search(capture) < source.indexOf("ctx.CommitEffect()"), `${name} captures before effect`);
    assert.equal((source.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1, name);
    assert.match(source, /CompletionProof\.(?:Matches|AutoSmoothMatches|DuplicateAndFlipMatches|FlipMatches)\(/);
    assert.match(source, /OutcomeUnknown\([^\n]+"(?:invoke|readback|postcondition)"/);
  }
});

test("canonical face proof has no area approximation or raw cross-snapshot identity", async () => {
  const source = await readFile(new URL("ProBuilderFaceCompletionProof.cs", root), "utf8");
  assert.match(source, /ExactMultisetEquals/);
  assert.match(source, /RawChannelValueEquals/);
  assert.match(source, /Mathf\.Abs\(Mathf\.Cos\(Mathf\.Clamp\(angleThreshold, 0f, 89\.999f\)/);
  assert.match(source, /Queue<int>/);
  assert.doesNotMatch(source, /TriangleArea|FaceArea|Vector3\.Angle/);
});
