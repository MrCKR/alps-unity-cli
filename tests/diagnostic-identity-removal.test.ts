import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { join, relative } from "node:path";
import test from "node:test";

const removedIdentities = [
  `diagnose.${"pre" + "flight"}`,
  `diagnose.${"cap" + "abilities"}`,
];
const sourceRoots = ["src", join("unitypkg", "Editor"), "docs"];

async function sourceFiles(root: string): Promise<string[]> {
  const entries = await readdir(root, { withFileTypes: true });
  const files: string[] = [];
  for (const entry of entries) {
    const path = join(root, entry.name);
    const normalized = path.replaceAll("\\", "/");
    if (entry.isDirectory()) {
      if (entry.name === "Generated") continue;
      files.push(...await sourceFiles(path));
      continue;
    }
    if (entry.name.startsWith(".generated-") || normalized.includes("/Generated/")) continue;
    if (/\.(?:ts|cs|md)$/.test(entry.name)) files.push(path);
  }
  return files;
}

test("removed aggregate diagnostic identities have no canonical source residual", async () => {
  const files = (await Promise.all(sourceRoots.map(sourceFiles))).flat();
  const residuals: string[] = [];
  for (const file of files) {
    const source = await readFile(file, "utf8");
    for (const identity of removedIdentities) {
      if (source.includes(identity)) residuals.push(`${relative(process.cwd(), file)}: ${identity}`);
    }
  }
  assert.deepEqual(residuals, []);
});
