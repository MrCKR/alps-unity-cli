import { createHash } from "node:crypto";
import { readdir, readFile, stat } from "node:fs/promises";
import { existsSync } from "node:fs";
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
const codeExecPath = join(packageRoot, contract.compiler?.sharedOwner ?? "Editor/Infrastructure/DynamicCodeCompiler.cs");
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

// 受保护文件名由随包 DLL 与 Editor 提供 DLL 的并集推导，不单独维护第二份名单。
const bundledFileNames = (contract.bundledAssemblies ?? []).map((entry) => entry.fileName);
const expectedProtected = [...new Set([...expectedPrecompiled, ...bundledFileNames])].sort();
const protectedAssemblies = [...(contract.conflictPolicy?.protectedAssemblyFileNames ?? [])].sort();
if (JSON.stringify(protectedAssemblies) !== JSON.stringify(expectedProtected)) failures.push(`conflictPolicy protected assemblies must equal the bundled plus Editor-provided DLL file names: expected ${JSON.stringify(expectedProtected)}`);
if (expectedProtected.some((name) => typeof name !== "string" || !name)) failures.push("protected assembly file names must be non-empty strings");
if (bundledFileNames.some((name) => expectedPrecompiled.includes(name))) failures.push("a bundled assembly cannot also be declared as Editor-provided");

// 每个随包 DLL 必须声明完整来源与身份，供安装冲突检查与许可证归档使用。
for (const entry of contract.bundledAssemblies ?? []) {
  for (const field of ["path", "fileName", "assemblyName", "assemblyVersion", "targetFramework", "provider", "license"]) {
    if (typeof entry[field] !== "string" || !entry[field]) failures.push(`bundled assembly ${entry.path ?? "<unknown>"} must declare ${field}`);
  }
  if (typeof entry.sha256 !== "string" || !/^[a-f0-9]{64}$/.test(entry.sha256)) failures.push(`bundled assembly ${entry.path ?? "<unknown>"} must declare a lowercase sha256`);
  if (typeof entry.path === "string" && !entry.path.endsWith(entry.fileName ?? "")) failures.push(`bundled assembly path must end with its fileName: ${entry.path}`);
}
if (!Array.isArray(contract.conflictPolicy?.allowedPackageProviders) || !contract.conflictPolicy.allowedPackageProviders.includes("com.unity.nuget.newtonsoft-json")) failures.push("conflictPolicy must allow the official Newtonsoft package provider");
for (const entry of [...(contract.bundledAssemblies ?? []), ...(contract.editorProvidedAssemblies ?? [])]) {
  if (typeof entry.assemblyName !== "string" || !entry.assemblyName) failures.push(`protected assembly ${entry.fileName ?? "<unknown>"} must declare an assemblyName for identity matching`);
}
for (const field of ["maxDirectories", "maxFiles", "maxIdentityFiles", "maxConflicts"]) {
  const value = contract.conflictPolicy?.[field];
  if (!Number.isSafeInteger(value) || value <= 0) failures.push(`conflictPolicy ${field} must be a positive integer`);
}

if (contract.compiler?.mode !== "editor-bundled-csc-subprocess") failures.push("compiler mode must be editor-bundled-csc-subprocess");

