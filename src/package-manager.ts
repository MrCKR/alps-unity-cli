import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";

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

const defaultManifest: UnityManifest = { dependencies: {} };

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
  const manifest = await readManifest(paths.manifestPath, true);
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
    throw new PackageManagerError("PACKAGE_NOT_FOUND", `Package is not declared in manifest: ${name}`, ["Run alps-unity package list to inspect installed packages."]);
  }
  delete manifest.dependencies?.[name];
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, removedVersion };
}

export async function setRegistry(projectPath: string, url: string): Promise<{ manifestPath: string; registry: string }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, true);
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
  const manifest = await readManifest(paths.manifestPath, true);
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
    throw new PackageManagerError("SCOPED_REGISTRY_NOT_FOUND", `Scoped registry not found: ${name}`, ["Run alps-unity package scoped-registry list."]);
  }
  const [removed] = registries.splice(index, 1);
  manifest.scopedRegistries = registries;
  await writeManifest(paths.manifestPath, manifest);
  return { manifestPath: paths.manifestPath, removed };
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

async function readManifest(path: string, allowMissing: boolean): Promise<UnityManifest> {
  try {
    return JSON.parse(await readFile(path, "utf8")) as UnityManifest;
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT" && allowMissing) {
      return structuredClone(defaultManifest);
    }
    if (nodeError.code === "ENOENT") {
      throw new PackageManagerError("MANIFEST_NOT_FOUND", `Unity manifest not found: ${path}`, ["Run the command from a Unity project root or pass --project <path>."]);
    }
    if (error instanceof SyntaxError) {
      throw new PackageManagerError("MANIFEST_INVALID", `Unity manifest is invalid JSON: ${path}`);
    }
    throw error;
  }
}

async function readLock(path: string): Promise<UnityLock> {
  try {
    return JSON.parse(await readFile(path, "utf8")) as UnityLock;
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
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
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
