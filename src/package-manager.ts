import { realpath, readFile, writeFile, readdir, stat, mkdir, copyFile, rm } from "node:fs/promises";
import { createHash } from "node:crypto";
import { join, relative, resolve, win32, dirname, posix } from "node:path";
import { fileURLToPath } from "node:url";

export interface UnityManifest {
  dependencies?: Record<string, string>;
  scopedRegistries?: ScopedRegistry[];
  registry?: string;
  [key: string]: unknown;
}

export interface UnityLock {
  dependencies?: Record<string, LockPackage>;
  [key: string]: unknown;
}

export interface ScopedRegistry {
  name: string;
  url: string;
  scopes: string[];
  [key: string]: unknown;
}

export interface LockPackage {
  version?: string;
  depth?: number;
  source?: string;
  dependencies?: Record<string, string>;
  url?: string;
  hash?: string;
  [key: string]: unknown;
}

export interface PackageManagerPaths {
  projectPath: string;
  manifestPath: string;
  lockPath: string;
}

export interface PackageEntry {
  name: string;
  requestedVersion?: string;
  lockedVersion?: string;
  source?: string;
  url?: string;
  hash?: string;
  dependencies?: Record<string, string>;
}

export interface PackageSnapshot {
  projectPath: string;
  manifestPath: string;
  lockPath: string;
  registry?: string;
  scopedRegistries: ScopedRegistry[];
  packages: PackageEntry[];
}

export interface PackageSpec {
  name: string;
  version?: string;
}

const bridgePackageName = "com.unity_cli";
const cliCompatibilityRange = ">=0.1.0 <0.2.0";
const installMetadataFileName = "unity_cli.install.json";
const fileManifestFileName = "unity_cli.files.json";

export function packagePaths(projectPath: string): PackageManagerPaths {
  const resolvedProject = resolve(projectPath);
  return {
    projectPath: resolvedProject,
    manifestPath: join(resolvedProject, "Packages", "manifest.json"),
    lockPath: join(resolvedProject, "Packages", "packages-lock.json")
  };
}

export async function readSnapshot(projectPath: string): Promise<PackageSnapshot> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const lock = await readLock(paths.lockPath);
  const dependencyNames = new Set([...Object.keys(manifest.dependencies ?? {}), ...Object.keys(lock.dependencies ?? {})]);
  const packages = [...dependencyNames].sort().map((name) => toPackageEntry(name, manifest.dependencies?.[name], lock.dependencies?.[name]));

  return {
    projectPath: paths.projectPath,
    manifestPath: paths.manifestPath,
    lockPath: paths.lockPath,
    registry: typeof manifest.registry === "string" ? manifest.registry : undefined,
    scopedRegistries: normalizeScopedRegistries(manifest.scopedRegistries),
    packages
  };
}

export async function packageInfo(projectPath: string, name: string): Promise<PackageEntry | undefined> {
  return (await readSnapshot(projectPath)).packages.find((entry) => entry.name === name);
}

export async function installPackage(projectPath: string, spec: PackageSpec): Promise<{ manifestPath: string; previousVersion?: string; installedVersion: string }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  if (!spec.version) {
    throw new PackageManagerError("PACKAGE_VERSION_REQUIRED", `Package install requires an explicit Unity package version for manifest writes: ${spec.name}`, [
      "Pass a SemVer version with --version or append @<version> to the package name, e.g. com.unity.textmeshpro@3.0.7.",
      "Use package.search or Unity Package Manager documentation to choose a compatible version."
    ]);
  }
  manifest.dependencies ??= {};
  const previousVersion = manifest.dependencies[spec.name];
  manifest.dependencies[spec.name] = spec.version;
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, previousVersion, installedVersion: spec.version };
}

export async function removePackage(projectPath: string, name: string): Promise<{ manifestPath: string; removedVersion: string }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const removedVersion = manifest.dependencies?.[name];
  if (!removedVersion) {
    throw new PackageManagerError("PACKAGE_NOT_FOUND", `Package is not declared in manifest: ${name}`, ["Run tools run package.list to inspect installed packages."]);
  }
  delete manifest.dependencies?.[name];
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, removedVersion };
}

