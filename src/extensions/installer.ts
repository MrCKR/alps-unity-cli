import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { copyFile, mkdir, readdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { basename, dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { readInstalledUnityPackages } from "./project-resolver.js";
import { ExtensionRegistry } from "./registry.js";
import type { ExtensionAvailabilityResult, ExtensionId, ExtensionManifest } from "./types.js";
import { stripBom } from "../util.js";

export type ExtensionInstallStatus = "installed" | "available_not_installed" | "missing_dependency" | "outdated" | "unknown_files";

export interface ManagedExtensionFile {
  path: string;
  sha256: string;
}

export interface InstalledExtensionManifestEntry {
  extensionId: ExtensionId;
  version: string;
  installedAt: string;
  sourceHash: string;
  files: ManagedExtensionFile[];
  requiresUnityPackages: string[];
  copyRoot: string;
  layout?: "directory";
  installMode: "copy-if-package-present";
}

export interface ExtensionInstallManifest {
  version: 1;
  extensions: InstalledExtensionManifestEntry[];
}

export interface ExtensionInstallResult {
  extensionId: ExtensionId;
  status: "installed" | "already_installed" | "removed";
  projectPath: string;
  targetRoot: string;
  fileCount: number;
  sourceHash?: string;
  files?: ManagedExtensionFile[];
}

export interface ExtensionStatusCheck {
  extensionId: ExtensionId;
  name: string;
  status: ExtensionInstallStatus;
  dependencyInstalled: boolean;
  installed: boolean;
  outdated: boolean;
  unknownFiles: string[];
  missingFiles: string[];
  changedFiles: string[];
  providedTools: string[];
  missingPackageIds: string[];
  nextActions: string[];
}

const packageName = "com.unity_cli";
const manifestFileName = "unity_cli.extensions.manifest.json";

/** 返回 extension 源码目录。 */
export function extensionSourceRoot(extensionId: ExtensionId): string {
  return resolve(fileURLToPath(new URL(`../../extensions/${extensionId}`, import.meta.url)));
}

/** 返回 extension Unity 安装投影使用的默认相对路径。 */
export function extensionUnityInstallIncludes(manifest: ExtensionManifest): string[] {
  return manifest.unityInstall?.include ?? ["extension.json", "unitypkg/", "docs/"];
}

/** 返回 legacy unitypkg 源码根目录；保留给旧测试和调用方。 */
export function extensionBundleSourceRoot(extensionId: ExtensionId): string {
  return join(extensionSourceRoot(extensionId), "unitypkg");
}

/** 返回 Unity 项目内 extension 目录安装根目录。 */
export function extensionInstallRoot(projectPath: string, extensionId: ExtensionId): string {
  return join(resolve(projectPath), "Packages", packageName, "Extensions", extensionId);
}

/** 返回 extension 安装 manifest 路径。 */
export function extensionInstallManifestPath(projectPath: string): string {
  return join(resolve(projectPath), "Packages", packageName, manifestFileName);
}

/** 读取 extension 安装 manifest；缺失或损坏时返回空 manifest。 */
export async function readExtensionInstallManifest(projectPath: string): Promise<ExtensionInstallManifest> {
  try {
    const parsed = JSON.parse(stripBom(await readFile(extensionInstallManifestPath(projectPath), "utf8"))) as Partial<ExtensionInstallManifest>;
    if (parsed.version !== 1 || !Array.isArray(parsed.extensions)) {
      return emptyManifest();
    }
    return {
      version: 1,
      extensions: parsed.extensions.filter(isInstalledEntry)
    };
  } catch {
    return emptyManifest();
  }
}

/** 写入 extension 安装 manifest。 */
export async function writeExtensionInstallManifest(projectPath: string, manifest: ExtensionInstallManifest): Promise<void> {
  const manifestPath = extensionInstallManifestPath(projectPath);
  await mkdir(dirname(manifestPath), { recursive: true });
  await writeFile(manifestPath, `${JSON.stringify({ version: 1, extensions: manifest.extensions.sort((a, b) => a.extensionId.localeCompare(b.extensionId)) }, null, 2)}\n`, "utf8");
}

/** 安装一个 extension 目录；只投影 Unity 侧文件，不安装可选 Unity package。 */
export async function installExtensionBundle(projectPath: string, manifest: ExtensionManifest): Promise<ExtensionInstallResult> {
  const installedPackages = await readInstalledUnityPackages(projectPath);
  const availability = new ExtensionRegistry().resolveAvailability(manifest.id, installedPackages);
  if (availability.status !== "available") {
    const missing = availability.missingPackageIds.join(", ");
    const error = new ExtensionInstallerError(
      "E_EXTENSION_DEPENDENCY_MISSING",
      `Extension "${manifest.id}" requires missing Unity package(s): ${missing}`,
      [
        `Install the Unity package first: ${missing}`,
        `Then run unity_cli extensions install ${manifest.id} --project <path>.`,
        "The CLI does not install optional Unity packages automatically."
      ]
    );
    error.availability = availability;
    throw error;
  }

  return installExtensionBundleUnchecked(projectPath, manifest, "installed");
}

/** 根据已解析依赖自动安装 extension 目录；调用方保证依赖可用。 */
export async function installExtensionBundleUnchecked(
  projectPath: string,
  manifest: ExtensionManifest,
  installedStatus: "installed" | "already_installed" = "installed"
): Promise<ExtensionInstallResult> {
  const sourceRoot = extensionSourceRoot(manifest.id);
  if (!existsSync(sourceRoot)) {
    throw new ExtensionInstallerError("E_EXTENSION_BUNDLE_MISSING", `Extension source directory not found: ${sourceRoot}`, ["Reinstall or rebuild the unity_cli package."]);
  }

  const targetRoot = extensionInstallRoot(projectPath, manifest.id);
  const installManifest = await readExtensionInstallManifest(projectPath);
  const previous = installManifest.extensions.find((entry) => entry.extensionId === manifest.id);
  if (previous) {
    await removeManagedFiles(targetRoot, previous.files);
  }

  const installIncludes = extensionUnityInstallIncludes(manifest);
  const copiedFiles = await copyBundleProjection(sourceRoot, targetRoot, installIncludes);
  const sourceHash = await hashBundleProjection(sourceRoot, installIncludes);
  const entry: InstalledExtensionManifestEntry = {
    extensionId: manifest.id,
    version: manifest.version,
    installedAt: new Date().toISOString(),
    sourceHash,
    files: copiedFiles,
    requiresUnityPackages: manifest.unityPackages.map((pkg) => pkg.packageId),
    copyRoot: "extension-directory",
    layout: "directory",
    installMode: "copy-if-package-present"
  };

  installManifest.extensions = installManifest.extensions.filter((item) => item.extensionId !== manifest.id);
  installManifest.extensions.push(entry);
  await writeExtensionInstallManifest(projectPath, installManifest);

  return {
    extensionId: manifest.id,
    status: installedStatus,
    projectPath: resolve(projectPath),
    targetRoot,
    fileCount: copiedFiles.length,
    sourceHash,
    files: copiedFiles
  };
}

/** 按 manifest 删除一个 extension 目录复制过的文件。 */
export async function removeExtensionBundle(projectPath: string, extensionId: ExtensionId): Promise<ExtensionInstallResult> {
  const installManifest = await readExtensionInstallManifest(projectPath);
  const entry = installManifest.extensions.find((item) => item.extensionId === extensionId);
  if (!entry) {
    throw new ExtensionInstallerError("EXTENSION_NOT_INSTALLED", `Extension is not installed: ${extensionId}`, ["Run unity_cli extensions list --project <path> to inspect extension state."]);
  }

  const targetRoot = extensionInstallRoot(projectPath, extensionId);
  await removeManagedFiles(targetRoot, entry.files);
  await removeEmptyDirectories(targetRoot);

  installManifest.extensions = installManifest.extensions.filter((item) => item.extensionId !== extensionId);
  await writeExtensionInstallManifest(projectPath, installManifest);

  return {
    extensionId,
    status: "removed",
    projectPath: resolve(projectPath),
    targetRoot,
    fileCount: entry.files.length
  };
}

/** 检查所有 extension 的物理安装状态。 */
export async function checkExtensionBundles(projectPath: string): Promise<ExtensionStatusCheck[]> {
  const registry = new ExtensionRegistry();
  const installedPackages = await readInstalledUnityPackages(projectPath);
  const installManifest = await readExtensionInstallManifest(projectPath);
  const checks: ExtensionStatusCheck[] = [];

  for (const extensionId of registry.extensionIds()) {
    const extension = registry.getExtension(extensionId)!;
    const availability = registry.resolveAvailability(extensionId, installedPackages);
    const entry = installManifest.extensions.find((item) => item.extensionId === extensionId);
    const targetRoot = extensionInstallRoot(projectPath, extensionId);
    const sourceRoot = extensionSourceRoot(extensionId);
    const sourceHash = existsSync(sourceRoot) ? await hashBundleProjection(sourceRoot, extensionUnityInstallIncludes(extension.manifest)) : undefined;
    const fileCheck = entry ? await checkManagedFiles(targetRoot, entry.files) : { missingFiles: [], changedFiles: [], unknownFiles: await listUnknownFiles(targetRoot, []) };
    const outdated = Boolean(entry && sourceHash && entry.sourceHash !== sourceHash);
    const hasUnknownFiles = fileCheck.unknownFiles.length > 0;
    const installed = Boolean(entry && fileCheck.missingFiles.length === 0 && fileCheck.changedFiles.length === 0 && !outdated && !hasUnknownFiles);
    const status = resolvePhysicalStatus(availability, entry !== undefined, outdated, fileCheck.missingFiles, fileCheck.changedFiles, fileCheck.unknownFiles);

    checks.push({
      extensionId,
      name: extension.manifest.name,
      status,
      dependencyInstalled: availability.status === "available",
      installed,
      outdated,
      unknownFiles: fileCheck.unknownFiles,
      missingFiles: fileCheck.missingFiles,
      changedFiles: fileCheck.changedFiles,
      providedTools: extension.manifest.contributions.tools?.map((tool) => tool.name) ?? [],
      missingPackageIds: availability.missingPackageIds,
      nextActions: nextActionsForStatus(extension.manifest, availability, status)
    });
  }

  return checks;
}

async function copyBundleProjection(sourceRoot: string, targetRoot: string, includes: string[]): Promise<ManagedExtensionFile[]> {
  const files: ManagedExtensionFile[] = [];
  for (const includePath of includes) {
    const sourcePath = safeSourcePath(sourceRoot, includePath);
    if (!existsSync(sourcePath)) continue;
    const sourceInfo = await stat(sourcePath);
    const targetRelativeRoot = projectionTargetRoot(includePath);
    if (sourceInfo.isDirectory()) {
      await copyRecursive(sourcePath, join(targetRoot, targetRelativeRoot), sourcePath, files, targetRelativeRoot);
    } else if (sourceInfo.isFile()) {
      const dest = join(targetRoot, targetRelativeRoot);
      await mkdir(dirname(dest), { recursive: true });
      await copyFile(sourcePath, dest);
      files.push({ path: normalizePath(targetRelativeRoot), sha256: await hashFile(sourcePath) });
    }
  }
  return files.sort((a, b) => a.path.localeCompare(b.path));
}

async function copyRecursive(sourcePath: string, targetRoot: string, sourceRoot: string, files: ManagedExtensionFile[], targetPrefix = ""): Promise<void> {
  const entries = (await readdir(sourcePath, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name));
  for (const entry of entries) {
    const src = join(sourcePath, entry.name);
    const sourceRelative = normalizePath(relative(sourceRoot, src));
    const rel = normalizePath(targetPrefix ? `${targetPrefix}/${sourceRelative}` : sourceRelative);
    const dest = join(targetRoot, sourceRelative);
    if (entry.isDirectory()) {
      await mkdir(dest, { recursive: true });
      await copyRecursive(src, targetRoot, sourceRoot, files, targetPrefix);
    } else if (entry.isFile()) {
      await mkdir(dirname(dest), { recursive: true });
      await copyFile(src, dest);
      files.push({ path: rel, sha256: await hashFile(src) });
    }
  }
}

async function removeManagedFiles(targetRoot: string, files: ManagedExtensionFile[]): Promise<void> {
  for (const file of files) {
    const safePath = safeManagedPath(targetRoot, file.path);
    try {
      await rm(safePath, { force: true });
    } catch {
      // 删除失败不应扩散到用户文件；doctor 会报告残留状态。
    }
  }
}

async function checkManagedFiles(targetRoot: string, files: ManagedExtensionFile[]): Promise<{ missingFiles: string[]; changedFiles: string[]; unknownFiles: string[] }> {
  const missingFiles: string[] = [];
  const changedFiles: string[] = [];
  const managed = new Set(files.map((file) => file.path));
  for (const file of files) {
    const fullPath = safeManagedPath(targetRoot, file.path);
    if (!existsSync(fullPath)) {
      missingFiles.push(file.path);
      continue;
    }
    const actualHash = await hashFile(fullPath);
    if (actualHash !== file.sha256) {
      changedFiles.push(file.path);
    }
  }
  const unknownFiles = await listUnknownFiles(targetRoot, managed);
  return { missingFiles, changedFiles, unknownFiles };
}

async function listUnknownFiles(targetRoot: string, managed: Iterable<string>): Promise<string[]> {
  if (!existsSync(targetRoot)) {
    return [];
  }
  const managedSet = new Set(managed);
  const allFiles = await listFiles(targetRoot);
  return allFiles.filter((file) => !managedSet.has(file)).sort();
}

async function listFiles(root: string, current = root): Promise<string[]> {
  const entries = await readdir(current, { withFileTypes: true });
  const files: string[] = [];
  for (const entry of entries) {
    const fullPath = join(current, entry.name);
    if (entry.isDirectory()) {
      files.push(...await listFiles(root, fullPath));
    } else if (entry.isFile()) {
      files.push(normalizePath(relative(root, fullPath)));
    }
  }
  return files;
}

async function removeEmptyDirectories(root: string): Promise<void> {
  if (!existsSync(root)) return;
  const entries = await readdir(root, { withFileTypes: true }).catch(() => []);
  for (const entry of entries) {
    if (entry.isDirectory()) {
      await removeEmptyDirectories(join(root, entry.name));
    }
  }
  const remaining = await readdir(root).catch(() => []);
  if (remaining.length === 0) {
    await rm(root, { recursive: true, force: true });
  }
}

async function hashBundleProjection(sourceRoot: string, includes: string[]): Promise<string> {
  const hash = createHash("sha256");
  for (const includePath of includes.map(normalizePath).sort()) {
    const sourcePath = safeSourcePath(sourceRoot, includePath);
    if (!existsSync(sourcePath)) continue;
    const sourceInfo = await stat(sourcePath);
    const targetRelativeRoot = projectionTargetRoot(includePath);
    if (sourceInfo.isDirectory()) {
      hash.update(`projection-dir:${targetRelativeRoot}/\n`);
      await hashDirectoryInto(hash, sourcePath, sourcePath, targetRelativeRoot);
    } else if (sourceInfo.isFile()) {
      hash.update(`projection-file:${targetRelativeRoot}\n`);
      hash.update(await readFile(sourcePath));
      hash.update("\n");
    }
  }
  return hash.digest("hex");
}

async function hashDirectoryInto(hash: ReturnType<typeof createHash>, dir: string, root: string, targetPrefix = ""): Promise<void> {
  const entries = (await readdir(dir, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name));
  for (const entry of entries) {
    const fullPath = join(dir, entry.name);
    const sourceRelative = normalizePath(relative(root, fullPath));
    const rel = normalizePath(targetPrefix ? `${targetPrefix}/${sourceRelative}` : sourceRelative);
    if (entry.isDirectory()) {
      hash.update(`dir:${rel}/\n`);
      await hashDirectoryInto(hash, fullPath, root, targetPrefix);
    } else if (entry.isFile()) {
      hash.update(`file:${rel}\n`);
      hash.update(await readFile(fullPath));
      hash.update("\n");
    }
  }
}

async function hashFile(path: string): Promise<string> {
  return createHash("sha256").update(await readFile(path)).digest("hex");
}

function resolvePhysicalStatus(
  availability: ExtensionAvailabilityResult,
  hasEntry: boolean,
  outdated: boolean,
  missingFiles: string[],
  changedFiles: string[],
  unknownFiles: string[]
): ExtensionInstallStatus {
  if (availability.status !== "available") return "missing_dependency";
  if (!hasEntry) return "available_not_installed";
  if (outdated || missingFiles.length > 0 || changedFiles.length > 0) return "outdated";
  if (unknownFiles.length > 0) return "unknown_files";
  return "installed";
}

function nextActionsForStatus(manifest: ExtensionManifest, availability: ExtensionAvailabilityResult, status: ExtensionInstallStatus): string[] {
  if (status === "missing_dependency") {
    return [
      `Install ${availability.missingPackageIds.join(", ")} manually via the Unity Package Manager or Packages/manifest.json.`,
      "Run tools run package.resolve after changing Packages/manifest.json.",
      `Then run unity_cli extensions install ${manifest.id} --project <path>.`,
      "The CLI does not install optional Unity packages automatically."
    ];
  }
  if (status === "available_not_installed") {
    return [`Run unity_cli extensions install ${manifest.id} --project <path> to install the optional Unity-side extension directory.`];
  }
  if (status === "outdated") {
    return [`Run unity_cli extensions install ${manifest.id} --project <path> to refresh managed extension files.`];
  }
  if (status === "unknown_files") {
    return [`Inspect Packages/${packageName}/Extensions/${manifest.id}; extension remove only deletes files listed in ${manifestFileName}.`];
  }
  return ["Extension bundle files are installed and match the manifest."];
}

function projectionTargetRoot(includePath: string): string {
  const normalized = normalizePath(includePath).replace(/\/+$/, "");
  if (!normalized || normalized === ".") {
    throw new ExtensionInstallerError("E_EXTENSION_MANIFEST_INVALID", `Invalid extension install include path: ${includePath}`);
  }
  if (normalized === "unitypkg") return "";
  return basename(normalized);
}

function safeSourcePath(sourceRoot: string, relPath: string): string {
  const normalized = normalizePath(relPath).replace(/\/+$/, "");
  if (normalized.startsWith("../") || normalized === ".." || normalized.includes("/../") || normalized.startsWith("/")) {
    throw new ExtensionInstallerError("E_EXTENSION_MANIFEST_INVALID", `Unsafe extension source path: ${relPath}`);
  }
  const fullPath = resolve(sourceRoot, ...normalized.split("/"));
  const root = resolve(sourceRoot);
  if (fullPath !== root && !fullPath.startsWith(root + sep)) {
    throw new ExtensionInstallerError("E_EXTENSION_MANIFEST_INVALID", `Extension source path escapes root: ${relPath}`);
  }
  return fullPath;
}

function safeManagedPath(targetRoot: string, relPath: string): string {
  const normalized = normalizePath(relPath);
  if (normalized.startsWith("../") || normalized === ".." || normalized.includes("/../") || normalized.startsWith("/")) {
    throw new ExtensionInstallerError("E_EXTENSION_MANIFEST_INVALID", `Unsafe managed extension path: ${relPath}`);
  }
  const fullPath = resolve(targetRoot, ...normalized.split("/"));
  const root = resolve(targetRoot);
  if (fullPath !== root && !fullPath.startsWith(root + sep)) {
    throw new ExtensionInstallerError("E_EXTENSION_MANIFEST_INVALID", `Managed extension path escapes root: ${relPath}`);
  }
  return fullPath;
}

function normalizePath(path: string): string {
  return path.split("\\").join("/");
}

function emptyManifest(): ExtensionInstallManifest {
  return { version: 1, extensions: [] };
}

function isInstalledEntry(value: unknown): value is InstalledExtensionManifestEntry {
  const entry = value as Partial<InstalledExtensionManifestEntry>;
  return typeof entry?.extensionId === "string" && typeof entry.version === "string" && Array.isArray(entry.files);
}

export class ExtensionInstallerError extends Error {
  public availability?: ExtensionAvailabilityResult;

  public constructor(
    public readonly code: string,
    message: string,
    public readonly nextActions: string[] = []
  ) {
    super(message);
  }
}
