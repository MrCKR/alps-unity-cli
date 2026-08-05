import { realpath, readFile, readdir, stat, mkdir, copyFile, rename, rm, open } from "node:fs/promises";
import { createHash } from "node:crypto";
import { basename, join, relative, resolve, win32, dirname, posix, isAbsolute } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
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

export interface DeployPackageOptions {
  packageName?: string;
  dryRun?: boolean;
  /** durable worker 只能使用 prepare 阶段冻结的备份路径，不得在 effect 后重新生成。 */
  plannedBackupPath?: string;
}

export interface RestorePackageOptions {
  dryRun?: boolean;
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
const cliCompatibilityRange = ">=0.1.0 <0.2.0";
const installMetadataFileName = "unity_cli.install.json";
const fileManifestFileName = "unity_cli.files.json";
const deploymentMetadataFileName = "unity_cli.package-deployments.json";
const deploymentBackupDirName = ".unity_cli_deploy_backups";

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

export async function ensurePackageBridge(projectPath: string, prepared?: UnityCliPackageInstallPlan, runtime: PackageBridgeInstallRuntime = {}): Promise<UnityCliPackageInstall> {
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
  // Check existing installation: skip if same version & hash
  const existing = await readExistingInstallMetadata(installMetadataPath).catch(() => undefined);
  if (existing && existing.packageVersion === packageManifest.version && existing.sourceHash === sourceHash && existing.projectHash === projectHash
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

  // 完整投影先写入同级 staging 目录，再以 rename 发布；不会暴露半复制的 Bridge 包。
  const publishNonce = `${process.pid}-${Date.now()}`;
  const stagingPath = `${targetPath}.staging-${publishNonce}`;
  const backupPath = `${targetPath}.previous-${publishNonce}`;
  const targetExists = await directoryExists(targetPath);
  // publish 阶段（复制+rename）必须尊重调用方 immutable deadline；未传时才用 5s 默认窗口。
  // Defender/索引器常短暂持有新复制文件的句柄，隐性 5s clamp 会系统性误判为 publish 失败。
  const publishDeadlineMs = runtime.absoluteDeadlineMs ?? Date.now() + 5_000;
  try {
    await rm(stagingPath, { recursive: true, force: true });
    if (targetExists) await copyDirRecursive(targetPath, stagingPath, targetPath, []);
    await removeStaleManagedFiles(stagingPath, fileManifestPath);
    const managedFiles = await copyPackageContents(sourcePath, stagingPath);
    await writeFileManifest(join(stagingPath, fileManifestFileName), managedFiles);
    if (targetExists) await renamePackageDirectory(targetPath, backupPath, publishDeadlineMs, runtime);
    await renamePackageDirectory(stagingPath, targetPath, publishDeadlineMs, runtime);
    // Unity 会扫描 Packages 下的每个 package.json；遗留 sibling 会形成重复 embedded package，必须纳入终态证明。
    await cleanupPublishedPackageSiblings(targetPath);
  } catch (error) {
    // 发布未完成时必须在同一 deadline 内恢复原目标；rollback 失败不得被吞掉。
    if (targetExists && !(await directoryExists(targetPath)) && await directoryExists(backupPath)) {
      try {
        await renamePackageDirectory(backupPath, targetPath, publishDeadlineMs, runtime);
      } catch {
        throw new PackageManagerError("INIT_ROLLBACK_FAILED", "Bridge package publish failed and the previous package directory could not be restored.", [
          "Inspect Packages/com.unity_cli and its sibling .previous directory before submitting a new init request."
        ]);
      }
    }
    throw error;
  } finally {
    await removePackagePublishDirectory(stagingPath, "INIT_STAGING_CLEANUP_FAILED");
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

  // 发布后的证明必须重新读取目标 metadata 和 file manifest，不能复用写前计划或内存对象。
  const observedMetadata = await readInstallMetadata(installMetadataPath);
  if (observedMetadata.packageName !== bridgePackageName
    || observedMetadata.packageVersion !== packageManifest.version
    || observedMetadata.sourceHash !== sourceHash
    || observedMetadata.projectHash !== projectHash
    || observedMetadata.installTime !== installTime
    || !await managedPackageFilesMatch(sourcePath, targetPath, fileManifestPath)) {
    throw new PackageManagerError("INIT_CHECK_FAILED", "Bridge package installation could not be proven from fresh filesystem readback.", ["Inspect Packages/com.unity_cli and run init again."]);
  }

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

/** 只清理 Core 发布器拥有的 sibling 命名空间，并以 fresh directory readback 证明终态。 */
async function cleanupPublishedPackageSiblings(targetPath: string): Promise<void> {
  const parent = dirname(targetPath);
  const prefix = `${basename(targetPath)}.`;
  const entries = await readdir(parent, { withFileTypes: true });
  const owned = entries
    .filter((entry) => entry.isDirectory() && entry.name.startsWith(prefix)
      && /\.(?:previous|staging)-\d+-\d+$/.test(entry.name))
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

export async function checkPackageBridge(projectPath: string): Promise<UnityCliPackageInstall> {
  const paths = packagePaths(projectPath);
  const targetPath = join(paths.projectPath, "Packages", bridgePackageName);
  const installMetadataPath = join(paths.projectPath, "Packages", installMetadataFileName);
  const fileManifestPath = join(targetPath, fileManifestFileName);
  const { packageManifest, installMetadata } = await readInstalledPackageBridge(paths.projectPath, "INIT_CHECK_FAILED", ["Run unity_cli init --project <path> to install the bundled Unity package."]);

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
  if (!await managedPackageFilesMatch(sourcePath, targetPath, fileManifestPath)) {
    throw new PackageManagerError("INIT_CHECK_FAILED", `${bridgePackageName} managed file projection hash does not match the bundled source.`, ["Run unity_cli init --project <path> to restore the bundled package projection."]);
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

/** 卸载 init 复制的 Bridge 包；只删除托管的 com.unity_cli copy-mode 安装。 */
export async function removePackageBridge(projectPath: string, options: RemovePackageBridgeOptions = {}): Promise<UnityCliPackageRemoval> {
  const paths = packagePaths(projectPath);
  const targetPath = join(paths.projectPath, "Packages", bridgePackageName);
  const installMetadataPath = join(paths.projectPath, "Packages", installMetadataFileName);
  const fileManifestPath = join(targetPath, fileManifestFileName);
  const { packageManifest } = await readInstalledPackageBridge(paths.projectPath, "BRIDGE_PACKAGE_NOT_INSTALLED", ["Run unity_cli init --project <path> before removing the bundled Unity package."]);
  const managedFiles = await readManagedFileList(fileManifestPath);
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
    await rm(targetPath, { recursive: true, force: true });
    await rm(installMetadataPath, { force: true });
    if (await pathExists(targetPath) || await pathExists(installMetadataPath)) {
      throw new PackageManagerError("BRIDGE_PACKAGE_REMOVE_READBACK_FAILED", "Bridge package files still exist after removal.", ["Inspect Packages/com.unity_cli and the install metadata before retrying; do not remove blindly."]);
    }
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
  const paths = packagePaths(projectPath);
  await readManifest(paths.manifestPath, false);
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
  const existingManifest = existingTarget ? await readAnyPackageManifest(targetPath).catch(() => undefined) : undefined;
  const existingTargetHash = existingTarget ? await hashSourcePath(targetPath) : undefined;
  const currentRecord = (await readDeploymentMetadata(metadataPath))[packageName];
  if (currentRecord !== undefined) {
    assertDeploymentRecordSafe(paths.projectPath, packageName, currentRecord);
  }
  const derivedBackupPath = currentRecord
    ? currentRecord.backupPath
    : (existingTarget ? deploymentBackupPath(paths.projectPath, packageName, new Date()) : undefined);
  const backupPath = options.plannedBackupPath ?? derivedBackupPath;
  if (options.plannedBackupPath !== undefined) {
    if (!existingTarget || !isPathInside(deploymentRootPath(paths.projectPath), options.plannedBackupPath)
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
    await mkdir(dirname(metadataPath), { recursive: true });
    if (existingTarget && !repeatedDeployment && backupPath) {
      await rm(backupPath, { recursive: true, force: true });
      await mkdir(dirname(backupPath), { recursive: true });
      await copyDirRecursive(targetPath, backupPath, targetPath, []);
    }
    await rm(targetPath, { recursive: true, force: true });
    await copyDirRecursive(resolvedSource, targetPath, resolvedSource, []);
    const metadata = await readDeploymentMetadata(metadataPath);
    metadata[packageName] = {
      packageName,
      packageVersion: sourceManifest.version,
      sourcePath: resolvedSource,
      sourceHash,
      targetPath,
      backupPath,
      backupPackageVersion: existingManifest?.version,
      deployedAt: new Date().toISOString()
    };
    await writeDeploymentMetadata(metadataPath, metadata);

    // 从目标目录、备份和 metadata 重新读取，证明本次请求的完整文件系统终态。
    const observedManifest = await readAnyPackageManifest(targetPath).catch(() => undefined);
    const observedHash = await hashSourcePath(targetPath).catch(() => undefined);
    const observedFileCount = await countPackageFiles(targetPath).catch(() => -1);
    const observedRecord = (await readDeploymentMetadata(metadataPath))[packageName];
    const backupHash = existingTarget && !repeatedDeployment && backupPath ? await hashSourcePath(backupPath).catch(() => undefined) : undefined;
    if (!observedManifest || observedManifest.name !== packageName || observedManifest.version !== sourceManifest.version
      || observedHash !== sourceHash || observedFileCount !== copiedFileCount
      || !observedRecord || observedRecord.targetPath !== targetPath || observedRecord.sourceHash !== sourceHash
      || (existingTarget && !repeatedDeployment && backupHash !== existingTargetHash)) {
      throw new PackageManagerError("PACKAGE_DEPLOY_READBACK_FAILED", `Deployed package did not retain the requested filesystem state: ${packageName}.`, ["Inspect the target package directory and deployment metadata before retrying; do not redeploy blindly."]);
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
    backupPackageVersion: existingManifest?.version,
    backupCreated: existingTarget && !repeatedDeployment,
    repeatedDeployment,
    copiedFileCount,
    dryRun: options.dryRun === true,
    plannedEffects,
    metadataPath
  };
}

/** 从 package.deploy 创建的备份恢复目标包；没有备份时移除本次部署的包目录。 */
export async function restoreDeployedPackage(projectPath: string, packageName: string, options: RestorePackageOptions = {}): Promise<PackageRestoreResult> {
  const paths = packagePaths(projectPath);
  await readManifest(paths.manifestPath, false);
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

  const backupExists = record.backupPath ? await directoryExists(record.backupPath) : false;
  const backupFileCount = backupExists && record.backupPath ? await countPackageFiles(record.backupPath) : 0;
  const backupHash = backupExists && record.backupPath ? await hashSourcePath(record.backupPath) : undefined;
  const plannedEffects: Array<Record<string, unknown>> = [];
  if (backupExists && record.backupPath) {
    plannedEffects.push({ type: "package.restore.backup", package: packageName, from: record.backupPath, to: record.targetPath, fileCount: backupFileCount });
  } else {
    plannedEffects.push({ type: "package.restore.remove-deployed", package: packageName, targetPath: record.targetPath });
  }
  plannedEffects.push({ type: "asset.refresh", reason: "package.restore" });

  if (!options.dryRun) {
    await rm(record.targetPath, { recursive: true, force: true });
    if (backupExists && record.backupPath) {
      await copyDirRecursive(record.backupPath, record.targetPath, record.backupPath, []);
      await rm(record.backupPath, { recursive: true, force: true });
    }
    delete metadata[packageName];
    await writeDeploymentMetadata(metadataPath, metadata);

    // restore 成功必须由目标目录与 metadata 的 fresh readback 同时证明。
    const observedMetadata = await readDeploymentMetadata(metadataPath);
    const targetExists = await directoryExists(record.targetPath);
    const observedTargetHash = targetExists ? await hashSourcePath(record.targetPath).catch(() => undefined) : undefined;
    const observedTargetCount = targetExists ? await countPackageFiles(record.targetPath).catch(() => -1) : 0;
    const backupStillExists = record.backupPath ? await directoryExists(record.backupPath) : false;
    if (observedMetadata[packageName]
      || (backupExists && (!targetExists || observedTargetHash !== backupHash || observedTargetCount !== backupFileCount || backupStillExists))
      || (!backupExists && targetExists)) {
      throw new PackageManagerError("PACKAGE_RESTORE_READBACK_FAILED", `Restored package did not retain the requested filesystem state: ${packageName}.`, ["Inspect the target package directory and deployment metadata before retrying; do not restore blindly."]);
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
    metadataHash: createHash("sha256").update(await readFile(metadataPath)).digest("hex"),
    record: metadata[packageName]
  };
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

/** 使用安装 manifest 中的受管路径逐一哈希，排除 Unity 生成的 .meta 等非投影文件。 */
async function managedPackageFilesMatch(sourcePath: string, targetPath: string, fileManifestPath: string): Promise<boolean> {
  const files = await readManagedFileList(fileManifestPath);
  if (files.length === 0) return false;
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

function deploymentBackupPath(projectPath: string, packageName: string, deployedAt: Date): string {
  const safeName = packageName.replace(/[^a-zA-Z0-9._-]/g, "_");
  const stamp = deployedAt.toISOString().replace(/[:.]/g, "-");
  return join(deploymentRootPath(projectPath), `${safeName}-${stamp}`);
}

/** 校验部署 metadata 只指向当前项目内允许被 restore 修改的位置。 */
function assertDeploymentRecordSafe(projectPath: string, packageName: string, record: PackageDeploymentRecord): void {
  if (!record || typeof record !== "object" || record.packageName !== packageName || typeof record.targetPath !== "string" || (record.backupPath !== undefined && typeof record.backupPath !== "string")) {
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

  if (record.backupPath && !isPathInside(deploymentRootPath(projectPath), record.backupPath)) {
    throw new PackageManagerError("PACKAGE_DEPLOYMENT_METADATA_UNSAFE", `Package deployment metadata backup path is outside Packages/.unity_cli_deploy_backups: ${record.backupPath}`, [
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
    if (!data || typeof data !== "object" || typeof data.deployments !== "object" || data.deployments === null) {
      return {};
    }
    return data.deployments as Record<string, PackageDeploymentRecord>;
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
  } catch {
    return false;
  }
}

async function directoryExists(path: string): Promise<boolean> {
  try {
    return (await stat(path)).isDirectory();
  } catch {
    return false;
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
  if (packageName.includes("/") || packageName.includes("\\") || packageName === "." || packageName === ".." || packageName === deploymentBackupDirName || packageName.split(".").includes("..")) {
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

/** 同目录临时文件 + fsync + rename，避免读者观察到截断的 JSON 元数据。 */
async function atomicWriteFile(path: string, content: string): Promise<void> {
  const temporaryPath = join(dirname(path), `.${process.pid}.${Date.now()}.${Math.random().toString(16).slice(2)}.tmp`);
  await mkdir(dirname(path), { recursive: true });
  const handle = await open(temporaryPath, "wx", 0o600);
  try {
    await handle.writeFile(content, "utf8");
    await handle.sync();
  } finally {
    await handle.close();
  }
  try {
    await rename(temporaryPath, path);
  } finally {
    await rm(temporaryPath, { force: true }).catch(() => undefined);
  }
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