export async function setRegistry(projectPath: string, url: string): Promise<{ manifestPath: string; registry: string }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  manifest.registry = url;
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, registry: url };
}

export async function clearRegistry(projectPath: string): Promise<{ manifestPath: string; previousRegistry?: string }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const previousRegistry = typeof manifest.registry === "string" ? manifest.registry : undefined;
  delete manifest.registry;
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, previousRegistry };
}

export async function addScopedRegistry(projectPath: string, registry: ScopedRegistry): Promise<{ manifestPath: string; registry: ScopedRegistry; replaced: boolean }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const registries = normalizeScopedRegistries(manifest.scopedRegistries);
  const index = registries.findIndex((existing) => existing.name === registry.name);
  const normalized = { name: registry.name, url: registry.url, scopes: uniqueScopes(registry.scopes) };
  const replaced = index >= 0;
  if (replaced) {
    registries[index] = normalized;
  } else {
    registries.push(normalized);
  }
  manifest.scopedRegistries = registries;
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, registry: normalized, replaced };
}

export async function removeScopedRegistry(projectPath: string, name: string): Promise<{ manifestPath: string; removed: ScopedRegistry }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const registries = normalizeScopedRegistries(manifest.scopedRegistries);
  const index = registries.findIndex((registry) => registry.name === name);
  if (index < 0) {
    throw new PackageManagerError("SCOPED_REGISTRY_NOT_FOUND", `Scoped registry not found: ${name}`, ["Run tools run package.scoped-registry --input '{\"action\":\"list\"}' to inspect scoped registries."]);
  }
  const [removed] = registries.splice(index, 1);
  manifest.scopedRegistries = registries;
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, removed };
}

export interface UnityCliInstallMetadata {
  packageName: string;
  packageVersion: string;
  sourceHash: string;
  projectHash: string;
  installMode: "copy";
  installTime: string;
  cliCompatibilityRange: string;
}

export interface UnityCliPackageInstall {
  manifestPath: string;
  bridgePath: string;
  dependencyName: string;
  packageVersion: string;
  sourceHash: string;
  projectHash: string;
  installMode: "copy";
  installTime: string;
  cliCompatibilityRange: string;
  installMetadataPath: string;
  fileManifestPath: string;
}

export async function ensurePackageBridge(projectPath: string): Promise<UnityCliPackageInstall> {
  const paths = packagePaths(projectPath);
  const sourcePath = packageSourcePath();
  const packageManifest = await readPackageManifest(sourcePath);
  const projectHash = await hashProjectPath(paths.projectPath);
  const sourceHash = await hashSourcePath(sourcePath);
  const targetPath = join(paths.projectPath, "Packages", bridgePackageName);
  const installMetadataPath = join(paths.projectPath, "Packages", installMetadataFileName);
  const fileManifestPath = join(targetPath, fileManifestFileName);

  // Check existing installation: skip if same version & hash
  const existing = await readExistingInstallMetadata(installMetadataPath).catch(() => undefined);
  if (existing && existing.packageVersion === packageManifest.version && existing.sourceHash === sourceHash && existing.projectHash === projectHash) {
    return {
      manifestPath: paths.manifestPath,
      bridgePath: targetPath,
      dependencyName: bridgePackageName,
      packageVersion: packageManifest.version,
      sourceHash,
      projectHash,
      installMode: "copy",
      installTime: existing.installTime,
      cliCompatibilityRange,
      installMetadataPath,
      fileManifestPath
    };
  }

  // Remove stale managed files before copying
  await removeStaleManagedFiles(targetPath, fileManifestPath);

  // Copy source to target, tracking managed files
  const managedFiles = await copyPackageContents(sourcePath, targetPath);

  // Write file manifest (tracks which files we manage so we don't delete user files on update)
  await writeFileManifest(fileManifestPath, managedFiles);

  // Remove any file: dependency from manifest if present (migration from old install mode)
  const manifest = await readManifest(paths.manifestPath, false);
  manifest.dependencies ??= {};
  if (manifest.dependencies[bridgePackageName]?.startsWith("file:")) {
    delete manifest.dependencies[bridgePackageName];
    await writeManifest(paths.manifestPath, manifest);
  }

  const installTime = new Date().toISOString();
  await writeInstallMetadata(installMetadataPath, {
    packageName: bridgePackageName,
    packageVersion: packageManifest.version,
    sourceHash,
    projectHash,
    installMode: "copy",
    installTime,
    cliCompatibilityRange
  });

  return {
    manifestPath: paths.manifestPath,
    bridgePath: targetPath,
    dependencyName: bridgePackageName,
    packageVersion: packageManifest.version,
    sourceHash,
    projectHash,
    installMode: "copy",
    installTime,
    cliCompatibilityRange,
    installMetadataPath,
    fileManifestPath
  };
}

