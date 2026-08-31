import { createHash } from "node:crypto";
import { readdir, readFile, stat } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const packageRoot = join(repoRoot, "unitypkg");
const contractPath = join(repoRoot, "contracts", "package-assembly-closure.json");
const normalize = (value) => value.split("\\").join("/");

async function collectFiles(root, directory = "") {
  const files = [];
  for (const entry of (await readdir(join(root, directory), { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
    const child = join(directory, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Package assembly audit refuses links: ${normalize(child)}`);
    if (entry.isDirectory()) files.push(...await collectFiles(root, child));
    else if (entry.isFile()) files.push(normalize(child));
    else throw new Error(`Package assembly audit found an unsupported entry: ${normalize(child)}`);
  }
  return files;
}

const failures = [];
const contract = JSON.parse(await readFile(contractPath, "utf8"));
const packageManifest = JSON.parse(await readFile(join(packageRoot, "package.json"), "utf8"));
const editorAsmdef = JSON.parse(await readFile(join(packageRoot, "UnityCli.Editor.asmdef"), "utf8"));
const codeExecPath = join(packageRoot, "Editor", "Infrastructure", "CodeExecSupport.cs");
const codeExec = await readFile(codeExecPath, "utf8");
const files = await collectFiles(packageRoot);
const dlls = files.filter((file) => file.toLowerCase().endsWith(".dll"));
const expectedDlls = (contract.bundledAssemblies ?? []).map((entry) => normalize(entry.path)).sort();

if (contract.schemaVersion !== 1) failures.push("package assembly closure schemaVersion must be 1");
if (contract.packageName !== packageManifest.name) failures.push("package assembly closure packageName must match unitypkg/package.json");
if (contract.minimumUnityVersion !== packageManifest.unity) failures.push("package assembly closure minimumUnityVersion must match unitypkg/package.json");
if (JSON.stringify(dlls) !== JSON.stringify(expectedDlls)) failures.push(`bundled DLL set mismatch: expected ${JSON.stringify(expectedDlls)}, actual ${JSON.stringify(dlls)}`);

for (const entry of contract.bundledAssemblies ?? []) {
  const path = join(packageRoot, entry.path);
  try {
    const bytes = await readFile(path);
    const sha256 = createHash("sha256").update(bytes).digest("hex");
    if (sha256 !== entry.sha256) failures.push(`bundled assembly hash mismatch: ${entry.path}`);
  } catch {
    failures.push(`bundled assembly is missing: ${entry.path}`);
  }
}

const expectedPrecompiled = (contract.editorProvidedAssemblies ?? []).map((entry) => entry.fileName).sort();
const actualPrecompiled = [...(editorAsmdef.precompiledReferences ?? [])].sort();
if (JSON.stringify(actualPrecompiled) !== JSON.stringify(expectedPrecompiled)) failures.push(`UnityCli.Editor.asmdef precompiledReferences mismatch: expected ${JSON.stringify(expectedPrecompiled)}, actual ${JSON.stringify(actualPrecompiled)}`);
if (editorAsmdef.overrideReferences !== true) failures.push("UnityCli.Editor.asmdef must keep overrideReferences=true");

const protectedAssemblies = [...(contract.conflictPolicy?.protectedAssemblyFileNames ?? [])].sort();
if (JSON.stringify(protectedAssemblies) !== JSON.stringify(expectedPrecompiled)) failures.push("conflictPolicy protected assemblies must equal the Editor precompiled reference set");
if (!Array.isArray(contract.conflictPolicy?.allowedPackageProviders) || !contract.conflictPolicy.allowedPackageProviders.includes("com.unity.nuget.newtonsoft-json")) failures.push("conflictPolicy must allow the official Newtonsoft package provider");
for (const field of ["maxDirectories", "maxFiles", "maxConflicts"]) {
  const value = contract.conflictPolicy?.[field];
  if (!Number.isSafeInteger(value) || value <= 0) failures.push(`conflictPolicy ${field} must be a positive integer`);
}

if (contract.compiler?.mode !== "editor-bundled-csc-subprocess") failures.push("compiler mode must be editor-bundled-csc-subprocess");
if (contract.compiler?.packageOwnedRoslyn !== false) failures.push("packageOwnedRoslyn must be false");
for (const segment of [contract.compiler?.monoRelativePath, contract.compiler?.compilerRelativePath, contract.compiler?.temporaryRoot]) {
  if (typeof segment !== "string" || !segment) failures.push("compiler paths must be non-empty strings");
}
if (!codeExec.includes('Path.Combine(editorDataPath, "MonoBleedingEdge", "bin", "mono.exe")')) failures.push("CodeExecSupport.cs must use the Unity-bundled mono executable");
if (!codeExec.includes('Path.Combine(editorDataPath, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe")')) failures.push("CodeExecSupport.cs must use the Unity-bundled csc executable");
if (!codeExec.includes('Path.Combine(ProjectRoot(), "Library", "UnityCli", "code-exec-temp")')) failures.push("CodeExecSupport.cs temporary root must match the closure contract");
for (const forbidden of ["Microsoft.CodeAnalysis", "CompileInProcess", "CodeExecUseInProcessRoslyn", "Editor/Roslyn"]) {
  if (codeExec.includes(forbidden)) failures.push(`CodeExecSupport.cs retains forbidden package-owned Roslyn symbol: ${forbidden}`);
}
try {
  const roslyn = await stat(join(packageRoot, "Editor", "Roslyn"));
  if (roslyn) failures.push("unitypkg/Editor/Roslyn must not exist");
} catch (error) {
  if (error?.code !== "ENOENT") throw error;
}

const result = {
  ok: failures.length === 0,
  contract: normalize(relative(repoRoot, contractPath)),
  packageName: packageManifest.name,
  minimumUnityVersion: packageManifest.unity,
  bundledAssemblies: dlls,
  editorProvidedAssemblies: expectedPrecompiled,
  compilerMode: contract.compiler?.mode,
  conflictPolicy: { protectedAssemblies, allowedPackageProviders: contract.conflictPolicy?.allowedPackageProviders ?? [] },
  failures,
};
console.log(JSON.stringify(result, null, 2));
if (failures.length) process.exitCode = 1;
