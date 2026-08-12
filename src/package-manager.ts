import { realpath, readFile, readdir, stat, mkdir, copyFile, rename, rm, rmdir, open } from "node:fs/promises";
import { createHash, randomUUID } from "node:crypto";
import { basename, join, relative, resolve, win32, dirname, posix, isAbsolute } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

export interface UnityManifest {
  dependencies?: Record<string, string>;
  scopedRegistries?: ScopedRegistry[];
  registry?: string;
  [key: string]: unknown;
}

/** 严格读取 Unity Packages/manifest.json；调用方不得把损坏的依赖声明当作空状态。 */
export async function readUnityManifestDependencies(projectPath: string): Promise<Record<string, string>> {
  const manifest = await readManifest(packagePaths(projectPath).manifestPath, false);
  if (!isPlainObject(manifest) || !isPlainObject(manifest.dependencies)
    || !Object.values(manifest.dependencies).every((value) => typeof value === "string")) {
    throw new PackageManagerError("MANIFEST_INVALID", `Unity manifest requires an object root with a string-valued dependencies object: ${packagePaths(projectPath).manifestPath}`);
  }
  return { ...manifest.dependencies };
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

export interface PackageDependent {
  name: string;
  requiredVersion?: string;
}

export interface RemovePackageOptions {
  force?: boolean;
  dryRun?: boolean;
}

export interface ManifestMutationOptions {
  dryRun?: boolean;
}

export interface RemoveScopedRegistryOptions {
  force?: boolean;
  dryRun?: boolean;
}

export interface RemovePackageBridgeOptions {
  dryRun?: boolean;
}

export interface PackageDeployExpectedPreState {
  sourcePath: string;
  sourceHash: string;
  targetExists: boolean;
  targetHash?: string;
  recordIdentity: string;
  backupPath?: string;
  backupExists: boolean;
  backupHash?: string;
}

export interface DeployPackageOptions {
  packageName?: string;
  dryRun?: boolean;
  /** durable worker 只能使用 prepare 阶段冻结的备份路径与完整业务pre-state。 */
  plannedBackupPath?: string;
  expectedPreState?: PackageDeployExpectedPreState;
}

export interface PackageRestoreExpectedPreState {
  targetPath: string;
  targetExists: boolean;
  targetHash?: string;
  recordIdentity: string;
  backupPath?: string;
  backupExists: boolean;
  backupHash?: string;
}

export interface RestorePackageOptions {
  dryRun?: boolean;
  expectedPreState?: PackageRestoreExpectedPreState;
}

export interface PackageDeploymentRecord {
  packageName: string;
  packageVersion: string;
  sourcePath: string;
  sourceHash: string;
  targetPath: string;
  backupPath?: string;
  backupPackageVersion?: string;
  deployedAt: string;
}

interface PackageDeploymentTransaction {
  version: 1;
  operation: "deploy" | "restore";
  phase: "prepared" | "target_moved" | "target_published" | "metadata_published" | "commit_proven";
  packageName: string;
  targetPath: string;
  stagingPath: string;
  rollbackPath: string;
  backupPath?: string;
  targetExisted: boolean;
  backupCreated: boolean;
  deleteBackupOnCommit: boolean;
  oldRecord?: PackageDeploymentRecord;
}

export interface PackageDeployResult {
  projectPath: string;
  manifestPath: string;
  packageName: string;
  packageVersion: string;
  sourcePath: string;
  sourceHash: string;
  targetPath: string;
  backupPath?: string;
  backupPackageVersion?: string;
  backupCreated: boolean;
  repeatedDeployment: boolean;
  copiedFileCount: number;
  dryRun: boolean;
  plannedEffects: Array<Record<string, unknown>>;
  metadataPath: string;
}

export interface PackageRestoreResult {
  projectPath: string;
  manifestPath: string;
  packageName: string;
  targetPath: string;
  backupPath?: string;
  restoredFromBackup: boolean;
  removedDeployedPackage: boolean;
  copiedFileCount: number;
  dryRun: boolean;
  plannedEffects: Array<Record<string, unknown>>;
  metadataPath: string;
}

export interface UnityCliPackageRemoval {
  projectPath: string;
  manifestPath: string;
  bridgePath: string;
  dependencyName: string;
  packageVersion: string;
  installMetadataPath: string;
  fileManifestPath: string;
  managedFileCount: number;
  dryRun: boolean;
  plannedEffects: Array<Record<string, unknown>>;
}

const bridgePackageName = "com.unity_cli";
const cliCompatibilityRange = ">=0.2.0 <0.3.0";
const installMetadataFileName = "unity_cli.install.json";
const fileManifestFileName = "unity_cli.files.json";
const deploymentMetadataFileName = "unity_cli.package-deployments.json";
const deploymentBackupDirName = ".unity_cli_deploy_backups";
const packageBridgeProcessStartIdentity = new Date(Date.now() - Math.floor(process.uptime() * 1_000)).toISOString();
const execFileAsync = promisify(execFile);
const activeFileMutationCandidates = new Set<string>();
const activeAtomicWriteCandidates = new Set<string>();
let cachedCurrentProcessStartIdentity: string | undefined;

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

export async function installPackage(projectPath: string, spec: PackageSpec, options: ManifestMutationOptions = {}): Promise<{ manifestPath: string; installedVersion: string; dependencyInstalled?: true; dryRun?: true; plannedEffects?: Array<Record<string, unknown>> }> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return options.dryRun ? installPackageLocked(resolvedProject, spec, options) : withProjectFileMutationLease(resolvedProject, "package-manifest-mutation", () => installPackageLocked(resolvedProject, spec, options));
}

async function installPackageLocked(projectPath: string, spec: PackageSpec, options: ManifestMutationOptions): Promise<{ manifestPath: string; installedVersion: string; dependencyInstalled?: true; dryRun?: true; plannedEffects?: Array<Record<string, unknown>> }> {
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
  if (previousVersion && isPackageVersionDowngrade(spec.version, previousVersion)) {
    throw new PackageManagerError("PACKAGE_DOWNGRADE_BLOCKED", `Refusing to downgrade ${spec.name} from ${previousVersion} to ${spec.version}.`, [
      "Choose a version greater than or equal to the currently requested manifest version.",
      "Run tools run package.info --input '{\"name\":\"<package>\"}' to inspect the current requested and locked versions."
    ]);
  }
  if (options.dryRun) return { manifestPath: paths.manifestPath, installedVersion: spec.version, dryRun: true, plannedEffects: [{ type: "package.install", package: spec.name, version: spec.version, previousVersion }] };
  manifest.dependencies[spec.name] = spec.version;
  await writeManifest(paths.manifestPath, manifest);
  const readback = await readManifest(paths.manifestPath, false);
  const installedVersion = readback.dependencies?.[spec.name];
  if (installedVersion !== spec.version) {
    throw manifestReadbackMismatch(paths.manifestPath, `dependency ${spec.name} was not written with the requested version`);
  }
  return { manifestPath: paths.manifestPath, installedVersion, dependencyInstalled: true };
}

export async function removePackage(projectPath: string, name: string, options: RemovePackageOptions = {}): Promise<{ manifestPath: string; removedVersion?: string; dependencyRemoved?: true; dependents: PackageDependent[]; forced: boolean; dryRun: boolean; blocked: boolean; plannedEffects?: Array<Record<string, unknown>> }> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return options.dryRun ? removePackageLocked(resolvedProject, name, options) : withProjectFileMutationLease(resolvedProject, "package-manifest-mutation", () => removePackageLocked(resolvedProject, name, options));
}

async function removePackageLocked(projectPath: string, name: string, options: RemovePackageOptions): Promise<{ manifestPath: string; removedVersion?: string; dependencyRemoved?: true; dependents: PackageDependent[]; forced: boolean; dryRun: boolean; blocked: boolean; plannedEffects?: Array<Record<string, unknown>> }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const lock = await readLock(paths.lockPath);
  const removedVersion = manifest.dependencies?.[name];
  if (!removedVersion) {
    throw new PackageManagerError("PACKAGE_NOT_FOUND", `Package is not declared in manifest: ${name}`, ["Run tools run package.list to inspect installed packages."]);
  }
  const dependents = packageDependents(lock, name);
  const blocked = dependents.length > 0 && !options.force;
  if (blocked && !options.dryRun) {
    throw new PackageManagerError("PACKAGE_DEPENDENTS_BLOCKED", `Package ${name} is required by ${dependents.length} locked package(s): ${dependents.map((entry) => entry.name).join(", ")}.`, [
      "Remove or replace dependent packages first, then retry package.remove.",
      "Rerun with force=true only when you intentionally want Unity PackageManager to resolve the broken dependency graph."
    ]);
  }
  const plannedEffects = [{ type: "package.remove", package: name, removedVersion, dependents, forced: options.force === true, blocked }];
  if (!options.dryRun) {
    delete manifest.dependencies?.[name];
    await writeManifest(paths.manifestPath, manifest);
    const readback = await readManifest(paths.manifestPath, false);
    if (readback.dependencies && Object.prototype.hasOwnProperty.call(readback.dependencies, name)) {
      throw manifestReadbackMismatch(paths.manifestPath, `dependency ${name} is still declared`);
    }
    return { manifestPath: paths.manifestPath, dependencyRemoved: true, dependents, forced: options.force === true, dryRun: false, blocked: false };
  }
  return { manifestPath: paths.manifestPath, removedVersion, dependents, forced: options.force === true, dryRun: true, blocked, plannedEffects };
}

export async function setRegistry(projectPath: string, url: string, options: ManifestMutationOptions = {}): Promise<{ manifestPath: string; registry: string; dryRun?: true; plannedEffects?: Array<Record<string, unknown>> }> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return options.dryRun ? setRegistryLocked(resolvedProject, url, options) : withProjectFileMutationLease(resolvedProject, "package-manifest-mutation", () => setRegistryLocked(resolvedProject, url, options));
}

async function setRegistryLocked(projectPath: string, url: string, options: ManifestMutationOptions): Promise<{ manifestPath: string; registry: string; dryRun?: true; plannedEffects?: Array<Record<string, unknown>> }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  if (options.dryRun) return { manifestPath: paths.manifestPath, registry: url, dryRun: true, plannedEffects: [{ type: "package.registry.set", registry: url, previousRegistry: manifest.registry }] };
  manifest.registry = url;
  await writeManifest(paths.manifestPath, manifest);
  const readback = await readManifest(paths.manifestPath, false);
  if (readback.registry !== url) {
    throw manifestReadbackMismatch(paths.manifestPath, "primary registry does not match the written value");
  }
  return { manifestPath: paths.manifestPath, registry: readback.registry };
}

export async function clearRegistry(projectPath: string, options: ManifestMutationOptions = {}): Promise<{ manifestPath: string; registryCleared?: true; dryRun?: true; plannedEffects?: Array<Record<string, unknown>> }> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return options.dryRun ? clearRegistryLocked(resolvedProject, options) : withProjectFileMutationLease(resolvedProject, "package-manifest-mutation", () => clearRegistryLocked(resolvedProject, options));
}

async function clearRegistryLocked(projectPath: string, options: ManifestMutationOptions): Promise<{ manifestPath: string; registryCleared?: true; dryRun?: true; plannedEffects?: Array<Record<string, unknown>> }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  if (options.dryRun) return { manifestPath: paths.manifestPath, dryRun: true, plannedEffects: [{ type: "package.registry.clear", registry: manifest.registry }] };
  delete manifest.registry;
  await writeManifest(paths.manifestPath, manifest);
  const readback = await readManifest(paths.manifestPath, false);
  if (typeof readback.registry === "string") {
    throw manifestReadbackMismatch(paths.manifestPath, "primary registry is still declared");
  }
  return { manifestPath: paths.manifestPath, registryCleared: true };
}

export async function addScopedRegistry(projectPath: string, registry: ScopedRegistry, options: ManifestMutationOptions = {}): Promise<{ manifestPath: string; registry: ScopedRegistry; dryRun?: true; plannedEffects?: Array<Record<string, unknown>> }> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return options.dryRun ? addScopedRegistryLocked(resolvedProject, registry, options) : withProjectFileMutationLease(resolvedProject, "package-manifest-mutation", () => addScopedRegistryLocked(resolvedProject, registry, options));
}