export async function checkPackageBridge(projectPath: string): Promise<UnityCliPackageInstall> {
  const paths = packagePaths(projectPath);
  const targetPath = join(paths.projectPath, "Packages", bridgePackageName);
  const installMetadataPath = join(paths.projectPath, "Packages", installMetadataFileName);
  const fileManifestPath = join(targetPath, fileManifestFileName);

  // Verify the copied package exists and has correct identity
  let packageManifest: { name: string; version: string };
  try {
    packageManifest = await readPackageManifest(targetPath);
  } catch (error) {
    if (error instanceof PackageManagerError) {
      throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} is not installed in copy mode: ${error.message}`, ["Run unity_cli init --project <path> to install the bundled Unity package."]);
    }
    throw error;
  }
  if (packageManifest.name !== bridgePackageName) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `Installed package identity mismatch: expected ${bridgePackageName}, found ${packageManifest.name}.`, ["Run unity_cli init --project <path> to reinstall the bundled Unity package."]);
  }

  // Verify install metadata
  const installMetadata = await readInstallMetadata(installMetadataPath);
  if (installMetadata.packageName !== bridgePackageName) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `Install metadata package name mismatch: expected ${bridgePackageName}, found ${installMetadata.packageName}.`, ["Run unity_cli init --project <path> to reinstall the bundled Unity package."]);
  }

  const projectHash = await hashProjectPath(paths.projectPath);
  const sourcePath = packageSourcePath();
  const sourceHash = await hashSourcePath(sourcePath);

  if (installMetadata.installMode !== "copy") {
    throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} is not installed in copy mode.`, ["Run unity_cli init --project <path> to reinstall the bundled Unity package in copy mode."]);
  }

  if (installMetadata.packageVersion !== packageManifest.version) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} version mismatch: metadata says ${installMetadata.packageVersion}, package.json says ${packageManifest.version}.`, ["Run unity_cli init --project <path> to reinstall the bundled Unity package."]);
  }

  if (installMetadata.sourceHash !== sourceHash) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} source has changed since installation (hash mismatch).`, ["Run unity_cli init --project <path> to update the bundled Unity package."]);
  }

  if (installMetadata.projectHash !== projectHash) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} install metadata project hash is stale.`, ["Run unity_cli init --project <path> to refresh install metadata."]);
  }

  if (installMetadata.cliCompatibilityRange !== cliCompatibilityRange) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} CLI compatibility range has changed.`, ["Run unity_cli init --project <path> to refresh install metadata."]);
  }

  return {
    manifestPath: paths.manifestPath,
    bridgePath: targetPath,
    dependencyName: bridgePackageName,
    packageVersion: packageManifest.version,
    sourceHash,
    projectHash,
    installMode: "copy",
    installTime: installMetadata.installTime,
    cliCompatibilityRange,
    installMetadataPath,
    fileManifestPath
  };
}

