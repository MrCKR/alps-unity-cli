import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { promisify } from "node:util";
import test from "node:test";

const execFileAsync = promisify(execFile);

test("generated Context registry is idempotent and contains only TS-local capability imports", async () => {
  const path = "src/contexts/.generated-registry.ts";
  const before = await readFile(path, "utf8");
  await execFileAsync(process.execPath, ["scripts/gen-ts-registries.mjs"]);
  const after = await readFile(path, "utf8");
  assert.equal(after, before);
  assert.match(after, /\.\/editor\/dialogs\.js/);
  assert.match(after, /\.\/editor\/state\.js/);
  assert.doesNotMatch(after, /tools\/custom|tools\/groups|project\.info|probuilder\.scene/);
});