// hook 后端程序集的依赖、precompiled 引用、平台限制与 owner id 由同一闭包声明校验。
const hookBackend = contract.hookBackend;
if (!hookBackend || typeof hookBackend.asmdefPath !== "string" || typeof hookBackend.assemblyName !== "string"
  || typeof hookBackend.typeName !== "string" || typeof hookBackend.ownerId !== "string"
  || !Array.isArray(hookBackend.precompiledReferences) || !Array.isArray(hookBackend.references)) {
  failures.push("hookBackend closure declaration is required");
} else {
  const hookAsmdefPath = join(packageRoot, hookBackend.asmdefPath);
  if (!existsSync(hookAsmdefPath)) {
    failures.push(`hook backend asmdef is missing: ${hookBackend.asmdefPath}`);
  } else {
    const hookAsmdef = JSON.parse(await readFile(hookAsmdefPath, "utf8"));
    if (hookAsmdef.name !== hookBackend.assemblyName) failures.push("hook backend asmdef name must match the closure declaration");
    if (JSON.stringify([...(hookAsmdef.precompiledReferences ?? [])].sort()) !== JSON.stringify([...hookBackend.precompiledReferences].sort()))
      failures.push("hook backend precompiledReferences must match the closure declaration");
    if (JSON.stringify([...(hookAsmdef.references ?? [])].sort()) !== JSON.stringify([...hookBackend.references].sort()))
      failures.push("hook backend references must match the closure declaration");
    if (hookAsmdef.overrideReferences !== true) failures.push("hook backend asmdef must keep overrideReferences=true");
    if (JSON.stringify(hookAsmdef.includePlatforms ?? []) !== JSON.stringify(["Editor"])) failures.push("hook backend asmdef must be Editor-only");
    if (hookAsmdef.autoReferenced !== false) failures.push("hook backend asmdef must not be auto-referenced by project assemblies");
    const backendSource = await readFile(join(packageRoot, "Hooks", "MethodHookHarmonyBackend.cs"), "utf8");
    if (!backendSource.includes(hookBackend.ownerId)) failures.push("hook backend must declare its exact Harmony owner id from the closure contract");
    const simpleTypeName = hookBackend.typeName.split(".").pop();
    if (!backendSource.includes(simpleTypeName)) failures.push("hook backend must declare its exact type name from the closure contract");

    // 插件导入投影：随包 .meta 必须声明仅 Editor、不面向全平台、自动引用关闭；运行后置核对必须按同一规则。
    if (typeof hookBackend.pluginAssetRelativePath !== "string" || !hookBackend.pluginAssetRelativePath
      || !hookBackend.pluginImporterRequirements || typeof hookBackend.pluginImporterRequirements !== "object"
      || hookBackend.pluginImporterRequirements.compatibleWithAnyPlatform !== false
      || hookBackend.pluginImporterRequirements.compatibleWithEditor !== true
      || hookBackend.pluginImporterRequirements.preloaded !== false) {
      failures.push("hookBackend must declare the packaged plugin asset path and its importer requirements");
    } else {
      const metaPath = join(packageRoot, "Editor", "Plugins", "0Harmony.dll.meta");
      if (!existsSync(metaPath)) {
        failures.push("the bundled plugin meta must exist for importer projection auditing");
      } else {
        const meta = await readFile(metaPath, "utf8");
        if (!/isExplicitlyReferenced:\s*1/.test(meta)) failures.push("the bundled plugin meta must keep auto reference disabled (isExplicitlyReferenced: 1)");
        if (!platformBlock(meta, "Any")) failures.push("the bundled plugin meta must exclude Any platform");
        if (!platformBlock(meta, "Editor")) failures.push("the bundled plugin meta must enable the Editor platform");
      }
      const hostSource = await readFile(join(packageRoot, "Editor", "Infrastructure", "MethodHooks", "MethodHookHost.cs"), "utf8");
      if (!hostSource.includes(hookBackend.pluginAssetRelativePath)) failures.push("the runtime plugin importer check must use the declared package-relative plugin path");
      for (const method of ["GetCompatibleWithAnyPlatform", "GetCompatibleWithEditor", "isPreloaded"]) {
        if (!hostSource.includes(method)) failures.push(`the runtime plugin importer check must read ${method}`);
      }
    }
  }
}

/** 判断 .meta 中指定平台的 enabled 值与声明需求一致（Any 必须 0，Editor 必须 1）。 */
function platformBlock(meta, platform) {
  const marker = platform === "Any" ? "      Any: " : "      Editor: Editor";
  const index = meta.indexOf(marker);
  if (index < 0) return false;
  const block = meta.slice(index, index + 200);
  return platform === "Any" ? /enabled:\s*0/.test(block) : /enabled:\s*1/.test(block);
}

if (contract.compiler?.packageOwnedRoslyn !== false) failures.push("packageOwnedRoslyn must be false");
if (typeof contract.compiler?.sharedOwner !== "string" || !contract.compiler.sharedOwner) failures.push("compiler owner path must be declared");
for (const segment of [contract.compiler?.monoRelativePath, contract.compiler?.compilerRelativePath, contract.compiler?.temporaryRoot]) {
  if (typeof segment !== "string" || !segment) failures.push("compiler paths must be non-empty strings");
}
if (!codeExec.includes('Path.Combine(editorDataPath, "MonoBleedingEdge", "bin", "mono.exe")')) failures.push(`${contract.compiler?.sharedOwner} must use the Unity-bundled mono executable`);
if (!codeExec.includes('Path.Combine(editorDataPath, "MonoBleedingEdge", "lib", "mono", "4.5", "csc.exe")')) failures.push(`${contract.compiler?.sharedOwner} must use the Unity-bundled csc executable`);
if (!codeExec.includes('Path.Combine(UnityCliBridge.ProjectRoot(), "Library", "UnityCli", "code-exec-temp")')) failures.push(`${contract.compiler?.sharedOwner} temporary root must match the closure contract`);
for (const forbidden of ["Microsoft.CodeAnalysis", "CompileInProcess", "CodeExecUseInProcessRoslyn", "Editor/Roslyn"]) {
  if (codeExec.includes(forbidden)) failures.push(`${contract.compiler?.sharedOwner} retains forbidden package-owned Roslyn symbol: ${forbidden}`);
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
