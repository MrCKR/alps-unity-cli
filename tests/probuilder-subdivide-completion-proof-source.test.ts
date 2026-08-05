import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);

test("subdivide consumes the frozen face connect plan and treats fresh capture failures as postconditions", async () => {
  const [tool, proof] = await Promise.all([
    readFile(new URL("subdivide.cs", root), "utf8"),
    readFile(new URL("ProBuilderConnectCompletionProof.cs", root), "utf8")
  ]);

  assert.equal((tool.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(tool, /CaptureFace\(go, frozenFaces\)/);
  assert.match(tool, /var typedMesh = mesh as UnityEngine\.ProBuilder\.ProBuilderMesh/);
  assert.match(tool, /ConnectElements\.Connect\(typedMesh, \(UnityEngine\.ProBuilder\.Face\[\]\)plan\.FrozenFaces\)/);
  assert.match(tool, /RefreshMeshPreservingVertexPayload\(typedMesh\)/);
  assert.match(tool, /try\s*\{[\s\S]*CanonicalMeshSnapshot\.Capture\(freshMesh\.gameObject\)[\s\S]*Matches\(plan, after\)[\s\S]*\}/);
  assert.match(tool, /catch \(UnityCliBridge\.BridgeCommandException\) \{ throw; \}/);
  assert.match(tool, /OutcomeUnknown\("subdivide", "postcondition"/);
  assert.match(tool, /exactly three raw and shared vertices/);
  assert.match(proof, /CaptureFace/);
  assert.match(proof, /InvokePure/);
});
