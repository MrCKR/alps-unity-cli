import { createHash, randomUUID } from "node:crypto";
import { existsSync } from "node:fs";
import { copyFile, mkdir, open, readdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { basename, dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { readInstalledUnityPackages } from "./project-resolver.js";
import { ExtensionRegistry, resolveExtensionAvailability } from "./registry.js";
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
  integrity: "shallow" | "target" | "full";
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

/** 一次公开调用冻结的 package/install 输入，后续检查不得重新读取。 */
export interface ExtensionStateSnapshot {
  projectPath: string;
  installedPackages: ReadonlyMap<string, string>;
  installManifest: ExtensionInstallManifest;
}

export interface ExtensionCheckOptions {
  snapshot?: ExtensionStateSnapshot;
  absoluteDeadlineMs?: number;
}

export type ExtensionFilesystemOperation = "snapshot" | "target-proof" | "source-proof" | "unknown-tree-entry";

const packageName = "com.alpsckr.unitycli";
const manifestFileName = "unitycli.extensions.manifest.json";
const manifestLockTimeoutMs = 15_000;
const processStartIdentity = new Date(Date.now() - Math.floor(process.uptime() * 1_000)).toISOString();

let afterProjectionCopyForTest: (() => Promise<void>) | undefined;
let sourceRootForTest: ((extensionId: ExtensionId) => string) | undefined;
let filesystemOperationObserverForTest: ((operation: ExtensionFilesystemOperation, extensionId?: ExtensionId) => void) | undefined;

/** 仅测试使用：在 source projection 复制后强制制造交错。 */
export function setAfterProjectionCopyForTest(hook: (() => Promise<void>) | undefined): void {
  afterProjectionCopyForTest = hook;
}

/** 仅测试使用：把可变 source 夹具隔离到临时目录。 */
export function setExtensionSourceRootForTest(resolver: ((extensionId: ExtensionId) => string) | undefined): void {
  sourceRootForTest = resolver;
}

/** 仅测试使用：计数 discovery 的语义文件系统操作，不暴露到公开结果。 */
export function setExtensionFilesystemOperationObserverForTest(
  observer: ((operation: ExtensionFilesystemOperation, extensionId?: ExtensionId) => void) | undefined,
): void {
  filesystemOperationObserverForTest = observer;
}

/** 返回 extension 源码目录。 */
export function extensionSourceRoot(extensionId: ExtensionId): string {
  return resolve(fileURLToPath(new URL(`../../extensions/${extensionId}`, import.meta.url)));
}

/** 返回 extension Unity 安装投影使用的默认相对路径。 */
export function extensionUnityInstallIncludes(manifest: ExtensionManifest): string[] {
  return manifest.unityInstall?.include ?? ["extension.json", "unitypkg/", "docs/"];
}

/** 返回 Unity 项目内 extension 目录安装根目录。 */
export function extensionInstallRoot(projectPath: string, extensionId: ExtensionId): string {
  return join(resolve(projectPath), "Packages", packageName, "Extensions", extensionId);
}

/** 返回 extension 安装 manifest 路径。 */
export function extensionInstallManifestPath(projectPath: string): string {
  return join(resolve(projectPath), "Packages", packageName, manifestFileName);
}

/** 读取 extension 安装 manifest；仅缺失文件代表空状态，损坏状态必须阻断变更。 */
export async function readExtensionInstallManifest(projectPath: string): Promise<ExtensionInstallManifest> {
  const path = extensionInstallManifestPath(projectPath);
  let content: string;
  try {
    content = await readFile(path, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return emptyManifest();
    throw error;
  }
  try {
    const parsed = JSON.parse(stripBom(content)) as Partial<ExtensionInstallManifest>;
    if (parsed.version !== 1 || !Array.isArray(parsed.extensions) || !parsed.extensions.every(isInstalledEntry)) {
      throw invalidInstallManifest(projectPath, "Expected version 1 and valid extension entries.");
    }
    if (new Set(parsed.extensions.map((entry) => entry.extensionId)).size !== parsed.extensions.length) {
      throw invalidInstallManifest(projectPath, "Extension entries must have unique extensionId values.");
    }
    return { version: 1, extensions: parsed.extensions };
  } catch (error) {
    if (error instanceof ExtensionInstallerError) throw error;
    throw invalidInstallManifest(projectPath, "The file is not valid JSON.");
  }
}

/** 原子写入 extension 安装 manifest，读取方不会观察到半写文件。 */
export async function writeExtensionInstallManifest(projectPath: string, manifest: ExtensionInstallManifest): Promise<void> {
  const manifestPath = extensionInstallManifestPath(projectPath);
  const temporaryPath = `${manifestPath}.${process.pid}.${randomUUID()}.tmp`;
  await mkdir(dirname(manifestPath), { recursive: true });
  try {
    await writeFile(temporaryPath, `${JSON.stringify({ version: 1, extensions: manifest.extensions.sort((a, b) => a.extensionId.localeCompare(b.extensionId)) }, null, 2)}\n`, "utf8");
    await rename(temporaryPath, manifestPath);
    await readExtensionInstallManifest(projectPath);
  } finally {
    await rm(temporaryPath, { force: true }).catch(() => undefined);
  }
}

/** 安装一个 extension 目录；只投影 Unity 侧文件，不安装可选 Unity package。 */
export async function installExtensionBundle(projectPath: string, manifest: ExtensionManifest): Promise<ExtensionInstallResult> {
  const installedPackages = await readInstalledUnityPackages(projectPath);
  const availability = resolveInstallAvailability(manifest, installedPackages);
  if (availability.status !== "available") {
    const missing = availability.missingPackageIds.join(", ");
    const error = new ExtensionInstallerError(
      "E_EXTENSION_DEPENDENCY_MISSING",
      `Extension "${manifest.id}" requires missing Unity package(s): ${missing}`,
      [
        `Install the Unity package first: ${missing}`,
        `Then run unitycli extensions install ${manifest.id} --project <path>.`,
        "The CLI does not install optional Unity packages automatically."
      ]
    );
    error.availability = availability;
    throw error;
  }

  return installExtensionBundleUnchecked(projectPath, manifest, "installed");
}

export function resolveInstallAvailability(
  manifest: ExtensionManifest,
  installedPackages: ReadonlyMap<string, string>
): ExtensionAvailabilityResult {
  return resolveExtensionAvailability(
    { ...manifest, contributions: { ...manifest.contributions, tools: [] } },
    installedPackages
  );
}

/** 根据已解析依赖自动安装 extension 目录；调用方保证依赖可用。 */
export async function installExtensionBundleUnchecked(
  projectPath: string,
  manifest: ExtensionManifest,
  installedStatus: "installed" | "already_installed" = "installed"
): Promise<ExtensionInstallResult> {
  const sourceRoot = sourceRootForTest?.(manifest.id) ?? extensionSourceRoot(manifest.id);
  if (!existsSync(sourceRoot)) {
    throw new ExtensionInstallerError("E_EXTENSION_BUNDLE_MISSING", `Extension source directory not found: ${sourceRoot}`, ["Reinstall or rebuild the @alpsckr/unitycli package."]);
  }

  return withExtensionManifestLock(projectPath, async () => {
    const targetRoot = extensionInstallRoot(projectPath, manifest.id);
    const stagingRoot = `${targetRoot}.staging-${process.pid}-${randomUUID()}`;
    const previousRoot = `${targetRoot}.previous-${process.pid}-${randomUUID()}`;
    const installManifest = await readExtensionInstallManifest(projectPath);
    const previous = installManifest.extensions.find((entry) => entry.extensionId === manifest.id);
    const hadTarget = existsSync(targetRoot);
    try {
      await rm(stagingRoot, { recursive: true, force: true });
      if (hadTarget) await copyRecursive(targetRoot, stagingRoot, targetRoot, []);
      if (previous) await removeManagedFiles(stagingRoot, previous.files);
      const installIncludes = extensionUnityInstallIncludes(manifest);
      const sourceHashBeforeCopy = await hashBundleProjection(sourceRoot, installIncludes);
      const copiedFiles = await copyBundleProjection(sourceRoot, stagingRoot, installIncludes);
      await afterProjectionCopyForTest?.();
      const sourceHashAfterCopy = await hashBundleProjection(sourceRoot, installIncludes);
      const stagingHash = await hashManagedProjection(stagingRoot, copiedFiles);
      // source 前后与 staging 必须是同一投影；不发布任何混合 source 的 target。
      if (sourceHashBeforeCopy !== sourceHashAfterCopy || sourceHashBeforeCopy !== stagingHash) {
        throw new ExtensionInstallerError("E_EXTENSION_SOURCE_SNAPSHOT_STALE", `Extension source changed while creating its projection: ${manifest.id}`, ["Retry the extension install after source changes have completed."]);
      }
      const sourceHash = stagingHash;
      if (hadTarget) await rename(targetRoot, previousRoot);
      await rename(stagingRoot, targetRoot);
      await rm(previousRoot, { recursive: true, force: true });

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
      const projection = await checkManagedFiles(targetRoot, copiedFiles);
      if (projection.missingFiles.length > 0 || projection.changedFiles.length > 0) {
        throw new ExtensionInstallerError("E_EXTENSION_TARGET_PROJECTION_INVALID", `Installed extension projection could not be verified: ${manifest.id}`, ["Inspect the target extension directory and retry the install."]);
      }
      const freshManifest = await readExtensionInstallManifest(projectPath);
      if (!freshManifest.extensions.some((item) => item.extensionId === manifest.id && item.sourceHash === sourceHash)) {
        throw new ExtensionInstallerError("E_EXTENSION_INSTALL_MANIFEST_INVALID", "Extension install manifest readback did not contain the installed projection.", ["Inspect Packages/com.alpsckr.unitycli/unitycli.extensions.manifest.json before retrying."]);
      }

      return {
        extensionId: manifest.id,
        status: installedStatus,
        projectPath: resolve(projectPath),
        targetRoot,
        fileCount: copiedFiles.length,
        sourceHash,
        files: copiedFiles
      };
    } catch (error) {
      if (hadTarget && !existsSync(targetRoot) && existsSync(previousRoot)) await rename(previousRoot, targetRoot).catch(() => undefined);
      throw error;
    } finally {
      await rm(stagingRoot, { recursive: true, force: true }).catch(() => undefined);
    }

  });
}

/** 按 manifest 删除一个 extension 目录复制过的文件。 */
export async function removeExtensionBundle(projectPath: string, extensionId: ExtensionId): Promise<ExtensionInstallResult> {
  return withExtensionManifestLock(projectPath, async () => {
    const installManifest = await readExtensionInstallManifest(projectPath);
    const entry = installManifest.extensions.find((item) => item.extensionId === extensionId);
    if (!entry) {
      throw new ExtensionInstallerError("EXTENSION_NOT_INSTALLED", `Extension is not installed: ${extensionId}`, ["Run unitycli extensions list --project <path> to inspect extension state."]);
    }

    const targetRoot = extensionInstallRoot(projectPath, extensionId);
    await removeManagedFiles(targetRoot, entry.files);
    await removeEmptyDirectories(targetRoot);
    const remaining = await checkManagedFiles(targetRoot, entry.files);
    if (remaining.missingFiles.length !== entry.files.length) {
      throw new ExtensionInstallerError("E_EXTENSION_REMOVE_INCOMPLETE", `One or more managed files remain for extension: ${extensionId}`, ["Resolve filesystem permissions or locks, then inspect the extension target directory."]);
    }

    installManifest.extensions = installManifest.extensions.filter((item) => item.extensionId !== extensionId);
    await writeExtensionInstallManifest(projectPath, installManifest);

    return {
      extensionId,
      status: "removed",
      projectPath: resolve(projectPath),
      targetRoot,
      fileCount: entry.files.length
    };
  });
}

/** package lock 与安装 manifest 每次 invocation 只读取一次。 */
export async function readExtensionStateSnapshot(projectPath: string): Promise<ExtensionStateSnapshot> {
  filesystemOperationObserverForTest?.("snapshot");
  const canonicalProjectPath = resolve(projectPath);
  const [installedPackages, installManifest] = await Promise.all([
    readInstalledUnityPackages(canonicalProjectPath),
    readExtensionInstallManifest(canonicalProjectPath),
  ]);
  return { projectPath: canonicalProjectPath, installedPackages, installManifest };
}

/** list 使用 manifest/version/root 的浅状态，不做 source 或 managed-file hash。 */
export async function listExtensionBundles(
  projectPath: string,
  snapshot?: ExtensionStateSnapshot,
): Promise<ExtensionStatusCheck[]> {
  const frozen = snapshot ?? await readExtensionStateSnapshot(projectPath);
  const registry = new ExtensionRegistry();
  return registry.extensionIds().map((extensionId) => {
    const extension = registry.getExtension(extensionId)!;
    const availability = resolveInstallAvailability(extension.manifest, frozen.installedPackages);
    const entry = frozen.installManifest.extensions.find((item) => item.extensionId === extensionId);
    const targetPresent = existsSync(extensionInstallRoot(frozen.projectPath, extensionId));
    const outdated = Boolean(entry && (entry.version !== extension.manifest.version || !targetPresent));
    const status = resolvePhysicalStatus(availability, entry !== undefined, outdated, [], [], []);
    return extensionStatus(extension.manifest, availability, status, "shallow", Boolean(entry && targetPresent && !outdated), outdated, {
      missingFiles: [], changedFiles: [], unknownFiles: [],
    });
  });
}

/** 普通 discovery/执行只证明一个 selected extension 的 target 投影，不读取 source tree。 */
export async function checkSelectedExtensionBundle(
  projectPath: string,
  extensionId: ExtensionId,
  options: ExtensionCheckOptions = {},
): Promise<ExtensionStatusCheck | undefined> {
  const registry = new ExtensionRegistry();
  const extension = registry.getExtension(extensionId);
  if (!extension) return undefined;
  const snapshot = options.snapshot ?? await readExtensionStateSnapshot(projectPath);
  const availability = resolveInstallAvailability(extension.manifest, snapshot.installedPackages);
  const entry = snapshot.installManifest.extensions.find((item) => item.extensionId === extensionId);
  const targetRoot = extensionInstallRoot(snapshot.projectPath, extensionId);
  filesystemOperationObserverForTest?.("target-proof", extensionId);
  const fileCheck = entry
    ? await checkManagedFiles(targetRoot, entry.files, options.absoluteDeadlineMs, extensionId)
    : { missingFiles: [], changedFiles: [], unknownFiles: [] };
  const outdated = Boolean(entry && entry.version !== extension.manifest.version);
  const installed = Boolean(entry && !outdated && fileCheck.missingFiles.length === 0 && fileCheck.changedFiles.length === 0 && fileCheck.unknownFiles.length === 0);
  const status = resolvePhysicalStatus(availability, entry !== undefined, outdated, fileCheck.missingFiles, fileCheck.changedFiles, fileCheck.unknownFiles);
  return extensionStatus(extension.manifest, availability, status, "target", installed, outdated, fileCheck);
}

/** extensions doctor 独占全 extension source/managed-file 深 hash。 */
export async function checkExtensionBundles(projectPath: string, options: ExtensionCheckOptions = {}): Promise<ExtensionStatusCheck[]> {
  const registry = new ExtensionRegistry();
  const snapshot = options.snapshot ?? await readExtensionStateSnapshot(projectPath);
  const checks: ExtensionStatusCheck[] = [];

  for (const extensionId of registry.extensionIds()) {
    requireExtensionScanDeadline(options.absoluteDeadlineMs);
    const extension = registry.getExtension(extensionId)!;
    const availability = resolveInstallAvailability(extension.manifest, snapshot.installedPackages);
    const entry = snapshot.installManifest.extensions.find((item) => item.extensionId === extensionId);
    const targetRoot = extensionInstallRoot(snapshot.projectPath, extensionId);
    const sourceRoot = extensionSourceRoot(extensionId);
    const includes = extensionUnityInstallIncludes(extension.manifest);
    const sourceProjectionComplete = existsSync(sourceRoot) && includes.every((include) => existsSync(safeSourcePath(sourceRoot, include)));
    filesystemOperationObserverForTest?.("source-proof", extensionId);
    const sourceHash = sourceProjectionComplete ? await hashBundleProjection(sourceRoot, includes, options.absoluteDeadlineMs, extensionId) : undefined;
    filesystemOperationObserverForTest?.("target-proof", extensionId);
    const fileCheck = entry
      ? await checkManagedFiles(targetRoot, entry.files, options.absoluteDeadlineMs, extensionId)
      : { missingFiles: [], changedFiles: [], unknownFiles: await listUnknownFiles(targetRoot, [], options.absoluteDeadlineMs, extensionId) };
    const outdated = Boolean(entry && (!sourceProjectionComplete || !sourceHash || entry.sourceHash !== sourceHash || entry.version !== extension.manifest.version));
    const installed = Boolean(entry && fileCheck.missingFiles.length === 0 && fileCheck.changedFiles.length === 0 && !outdated && fileCheck.unknownFiles.length === 0);
    const status = resolvePhysicalStatus(availability, entry !== undefined, outdated, fileCheck.missingFiles, fileCheck.changedFiles, fileCheck.unknownFiles);
    checks.push(extensionStatus(extension.manifest, availability, status, "full", installed, outdated, fileCheck));
  }

  return checks;
}

function extensionStatus(
  manifest: ExtensionManifest,
  availability: ExtensionAvailabilityResult,
  status: ExtensionInstallStatus,
  integrity: "shallow" | "target" | "full",
  installed: boolean,
  outdated: boolean,
  files: { missingFiles: string[]; changedFiles: string[]; unknownFiles: string[] },
): ExtensionStatusCheck {
  return {
    extensionId: manifest.id,
    name: manifest.name,
    status,
    integrity,
    dependencyInstalled: availability.status === "available",
    installed,
    outdated,
    unknownFiles: files.unknownFiles,
    missingFiles: files.missingFiles,
    changedFiles: files.changedFiles,
    providedTools: manifest.contributions.tools?.map((tool) => tool.name) ?? [],
    missingPackageIds: availability.missingPackageIds,
    nextActions: nextActionsForStatus(manifest, availability, status),
  };
}

/** 跨进程序列化同一项目的 extension 文件与 manifest 变更；活 owner 与损坏锁一律不按时间抢占。 */
async function withExtensionManifestLock<T>(projectPath: string, action: () => Promise<T>): Promise<T> {
  const lockPath = `${extensionInstallManifestPath(projectPath)}.lock`;
  const deadline = Date.now() + manifestLockTimeoutMs;
  await mkdir(dirname(lockPath), { recursive: true });

  while (true) {
    const token = randomUUID();
    const owner = { token, pid: process.pid, processStartIdentity, acquiredAt: new Date().toISOString(), heartbeatAt: new Date().toISOString() };
    try {
      const handle = await open(lockPath, "wx");
      await handle.writeFile(`${JSON.stringify(owner)}\n`, "utf8");
      await handle.close();
      try {
        return await action();
      } finally {
        const current = await readLockRecord(lockPath);
        if (current?.owner.token === token) await rm(lockPath, { force: true });
      }
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code !== "EEXIST") throw error;
      const observed = await readLockRecord(lockPath);
      if (observed && !isProcessAlive(observed.owner.pid)) {
        await recoverExitedLock(lockPath, observed);
        continue;
      }
      if (Date.now() >= deadline) {
        throw new ExtensionInstallerError("E_EXTENSION_INSTALL_LOCK_TIMEOUT", `Timed out waiting for extension install lock: ${lockPath}`, ["Wait for the other extension install/remove process to finish, then retry."]);
      }
      await new Promise((resolveDelay) => setTimeout(resolveDelay, 50));
    }
  }
}

interface LockOwner { token: string; pid: number; processStartIdentity: string; acquiredAt: string; heartbeatAt: string; }
interface LockRecord { raw: string; fingerprint: string; owner: LockOwner; }

async function readLockRecord(path: string): Promise<LockRecord | undefined> {
  try {
    const raw = await readFile(path, "utf8");
    const value = JSON.parse(raw) as Partial<LockOwner>;
    if (typeof value.token !== "string" || !value.token || !Number.isInteger(value.pid) || (value.pid as number) <= 0
      || typeof value.processStartIdentity !== "string" || !value.processStartIdentity
      || typeof value.acquiredAt !== "string" || typeof value.heartbeatAt !== "string") return undefined;
    return { raw, fingerprint: createHash("sha256").update(raw).digest("hex"), owner: value as LockOwner };
  } catch {
    return undefined;
  }
}

/** 以独占 rename 将已退出 owner 隔离到唯一 claim；绝不对原 lock 路径执行盲删。 */
async function recoverExitedLock(lockPath: string, observed: LockRecord): Promise<void> {
  const recoveryToken = randomUUID();
  const claimPath = `${lockPath}.recovery-${observed.fingerprint}`;
  try {
    const claim = await open(claimPath, "wx");
    await claim.writeFile(`${JSON.stringify({ recoveryToken, pid: process.pid, processStartIdentity, lockFingerprint: observed.fingerprint, lockToken: observed.owner.token })}\n`, "utf8");
    await claim.close();
  } catch (error) {
    // 已有 claim 表示另一个恢复者正在独占处理同一 fingerprint；保留原 lock，等待其完成。
    if ((error as NodeJS.ErrnoException).code === "EEXIST") return;
    throw error;
  }
  try {
    const reread = await readLockRecord(lockPath);
    if (!reread || reread.fingerprint !== observed.fingerprint || reread.owner.token !== observed.owner.token || isProcessAlive(reread.owner.pid)) return;
    const isolatedPath = `${lockPath}.recovered-${recoveryToken}`;
    try {
      await rename(lockPath, isolatedPath);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return;
      throw error;
    }
    const isolated = await readLockRecord(isolatedPath);
    if (!isolated || isolated.fingerprint !== observed.fingerprint || isolated.owner.token !== observed.owner.token || isProcessAlive(isolated.owner.pid)) {
      // 隔离内容异常时保留文件供人工检查，避免删除任何可能的 owner。
      return;
    }
    await rm(isolatedPath, { force: true });
  } finally {
    await rm(claimPath, { force: true }).catch(() => undefined);
  }
}

function isProcessAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
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
      files.push({ path: normalizePath(targetRelativeRoot), sha256: await hashFile(dest) });
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
      files.push({ path: rel, sha256: await hashFile(dest) });
    }
  }
}

