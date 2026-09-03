import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { inspectPackageDeploymentFiles, restoreDeployedPackage, type PackageRestoreResult as PackageRestoreFileResult } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { runPackageFileMutationWorker } from "./file-mutation-worker.js";
import { bindLivePackageProject, runLivePackageResolve, type LiveResolveResult, type PackageFileMutationBundle } from "./live-resolve.js";
import { projectRelativePath, publicPackageEffects } from "./public-result.js";

export interface PackageRestoreRequest { packageName: string; }
export interface PackageRestorePlannedEffect { type?: string; package?: string; from?: string; to?: string; targetPath?: string; fileCount?: number; reason?: string; }
export interface PackageRestoreFilesProof { proven: true; targetPath: string; targetHash?: string; copiedFileCount: number; restoredFromBackup: boolean; removedDeployedPackage: boolean; backupPath?: string; metadataPath: string; metadataHash: string; }
export interface PackageRestoreResult {
  action: string; status: string; manifestPath: string; packageName: string; targetPath: string;
  backupPath?: string; restoredFromBackup: boolean; removedDeployedPackage: boolean; copiedFileCount: number; dryRun: boolean;
  plannedEffects: PackageRestorePlannedEffect[]; metadataPath: string; files?: PackageRestoreFilesProof; resolve?: LiveResolveResult;
}
interface RestorePlan extends PackageRestoreFileResult {
  expectedTargetExists: boolean;
  expectedTargetHash?: string;
  expectedRecordIdentity: string;
  expectedBackupExists: boolean;
  expectedBackupHash?: string;
}

/** restore 删除/还原、UPM fence 与 PackageInfo readback 共用一个公开 root。 */
async function runPackageRestore(request: PackageRestoreRequest, context: ToolContext): Promise<PackageRestoreResult> {
  if (context.execution.dryRun) {
    await bindLivePackageProject(context, "restore");
    const projectPath = resolve(context.projectPath);
    const prepared = await restoreDeployedPackage(projectPath, request.packageName, { dryRun: true });
    return publicRestoreResult(projectPath, "dry_run", prepared);
  }
  return runLivePackageResolve<PackageRestoreResult>(context, "restore", { packageName: request.packageName }, {
    async prepare(signal) {
      const projectPath = resolve(context.projectPath);
      signal.throwIfAborted();
      const prepared = await restoreDeployedPackage(projectPath, request.packageName, { dryRun: true });
      const observed = await inspectPackageDeploymentFiles(projectPath, prepared.packageName, prepared.targetPath, prepared.backupPath);
      signal.throwIfAborted();
      return {
        ...prepared,
        expectedTargetExists: observed.targetExists,
        expectedTargetHash: observed.targetHash,
        expectedRecordIdentity: observed.recordIdentity,
        expectedBackupExists: observed.backupExists,
        expectedBackupHash: observed.backupHash,
      } as unknown as Record<string, unknown>;
    },
    async apply(plan, signal, stopAtMs) {
      const projectPath = resolve(context.projectPath);
      const expected = restorePlan(plan);
      const restored = await runPackageFileMutationWorker("restore", projectPath, plan, stopAtMs, signal) as unknown as PackageRestoreFileResult;
      return restoreBundle(projectPath, expected, restored);
    },
    async recover(plan, signal) {
      const projectPath = resolve(context.projectPath);
      signal.throwIfAborted();
      const expected = restorePlan(plan);
      const bundle = await restoreBundle(projectPath, expected, { ...expected, dryRun: false });
      signal.throwIfAborted();
      return bundle;
    },
  });
}