export function parsePackageSpec(raw: string, explicitVersion?: string): PackageSpec {
  const atIndex = raw.lastIndexOf("@");
  if (atIndex > 0) {
    return { name: raw.slice(0, atIndex), version: explicitVersion ?? raw.slice(atIndex + 1) };
  }
  return { name: raw, version: explicitVersion };
}

export function validatePackageName(name: string | undefined): string {
  if (!name) {
    throw new PackageManagerError("PACKAGE_NAME_REQUIRED", "Pass a package name.");
  }
  return name;
}

export function validateRegistryUrl(url: string | undefined): string {
  if (!url) {
    throw new PackageManagerError("REGISTRY_URL_REQUIRED", "Pass --url <registryUrl>.");
  }
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== "https:" && parsed.protocol !== "http:") {
      throw new Error("Unsupported protocol");
    }
    return url;
  } catch {
    throw new PackageManagerError("REGISTRY_URL_INVALID", `Registry URL is invalid: ${url}`);
  }
}

export function parseScopes(raw: string | undefined): string[] {
  const scopes = (raw ?? "").split(",").map((scope) => scope.trim()).filter(Boolean);
  if (scopes.length === 0) {
    throw new PackageManagerError("SCOPES_REQUIRED", "Pass --scope <scope> or comma-separated scopes.");
  }
  return uniqueScopes(scopes);
}

export async function searchRegistry(registry: string, query: string): Promise<Array<{ name: string; version?: string; description?: string }>> {
  const endpoint = `${registry.replace(/\/$/, "")}/-/v1/search?text=${encodeURIComponent(query)}`;
  let response: Response;
  try {
    response = await fetch(endpoint);
  } catch (error) {
    throw new PackageManagerError("REGISTRY_UNREACHABLE", error instanceof Error ? error.message : String(error), ["Check the registry URL and network connectivity."]);
  }
  if (!response.ok) {
    throw new PackageManagerError("REGISTRY_UNREACHABLE", `Registry returned HTTP ${response.status}.`, ["Check the registry URL and credentials."]);
  }
  const payload = (await response.json()) as { objects?: Array<{ package?: { name?: string; version?: string; description?: string } }> };
  return (payload.objects ?? [])
    .map((entry) => entry.package)
    .filter((entry): entry is { name: string; version?: string; description?: string } => typeof entry?.name === "string");
}

export class PackageManagerError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly nextActions: string[] = []
  ) {
    super(message);
  }
}

function packageSourcePath(): string {
  return resolve(fileURLToPath(new URL("../unitypkg", import.meta.url)));
}

async function readPackageManifest(packagePath: string): Promise<{ name: string; version: string }> {
  try {
    const manifest = JSON.parse(stripJsonBom(await readFile(join(packagePath, "package.json"), "utf8"))) as { name?: unknown; version?: unknown };
    if (manifest.name !== bridgePackageName) {
      throw new PackageManagerError("PACKAGE_IDENTITY_INVALID", `Unity package name must be ${bridgePackageName}.`);
    }
    if (typeof manifest.version !== "string" || !manifest.version) {
      throw new PackageManagerError("PACKAGE_VERSION_INVALID", `${bridgePackageName} package.json requires a version.`);
    }
    return { name: manifest.name, version: manifest.version };
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT") {
      throw new PackageManagerError("PACKAGE_MANIFEST_NOT_FOUND", `Unity package manifest not found under: ${packagePath}`);
    }
    if (error instanceof SyntaxError) {
      throw new PackageManagerError("PACKAGE_MANIFEST_INVALID", `Unity package manifest is invalid JSON: ${join(packagePath, "package.json")}`);
    }
    throw error;
  }
}

export function fileDependencySpec(projectPath: string, packagePath: string): string {
  const path = pathHelpersFor(projectPath, packagePath);
  const dependencyPath = normalizeRelativePath(path.relative(path.join(projectPath, "Packages"), packagePath));
  if (isAbsoluteDependencyPath(dependencyPath)) {
    return `file:${dependencyPath}`;
  }
  const normalized = dependencyPath.startsWith(".") ? dependencyPath : `./${dependencyPath}`;
  return `file:${normalized}`;
}

