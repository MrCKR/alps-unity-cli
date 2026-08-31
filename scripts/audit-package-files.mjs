import { createHash } from "node:crypto";
import { readdir, readFile } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const packageRoot = join(repoRoot, "unitypkg");
const contractPath = join(repoRoot, "contracts", "package-files.json");
const normalize = (value) => value.split("\\").join("/");

async function collectDirectory(directory, root, treeHash, files, failures) {
  const entries = (await readdir(directory, { withFileTypes: true })).sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
  for (const entry of entries) {
    const absolute = join(directory, entry.name);
    const path = normalize(relative(root, absolute));
    if (entry.isSymbolicLink()) { failures.push(`canonical package source contains a link: ${path}`); continue; }
    if (entry.isDirectory()) {
      treeHash.update(`dir:${path}/\n`);
      await collectDirectory(absolute, root, treeHash, files, failures);
      continue;
    }
    if (!entry.isFile()) { failures.push(`canonical package source contains an unsupported entry: ${path}`); continue; }
    const bytes = await readFile(absolute);
    treeHash.update(`file:${path}\n`);
    treeHash.update(bytes);
    treeHash.update("\n");
    files.push({ path, size: bytes.length, sha256: createHash("sha256").update(bytes).digest("hex") });
  }
}

const failures = [];
const contract = JSON.parse(await readFile(contractPath, "utf8"));
const packageManifest = JSON.parse(await readFile(join(packageRoot, "package.json"), "utf8"));
const treeHash = createHash("sha256");
const files = [];
await collectDirectory(packageRoot, packageRoot, treeHash, files, failures);
files.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
const sourceHash = treeHash.digest("hex");
if (contract.schemaVersion !== 1) failures.push("package-files schemaVersion must be 1");
if (contract.packageName !== packageManifest.name) failures.push("package-files packageName does not match unitypkg/package.json");
if (contract.packageVersion !== packageManifest.version) failures.push("package-files packageVersion does not match unitypkg/package.json");
if (contract.sourceHash !== sourceHash) failures.push(`package-files sourceHash mismatch: expected ${contract.sourceHash}, actual ${sourceHash}`);
if (JSON.stringify(contract.files) !== JSON.stringify(files)) failures.push("package-files path/size/hash entries do not match canonical unitypkg bytes");
const identities = new Set();
for (const entry of contract.files ?? []) {
  if (typeof entry.path !== "string" || typeof entry.size !== "number" || !Number.isSafeInteger(entry.size) || entry.size < 0 || typeof entry.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(entry.sha256)) failures.push(`invalid package-files entry: ${JSON.stringify(entry)}`);
  const identity = String(entry.path).toLowerCase();
  if (identities.has(identity)) failures.push(`duplicate package-files path: ${entry.path}`);
  identities.add(identity);
}
const result = { ok: failures.length === 0, contract: normalize(relative(repoRoot, contractPath)), packageName: packageManifest.name, packageVersion: packageManifest.version, sourceHash, fileCount: files.length, failures };
console.log(JSON.stringify(result, null, 2));
if (failures.length) process.exitCode = 1;