async function restoreBundle(projectPath: string, plan: RestorePlan, restored: PackageRestoreFileResult): Promise<PackageFileMutationBundle> {
  if (restored.packageName !== plan.packageName || restored.targetPath !== plan.targetPath || restored.restoredFromBackup !== plan.restoredFromBackup) throw new Error("Restored package does not match its durable file plan.");
  const observed = await inspectPackageDeploymentFiles(projectPath, plan.packageName, plan.targetPath, plan.backupPath);
  if (observed.record) throw new Error("Package deployment metadata still contains the restored package.");
  if (plan.restoredFromBackup) {
    if (!plan.expectedBackupHash || !observed.targetExists || observed.targetHash !== plan.expectedBackupHash || observed.targetFileCount !== plan.copiedFileCount || observed.backupExists) throw new Error("Restored package target does not match the committed backup tree.");
  } else if (observed.targetExists || observed.backupExists) {
    throw new Error("Remove-only package restore left a target or backup directory.");
  }
  const manifestHash = await hash(plan.manifestPath);
  const files: PackageRestoreFilesProof = {
    proven: true, targetPath: plan.targetPath, copiedFileCount: observed.targetFileCount,
    restoredFromBackup: plan.restoredFromBackup, removedDeployedPackage: plan.removedDeployedPackage,
    metadataPath: observed.metadataPath, metadataHash: observed.metadataHash,
    ...(observed.targetHash ? { targetHash: observed.targetHash } : {}), ...(plan.backupPath ? { backupPath: plan.backupPath } : {}),
  };
  // 文件内容已在 files.prove 时由 Editor fresh readback 精确验证；Resolve 后 Unity 可合法生成 .meta。
  const predicate = plan.restoredFromBackup
    ? { name: plan.packageName, present: true, resolvedPath: plan.targetPath }
    : { name: plan.packageName, present: false };
  return {
    expectedManifestSha256: manifestHash,
    predicates: [predicate],
    filesProof: files as unknown as Record<string, unknown>,
    publicResultSeed: publicRestoreResult(projectPath, "restored", restored, files) as unknown as Record<string, unknown>,
  };
}

/** 从内部 durable 文件结果构造公开 DTO；绝对路径只保留在 journal 与完成证明中。 */
function publicRestoreResult(
  projectPath: string,
  status: string,
  restored: PackageRestoreFileResult,
  files?: PackageRestoreFilesProof,
): PackageRestoreResult {
  const publicFiles = files ? {
    ...files,
    targetPath: projectRelativePath(projectPath, files.targetPath, "files.targetPath"),
    metadataPath: projectRelativePath(projectPath, files.metadataPath, "files.metadataPath"),
    ...(files.backupPath ? { backupPath: projectRelativePath(projectPath, files.backupPath, "files.backupPath") } : {}),
  } : undefined;
  return {
    action: "restore",
    status,
    manifestPath: projectRelativePath(projectPath, restored.manifestPath, "manifestPath"),
    packageName: restored.packageName,
    targetPath: projectRelativePath(projectPath, restored.targetPath, "targetPath"),
    ...(restored.backupPath ? { backupPath: projectRelativePath(projectPath, restored.backupPath, "backupPath") } : {}),
    restoredFromBackup: restored.restoredFromBackup,
    removedDeployedPackage: restored.removedDeployedPackage,
    copiedFileCount: restored.copiedFileCount,
    dryRun: restored.dryRun,
    plannedEffects: publicPackageEffects(projectPath, restored.plannedEffects) as PackageRestorePlannedEffect[],
    metadataPath: projectRelativePath(projectPath, restored.metadataPath, "metadataPath"),
    ...(publicFiles ? { files: publicFiles } : {}),
  };
}

function restorePlan(value: Record<string, unknown>): RestorePlan {
  for (const key of ["projectPath", "manifestPath", "packageName", "targetPath", "metadataPath"])
    if (typeof value[key] !== "string") throw new Error(`Durable package.restore file plan is missing ${key}.`);
  if (typeof value.restoredFromBackup !== "boolean" || typeof value.removedDeployedPackage !== "boolean" || typeof value.copiedFileCount !== "number"
    || typeof value.expectedTargetExists !== "boolean" || typeof value.expectedRecordIdentity !== "string"
    || typeof value.expectedBackupExists !== "boolean" || !Array.isArray(value.plannedEffects)) throw new Error("Durable package.restore file plan is corrupt.");
  return value as unknown as RestorePlan;
}
async function hash(path: string): Promise<string> { return createHash("sha256").update(await readFile(path)).digest("hex"); }

export default defineTool<PackageRestoreRequest, PackageRestoreResult>({
  name: "package.restore", summary: "Restore a package directory from the latest package.deploy backup.",
  description: "EditMode only. Restore the exact package.deploy backup, or remove the deployed directory when no predecessor existed. The durable worker applies one file plan, then Unity PackageManager Resolve, PackageInfo, target path, and tree-hash readback prove the imported terminal. dryRun previews effects; timeout never authorizes replay.",
  group: "package", destructiveHint: true, supportsDryRun: true, sideEffects: ["project-file-write", "project-file-delete", "asset-import", "session-affecting"], playModeSupport: "editor-only", handler: "hybrid", supportStatus: "live-bridge", timeoutMs: 120_000,
  examples: [{ description: "Preview restoring a deployed package", argv: ["tools", "run", "package.restore", "--input", "{\"packageName\":\"com.acme.tool\"}", "--dry-run"] }],
  async execute(request, context) { return runPackageRestore(request, context); }
});
