import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
const root = new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/", import.meta.url);
async function source(name: string) { return readFile(new URL(name, root), "utf8"); }

test("request-specific handlers have one effect barrier and unknown post-effect failures", async () => {
  const [objects, merge, weld] = await Promise.all([source("merge-objects.cs"), source("merge-vertices.cs"), source("weld-vertices.cs")]);
  for (const value of [objects, merge, weld]) assert.equal((value.match(/ctx\.CommitEffect\(\)/g) ?? []).length, 1);
  assert.match(merge, /TryMergeVerticesWithCollapse[\s\S]*OutcomeUnknown/);
  assert.match(weld, /TryWeldVerticesWithRadius[\s\S]*OutcomeUnknown/);
  assert.match(objects, /OutcomeUnknown\("merge-objects", "(?:invoke|readback|postcondition)"/);
});
