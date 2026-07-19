import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";

const root = new URL("../extensions/", import.meta.url);
async function csFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.map(async entry => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) return csFiles(child);
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return nested.flat();
}

test("extension mutation descriptors commit their first effect", async () => {
  const packages = ["cinemachine", "hdrp", "probuilder", "urp", "vfx-graph"];
  const files = (await Promise.all(packages.map(id => csFiles(new URL(`${id}/unitypkg/`, root))))).flat();
  const descriptors = await Promise.all(files.map(async file => ({
    file: path.basename(file.pathname),
    source: await readFile(file, "utf8")
  })));
  const mutations = descriptors.flatMap(({ file, source }) => {
    const name = source.match(/Name\s*=\s*"([^"]+)"/)?.[1];
    const sideEffects = source.match(/SideEffects\s*=\s*new(?:\[\]| string\[0\])\s*\{?([^;]*)/s)?.[1] ?? "";
    return name && sideEffects && !/new string\[0\]/.test(source) ? [{ file, name, source }] : [];
  });

  assert.ok(mutations.length > 0, "expected mutation descriptors from all extensions");
  const committed = mutations.filter(mutation => /\b(?:ctx|context)\.CommitEffect\(\)/.test(mutation.source));
  const missingCommit = mutations.filter(mutation => !/\b(?:ctx|context)\.CommitEffect\(\)/.test(mutation.source));

  for (const mutation of committed) {
    const commits = mutation.source.match(/\b(?:ctx|context)\.CommitEffect\(\)/g) ?? [];
    assert.equal(commits.length, 1, `${mutation.name} must commit exactly once before its effect`);
  }
  assert.deepEqual(missingCommit.map(mutation => mutation.name).sort(), [], "every non-fail-closed mutation descriptor must commit its first effect");
});
