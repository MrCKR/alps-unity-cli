import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const input = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/ProBuilderInput.cs", import.meta.url);
const combine = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/combine-meshes.cs", import.meta.url);
const merge = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/merge-objects.cs", import.meta.url);

test("ProBuilder target resolution fails closed for ambiguous and batch targets", async () => {
  const source = await readFile(input, "utf8");

  assert.match(source, /candidates\.Count == 1 \? candidates\[0\] : null/);
  assert.match(source, /HierarchyPath\(go\) == target\.Trim\('\/'\)/);
  assert.match(source, /go\.name == target/);
  assert.match(source, /if \(go == null \|\| !seen\.Add\(go\.GetInstanceID\(\)\)\) return new GameObject\[0\];/);
  assert.match(source, /results\.Add\(go\);/);
  assert.doesNotMatch(source, /GameObject\.Find\(/);
});

test("multi-target ProBuilder mutations reject partial resolution", async () => {
  for (const source of await Promise.all([combine, merge].map((file) => readFile(file, "utf8")))) {
    assert.match(source, /request\.Targets == null \|\| targets == null \|\| targets\.Length != request\.Targets\.Count \|\| targets\.Length < 2/);
  }
});