async function addScopedRegistryLocked(projectPath: string, registry: ScopedRegistry, options: ManifestMutationOptions): Promise<{ manifestPath: string; registry: ScopedRegistry; dryRun?: true; plannedEffects?: Array<Record<string, unknown>> }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const registries = normalizeScopedRegistries(manifest.scopedRegistries);
  const index = registries.findIndex((existing) => existing.name === registry.name);
  const normalized = { name: registry.name, url: registry.url, scopes: uniqueScopes(registry.scopes) };
  if (options.dryRun) return { manifestPath: paths.manifestPath, registry: normalized, dryRun: true, plannedEffects: [{ type: "package.scoped-registry.add", ...normalized }] };
  if (index >= 0) {
    registries[index] = normalized;
  } else {
    registries.push(normalized);
  }
  manifest.scopedRegistries = registries;
  await writeManifest(paths.manifestPath, manifest);
  const readback = await readManifest(paths.manifestPath, false);
  const persisted = normalizeScopedRegistries(readback.scopedRegistries).find((entry) => entry.name === normalized.name);
  if (!persisted || persisted.url !== normalized.url || !sameScopes(persisted.scopes, normalized.scopes)) {
    throw manifestReadbackMismatch(paths.manifestPath, `scoped registry ${normalized.name} does not match the written value`);
  }
  return { manifestPath: paths.manifestPath, registry: persisted };
}

export async function removeScopedRegistry(projectPath: string, name: string, options: RemoveScopedRegistryOptions = {}): Promise<{ manifestPath: string; removed?: ScopedRegistry; registryRemoved?: true; matchedPackages: string[]; forced: boolean; dryRun: boolean; blocked: boolean; plannedEffects?: Array<Record<string, unknown>> }> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return options.dryRun ? removeScopedRegistryLocked(resolvedProject, name, options) : withProjectFileMutationLease(resolvedProject, "package-manifest-mutation", () => removeScopedRegistryLocked(resolvedProject, name, options));
}

async function removeScopedRegistryLocked(projectPath: string, name: string, options: RemoveScopedRegistryOptions): Promise<{ manifestPath: string; removed?: ScopedRegistry; registryRemoved?: true; matchedPackages: string[]; forced: boolean; dryRun: boolean; blocked: boolean; plannedEffects?: Array<Record<string, unknown>> }> {
  const paths = packagePaths(projectPath);
  const manifest = await readManifest(paths.manifestPath, false);
  const registries = normalizeScopedRegistries(manifest.scopedRegistries);
  const index = registries.findIndex((registry) => registry.name === name);
  if (index < 0) {
    throw new PackageManagerError("SCOPED_REGISTRY_NOT_FOUND", `Scoped registry not found: ${name}`, ["Run tools run package.scoped-registry.list to inspect scoped registries."]);
  }
  const removed = registries[index];
  const matchedPackages = packagesUsingScopes(manifest, removed.scopes);
  const blocked = matchedPackages.length > 0 && !options.force;
  if (blocked && !options.dryRun) {
    throw new PackageManagerError("SCOPED_REGISTRY_IN_USE", `Scoped registry ${name} still matches manifest package(s): ${matchedPackages.join(", ")}.`, [
      "Remove or move packages using this registry scope before removing the scoped registry.",
      "Rerun with force=true only when you intentionally want to leave those packages without their scoped registry."
    ]);
  }
  const plannedEffects = [{ type: "package.scoped-registry.remove", name, scopes: removed.scopes, matchedPackages, forced: options.force === true, blocked }];
  if (!options.dryRun) {
    registries.splice(index, 1);
    manifest.scopedRegistries = registries;
    await writeManifest(paths.manifestPath, manifest);
    const readback = await readManifest(paths.manifestPath, false);
    if (normalizeScopedRegistries(readback.scopedRegistries).some((entry) => entry.name === name)) {
      throw manifestReadbackMismatch(paths.manifestPath, `scoped registry ${name} is still declared`);
    }
    return { manifestPath: paths.manifestPath, registryRemoved: true, matchedPackages, forced: options.force === true, dryRun: false, blocked: false };
  }
  return { manifestPath: paths.manifestPath, removed, matchedPackages, forced: options.force === true, dryRun: true, blocked, plannedEffects };
}

export interface UnityCliInstallMetadata {
  packageName: string;
  packageVersion: string;
  sourceHash: string;
  managedFilesSha256: string;
  projectHash: string;
  installMode: "copy";
  installTime: string;
  cliCompatibilityRange: string;
}

