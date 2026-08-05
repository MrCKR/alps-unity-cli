import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import { constants } from "node:fs";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

async function source(name: string) {
  return readFile(new URL(name, root), "utf8");
}

test("approved topology proof helpers retain bounded refresh arithmetic", async () => {
  const names = [
    "ProBuilderCanonicalMeshSnapshot.cs",
    "ProBuilderFaceCompletionProof.cs",
    "ProBuilderVertexObjectCompletionProof.cs",
    "ProBuilderConnectCompletionProof.cs",
    "ProBuilderInsertCompletionProof.cs",
    "ProBuilderAppendCompletionProof.cs",
    "ProBuilderBevelCompletionProof.cs",
    "ProBuilderFlipCompletionProof.cs",
    "ProBuilderEdgeCompletionProof.cs",
    "ProBuilderFaceExtrudeCompletionProof.cs",
  ];
  const files = await Promise.all(names.map(source));
  for (let index = 0; index < names.length; index += 1)
    assert.match(files[index], /RefreshArithmeticFinite/, `${names[index]} keeps its approved arithmetic preflight`);

  const [canonical, face, vertex, connect, insert, append, bevel, flip] = files;
  assert.match(canonical, /NormalArithmeticFinite\(positions, frozenStreams\)/);
  assert.match(canonical, /TangentArithmeticFinite\(positions, uv0\.Select/);
  assert.match(face, /ProBuilderRefreshUvProof\.RefreshUvsAreTerminal\(before\)/);
  assert.match(vertex, /ProBuilderRefreshUvProof\.RefreshUvsAreTerminal\(source\)/);
  assert.match(vertex, /ExpectedMeshFinite\(target, expected\)/);
  assert.match(connect, /RefreshArithmeticFinite\(before, true\)/);
  assert.match(insert, /TerminalCanRefresh\(Request request, Vector3 point\)/);
  assert.match(insert, /ProBuilderRefreshUvProof\.RefreshUvsAreTerminal\(initial\)/);
  assert.match(append, /TerminalTopologyReadable\(positions, streams, terminalShared\)/);
  assert.match(bevel, /TerminalTopologyReadable\(positions/);
  assert.match(flip, /private static bool RefreshArithmeticFinite\(IEnumerable<RawPlan>/);
});

test("UV terminal replay has one approved helper owner", async () => {
  const helper = await source("ProBuilderRefreshUvProof.cs");
  assert.match(helper, /internal static bool RefreshUvsAreTerminal\(CanonicalMeshSnapshot snapshot\)/);
  assert.doesNotMatch(helper, /CommitEffect|Undo\.|\.Refresh\(/);
  for (const removed of [
    "ProBuilderAttributeTransformCompletionProof.cs",
    "ProBuilderComplexTopologyCompletionProof.cs",
    "ProBuilderLocalTopologyCompletionProof.cs",
    "ProBuilderRequestProof.cs",
  ]) await assert.rejects(access(new URL(removed, root), constants.F_OK));
});

test("insert-vertex completes arithmetic preflight before its effect barrier", async () => {
  const handler = await source("insert-vertex.cs");
  assert.ok(handler.indexOf("TerminalCanRefresh(before, point)") < handler.indexOf("ctx.CommitEffect()"));
});
