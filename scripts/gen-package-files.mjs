import { createHash } from "node:crypto";
import { readdir, readFile, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const packageRoot = join(repoRoot, "unitypkg");
const outputPath = join(repoRoot, "contracts", "package-files.json");
const normalize = (value) => value.split("\\").join("/");

async function collectDirectory(directory, root, treeHash, files) {
  const entries = (await readdir(directory, { withFileTypes: true })).sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
  for (const entry of entries) {
    const absolute = join(directory, entry.name);
    const path = normalize(relative(root, absolute));
    if (entry.isSymbolicLink()) throw new Error(`Canonical package source cannot contain links: ${path}`);
    if (entry.isDirectory()) {
      treeHash.update(`dir:${path}/\n`);
      await collectDirectory(absolute, root, treeHash, files);
      continue;
    }
    if (!entry.isFile()) throw new Error(`Canonical package source contains an unsupported entry: ${path}`);
    const bytes = await readFile(absolute);
    treeHash.update(`file:${path}\n`);
    treeHash.update(bytes);
    treeHash.update("\n");
    files.push({ path, size: bytes.length, sha256: createHash("sha256").update(bytes).digest("hex") });
  }
}

const packageManifest = JSON.parse(await readFile(join(packageRoot, "package.json"), "utf8"));
const treeHash = createHash("sha256");
const files = [];
await collectDirectory(packageRoot, packageRoot, treeHash, files);
files.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
const contract = {
  schemaVersion: 1,
  packageName: packageManifest.name,
  packageVersion: packageManifest.version,
  sourceHash: treeHash.digest("hex"),
  files,
};
await writeFile(outputPath, `${JSON.stringify(contract, null, 2)}\n`, "utf8");
console.log(JSON.stringify({ ok: true, output: normalize(relative(repoRoot, outputPath)), packageName: contract.packageName, packageVersion: contract.packageVersion, sourceHash: contract.sourceHash, fileCount: files.length }, null, 2));