export interface UnityCliPackageInstallPlan {
  manifestPath: string;
  bridgePath: string;
  dependencyName: string;
  packageVersion: string;
  sourceHash: string;
  projectHash: string;
  installMetadataPath: string;
  fileManifestPath: string;
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

export interface PackageBridgeInstallRuntime {
  absoluteDeadlineMs?: number;
  publishRetryDelayMs?: number;
  renameDirectory?: typeof rename;
}

/** 在任何 init filesystem effect 前冻结 Bridge 源、目标与哈希。 */
export async function preparePackageBridgeInstall(projectPath: string): Promise<UnityCliPackageInstallPlan> {
  const paths = packagePaths(projectPath);
  const sourcePath = packageSourcePath();
  const packageManifest = await readPackageManifest(sourcePath);
  const projectHash = await hashProjectPath(paths.projectPath);
  const sourceHash = await hashSourcePath(sourcePath);
  const bridgePath = join(paths.projectPath, "Packages", bridgePackageName);
  return {
    manifestPath: paths.manifestPath,
    bridgePath,
    dependencyName: bridgePackageName,
    packageVersion: packageManifest.version,
    sourceHash,
    projectHash,
    installMetadataPath: join(paths.projectPath, "Packages", installMetadataFileName),
    fileManifestPath: join(bridgePath, fileManifestFileName),
  };
}

interface PackageBridgeLockOwner { token: string; pid: number; processStartIdentity: string; }

/** owner先在候选目录完整fsync，再以目录rename原子发布为固定lock；崩溃不会留下不完整primary。 */
async function withProjectFileMutationLease<T>(projectPath: string, leaseName: string, action: () => Promise<T>): Promise<T> {
  const stateRoot = join(projectPath, "Library", "UnityCli");
  const lockPath = join(stateRoot, `${leaseName}.lock`);
  const recoveredPath = join(stateRoot, `${leaseName}.recovered`);
  const candidatePrefix = `${leaseName}.candidate-`;
  await mkdir(stateRoot, { recursive: true });
  await cleanupPackageBridgeLockCandidates(stateRoot, candidatePrefix);
  if (await directoryExists(recoveredPath)) {
    const recovered = await readPackageBridgeLock(recoveredPath);
    if (!recovered || await isPackageBridgeOwnerAlive(recovered)) throw new PackageManagerError("E_UNITY_BUSY", "Bridge publish lease recovery owner is not proven dead.");
    await removePackagePublishDirectory(recoveredPath, "INIT_PUBLISH_LEASE_RECOVERY_FAILED");
  }

  const token = randomUUID();
  const currentProcessStartIdentity = await processStartIdentity(process.pid);
  if (!currentProcessStartIdentity) throw new PackageManagerError("PROCESS_IDENTITY_UNAVAILABLE", "Current process start identity is unavailable; file mutation cannot acquire a recoverable lease.");
  const startHash = createHash("sha256").update(currentProcessStartIdentity).digest("hex").slice(0, 16);
  const candidatePath = join(stateRoot, `${candidatePrefix}${process.pid}-${startHash}-${token}`);
  const owner: PackageBridgeLockOwner = { token, pid: process.pid, processStartIdentity: currentProcessStartIdentity };
  await mkdir(candidatePath, { recursive: false });
  activeFileMutationCandidates.add(candidatePath);
  try {
    const ownerHandle = await open(join(candidatePath, "owner.json"), "wx", 0o600);
    try { await ownerHandle.writeFile(`${JSON.stringify(owner)}\n`, "utf8"); await ownerHandle.sync(); }
    finally { await ownerHandle.close(); }

    for (let attempt = 0; attempt < 3; attempt++) {
    try {
      await rename(candidatePath, lockPath);
      try { return await action(); }
      finally {
        const current = await readPackageBridgeLock(lockPath);
        if (!samePackageBridgeLockOwner(owner, current)) throw new PackageManagerError("INIT_PUBLISH_LEASE_LOST", "Bridge publish lease ownership changed before release.");
        await removePackagePublishDirectory(lockPath, "INIT_PUBLISH_LEASE_RELEASE_FAILED");
      }
    } catch (error) {
      if (!await directoryExists(candidatePath)) throw error;
      const current = await readPackageBridgeLock(lockPath);
      if (!current) throw new PackageManagerError("INIT_PUBLISH_LEASE_RECOVERY_FAILED", "Bridge publish lock is corrupt or unreadable.");
      if (await isPackageBridgeOwnerAlive(current)) {
        await removePackagePublishDirectory(candidatePath, "INIT_PUBLISH_LEASE_RELEASE_FAILED");
        throw new PackageManagerError("E_UNITY_BUSY", "Another process owns the Bridge package publish lease.");
      }
      try { await rename(lockPath, recoveredPath); }
      catch (recoveryError) {
        const code = (recoveryError as NodeJS.ErrnoException)?.code;
        if (code === "ENOENT" || code === "EEXIST" || code === "EPERM") continue;
        throw recoveryError;
      }
      const isolated = await readPackageBridgeLock(recoveredPath);
      if (!isolated || !samePackageBridgeLockOwner(current, isolated) || await isPackageBridgeOwnerAlive(isolated))
        throw new PackageManagerError("INIT_PUBLISH_LEASE_RECOVERY_FAILED", "Recovered Bridge publish lease identity is not exact.");
      await removePackagePublishDirectory(recoveredPath, "INIT_PUBLISH_LEASE_RECOVERY_FAILED");
    }
    }
    throw new PackageManagerError("INIT_PUBLISH_LEASE_RECOVERY_FAILED", "Bridge publish lease could not be acquired after exact recovery.");
  }
  finally
  {
    activeFileMutationCandidates.delete(candidatePath);
    if (await directoryExists(candidatePath)) await removePackagePublishDirectory(candidatePath, "INIT_PUBLISH_LEASE_RELEASE_FAILED");
  }
}

async function cleanupPackageBridgeLockCandidates(stateRoot: string, prefix: string): Promise<void> {
  const entries = await readdir(stateRoot, { withFileTypes: true });
  for (const entry of entries) {
    if (!entry.isDirectory() || !entry.name.startsWith(prefix)) continue;
    const match = new RegExp(`^${escapeRegExp(prefix)}(\\d+)-([0-9a-f]{16})-[0-9a-f-]{36}$`, "i").exec(entry.name);
    const candidatePath = join(stateRoot, entry.name);
    const sameProcessStale = !!match && Number(match[1]) === process.pid && !activeFileMutationCandidates.has(candidatePath);
    if (!match || await isCandidateProcessActive(Number(match[1]), match[2]) && !sameProcessStale) throw new PackageManagerError("E_UNITY_BUSY", "A Bridge publish lease candidate is still active or invalid.");
    await removePackagePublishDirectory(candidatePath, "INIT_PUBLISH_LEASE_RECOVERY_FAILED");
  }
}

async function readPackageBridgeLock(path: string): Promise<PackageBridgeLockOwner | undefined> {
  try { return parsePackageBridgeLock(await readFile(join(path, "owner.json"), "utf8")); }
  catch (error) {
    const code = (error as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT" || code === "ENOTDIR") return undefined;
    throw error;
  }
}

function parsePackageBridgeLock(raw: string): PackageBridgeLockOwner | undefined {
  try {
    const value = JSON.parse(raw) as Partial<PackageBridgeLockOwner>;
    return typeof value.token === "string" && value.token.length > 0 && Number.isInteger(value.pid) && (value.pid as number) > 0
      && typeof value.processStartIdentity === "string" && value.processStartIdentity.length > 0 ? value as PackageBridgeLockOwner : undefined;
  } catch { return undefined; }
}

function samePackageBridgeLockOwner(left: PackageBridgeLockOwner, right: PackageBridgeLockOwner | undefined): boolean {
  return !!right && left.token === right.token && left.pid === right.pid && left.processStartIdentity === right.processStartIdentity;
}

function isProcessAlive(pid: number): boolean {
  try { process.kill(pid, 0); return true; }
  catch (error) { return (error as NodeJS.ErrnoException)?.code === "EPERM"; }
}

async function processStartIdentity(pid: number): Promise<string | undefined> {
  if (pid === process.pid && cachedCurrentProcessStartIdentity) return cachedCurrentProcessStartIdentity;
  if (process.platform !== "win32") return pid === process.pid ? packageBridgeProcessStartIdentity : undefined;
  try {
    const { stdout } = await execFileAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", `$p=Get-Process -Id ${pid} -ErrorAction Stop; $p.StartTime.ToUniversalTime().ToString('O')`], { timeout: 5000, windowsHide: true });
    const value = String(stdout).trim();
    if (pid === process.pid && value) cachedCurrentProcessStartIdentity = value;
    return value || undefined;
  } catch { return undefined; }
}

async function isPackageBridgeOwnerAlive(owner: PackageBridgeLockOwner): Promise<boolean> {
  if (!isProcessAlive(owner.pid)) return false;
  const observedStart = await processStartIdentity(owner.pid);
  return observedStart === undefined || observedStart === owner.processStartIdentity;
}

async function isCandidateProcessActive(pid: number, startHash: string): Promise<boolean> {
  if (!isProcessAlive(pid)) return false;
  const observedStart = await processStartIdentity(pid);
  return observedStart === undefined || createHash("sha256").update(observedStart).digest("hex").slice(0, 16) === startHash.toLowerCase();
}

export async function ensurePackageBridge(projectPath: string, prepared?: UnityCliPackageInstallPlan, runtime: PackageBridgeInstallRuntime = {}): Promise<UnityCliPackageInstall> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return withProjectFileMutationLease(resolvedProject, "package-bridge-publish", () => ensurePackageBridgeLocked(resolvedProject, prepared, runtime));
}

async function ensurePackageBridgeLocked(projectPath: string, prepared?: UnityCliPackageInstallPlan, runtime: PackageBridgeInstallRuntime = {}): Promise<UnityCliPackageInstall> {
  const paths = packagePaths(projectPath);
  const sourcePath = packageSourcePath();
  const plan = prepared ?? await preparePackageBridgeInstall(paths.projectPath);
  const packageManifest = await readPackageManifest(sourcePath);
  // init plan 的 sourceHash 是 effect 前冻结证据；任何目标写入前必须重新散列并严格匹配。
  const freshSourceHash = await hashSourcePath(sourcePath);
  if (plan.sourceHash !== freshSourceHash || plan.packageVersion !== packageManifest.version) {
    throw new PackageManagerError("INIT_PLAN_STALE", "Bridge package source changed after init prevalidation.", ["Submit init again so it can build a fresh transaction plan."]);
  }
  const projectHash = plan.projectHash;
  const sourceHash = freshSourceHash;
  const targetPath = plan.bridgePath;
  const installMetadataPath = plan.installMetadataPath;
  const fileManifestPath = plan.fileManifestPath;
  const publishDeadlineMs = runtime.absoluteDeadlineMs ?? Date.now() + 5_000;
  const abandonedPreviousCount = await recoverAbandonedPackagePublish(targetPath, publishDeadlineMs, runtime);
  // Check existing installation: skip if same version & hash
  const expectedManagedFiles = await listManagedSourceFiles(sourcePath);
  const expectedManagedFilesSha256 = managedFilesSha256(expectedManagedFiles);
  const targetExists = await directoryExists(targetPath);
  // 包目录不存在时 metadata 只是孤立残留；普通 init 可用当前完整投影替换它。
  const existing = targetExists ? await readOptionalInstallMetadata(installMetadataPath) : undefined;
  if (existing) await readVerifiedManagedFileList(fileManifestPath, existing.managedFilesSha256);
  if (existing && existing.packageName === bridgePackageName && existing.installMode === "copy"
    && existing.packageVersion === packageManifest.version && existing.sourceHash === sourceHash && existing.projectHash === projectHash
    && existing.managedFilesSha256 === expectedManagedFilesSha256 && existing.cliCompatibilityRange === cliCompatibilityRange
    && await managedPackageFilesMatch(sourcePath, targetPath, fileManifestPath)) {
    await cleanupPublishedPackageSiblings(targetPath);
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

  if (abandonedPreviousCount > 0)
    throw new PackageManagerError("INIT_ROLLBACK_REQUIRED", "An abandoned Bridge publish backup cannot be discarded because the current installation is not fully proven.", ["Inspect the current package and its .previous sibling before retrying init."]);

  // 完整投影先写入同级 staging 目录，再以 rename 发布；不会暴露半复制的 Bridge 包。
  const publishNonce = randomUUID();
  const stagingPath = `${targetPath}.staging-${publishNonce}`;
  const backupPath = `${targetPath}.previous-${publishNonce}`;
  // publish 阶段（复制+rename）必须尊重调用方 immutable deadline；Defender/索引器短暂占用时只在该窗口内重试。
  let installTime: string | undefined;
  let observedMetadata: UnityCliInstallMetadata | undefined;
  let commitProven = false;
  let metadataWriteAttempted = false;
  try {
    const freshTargetExists = await directoryExists(targetPath);
    const freshExisting = freshTargetExists ? await readOptionalInstallMetadata(installMetadataPath) : undefined;
    if (freshTargetExists !== targetExists)
      throw new PackageManagerError("INIT_PLAN_STALE", "Bridge package target changed before staging; no package was published.");
    if ((!existing && freshExisting) || (existing && (!freshExisting || !sameInstallMetadata(existing, freshExisting))))
      throw new PackageManagerError("PACKAGE_FILE_MANIFEST_INVALID", "Bridge install metadata changed before managed cleanup; no package was published.");
    const freshExistingManagedFiles = freshExisting ? await readVerifiedManagedFileList(fileManifestPath, freshExisting.managedFilesSha256) : [];
    await mkdir(stagingPath, { recursive: false });
    if (freshTargetExists) await copyDirRecursive(targetPath, stagingPath, targetPath, []);
    await removeStaleManagedFiles(stagingPath, freshExistingManagedFiles);
    await removeEmptyManagedDirectories(stagingPath, false);
    const managedFiles = await copyPackageContents(sourcePath, stagingPath);
    await assertRemovedManagedFilesAbsent(stagingPath, freshExistingManagedFiles, expectedManagedFiles);
    const stagingManifestPath = join(stagingPath, fileManifestFileName);
    await writeFileManifest(stagingManifestPath, managedFiles);
    const publishSourceHash = await hashSourcePath(sourcePath);
    if (publishSourceHash !== sourceHash || managedFilesSha256(managedFiles) !== expectedManagedFilesSha256
      || !await managedPackageFilesMatch(sourcePath, stagingPath, stagingManifestPath))
      throw new PackageManagerError("INIT_PLAN_STALE", "Bridge package source or staging projection changed before publish.", ["Submit init again with a fresh source plan."]);
    if (targetExists) await renamePackageDirectory(targetPath, backupPath, publishDeadlineMs, runtime);
    await renamePackageDirectory(stagingPath, targetPath, publishDeadlineMs, runtime);
    installTime = new Date().toISOString();
    metadataWriteAttempted = true;
    await writeInstallMetadata(installMetadataPath, {
      packageName: bridgePackageName,
      packageVersion: packageManifest.version,
      sourceHash,
      managedFilesSha256: expectedManagedFilesSha256,
      projectHash,
      installMode: "copy",
      installTime,
      cliCompatibilityRange
    });
    // commit前必须fresh证明目标投影和metadata；旧backup在此之前不可删除。
    observedMetadata = await readInstallMetadata(installMetadataPath);
    if (observedMetadata.packageName !== bridgePackageName
      || observedMetadata.packageVersion !== packageManifest.version
      || observedMetadata.sourceHash !== sourceHash
      || observedMetadata.managedFilesSha256 !== expectedManagedFilesSha256
      || observedMetadata.projectHash !== projectHash
      || observedMetadata.installMode !== "copy"
      || observedMetadata.installTime !== installTime
      || observedMetadata.cliCompatibilityRange !== cliCompatibilityRange
      || !await managedPackageFilesMatch(sourcePath, targetPath, fileManifestPath))
      throw new PackageManagerError("INIT_CHECK_FAILED", "Bridge package installation could not be proven from fresh filesystem readback.", ["Inspect Packages/com.unity_cli and run init again."]);
    commitProven = true;
    // 独占publish lease覆盖整个事务，因此这里只会清理由当前或已退出owner留下的sibling。
    await cleanupPublishedPackageSiblings(targetPath);
  } catch (error) {
    if (!commitProven) {
      try {
        const currentExists = await directoryExists(targetPath);
        const backupExists = await directoryExists(backupPath);
        if (targetExists)
        {
          if (backupExists)
          {
            if (currentExists) await removePackagePublishDirectory(targetPath, "INIT_ROLLBACK_FAILED");
            await renamePackageDirectory(backupPath, targetPath, publishDeadlineMs, runtime);
            if (!await directoryExists(targetPath) || await directoryExists(backupPath)) throw new Error("rollback-readback-mismatch");
          }
          else if (!currentExists) throw new Error("previous-target-and-backup-missing");
        }
        else if (currentExists) await removePackagePublishDirectory(targetPath, "INIT_ROLLBACK_FAILED");
        if (metadataWriteAttempted)
        {
          if (existing)
          {
            await writeInstallMetadata(installMetadataPath, existing);
            const restoredMetadata = await readInstallMetadata(installMetadataPath);
            if (!sameInstallMetadata(existing, restoredMetadata)) throw new Error("metadata-rollback-readback-mismatch");
          }
          else { await rm(installMetadataPath, { force: true }); await assertPathMissing(installMetadataPath, "INIT_ROLLBACK_FAILED"); }
        }
      }
      catch
      {
        throw new PackageManagerError("INIT_ROLLBACK_FAILED", "Bridge package publish failed and the previous package state could not be restored.", [
          "Inspect Packages/com.unity_cli and its sibling .previous directory before submitting a new init request."
        ]);
      }
    }
    throw error;
  } finally {
    await removePackagePublishDirectory(stagingPath, "INIT_STAGING_CLEANUP_FAILED");
  }

  if (!observedMetadata || !installTime)
    throw new PackageManagerError("INIT_CHECK_FAILED", "Bridge package commit proof is unavailable after publish.");
  return {
    manifestPath: paths.manifestPath,
    bridgePath: targetPath,
    dependencyName: bridgePackageName,
    packageVersion: packageManifest.version,
    sourceHash,
    projectHash,
    installMode: "copy",
    installTime: observedMetadata.installTime,
    cliCompatibilityRange,
    installMetadataPath,
    fileManifestPath
  };
}

/** lease恢复后只删除无业务价值的staging；目标缺失时仅从唯一backup恢复，绝不猜测多个候选。 */
async function recoverAbandonedPackagePublish(targetPath: string, publishDeadlineMs: number, runtime: PackageBridgeInstallRuntime): Promise<number> {
  const parent = dirname(targetPath);
  const prefix = `${basename(targetPath)}.`;
  const entries = await readdir(parent, { withFileTypes: true });
  const staging: string[] = [];
  const previous: string[] = [];
  for (const entry of entries) {
    if (!entry.isDirectory() || !entry.name.startsWith(prefix)) continue;
    if (/\.staging-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(entry.name)) staging.push(join(parent, entry.name));
    if (/\.previous-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(entry.name)) previous.push(join(parent, entry.name));
  }
  for (const path of staging) await removePackagePublishDirectory(path, "INIT_STAGING_CLEANUP_FAILED");
  if (!await directoryExists(targetPath) && previous.length > 0) {
    if (previous.length !== 1) throw new PackageManagerError("INIT_ROLLBACK_REQUIRED", "Multiple abandoned Bridge backups exist; automatic recovery is ambiguous.");
    await renamePackageDirectory(previous[0], targetPath, publishDeadlineMs, runtime);
    if (!await directoryExists(targetPath) || await directoryExists(previous[0]))
      throw new PackageManagerError("INIT_ROLLBACK_FAILED", "The abandoned Bridge backup could not be restored exactly.");
    return 0;
  }
  return previous.length;
}

/** 只清理 Core 发布器拥有的 sibling 命名空间，并以 fresh directory readback 证明终态。 */
async function cleanupPublishedPackageSiblings(targetPath: string): Promise<void> {
  const parent = dirname(targetPath);
  const prefix = `${basename(targetPath)}.`;
  const entries = await readdir(parent, { withFileTypes: true });
  const owned = entries
    .filter((entry) => entry.isDirectory() && entry.name.startsWith(prefix)
      && /\.(?:previous|staging)-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(entry.name))
    .map((entry) => join(parent, entry.name));
  for (const path of owned) await removePackagePublishDirectory(path, "INIT_BACKUP_CLEANUP_FAILED");
}

/** rm 返回后再次读取目录；清理失败不能被当作 package publish 成功。 */
async function removePackagePublishDirectory(path: string, code: string): Promise<void> {
  try {
    await rm(path, { recursive: true, force: true });
  } catch (error) {
    throw new PackageManagerError(code, `Unity package publish cleanup failed: ${path}`, [
      "Close file handles that own the generated package sibling, then submit init again."
    ]);
  }
  if (await directoryExists(path)) {
    throw new PackageManagerError(code, `Unity package publish cleanup could not prove directory absence: ${path}`, [
      "Inspect the generated package sibling before submitting init again."
    ]);
  }
}

/** Windows 文件观察器可能短暂占用 package 目录；只重试明确的 transient rename 错误。 */
async function renamePackageDirectory(source: string, target: string, absoluteDeadlineMs: number, runtime: PackageBridgeInstallRuntime): Promise<void> {
  const renameDirectory = runtime.renameDirectory ?? rename;
  let retryDelayMs = Math.max(1, runtime.publishRetryDelayMs ?? 25);
  while (true) {
    try {
      await renameDirectory(source, target);
      return;
    } catch (error) {
      if (!isTransientRenameError(error) || Date.now() + retryDelayMs >= absoluteDeadlineMs) throw error;
      await delay(retryDelayMs);
      retryDelayMs = Math.min(250, retryDelayMs * 2);
    }
  }
}

/** 仅 Windows 常见的短时占用错误允许 retry；目标冲突和路径错误继续 fail closed。 */
function isTransientRenameError(error: unknown): boolean {
  const code = error && typeof error === "object" && "code" in error ? String((error as { code?: unknown }).code) : "";
  return code === "EPERM" || code === "EBUSY" || code === "EACCES";
}

interface VerifiedPackageBridge {
  paths: PackageManagerPaths;
  targetPath: string;
  installMetadataPath: string;
  fileManifestPath: string;
  packageManifest: { name: string; version: string };
  installMetadata: UnityCliInstallMetadata;
  projectHash: string;
  sourceHash: string;
  managedFiles: string[];
}

async function verifyCurrentPackageBridge(projectPath: string, code: string, nextActions: string[]): Promise<VerifiedPackageBridge> {
  const paths = packagePaths(projectPath);
  const targetPath = join(paths.projectPath, "Packages", bridgePackageName);
  const installMetadataPath = join(paths.projectPath, "Packages", installMetadataFileName);
  const fileManifestPath = join(targetPath, fileManifestFileName);
  const { packageManifest, installMetadata } = await readInstalledPackageBridge(paths.projectPath, code, nextActions);
  const projectHash = await hashProjectPath(paths.projectPath);
  const sourcePath = packageSourcePath();
  const sourceHash = await hashSourcePath(sourcePath);
  if (installMetadata.packageVersion !== packageManifest.version || installMetadata.sourceHash !== sourceHash
    || installMetadata.projectHash !== projectHash || installMetadata.cliCompatibilityRange !== cliCompatibilityRange)
    throw new PackageManagerError(code, `${bridgePackageName} install metadata does not match the current project and bundled source.`, nextActions);
  const managedFiles = await readVerifiedManagedFileList(fileManifestPath, installMetadata.managedFilesSha256);
  if (!await managedPackageFilesMatch(sourcePath, targetPath, fileManifestPath))
    throw new PackageManagerError(code, `${bridgePackageName} managed file projection does not match the bundled source.`, nextActions);
  return { paths, targetPath, installMetadataPath, fileManifestPath, packageManifest, installMetadata, projectHash, sourceHash, managedFiles };
}

export async function checkPackageBridge(projectPath: string): Promise<UnityCliPackageInstall> {
  const verified = await verifyCurrentPackageBridge(projectPath, "INIT_CHECK_FAILED", ["Run unity_cli init --project <path> to restore the bundled package projection."]);
  const { paths, targetPath, installMetadataPath, fileManifestPath, packageManifest, installMetadata, projectHash, sourceHash } = verified;
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

/** 卸载 init 复制的 Bridge 包；只删除托管的 com.unity_cli copy-mode 安装。 */
export async function removePackageBridge(projectPath: string, options: RemovePackageBridgeOptions = {}): Promise<UnityCliPackageRemoval> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return withProjectFileMutationLease(resolvedProject, "package-bridge-publish", () => removePackageBridgeLocked(resolvedProject, options));
}

async function removePackageBridgeLocked(projectPath: string, options: RemovePackageBridgeOptions = {}): Promise<UnityCliPackageRemoval> {
  const verificationActions = ["Run unity_cli init --project <path> to restore a fully verified bundled package before removal."];
  const verified = await verifyCurrentPackageBridge(projectPath, "BRIDGE_PACKAGE_REMOVE_UNVERIFIED", verificationActions);
  const { paths, targetPath, installMetadataPath, fileManifestPath, packageManifest, installMetadata, managedFiles } = verified;
  const plannedEffects: Array<Record<string, unknown>> = [
    {
      type: "package.bridge-remove",
      package: bridgePackageName,
      bridgePath: targetPath,
      installMetadataPath,
      managedFileCount: managedFiles.length
    }
  ];

  if (!options.dryRun) {
    const fresh = await verifyCurrentPackageBridge(projectPath, "BRIDGE_PACKAGE_REMOVE_UNVERIFIED", verificationActions);
    if (!sameInstallMetadata(installMetadata, fresh.installMetadata))
      throw new PackageManagerError("BRIDGE_PACKAGE_REMOVE_UNVERIFIED", "Bridge ownership changed before removal; no files were deleted.");
    await removeManagedFilesStrict(targetPath, fresh.managedFiles);
    await removeEmptyManagedDirectories(targetPath, false);
    await rm(fileManifestPath, { force: true });
    await assertPathMissing(fileManifestPath, "BRIDGE_PACKAGE_REMOVE_READBACK_FAILED");
    await rm(installMetadataPath, { force: true });
    await assertPathMissing(installMetadataPath, "BRIDGE_PACKAGE_REMOVE_READBACK_FAILED");
  }

  return {
    projectPath: paths.projectPath,
    manifestPath: paths.manifestPath,
    bridgePath: targetPath,
    dependencyName: bridgePackageName,
    packageVersion: packageManifest.version,
    installMetadataPath,
    fileManifestPath,
    managedFileCount: managedFiles.length,
    dryRun: options.dryRun === true,
    plannedEffects
  };
}

/** 将本地 Unity package 源复制到项目 Packages/<packageName>，并备份原目录以支持 package.restore。 */
export async function deployPackageSource(projectPath: string, sourcePath: string, options: DeployPackageOptions = {}): Promise<PackageDeployResult> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return options.dryRun ? deployPackageSourceLocked(resolvedProject, sourcePath, options) : withProjectFileMutationLease(resolvedProject, "package-deployment-mutation", () => deployPackageSourceLocked(resolvedProject, sourcePath, options));
}

async function deployPackageSourceLocked(projectPath: string, sourcePath: string, options: DeployPackageOptions): Promise<PackageDeployResult> {
  const paths = packagePaths(projectPath);
  await readManifest(paths.manifestPath, false);
  if (!options.dryRun) await recoverPackageDeploymentTransaction(paths.projectPath);
  const resolvedSource = resolve(sourcePath);
  const sourceManifest = await readAnyPackageManifest(resolvedSource);
  const packageName = options.packageName ?? sourceManifest.name;
  if (!packageName) {
    throw new PackageManagerError("PACKAGE_DEPLOY_NAME_REQUIRED", "Package deploy requires a package name in source package.json or packageName input.", [
      "Pass source as a Unity package folder containing package.json with name/version.",
      "Or pass packageName explicitly when deploying a source folder for a known package."
    ]);
  }
  if (sourceManifest.name !== packageName) {
    throw new PackageManagerError("PACKAGE_DEPLOY_NAME_MISMATCH", `Source package.json name ${sourceManifest.name} does not match requested packageName ${packageName}.`, [
      "Pass the matching packageName, or choose a source package folder with the expected package.json name."
    ]);
  }
  assertSafePackageDirectoryName(packageName);
  assertDeploySourceOutsideProjectPackages(resolvedSource, paths.projectPath);

  const sourceHash = await hashSourcePath(resolvedSource);
  const targetPath = join(paths.projectPath, "Packages", packageName);
  const metadataPath = deploymentMetadataPath(paths.projectPath);
  const existingTarget = await directoryExists(targetPath);
  const existingManifest = existingTarget ? await readAnyPackageManifest(targetPath) : undefined;
  const existingTargetHash = existingTarget ? await hashSourcePath(targetPath) : undefined;
  const currentRecord = (await readDeploymentMetadata(metadataPath))[packageName];
  if (currentRecord !== undefined) {
    assertDeploymentRecordSafe(paths.projectPath, packageName, currentRecord);
  }
  const derivedBackupPath = currentRecord
    ? currentRecord.backupPath
    : (existingTarget ? deploymentBackupPath(paths.projectPath, packageName, new Date()) : undefined);
  const backupPath = options.plannedBackupPath ?? derivedBackupPath;
  const retainedBackupPackageVersion = backupPath
    ? (currentRecord ? currentRecord.backupPackageVersion : existingManifest?.version)
    : undefined;
  if (options.plannedBackupPath !== undefined) {
    if (!existingTarget || !isCanonicalDeploymentBackupPath(paths.projectPath, packageName, options.plannedBackupPath)
      || (currentRecord !== undefined && !isSameOptionalResolvedPath(options.plannedBackupPath, currentRecord.backupPath))) {
      throw new PackageManagerError("PACKAGE_DEPLOY_PLAN_INVALID", "Package deploy backup path does not match the frozen durable file plan.", [
        "Inspect the canonical request journal and package deployment metadata before retrying; do not redeploy blindly."
      ]);
    }
  }
  const repeatedDeployment = currentRecord !== undefined;
  const copiedFileCount = await countPackageFiles(resolvedSource);
  const plannedEffects: Array<Record<string, unknown>> = [];
  if (existingTarget && !repeatedDeployment) {
    plannedEffects.push({ type: "package.deploy.backup", package: packageName, from: targetPath, to: backupPath, packageVersion: existingManifest?.version ?? null });
  }
  plannedEffects.push({ type: "package.deploy.copy", package: packageName, from: resolvedSource, to: targetPath, fileCount: copiedFileCount });
  plannedEffects.push({ type: "asset.refresh", reason: "package.deploy" });

  if (!options.dryRun) {
    if (options.expectedPreState)
      await assertPackageDeployPreState(paths.projectPath, resolvedSource, sourceHash, targetPath, metadataPath, backupPath, options.expectedPreState);
    await mkdir(dirname(metadataPath), { recursive: true });
    const transactionRoot = join(deploymentRootPath(paths.projectPath), "transactions", randomUUID());
    const stagingPath = join(transactionRoot, "staging");
    const rollbackPath = join(transactionRoot, "rollback");
    const transaction: PackageDeploymentTransaction = {
      version: 1, operation: "deploy", phase: "prepared", packageName, targetPath, stagingPath, rollbackPath, backupPath,
      targetExisted: existingTarget, backupCreated: existingTarget && !repeatedDeployment && !!backupPath,
      deleteBackupOnCommit: false, oldRecord: currentRecord
    };
    await writeDeploymentTransaction(paths.projectPath, transaction);
    try {
      await mkdir(transactionRoot, { recursive: true });
      await copyDirRecursive(resolvedSource, stagingPath, resolvedSource, []);
      if (await hashSourcePath(resolvedSource) !== sourceHash || await hashSourcePath(stagingPath) !== sourceHash || await countPackageFiles(stagingPath) !== copiedFileCount)
        throw new PackageManagerError("PACKAGE_DEPLOY_SOURCE_CHANGED", "Package source or staging changed before publish.");
      if (transaction.backupCreated && backupPath) {
        if (await directoryExists(backupPath)) throw new PackageManagerError("PACKAGE_DEPLOY_BACKUP_CONFLICT", "Planned package backup path already exists.");
        await copyDirRecursive(targetPath, backupPath, targetPath, []);
        if (await hashSourcePath(backupPath) !== existingTargetHash) throw new PackageManagerError("PACKAGE_DEPLOY_BACKUP_FAILED", "Package backup could not be proven before publish.");
      }
      if (existingTarget) {
        await rename(targetPath, rollbackPath);
        transaction.phase = "target_moved";
        await writeDeploymentTransaction(paths.projectPath, transaction);
      }
      await rename(stagingPath, targetPath);
      transaction.phase = "target_published";
      await writeDeploymentTransaction(paths.projectPath, transaction);
      const metadata = await readDeploymentMetadata(metadataPath);
      metadata[packageName] = {
        packageName, packageVersion: sourceManifest.version, sourcePath: resolvedSource, sourceHash, targetPath, backupPath,
        backupPackageVersion: retainedBackupPackageVersion, deployedAt: new Date().toISOString()
      };
      await writeDeploymentMetadata(metadataPath, metadata);
      transaction.phase = "metadata_published";
      await writeDeploymentTransaction(paths.projectPath, transaction);
      const observedManifest = await readAnyPackageManifest(targetPath);
      const observedRecord = (await readDeploymentMetadata(metadataPath))[packageName];
      const backupHash = transaction.backupCreated && backupPath ? await hashSourcePath(backupPath) : undefined;
      if (observedManifest.name !== packageName || observedManifest.version !== sourceManifest.version
        || await hashSourcePath(targetPath) !== sourceHash || await countPackageFiles(targetPath) !== copiedFileCount
        || !observedRecord || observedRecord.targetPath !== targetPath || observedRecord.sourceHash !== sourceHash
        || (transaction.backupCreated && backupHash !== existingTargetHash))
        throw new PackageManagerError("PACKAGE_DEPLOY_READBACK_FAILED", `Deployed package did not retain the requested filesystem state: ${packageName}.`);
      transaction.phase = "commit_proven";
      await writeDeploymentTransaction(paths.projectPath, transaction);
      await removePackagePublishDirectory(transactionRoot, "PACKAGE_DEPLOYMENT_CLEANUP_FAILED");
      await clearDeploymentTransaction(paths.projectPath);
    } catch (error) {
      await recoverPackageDeploymentTransaction(paths.projectPath);
      throw error;
    }
  }

  return {
    projectPath: paths.projectPath,
    manifestPath: paths.manifestPath,
    packageName,
    packageVersion: sourceManifest.version,
    sourcePath: resolvedSource,
    sourceHash,
    targetPath,
    backupPath,
    backupPackageVersion: retainedBackupPackageVersion,
    backupCreated: existingTarget && !repeatedDeployment,
    repeatedDeployment,
    copiedFileCount,
    dryRun: options.dryRun === true,
    plannedEffects,
    metadataPath
  };
}

/** effect barrier内重读完整业务pre-state；任何target/record/backup漂移都在marker与rename前失败。 */
async function assertPackageDeployPreState(
  projectPath: string,
  sourcePath: string,
  sourceHash: string,
  targetPath: string,
  metadataPath: string,
  backupPath: string | undefined,
  expected: PackageDeployExpectedPreState,
): Promise<void> {
  if (!isSameResolvedPath(sourcePath, expected.sourcePath) || sourceHash !== expected.sourceHash
    || !isSameOptionalResolvedPath(backupPath, expected.backupPath))
    throw new PackageManagerError("PACKAGE_DEPLOY_PRESTATE_CHANGED", "Package deploy source or planned backup identity changed after prepare.");
  const targetExists = await directoryExists(targetPath);
  const targetHash = targetExists ? await hashSourcePath(targetPath) : undefined;
  const metadata = await readDeploymentMetadata(metadataPath);
  const recordIdentity = deploymentRecordIdentity(metadata[basename(targetPath)]);
  const backupExists = backupPath !== undefined ? await directoryExists(backupPath) : false;
  const backupHash = backupExists && backupPath ? await hashSourcePath(backupPath) : undefined;
  if (await hashSourcePath(sourcePath) !== expected.sourceHash || targetExists !== expected.targetExists
    || targetHash !== expected.targetHash || recordIdentity !== expected.recordIdentity
    || backupExists !== expected.backupExists || backupHash !== expected.backupHash)
    throw new PackageManagerError("PACKAGE_DEPLOY_PRESTATE_CHANGED", "Package deploy filesystem pre-state changed after prepare; no transaction marker or target mutation was created.");
  if (backupPath !== undefined && !isCanonicalDeploymentBackupPath(projectPath, basename(targetPath), backupPath))
    throw new PackageManagerError("PACKAGE_DEPLOY_PRESTATE_CHANGED", "Package deploy backup path is not canonical for the frozen package.");
}

/** 从 package.deploy 创建的备份恢复目标包；没有备份时移除本次部署的包目录。 */
export async function restoreDeployedPackage(projectPath: string, packageName: string, options: RestorePackageOptions = {}): Promise<PackageRestoreResult> {
  const resolvedProject = packagePaths(projectPath).projectPath;
  return options.dryRun ? restoreDeployedPackageLocked(resolvedProject, packageName, options) : withProjectFileMutationLease(resolvedProject, "package-deployment-mutation", () => restoreDeployedPackageLocked(resolvedProject, packageName, options));
}

async function restoreDeployedPackageLocked(projectPath: string, packageName: string, options: RestorePackageOptions): Promise<PackageRestoreResult> {
  const paths = packagePaths(projectPath);
  await readManifest(paths.manifestPath, false);
  if (!options.dryRun) await recoverPackageDeploymentTransaction(paths.projectPath);
  if (!packageName) {
    throw new PackageManagerError("PACKAGE_NAME_REQUIRED", "Pass packageName to restore a deployed package.", [
      "Run tools run package.restore --input '{\"packageName\":\"<package>\"}' --dry-run first to inspect planned effects."
    ]);
  }
  assertSafePackageDirectoryName(packageName);
  const metadataPath = deploymentMetadataPath(paths.projectPath);
  const metadata = await readDeploymentMetadata(metadataPath);
  const record = metadata[packageName];
  if (!record) {
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_NOT_FOUND", `No package deployment metadata found for ${packageName}.`, [
      "Run package.deploy first, or pass the packageName that was deployed.",
      "Check Packages/.unity_cli_deploy_backups/unity_cli.package-deployments.json in the target project."
    ]);
  }
  assertDeploymentRecordSafe(paths.projectPath, packageName, record);

  const hasDeclaredBackup = record.backupPath !== undefined;
  const backupExists = hasDeclaredBackup ? await directoryExists(record.backupPath!) : false;
  if (hasDeclaredBackup && !backupExists)
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_BACKUP_MISSING", `Declared package backup is missing or is not a directory: ${record.backupPath}`);
  const backupFileCount = backupExists ? await countPackageFiles(record.backupPath!) : 0;
  const backupHash = backupExists ? await hashSourcePath(record.backupPath!) : undefined;
  const plannedEffects: Array<Record<string, unknown>> = [];
  if (backupExists && record.backupPath) {
    plannedEffects.push({ type: "package.restore.backup", package: packageName, from: record.backupPath, to: record.targetPath, fileCount: backupFileCount });
  } else {
    plannedEffects.push({ type: "package.restore.remove-deployed", package: packageName, targetPath: record.targetPath });
  }
  plannedEffects.push({ type: "asset.refresh", reason: "package.restore" });

  if (!options.dryRun) {
    if (options.expectedPreState)
      await assertPackageRestorePreState(packageName, record.targetPath, metadataPath, record.backupPath, options.expectedPreState);
    const transactionRoot = join(deploymentRootPath(paths.projectPath), "transactions", randomUUID());
    const stagingPath = join(transactionRoot, "staging");
    const rollbackPath = join(transactionRoot, "rollback");
    const targetExisted = await directoryExists(record.targetPath);
    const transaction: PackageDeploymentTransaction = {
      version: 1, operation: "restore", phase: "prepared", packageName, targetPath: record.targetPath,
      stagingPath, rollbackPath, backupPath: record.backupPath, targetExisted, backupCreated: false,
      deleteBackupOnCommit: backupExists, oldRecord: record
    };
    await writeDeploymentTransaction(paths.projectPath, transaction);
    try {
      await mkdir(transactionRoot, { recursive: true });
      if (backupExists && record.backupPath) {
        await copyDirRecursive(record.backupPath, stagingPath, record.backupPath, []);
        if (await hashSourcePath(stagingPath) !== backupHash || await countPackageFiles(stagingPath) !== backupFileCount)
          throw new PackageManagerError("PACKAGE_RESTORE_STAGING_FAILED", "Package restore staging could not be proven before publish.");
      }
      if (targetExisted) {
        await rename(record.targetPath, rollbackPath);
        transaction.phase = "target_moved";
        await writeDeploymentTransaction(paths.projectPath, transaction);
      }
      if (backupExists) {
        await rename(stagingPath, record.targetPath);
        transaction.phase = "target_published";
        await writeDeploymentTransaction(paths.projectPath, transaction);
      }
      delete metadata[packageName];
      await writeDeploymentMetadata(metadataPath, metadata);
      transaction.phase = "metadata_published";
      await writeDeploymentTransaction(paths.projectPath, transaction);
      const observedMetadata = await readDeploymentMetadata(metadataPath);
      const observedTargetExists = await directoryExists(record.targetPath);
      if (observedMetadata[packageName]
        || (backupExists && (!observedTargetExists || await hashSourcePath(record.targetPath) !== backupHash || await countPackageFiles(record.targetPath) !== backupFileCount))
        || (!backupExists && observedTargetExists))
        throw new PackageManagerError("PACKAGE_RESTORE_READBACK_FAILED", `Restored package did not retain the requested filesystem state: ${packageName}.`);
      transaction.phase = "commit_proven";
      await writeDeploymentTransaction(paths.projectPath, transaction);
      if (backupExists && record.backupPath) await removePackagePublishDirectory(record.backupPath, "PACKAGE_DEPLOYMENT_CLEANUP_FAILED");
      await removePackagePublishDirectory(transactionRoot, "PACKAGE_DEPLOYMENT_CLEANUP_FAILED");
      await clearDeploymentTransaction(paths.projectPath);
    } catch (error) {
      await recoverPackageDeploymentTransaction(paths.projectPath);
      throw error;
    }
  }

  return {
    projectPath: paths.projectPath,
    manifestPath: paths.manifestPath,
    packageName,
    targetPath: record.targetPath,
    backupPath: record.backupPath,
    restoredFromBackup: backupExists,
    removedDeployedPackage: !backupExists,
    copiedFileCount: backupFileCount,
    dryRun: options.dryRun === true,
    plannedEffects,
    metadataPath
  };
}

