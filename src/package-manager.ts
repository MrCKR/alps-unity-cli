import { realpath, readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { join, relative, resolve, win32 } from "node:path";
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
  version: string;
}

const bridgePackageName = "com.unity_cli";
const cliCompatibilityRange = ">=0.1.0 <0.2.0";
const installMetadataFileName = "unity_cli.install.json";

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
    throw new PackageManagerError("PACKAGE_NOT_FOUND", `Package is not declared in manifest: ${name}`, ["Run unity_cli package list to inspect installed packages."]);
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
    throw new PackageManagerError("SCOPED_REGISTRY_NOT_FOUND", `Scoped registry not found: ${name}`, ["Run unity_cli package scoped-registry list."]);
  }
  const [removed] = registries.splice(index, 1);
  manifest.scopedRegistries = registries;
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, removed };
}

export interface UnityCliInstallMetadata {
  packageName: string;
  packageVersion: string;
  dependencySpec: string;
  projectHash: string;
  cliCompatibilityRange: string;
}

export interface UnityCliPackageInstall {
  manifestPath: string;
  bridgePath: string;
  dependencyName: string;
  dependencySpec: string;
  packageVersion: string;
  projectHash: string;
  cliCompatibilityRange: string;
  installMetadataPath: string;
}

export async function ensurePackageBridge(projectPath: string): Promise<UnityCliPackageInstall> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  manifest.dependencies ??= {};

  const sourcePath = packageSourcePath();
  const packageManifest = await readPackageManifest(sourcePath);
  const dependencySpec = fileDependencySpec(paths.projectPath, sourcePath);
  const projectHash = await hashProjectPath(paths.projectPath);
  const installMetadataPath = join(paths.projectPath, "Packages", installMetadataFileName);

  manifest.dependencies[bridgePackageName] = dependencySpec;
  await writeManifest(paths.manifestPath, manifest);
  await writeInstallMetadata(installMetadataPath, {
    packageName: bridgePackageName,
    packageVersion: packageManifest.version,
    dependencySpec,
    projectHash,
    cliCompatibilityRange
  });

  return {
    manifestPath: paths.manifestPath,
    bridgePath: sourcePath,
    dependencyName: bridgePackageName,
    dependencySpec,
    packageVersion: packageManifest.version,
    projectHash,
    cliCompatibilityRange,
    installMetadataPath
  };
}

export async function checkPackageBridge(projectPath: string): Promise<UnityCliPackageInstall> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const dependencySpec = manifest.dependencies?.[bridgePackageName];
  if (!dependencySpec?.startsWith("file:")) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} is not installed as a local file: dependency.`, ["Run unity_cli init --project <path> to install the bundled Unity package."]);
  }

  const bridgePath = resolveFileDependency(paths.projectPath, dependencySpec);
  const packageManifest = await readPackageManifest(bridgePath);
  if (packageManifest.name !== bridgePackageName) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `Installed package identity mismatch: expected ${bridgePackageName}, found ${packageManifest.name}.`, ["Run unity_cli init --project <path> to reinstall the bundled Unity package."]);
  }

  const installMetadataPath = join(paths.projectPath, "Packages", installMetadataFileName);
  const installMetadata = await readInstallMetadata(installMetadataPath);
  const projectHash = await hashProjectPath(paths.projectPath);
  if (installMetadata.packageName !== bridgePackageName || installMetadata.packageVersion !== packageManifest.version || installMetadata.projectHash !== projectHash || installMetadata.cliCompatibilityRange !== cliCompatibilityRange) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} install metadata is stale or incomplete.`, ["Run unity_cli init --project <path> to refresh install metadata."]);
  }

  return {
    manifestPath: paths.manifestPath,
    bridgePath,
    dependencyName: bridgePackageName,
    dependencySpec,
    packageVersion: packageManifest.version,
    projectHash,
    cliCompatibilityRange,
    installMetadataPath
  };
}

export function parsePackageSpec(raw: string, explicitVersion?: string): PackageSpec {
  const atIndex = raw.lastIndexOf("@");
  if (atIndex > 0) {
    return { name: raw.slice(0, atIndex), version: explicitVersion ?? raw.slice(atIndex + 1) };
  }
  return { name: raw, version: explicitVersion ?? "latest" };
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
  if (isWindowsAbsolutePath(packagePath)) {
    return `file:${normalizeRelativePath(packagePath)}`;
  }
  const dependencyPath = normalizeRelativePath(relative(join(projectPath, "Packages"), packagePath));
  const normalized = dependencyPath.startsWith(".") ? dependencyPath : `./${dependencyPath}`;
  return `file:${normalized}`;
}

function resolveFileDependency(projectPath: string, spec: string): string {
  const dependencyPath = spec.slice("file:".length);
  if (isWindowsAbsolutePath(dependencyPath)) {
    return dependencyPath;
  }
  return resolve(join(projectPath, "Packages"), dependencyPath);
}

async function hashProjectPath(projectPath: string): Promise<string> {
  const canonical = await realpath(projectPath).catch(() => resolve(projectPath));
  return createHash("sha256").update(canonical.toLowerCase()).digest("hex");
}

async function writeInstallMetadata(path: string, metadata: UnityCliInstallMetadata): Promise<void> {
  await writeFile(path, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");
}

async function readInstallMetadata(path: string): Promise<UnityCliInstallMetadata> {
  try {
    const metadata = JSON.parse(stripJsonBom(await readFile(path, "utf8"))) as Partial<UnityCliInstallMetadata>;
    if (typeof metadata.packageName !== "string" || typeof metadata.packageVersion !== "string" || typeof metadata.dependencySpec !== "string" || typeof metadata.projectHash !== "string" || typeof metadata.cliCompatibilityRange !== "string") {
      throw new PackageManagerError("INIT_CHECK_FAILED", `Install metadata is invalid: ${path}`, ["Run unity_cli init --project <path> to refresh install metadata."]);
    }
    return metadata as UnityCliInstallMetadata;
  } catch (error) {
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

function isWindowsAbsolutePath(path: string): boolean {
  return win32.isAbsolute(path) || /^[A-Za-z]:[\\/]/.test(path);
}

function normalizeRelativePath(path: string): string {
  return path.split("\\").join("/");
}