async function removeManagedFiles(targetRoot: string, files: ManagedExtensionFile[]): Promise<void> {
  for (const file of files) {
    await rm(safeManagedPath(targetRoot, file.path), { force: true });
  }
}

async function checkManagedFiles(
  targetRoot: string,
  files: ManagedExtensionFile[],
  absoluteDeadlineMs?: number,
  extensionId?: ExtensionId,
): Promise<{ missingFiles: string[]; changedFiles: string[]; unknownFiles: string[] }> {
  const missingFiles: string[] = [];
  const changedFiles: string[] = [];
  const managed = new Set(files.map((file) => file.path));
  for (const file of files) {
    requireExtensionScanDeadline(absoluteDeadlineMs);
    const fullPath = safeManagedPath(targetRoot, file.path);
    if (!existsSync(fullPath)) {
      missingFiles.push(file.path);
      continue;
    }
    const actualHash = await hashFile(fullPath, absoluteDeadlineMs);
    if (actualHash !== file.sha256) changedFiles.push(file.path);
  }
  const unknownFiles = await listUnknownFiles(targetRoot, managed, absoluteDeadlineMs, extensionId);
  return { missingFiles, changedFiles, unknownFiles };
}

async function listUnknownFiles(
  targetRoot: string,
  managed: Iterable<string>,
  absoluteDeadlineMs?: number,
  extensionId?: ExtensionId,
): Promise<string[]> {
  requireExtensionScanDeadline(absoluteDeadlineMs);
  if (!existsSync(targetRoot)) return [];
  const managedSet = new Set(managed);
  const allFiles = await listFiles(targetRoot, targetRoot, absoluteDeadlineMs, extensionId);
  return allFiles.filter((file) => !managedSet.has(file) && !isUnityGeneratedMetaFile(file)).sort();
}