export function resolveFileDependency(projectPath: string, spec: string): string {
  const dependencyPath = spec.slice("file:".length);
  const path = pathHelpersFor(projectPath, dependencyPath);
  return path.resolve(path.join(projectPath, "Packages"), dependencyPath);
}

async function hashProjectPath(projectPath: string): Promise<string> {
  const canonical = await realpath(projectPath).catch(() => resolve(projectPath));
  return createHash("sha256").update(canonical.toLowerCase()).digest("hex");
}

async function writeInstallMetadata(path: string, metadata: UnityCliInstallMetadata): Promise<void> {
  await writeFile(path, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");
}

async function readExistingInstallMetadata(path: string): Promise<UnityCliInstallMetadata> {
  return readInstallMetadata(path);
}

async function readInstallMetadata(path: string): Promise<UnityCliInstallMetadata> {
  try {
    const metadata = JSON.parse(stripJsonBom(await readFile(path, "utf8"))) as Partial<UnityCliInstallMetadata>;
    if (typeof metadata.packageName !== "string" || typeof metadata.packageVersion !== "string" || typeof metadata.sourceHash !== "string" || typeof metadata.projectHash !== "string" || metadata.installMode !== "copy" || typeof metadata.installTime !== "string" || typeof metadata.cliCompatibilityRange !== "string") {
      throw new PackageManagerError("INIT_CHECK_FAILED", `Install metadata is invalid: ${path}`, ["Run unity_cli init --project <path> to refresh install metadata."]);
    }
    return metadata as UnityCliInstallMetadata;
  } catch (error) {
    if (error instanceof PackageManagerError) {
      throw error;
    }
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT") {
      throw new PackageManagerError("INIT_CHECK_FAILED", `Install metadata not found: ${path}`, ["Run unity_cli init --project <path> to create install metadata."]);
    }
    if (error instanceof SyntaxError) {
      throw new PackageManagerError("INIT_CHECK_FAILED", `Install metadata is invalid JSON: ${path}`, ["Run unity_cli init --project <path> to refresh install metadata."]);
    }
    throw error;
  }
}

interface FileManifest {
  version: number;
  files: string[];
}

async function copyPackageContents(sourcePath: string, targetPath: string): Promise<string[]> {
  const files: string[] = [];
  await copyDirRecursive(sourcePath, targetPath, sourcePath, files);
  return files;
}

async function copyDirRecursive(dir: string, targetRoot: string, sourceRoot: string, files: string[]): Promise<void> {
  const entries = await readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    const srcPath = join(dir, entry.name);
    const relPath = posix.normalize(relative(sourceRoot, srcPath).split("\\").join("/"));
    const destPath = join(targetRoot, relPath);
    if (entry.isDirectory()) {
      await mkdir(destPath, { recursive: true });
      await copyDirRecursive(srcPath, targetRoot, sourceRoot, files);
    } else if (entry.isFile()) {
      await mkdir(dirname(destPath), { recursive: true });
      await copyFile(srcPath, destPath);
      files.push(relPath);
    }
    // Symlinks are skipped
  }
}

async function hashSourcePath(sourcePath: string): Promise<string> {
  const hash = createHash("sha256");
  await hashDirectory(hash, sourcePath, sourcePath);
  return hash.digest("hex");
}

async function hashDirectory(hash: ReturnType<typeof createHash>, dir: string, root: string): Promise<void> {
  const entries = await readdir(dir, { withFileTypes: true });
  // Sort for deterministic hash
  const sorted = entries.sort((a, b) => a.name.localeCompare(b.name));
  for (const entry of sorted) {
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      hash.update(`dir:${relative(root, fullPath).split("\\").join("/")}/\n`);
      await hashDirectory(hash, fullPath, root);
    } else if (entry.isFile()) {
      const relPath = relative(root, fullPath).split("\\").join("/");
      hash.update(`file:${relPath}\n`);
      const content = await readFile(fullPath);
      hash.update(content);
      hash.update("\n");
    }
  }
}