/** restore effect前重读target、record与backup；漂移时不得创建marker或移动target。 */
async function assertPackageRestorePreState(
  packageName: string,
  targetPath: string,
  metadataPath: string,
  backupPath: string | undefined,
  expected: PackageRestoreExpectedPreState,
): Promise<void> {
  if (!isSameResolvedPath(targetPath, expected.targetPath) || !isSameOptionalResolvedPath(backupPath, expected.backupPath))
    throw new PackageManagerError("PACKAGE_RESTORE_PRESTATE_CHANGED", "Package restore target or backup identity changed after prepare.");
  const targetExists = await directoryExists(targetPath);
  const targetHash = targetExists ? await hashSourcePath(targetPath) : undefined;
  const metadata = await readDeploymentMetadata(metadataPath);
  const backupExists = backupPath !== undefined ? await directoryExists(backupPath) : false;
  const backupHash = backupExists && backupPath ? await hashSourcePath(backupPath) : undefined;
  if (targetExists !== expected.targetExists || targetHash !== expected.targetHash
    || deploymentRecordIdentity(metadata[packageName]) !== expected.recordIdentity
    || backupExists !== expected.backupExists || backupHash !== expected.backupHash)
    throw new PackageManagerError("PACKAGE_RESTORE_PRESTATE_CHANGED", "Package restore filesystem pre-state changed after prepare; no transaction marker or target mutation was created.");
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
    throw new PackageManagerError("REGISTRY_URL_REQUIRED", "Pass --url <registryUrl>.", [
      "Pass an http(s) registry URL, for example https://package.openupm.com."
    ]);
  }
  try {
    const parsed = new URL(url);
    if (parsed.protocol !== "https:" && parsed.protocol !== "http:") {
      throw new Error("Unsupported protocol");
    }
    return url;
  } catch {
    throw new PackageManagerError("REGISTRY_URL_INVALID", `Registry URL is invalid: ${url}`, [
      "Use an http(s) registry URL, for example https://package.openupm.com.",
      "Run unity_cli tools describe package.search or package.registry.set for accepted registry inputs."
    ]);
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
  return (payload.objects ?? []).flatMap((entry) => {
    const candidate = entry.package;
    if (typeof candidate?.name !== "string") return [];
    return [{
      name: candidate.name,
      ...(typeof candidate.version === "string" ? { version: candidate.version } : {}),
      ...(typeof candidate.description === "string" ? { description: candidate.description } : {}),
    }];
  });
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

/** 读取并确认项目内 Bridge 包是 init 托管的 copy-mode 安装。 */
async function readInstalledPackageBridge(projectPath: string, code: string, nextActions: string[]): Promise<{ packageManifest: { name: string; version: string }; installMetadata: UnityCliInstallMetadata }> {
  const targetPath = join(projectPath, "Packages", bridgePackageName);
  const installMetadataPath = join(projectPath, "Packages", installMetadataFileName);
  let packageManifest: { name: string; version: string };
  try {
    packageManifest = await readPackageManifest(targetPath);
  } catch (error) {
    if (error instanceof PackageManagerError) {
      throw new PackageManagerError(code, `${bridgePackageName} is not installed in copy mode: ${error.message}`, nextActions);
    }
    throw error;
  }
  const installMetadata = await readInstallMetadata(installMetadataPath).catch((error: unknown) => {
    if (error instanceof PackageManagerError) {
      throw new PackageManagerError(code, `${bridgePackageName} install metadata is missing or invalid: ${error.message}`, nextActions);
    }
    throw error;
  });
  if (installMetadata.packageName !== bridgePackageName || installMetadata.installMode !== "copy") {
    throw new PackageManagerError(code, `${bridgePackageName} is not a managed copy-mode installation.`, nextActions);
  }
  return { packageManifest, installMetadata };
}

async function readPackageManifest(packagePath: string): Promise<{ name: string; version: string }> {
  const manifest = await readAnyPackageManifest(packagePath);
  if (manifest.name !== bridgePackageName) {
    throw new PackageManagerError("PACKAGE_IDENTITY_INVALID", `Unity package name must be ${bridgePackageName}.`);
  }
  return manifest;
}

async function readAnyPackageManifest(packagePath: string): Promise<{ name: string; version: string }> {
  try {
    const manifest = JSON.parse(stripJsonBom(await readFile(join(packagePath, "package.json"), "utf8"))) as { name?: unknown; version?: unknown };
    if (typeof manifest.name !== "string" || !manifest.name) {
      throw new PackageManagerError("PACKAGE_IDENTITY_INVALID", `Unity package.json requires a non-empty name: ${join(packagePath, "package.json")}`);
    }
    if (typeof manifest.version !== "string" || !manifest.version) {
      throw new PackageManagerError("PACKAGE_VERSION_INVALID", `${manifest.name} package.json requires a version.`);
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
  await atomicWriteFile(path, `${JSON.stringify(metadata, null, 2)}\n`);
}

async function readExistingInstallMetadata(path: string): Promise<UnityCliInstallMetadata> {
  return readInstallMetadata(path);
}

async function readOptionalInstallMetadata(path: string): Promise<UnityCliInstallMetadata | undefined> {
  try {
    await stat(path);
  } catch (error) {
    const code = (error as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT" || code === "ENOTDIR") return undefined;
    throw new PackageManagerError("INIT_CHECK_FAILED", `Install metadata cannot be observed: ${path}`);
  }
  return readInstallMetadata(path);
}

function sameInstallMetadata(left: UnityCliInstallMetadata, right: UnityCliInstallMetadata): boolean {
  return left.packageName === right.packageName && left.packageVersion === right.packageVersion
    && left.sourceHash === right.sourceHash && left.managedFilesSha256 === right.managedFilesSha256
    && left.projectHash === right.projectHash && left.installMode === right.installMode
    && left.installTime === right.installTime && left.cliCompatibilityRange === right.cliCompatibilityRange;
}

async function readInstallMetadata(path: string): Promise<UnityCliInstallMetadata> {
  try {
    const metadata = JSON.parse(stripJsonBom(await readFile(path, "utf8"))) as Partial<UnityCliInstallMetadata>;
    if (typeof metadata.packageName !== "string" || typeof metadata.packageVersion !== "string" || !isSha256(metadata.sourceHash) || !isSha256(metadata.managedFilesSha256) || typeof metadata.projectHash !== "string" || metadata.installMode !== "copy" || typeof metadata.installTime !== "string" || typeof metadata.cliCompatibilityRange !== "string") {
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

export async function hashPackageTree(sourcePath: string): Promise<string> {
  return hashSourcePath(sourcePath);
}

export interface PackageDeploymentFileReadback {
  targetExists: boolean;
  targetHash?: string;
  targetFileCount: number;
  backupExists: boolean;
  backupHash?: string;
  metadataPath: string;
  metadataHash: string;
  record?: PackageDeploymentRecord;
  recordIdentity: string;
}

/** 仅重算 package.deploy/restore 的文件与 metadata 终态，供单 root redelivery 恢复。 */
export async function inspectPackageDeploymentFiles(projectPath: string, packageName: string, targetPath: string, backupPath?: string): Promise<PackageDeploymentFileReadback> {
  const paths = packagePaths(projectPath);
  assertSafePackageDirectoryName(packageName);
  const expectedTarget = join(paths.projectPath, "Packages", packageName);
  if (resolve(targetPath) !== resolve(expectedTarget)) throw new PackageManagerError("PACKAGE_DEPLOY_READBACK_FAILED", "Package target path does not match the project package root.");
  if (backupPath) {
    const backupRoot = `${resolve(deploymentRootPath(paths.projectPath))}${win32.sep}`.toLowerCase();
    if (!`${resolve(backupPath)}${win32.sep}`.toLowerCase().startsWith(backupRoot)) throw new PackageManagerError("PACKAGE_DEPLOY_READBACK_FAILED", "Package backup path is outside the deployment backup root.");
  }
  const metadataPath = deploymentMetadataPath(paths.projectPath);
  const metadata = await readDeploymentMetadata(metadataPath);
  const targetExists = await directoryExists(targetPath);
  const backupExists = backupPath ? await directoryExists(backupPath) : false;
  return {
    targetExists,
    targetHash: targetExists ? await hashSourcePath(targetPath) : undefined,
    targetFileCount: targetExists ? await countPackageFiles(targetPath) : 0,
    backupExists,
    backupHash: backupExists && backupPath ? await hashSourcePath(backupPath) : undefined,
    metadataPath,
    metadataHash: await optionalFileIdentity(metadataPath),
    record: metadata[packageName],
    recordIdentity: deploymentRecordIdentity(metadata[packageName])
  };
}

async function optionalFileIdentity(path: string): Promise<string> {
  try { return createHash("sha256").update(await readFile(path)).digest("hex"); }
  catch (error) {
    const code = (error as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT" || code === "ENOTDIR") return createHash("sha256").update("absent").digest("hex");
    throw error;
  }
}

function deploymentRecordIdentity(record: PackageDeploymentRecord | undefined): string {
  if (!record) return createHash("sha256").update("absent").digest("hex");
  return createHash("sha256").update(JSON.stringify({
    packageName: record.packageName, packageVersion: record.packageVersion, sourcePath: record.sourcePath,
    sourceHash: record.sourceHash, targetPath: record.targetPath, backupPath: record.backupPath ?? null,
    backupPackageVersion: record.backupPackageVersion ?? null, deployedAt: record.deployedAt
  })).digest("hex");
}

async function hashSourcePath(sourcePath: string): Promise<string> {
  const hash = createHash("sha256");
  await hashDirectory(hash, sourcePath, sourcePath);
  return hash.digest("hex");
}

async function hashDirectory(hash: ReturnType<typeof createHash>, dir: string, root: string): Promise<void> {
  const entries = await readdir(dir, { withFileTypes: true });
  // 与 C# StringComparer.Ordinal 一致，禁止 locale 改变 package tree proof。
  const sorted = entries.sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
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

async function removeStaleManagedFiles(targetPath: string, managedFiles: string[]): Promise<void> {
  await removeManagedFilesStrict(targetPath, managedFiles);
}

async function removeManagedFilesStrict(targetPath: string, managedFiles: string[]): Promise<void> {
  for (const relPath of managedFiles) {
    const fullPath = resolveManagedFilePath(targetPath, relPath);
    try {
      await rm(fullPath, { force: true });
      await assertPathMissing(fullPath, "PACKAGE_MANAGED_FILE_DELETE_FAILED");
    } catch (error) {
      if (error instanceof PackageManagerError) throw error;
      throw new PackageManagerError("PACKAGE_MANAGED_FILE_DELETE_FAILED", `Managed package file could not be removed: ${relPath}`);
    }
  }
}

async function assertRemovedManagedFilesAbsent(targetPath: string, oldFiles: string[], newFiles: string[]): Promise<void> {
  const current = new Set(newFiles.map((file) => file.toLowerCase()));
  for (const relPath of oldFiles) {
    const identity = relPath.toLowerCase();
    if (current.has(identity) || [...current].some((file) => file.startsWith(`${identity}/`))) continue;
    await assertPathMissing(resolveManagedFilePath(targetPath, relPath), "PACKAGE_MANAGED_FILE_DELETE_FAILED");
  }
}

async function removeEmptyManagedDirectories(path: string, removeSelf: boolean): Promise<void> {
  for (const entry of await readdir(path, { withFileTypes: true })) {
    if (entry.isDirectory()) await removeEmptyManagedDirectories(join(path, entry.name), true);
  }
  if (!removeSelf || (await readdir(path)).length !== 0) return;
  await rmdir(path);
  await assertPathMissing(path, "BRIDGE_PACKAGE_REMOVE_READBACK_FAILED");
}

async function assertPathMissing(path: string, code: string): Promise<void> {
  try {
    await stat(path);
    throw new PackageManagerError(code, `Path still exists after managed removal: ${path}`);
  } catch (error) {
    if ((error as NodeJS.ErrnoException)?.code === "ENOENT") return;
    throw error;
  }
}

async function readVerifiedManagedFileList(fileManifestPath: string, expectedSha256: string): Promise<string[]> {
  const files = await readManagedFileList(fileManifestPath);
  if (managedFilesSha256(files) !== expectedSha256)
    throw new PackageManagerError("PACKAGE_FILE_MANIFEST_INVALID", `Managed file manifest does not match its install metadata: ${fileManifestPath}`);
  return files;
}

function managedFilesSha256(files: string[]): string {
  return createHash("sha256").update(`unity-cli-managed-files-v1\0${[...files].sort().join("\n")}`, "utf8").digest("hex");
}

function isSha256(value: unknown): value is string {
  return typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
}

async function readManagedFileList(fileManifestPath: string): Promise<string[]> {
  try {
    const data = JSON.parse(stripJsonBom(await readFile(fileManifestPath, "utf8"))) as FileManifest;
    if (!data || data.version !== 1 || !Array.isArray(data.files) || data.files.length === 0) throw new PackageManagerError("PACKAGE_FILE_MANIFEST_INVALID", `Managed file manifest is invalid: ${fileManifestPath}`);
    const identities = new Set<string>();
    for (const entry of data.files as unknown[]) {
      assertManagedRelativeFilePath(entry, fileManifestPath);
      const identity = entry.toLowerCase();
      if (identities.has(identity)) throw new PackageManagerError("PACKAGE_FILE_MANIFEST_INVALID", `Managed file manifest contains a duplicate path: ${fileManifestPath}`);
      identities.add(identity);
    }
    return [...data.files];
  } catch (error) {
    if (error instanceof PackageManagerError) throw error;
    throw new PackageManagerError("PACKAGE_FILE_MANIFEST_INVALID", `Managed file manifest cannot be read: ${fileManifestPath}`);
  }
}

function assertManagedRelativeFilePath(value: unknown, source: string): asserts value is string {
  if (typeof value !== "string" || value.length === 0 || value.includes("\0") || value.includes("\\") || value !== posix.normalize(value)
    || isAbsolute(value) || win32.isAbsolute(value) || posix.isAbsolute(value)
    || value.includes(":") || value.split("/").some((segment) => segment === "" || segment === "." || segment === "..")) {
    throw new PackageManagerError("PACKAGE_FILE_MANIFEST_INVALID", `Managed file manifest contains an unsafe path: ${source}`);
  }
}

function resolveManagedFilePath(targetPath: string, relPath: string): string {
  assertManagedRelativeFilePath(relPath, targetPath);
  const root = resolve(targetPath);
  const fullPath = resolve(root, relPath);
  const fromRoot = relative(root, fullPath);
  if (!fromRoot || fromRoot.split(/[\\/]/)[0] === ".." || isAbsolute(fromRoot) || win32.isAbsolute(fromRoot)) {
    throw new PackageManagerError("PACKAGE_FILE_MANIFEST_INVALID", `Managed file path escapes its package root: ${relPath}`);
  }
  return fullPath;
}

/** 使用安装 manifest 中的受管路径逐一哈希，排除 Unity 生成的 .meta 等非投影文件。 */
async function managedPackageFilesMatch(sourcePath: string, targetPath: string, fileManifestPath: string): Promise<boolean> {
  const files = await readManagedFileList(fileManifestPath);
  const expectedFiles = await listManagedSourceFiles(sourcePath);
  if (files.length !== expectedFiles.length || [...files].sort().some((file, index) => file !== expectedFiles[index])) return false;
  for (const file of files) {
    try {
      const [source, target] = await Promise.all([readFile(join(sourcePath, file)), readFile(join(targetPath, file))]);
      if (!source.equals(target)) return false;
    } catch {
      return false;
    }
  }
  return true;
}

async function listManagedSourceFiles(sourcePath: string): Promise<string[]> {
  const files: string[] = [];
  const visit = async (dir: string): Promise<void> => {
    for (const entry of await readdir(dir, { withFileTypes: true })) {
      const fullPath = join(dir, entry.name);
      if (entry.isDirectory()) await visit(fullPath);
      else if (entry.isFile()) files.push(posix.normalize(relative(sourcePath, fullPath).split("\\").join("/")));
    }
  };
  await visit(sourcePath);
  return files.sort();
}

async function writeFileManifest(path: string, files: string[]): Promise<void> {
  const manifest: FileManifest = { version: 1, files };
  await atomicWriteFile(path, `${JSON.stringify(manifest, null, 2)}\n`);
}

interface PackageDeploymentMetadata {
  version: number;
  deployments: Record<string, PackageDeploymentRecord>;
}

function deploymentRootPath(projectPath: string): string {
  return join(projectPath, "Packages", deploymentBackupDirName);
}

function deploymentMetadataPath(projectPath: string): string {
  return join(deploymentRootPath(projectPath), deploymentMetadataFileName);
}

function deploymentTransactionPath(projectPath: string): string {
  return join(deploymentRootPath(projectPath), "package-deployment-transaction.json");
}

async function readDeploymentTransaction(projectPath: string): Promise<PackageDeploymentTransaction | undefined> {
  const path = deploymentTransactionPath(projectPath);
  try {
    const value = JSON.parse(await readFile(path, "utf8")) as PackageDeploymentTransaction;
    if (typeof value.packageName !== "string" || !value.packageName) throw new Error("invalid-package-name");
    assertSafePackageDirectoryName(value.packageName);
    const transactionsRoot = join(deploymentRootPath(projectPath), "transactions");
    const transactionRoot = dirname(value.stagingPath);
    const transactionId = basename(transactionRoot);
    const expectedTarget = join(resolve(projectPath), "Packages", value.packageName);
    if (value.version !== 1 || !["deploy", "restore"].includes(value.operation)
      || !["prepared", "target_moved", "target_published", "metadata_published", "commit_proven"].includes(value.phase)
      || !isSameResolvedPath(value.targetPath, expectedTarget)
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(transactionId)
      || !isSameResolvedPath(dirname(transactionRoot), transactionsRoot)
      || !isSameResolvedPath(value.stagingPath, join(transactionRoot, "staging"))
      || !isSameResolvedPath(value.rollbackPath, join(transactionRoot, "rollback"))
      || value.backupPath !== undefined && !isCanonicalDeploymentBackupPath(projectPath, value.packageName, value.backupPath)
      || typeof value.targetExisted !== "boolean" || typeof value.backupCreated !== "boolean" || typeof value.deleteBackupOnCommit !== "boolean"
      || value.operation === "deploy" && value.deleteBackupOnCommit
      || value.operation === "restore" && (value.backupCreated || value.oldRecord === undefined)
      || value.operation === "restore" && value.oldRecord !== undefined
        && (!isSameOptionalResolvedPath(value.backupPath, value.oldRecord.backupPath)
          || value.deleteBackupOnCommit !== (value.oldRecord.backupPath !== undefined))
      || value.backupCreated && (!value.targetExisted || value.backupPath === undefined)
      || value.deleteBackupOnCommit && value.backupPath === undefined
      || value.oldRecord !== undefined && (!value.oldRecord || typeof value.oldRecord !== "object"))
      throw new Error("invalid-transaction");
    if (value.oldRecord !== undefined) assertDeploymentRecordSafe(projectPath, value.packageName, value.oldRecord);
    return value;
  } catch (error) {
    const code = (error as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT" || code === "ENOTDIR") return undefined;
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_TRANSACTION_INVALID", `Package deployment transaction is invalid or unreadable: ${path}`);
  }
}

async function writeDeploymentTransaction(projectPath: string, transaction: PackageDeploymentTransaction): Promise<void> {
  await atomicWriteFile(deploymentTransactionPath(projectPath), `${JSON.stringify(transaction, null, 2)}\n`);
  const observed = await readDeploymentTransaction(projectPath);
  if (!observed || observed.packageName !== transaction.packageName || observed.phase !== transaction.phase
    || observed.operation !== transaction.operation || observed.targetPath !== transaction.targetPath)
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_TRANSACTION_WRITE_FAILED", "Package deployment transaction readback does not match the written phase.");
}

async function clearDeploymentTransaction(projectPath: string): Promise<void> {
  const path = deploymentTransactionPath(projectPath);
  await rm(path, { force: true });
  await assertPathMissing(path, "PACKAGE_DEPLOYMENT_TRANSACTION_CLEANUP_FAILED");
}

/** 独占deployment lease下按固定marker恢复：commit只清理，其他phase一律回滚到旧target和旧record。 */
async function recoverPackageDeploymentTransaction(projectPath: string): Promise<void> {
  const transaction = await readDeploymentTransaction(projectPath);
  if (!transaction) return;
  if (transaction.phase === "commit_proven") {
    if (transaction.deleteBackupOnCommit && transaction.backupPath) await removePackagePublishDirectory(transaction.backupPath, "PACKAGE_DEPLOYMENT_CLEANUP_FAILED");
    await removePackagePublishDirectory(dirname(transaction.stagingPath), "PACKAGE_DEPLOYMENT_CLEANUP_FAILED");
    await clearDeploymentTransaction(projectPath);
    return;
  }
  const rollbackExists = await directoryExists(transaction.rollbackPath);
  const targetExists = await directoryExists(transaction.targetPath);
  if (rollbackExists) {
    if (targetExists) await removePackagePublishDirectory(transaction.targetPath, "PACKAGE_DEPLOYMENT_ROLLBACK_FAILED");
    await rename(transaction.rollbackPath, transaction.targetPath);
    if (!await directoryExists(transaction.targetPath) || await directoryExists(transaction.rollbackPath))
      throw new PackageManagerError("PACKAGE_DEPLOYMENT_ROLLBACK_FAILED", "Package deployment target rollback could not be proven.");
  } else if (!transaction.targetExisted && targetExists) {
    await removePackagePublishDirectory(transaction.targetPath, "PACKAGE_DEPLOYMENT_ROLLBACK_FAILED");
  } else if (transaction.targetExisted && !targetExists) {
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_ROLLBACK_FAILED", "Package deployment lost both its prior target and rollback copy.");
  }
  const metadataPath = deploymentMetadataPath(projectPath);
  const metadata = await readDeploymentMetadata(metadataPath);
  if (transaction.oldRecord) metadata[transaction.packageName] = transaction.oldRecord;
  else delete metadata[transaction.packageName];
  await writeDeploymentMetadata(metadataPath, metadata);
  const restored = await readDeploymentMetadata(metadataPath);
  if (transaction.oldRecord ? !restored[transaction.packageName] || restored[transaction.packageName].sourceHash !== transaction.oldRecord.sourceHash : !!restored[transaction.packageName])
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_ROLLBACK_FAILED", "Package deployment metadata rollback could not be proven.");
  if (transaction.backupCreated && transaction.backupPath) await removePackagePublishDirectory(transaction.backupPath, "PACKAGE_DEPLOYMENT_ROLLBACK_FAILED");
  await removePackagePublishDirectory(dirname(transaction.stagingPath), "PACKAGE_DEPLOYMENT_ROLLBACK_FAILED");
  await clearDeploymentTransaction(projectPath);
}

function deploymentBackupPath(projectPath: string, packageName: string, deployedAt: Date): string {
  const safeName = packageName.replace(/[^a-zA-Z0-9._-]/g, "_");
  const stamp = deployedAt.toISOString().replace(/[:.]/g, "-");
  return join(deploymentRootPath(projectPath), `${safeName}-${stamp}`);
}

function isCanonicalDeploymentBackupPath(projectPath: string, packageName: string, backupPath: string): boolean {
  const root = resolve(deploymentRootPath(projectPath));
  const resolvedBackup = resolve(backupPath);
  const safeName = packageName.replace(/[^a-zA-Z0-9._-]/g, "_");
  const expectedName = new RegExp(`^${escapeRegExp(safeName)}-\\d{4}-\\d{2}-\\d{2}T\\d{2}-\\d{2}-\\d{2}-\\d{3}Z$`);
  return isSameResolvedPath(dirname(resolvedBackup), root) && expectedName.test(basename(resolvedBackup));
}

/** 校验部署 metadata 只指向当前项目内允许被 restore 修改的位置。 */
function assertDeploymentRecordSafe(projectPath: string, packageName: string, record: PackageDeploymentRecord): void {
  if (!record || typeof record !== "object" || record.packageName !== packageName || typeof record.packageVersion !== "string"
    || typeof record.sourcePath !== "string" || !isSha256(record.sourceHash) || typeof record.targetPath !== "string"
    || typeof record.deployedAt !== "string" || (record.backupPath !== undefined && typeof record.backupPath !== "string")
    || (record.backupPackageVersion !== undefined && typeof record.backupPackageVersion !== "string")
    || (record.backupPath !== undefined) !== (typeof record.backupPackageVersion === "string" && record.backupPackageVersion.length > 0)) {
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_METADATA_INVALID", `Package deployment metadata is invalid for ${packageName}.`, [
      "Inspect or remove Packages/.unity_cli_deploy_backups/unity_cli.package-deployments.json before retrying."
    ]);
  }

  const expectedTargetPath = join(resolve(projectPath), "Packages", packageName);
  if (!isSameResolvedPath(record.targetPath, expectedTargetPath)) {
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_METADATA_UNSAFE", `Package deployment metadata target path is not the current project's Packages/${packageName}: ${record.targetPath}`, [
      "Inspect or remove Packages/.unity_cli_deploy_backups/unity_cli.package-deployments.json before retrying.",
      "Run package.deploy again from the current project if the project was moved."
    ]);
  }

  if (record.backupPath !== undefined && !isCanonicalDeploymentBackupPath(projectPath, packageName, record.backupPath)) {
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_METADATA_UNSAFE", `Package deployment metadata backup path is not the canonical direct backup for ${packageName}: ${record.backupPath}`, [
      "Inspect or remove Packages/.unity_cli_deploy_backups/unity_cli.package-deployments.json before retrying.",
      "Do not edit package.deploy metadata by hand unless every path stays inside the current project."
    ]);
  }
}

function isSameResolvedPath(left: string, right: string): boolean {
  return relative(resolve(left), resolve(right)) === "";
}

/** 可选路径只有同时缺失或解析后相等才属于同一 frozen plan。 */
function isSameOptionalResolvedPath(left?: string, right?: string): boolean {
  if (left === undefined || right === undefined) return left === right;
  return isSameResolvedPath(left, right);
}

function isPathInside(rootPath: string, candidatePath: string): boolean {
  const relativePath = relative(resolve(rootPath), resolve(candidatePath));
  return relativePath === "" || (!relativePath.startsWith("..") && !isAbsolute(relativePath));
}

async function readDeploymentMetadata(path: string): Promise<Record<string, PackageDeploymentRecord>> {
  try {
    const data = JSON.parse(stripJsonBom(await readFile(path, "utf8"))) as Partial<PackageDeploymentMetadata>;
    if (!data || typeof data !== "object" || data.version !== 1 || !isPlainObject(data.deployments))
      throw new PackageManagerError("PACKAGE_DEPLOYMENT_METADATA_INVALID", `Package deployment metadata requires version 1 and an object deployments map: ${path}`);
    const deployments = data.deployments as Record<string, PackageDeploymentRecord>;
    const projectPath = dirname(dirname(dirname(path)));
    const backupOwners = new Map<string, string>();
    for (const [packageName, record] of Object.entries(deployments)) {
      assertSafePackageDirectoryName(packageName);
      assertDeploymentRecordSafe(projectPath, packageName, record);
      if (record.backupPath === undefined) continue;
      const identity = resolve(record.backupPath).toLowerCase();
      const previousOwner = backupOwners.get(identity);
      if (previousOwner) throw new PackageManagerError("PACKAGE_DEPLOYMENT_METADATA_UNSAFE", `Packages ${previousOwner} and ${packageName} reference the same deployment backup.`);
      backupOwners.set(identity, packageName);
    }
    return deployments;
  } catch (error) {
    const nodeError = error as NodeJS.ErrnoException;
    if (nodeError.code === "ENOENT") return {};
    if (error instanceof SyntaxError) {
      throw new PackageManagerError("PACKAGE_DEPLOYMENT_METADATA_INVALID", `Package deployment metadata is invalid JSON: ${path}`, [
        "Inspect or remove the metadata file before running package.restore."
      ]);
    }
    throw error;
  }
}

async function writeDeploymentMetadata(path: string, deployments: Record<string, PackageDeploymentRecord>): Promise<void> {
  const metadata: PackageDeploymentMetadata = { version: 1, deployments };
  await atomicWriteFile(path, `${JSON.stringify(metadata, null, 2)}\n`);
}

async function pathExists(path: string): Promise<boolean> {
  try {
    await stat(path);
    return true;
  } catch (error) {
    const code = (error as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT" || code === "ENOTDIR") return false;
    throw error;
  }
}

async function directoryExists(path: string): Promise<boolean> {
  try {
    return (await stat(path)).isDirectory();
  } catch (error) {
    const code = (error as NodeJS.ErrnoException)?.code;
    if (code === "ENOENT" || code === "ENOTDIR") return false;
    throw new PackageManagerError("INIT_PATH_OBSERVATION_FAILED", `Unity package directory cannot be observed: ${path}`);
  }
}

async function countPackageFiles(sourcePath: string): Promise<number> {
  return (await listFilesRecursive(sourcePath)).length;
}

async function listFilesRecursive(dir: string): Promise<string[]> {
  const files: string[] = [];
  const entries = await readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...await listFilesRecursive(fullPath));
    } else if (entry.isFile()) {
      files.push(fullPath);
    }
  }
  return files;
}

function assertSafePackageDirectoryName(packageName: string): void {
  const firstSegment = packageName.split(".", 1)[0].toLowerCase();
  const reserved = /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(firstSegment);
  const canonical = /^[a-z0-9][a-z0-9_-]*(?:\.[a-z0-9][a-z0-9_-]*)*$/.test(packageName);
  if (!canonical || reserved || packageName.toLowerCase() === deploymentBackupDirName.toLowerCase()) {
    throw new PackageManagerError("PACKAGE_NAME_INVALID", `Package name is not safe for a Packages/<name> directory: ${packageName}`, [
      "Use a valid Unity package name such as com.example.tool."
    ]);
  }
}

function assertDeploySourceOutsideProjectPackages(sourcePath: string, projectPath: string): void {
  const packagesPath = join(resolve(projectPath), "Packages");
  const source = resolve(sourcePath);
  const relativeToPackages = relative(packagesPath, source);
  if (!relativeToPackages || (!relativeToPackages.startsWith("..") && !isAbsolute(relativeToPackages))) {
    throw new PackageManagerError("PACKAGE_DEPLOY_SOURCE_INSIDE_PROJECT", "package.deploy source must be outside the target project's Packages directory.", [
      "Choose a separate local package source directory, not the already-installed package directory.",
      "Use package.restore to revert a previous package.deploy operation."
    ]);
  }
}

async function readManifest(path: string, allowMissing: boolean): Promise<UnityManifest> {
  try {
    const manifest = JSON.parse(stripJsonBom(await readFile(path, "utf8"))) as unknown;
    if (!isPlainObject(manifest) || !isPlainObject(manifest.dependencies)
      || !Object.values(manifest.dependencies).every((value) => typeof value === "string")) {
      throw new PackageManagerError("MANIFEST_INVALID", `Unity manifest requires an object root with a string-valued dependencies object: ${path}`);
    }
    return manifest as UnityManifest;
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

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
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
  await atomicWriteFile(path, `${JSON.stringify(manifest, null, 2)}\n`);
}

/** 临时文件名携带owner启动身份与内容摘要；dead owner残留在下次写入前精确回收，数量不随历史增长。 */
async function atomicWriteFile(path: string, content: string): Promise<void> {
  const parent = dirname(path);
  const prefix = `.${basename(path)}.tmp-`;
  await mkdir(parent, { recursive: true });
  for (const entry of await readdir(parent, { withFileTypes: true })) {
    if (!entry.isFile() || !entry.name.startsWith(prefix)) continue;
    const match = new RegExp(`^\\.${escapeRegExp(basename(path))}\\.tmp-(\\d+)-([0-9a-f]{16})-[0-9a-f]{64}$`, "i").exec(entry.name);
    const stalePath = join(parent, entry.name);
    const sameProcessStale = !!match && Number(match[1]) === process.pid && !activeAtomicWriteCandidates.has(stalePath);
    if (!match || await isCandidateProcessActive(Number(match[1]), match[2]) && !sameProcessStale)
      throw new PackageManagerError("ATOMIC_WRITE_BUSY", `Another writer owns temporary metadata for ${path}.`);
    await rm(stalePath, { force: false });
    await assertPathMissing(stalePath, "ATOMIC_WRITE_RECOVERY_FAILED");
  }
  const currentProcessStartIdentity = await processStartIdentity(process.pid);
  if (!currentProcessStartIdentity) throw new PackageManagerError("PROCESS_IDENTITY_UNAVAILABLE", `Current process start identity is unavailable before writing ${path}.`);
  const startHash = createHash("sha256").update(currentProcessStartIdentity).digest("hex").slice(0, 16);
  const contentHash = createHash("sha256").update(content, "utf8").digest("hex");
  const temporaryPath = join(parent, `${prefix}${process.pid}-${startHash}-${contentHash}`);
  activeAtomicWriteCandidates.add(temporaryPath);
  try {
    const handle = await open(temporaryPath, "wx", 0o600);
    try {
      await handle.writeFile(content, "utf8");
      await handle.sync();
    } finally {
      await handle.close();
    }
    if (createHash("sha256").update(await readFile(temporaryPath, "utf8"), "utf8").digest("hex") !== contentHash)
      throw new PackageManagerError("ATOMIC_WRITE_READBACK_FAILED", `Temporary metadata content changed before publish: ${path}`);
    await rename(temporaryPath, path);
  } finally {
    activeAtomicWriteCandidates.delete(temporaryPath);
    if (await pathExists(temporaryPath)) {
      await rm(temporaryPath, { force: false });
      await assertPathMissing(temporaryPath, "ATOMIC_WRITE_CLEANUP_FAILED");
    }
  }
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** manifest 写入后必须以磁盘重读结果确认，避免把请求或写前对象误报为终态。 */
function manifestReadbackMismatch(manifestPath: string, detail: string): PackageManagerError {
  return new PackageManagerError("MANIFEST_READBACK_MISMATCH", `Manifest write could not be verified: ${detail}.`, [
    "Inspect Packages/manifest.json and retry after resolving concurrent manifest writes."
  ]);
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

/** 比较 Unity registry 常见数字版本；git/file/custom spec 无法可靠比较时不拦截。 */
function isPackageVersionDowngrade(candidate: string, current: string): boolean {
  const candidateParts = parseComparableVersion(candidate);
  const currentParts = parseComparableVersion(current);
  if (!candidateParts || !currentParts) {
    return false;
  }
  const length = Math.max(candidateParts.length, currentParts.length);
  for (let index = 0; index < length; index += 1) {
    const left = candidateParts[index] ?? 0;
    const right = currentParts[index] ?? 0;
    if (left < right) return true;
    if (left > right) return false;
  }
  return false;
}

/** 解析可安全比较的数字版本；非 registry 版本返回 undefined 交给 Unity 处理。 */
function parseComparableVersion(version: string): number[] | undefined {
  const normalized = version.trim().replace(/^v/i, "");
  if (!/^\d+(?:\.\d+)*(?:[-+].*)?$/.test(normalized)) {
    return undefined;
  }
  return normalized.split(/[+-]/, 1)[0].split(".").map((part) => Number(part));
}

/** 从 packages-lock.json 中找出直接依赖目标包的 locked package。 */
function packageDependents(lock: UnityLock, packageName: string): PackageDependent[] {
  return Object.entries(lock.dependencies ?? {})
    .filter(([name, entry]) => name !== packageName && entry.dependencies && Object.prototype.hasOwnProperty.call(entry.dependencies, packageName))
    .map(([name, entry]) => ({ name, requiredVersion: entry.dependencies?.[packageName] }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

/** 根据 scoped registry scopes 找出仍会由该 registry 覆盖的 manifest 包。 */
function packagesUsingScopes(manifest: UnityManifest, scopes: string[]): string[] {
  const names = Object.keys(manifest.dependencies ?? {});
  return names.filter((name) => scopes.some((scope) => packageMatchesScope(name, scope))).sort();
}

/** Unity scoped registry scope 按完整包名或点分前缀匹配。 */
function packageMatchesScope(packageName: string, scope: string): boolean {
  return packageName === scope || packageName.startsWith(`${scope}.`);
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

function sameScopes(left: string[], right: string[]): boolean {
  return left.length === right.length && left.every((scope, index) => scope === right[index]);
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