function isUnityGeneratedMetaFile(path: string): boolean {
  return path.endsWith(".meta");
}

async function listFiles(root: string, current = root, absoluteDeadlineMs?: number, extensionId?: ExtensionId): Promise<string[]> {
  requireExtensionScanDeadline(absoluteDeadlineMs);
  const entries = await readdir(current, { withFileTypes: true });
  const files: string[] = [];
  for (const entry of entries) {
    requireExtensionScanDeadline(absoluteDeadlineMs);
    filesystemOperationObserverForTest?.("unknown-tree-entry", extensionId);
    const fullPath = join(current, entry.name);
    if (entry.isDirectory()) files.push(...await listFiles(root, fullPath, absoluteDeadlineMs, extensionId));
    else if (entry.isFile()) files.push(normalizePath(relative(root, fullPath)));
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

async function hashManagedProjection(root: string, files: ManagedExtensionFile[]): Promise<string> {
  const hash = createHash("sha256");
  for (const file of files) {
    hash.update(`file:${file.path}\n`);
    hash.update(await readFile(safeManagedPath(root, file.path)));
    hash.update("\n");
  }
  return hash.digest("hex");
}

async function hashBundleProjection(
  sourceRoot: string,
  includes: string[],
  absoluteDeadlineMs?: number,
  extensionId?: ExtensionId,
): Promise<string> {
  const files: Array<{ path: string; sourcePath: string }> = [];
  for (const includePath of includes.map(normalizePath).sort()) {
    requireExtensionScanDeadline(absoluteDeadlineMs);
    const sourcePath = safeSourcePath(sourceRoot, includePath);
    if (!existsSync(sourcePath)) continue;
    const targetRelativeRoot = projectionTargetRoot(includePath);
    if ((await stat(sourcePath)).isDirectory()) await collectProjectionFiles(sourcePath, sourcePath, targetRelativeRoot, files, absoluteDeadlineMs, extensionId);
    else files.push({ path: targetRelativeRoot, sourcePath });
  }
  const hash = createHash("sha256");
  for (const file of files.sort((left, right) => left.path.localeCompare(right.path))) {
    requireExtensionScanDeadline(absoluteDeadlineMs);
    hash.update(`file:${file.path}\n`);
    hash.update(await readFile(file.sourcePath));
    hash.update("\n");
  }
  return hash.digest("hex");
}

async function collectProjectionFiles(
  dir: string,
  root: string,
  targetPrefix: string,
  files: Array<{ path: string; sourcePath: string }>,
  absoluteDeadlineMs?: number,
  extensionId?: ExtensionId,
): Promise<void> {
  requireExtensionScanDeadline(absoluteDeadlineMs);
  for (const entry of (await readdir(dir, { withFileTypes: true })).sort((left, right) => left.name.localeCompare(right.name))) {
    requireExtensionScanDeadline(absoluteDeadlineMs);
    filesystemOperationObserverForTest?.("unknown-tree-entry", extensionId);
    const sourcePath = join(dir, entry.name);
    if (entry.isDirectory()) await collectProjectionFiles(sourcePath, root, targetPrefix, files, absoluteDeadlineMs, extensionId);
    else if (entry.isFile()) {
      const relativePath = normalizePath(relative(root, sourcePath));
      files.push({ path: normalizePath(targetPrefix ? `${targetPrefix}/${relativePath}` : relativePath), sourcePath });
    }
  }
}

async function hashFile(path: string, absoluteDeadlineMs?: number): Promise<string> {
  requireExtensionScanDeadline(absoluteDeadlineMs);
  return createHash("sha256").update(await readFile(path)).digest("hex");
}

function requireExtensionScanDeadline(absoluteDeadlineMs?: number): void {
  if (absoluteDeadlineMs !== undefined && Date.now() >= absoluteDeadlineMs) {
    throw new ExtensionInstallerError("E_EXTENSION_SCAN_TIMEOUT", "Extension filesystem inspection exceeded the immutable command deadline.", [
      "Remove unexpected files or retry extensions doctor with a larger --timeout-ms after filesystem activity settles."
    ]);
  }
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
      `Then run unitycli extensions install ${manifest.id} --project <path>.`,
      "The CLI does not install optional Unity packages automatically."
    ];
  }
  if (status === "available_not_installed") {
    return [`Run unitycli extensions install ${manifest.id} --project <path> to install the optional Unity-side extension directory.`];
  }
  if (status === "outdated") {
    return [`Run unitycli extensions install ${manifest.id} --project <path> to refresh managed extension files.`];
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

function invalidInstallManifest(projectPath: string, reason: string): ExtensionInstallerError {
  return new ExtensionInstallerError(
    "E_EXTENSION_INSTALL_MANIFEST_INVALID",
    `Extension install manifest is invalid: ${reason}`,
    [`Repair or remove ${extensionInstallManifestPath(projectPath)} only after inspecting its contents.`]
  );
}

function isInstalledEntry(value: unknown): value is InstalledExtensionManifestEntry {
  const entry = value as Partial<InstalledExtensionManifestEntry>;
  return typeof entry?.extensionId === "string" && entry.extensionId.length > 0
    && typeof entry.version === "string" && entry.version.length > 0
    && typeof entry.installedAt === "string" && !Number.isNaN(Date.parse(entry.installedAt))
    && typeof entry.sourceHash === "string" && /^[a-f0-9]{64}$/i.test(entry.sourceHash)
    && Array.isArray(entry.files) && entry.files.every((file) => isManagedExtensionFile(file))
    && Array.isArray(entry.requiresUnityPackages) && entry.requiresUnityPackages.every((pkg) => typeof pkg === "string" && pkg.length > 0)
    && entry.copyRoot === "extension-directory" && entry.layout === "directory" && entry.installMode === "copy-if-package-present";
}

function isManagedExtensionFile(value: unknown): value is ManagedExtensionFile {
  const file = value as Partial<ManagedExtensionFile>;
  return typeof file?.path === "string" && file.path.length > 0 && isSafeRelativePath(file.path)
    && typeof file.sha256 === "string" && /^[a-f0-9]{64}$/i.test(file.sha256);
}

function isSafeRelativePath(path: string): boolean {
  const normalized = normalizePath(path);
  return !normalized.startsWith("../") && normalized !== ".." && !normalized.includes("/../") && !normalized.startsWith("/");
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