async function removeStaleManagedFiles(targetPath: string, fileManifestPath: string): Promise<void> {
  const managedFiles = await readManagedFileList(fileManifestPath);
  if (managedFiles.length === 0) {
    return;
  }
  for (const relPath of managedFiles) {
    const fullPath = join(targetPath, relPath);
    try {
      await rm(fullPath, { force: true });
    } catch {
      // Best-effort: skip files that can't be removed
    }
  }
}

async function readManagedFileList(fileManifestPath: string): Promise<string[]> {
  try {
    const data = JSON.parse(stripJsonBom(await readFile(fileManifestPath, "utf8"))) as FileManifest;
    if (data && Array.isArray(data.files)) {
      return data.files;
    }
    return [];
  } catch {
    return [];
  }
}

async function writeFileManifest(path: string, files: string[]): Promise<void> {
  const manifest: FileManifest = { version: 1, files };
  await writeFile(path, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
}

async function readManifest(path: string, allowMissing: boolean): Promise<UnityManifest> {
  try {
    return JSON.parse(stripJsonBom(await readFile(path, "utf8"))) as UnityManifest;
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT") {
      throw new PackageManagerError("MANIFEST_NOT_FOUND", `Unity manifest not found: ${path}`, ["Run the command from a Unity project root or pass --project <path>. Package write commands do not create a new Unity manifest implicitly."]);
    }
    if (error instanceof SyntaxError) {
      throw new PackageManagerError("MANIFEST_INVALID", `Unity manifest is invalid JSON: ${path}`);
    }
    throw error;
  }
}

async function readLock(path: string): Promise<UnityLock> {
  try {
    return JSON.parse(stripJsonBom(await readFile(path, "utf8"))) as UnityLock;
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT") {
      return { dependencies: {} };
    }
    if (error instanceof SyntaxError) {
      throw new PackageManagerError("LOCK_INVALID", `Unity packages lock is invalid JSON: ${path}`);
    }
    throw error;
  }
}

async function writeManifest(path: string, manifest: UnityManifest): Promise<void> {
  await writeFile(path, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
}

function stripJsonBom(value: string): string {
  return value.charCodeAt(0) === 0xfeff ? value.slice(1) : value;
}

function toPackageEntry(name: string, requestedVersion: string | undefined, locked: LockPackage | undefined): PackageEntry {
  return {
    name,
    requestedVersion,
    lockedVersion: locked?.version,
    source: locked?.source,
    url: locked?.url,
    hash: locked?.hash,
    dependencies: locked?.dependencies
  };
}

function normalizeScopedRegistries(scopedRegistries: unknown): ScopedRegistry[] {
  if (!Array.isArray(scopedRegistries)) {
    return [];
  }
  return scopedRegistries.filter((registry): registry is ScopedRegistry => {
    return typeof registry?.name === "string" && typeof registry.url === "string" && Array.isArray(registry.scopes);
  }).map((registry) => ({ name: registry.name, url: registry.url, scopes: uniqueScopes(registry.scopes.map(String)) }));
}

function uniqueScopes(scopes: string[]): string[] {
  return [...new Set(scopes.map((scope) => scope.trim()).filter(Boolean))];
}

function normalizeRelativePath(path: string): string {
  return path.split("\\").join("/");
}

function pathHelpersFor(firstPath: string, secondPath: string): Pick<typeof win32, "join" | "relative" | "resolve"> {
  return hasWindowsDrivePath(firstPath) || hasWindowsDrivePath(secondPath) ? win32 : { join, relative, resolve };
}

function hasWindowsDrivePath(path: string): boolean {
  return /^[A-Za-z]:[\\/]/.test(path);
}

function isAbsoluteDependencyPath(path: string): boolean {
  return path.startsWith("/") || /^[A-Za-z]:\//.test(path);
}
