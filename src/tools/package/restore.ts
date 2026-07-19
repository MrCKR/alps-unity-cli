import { resolve } from "node:path";
import { refreshPackageEditor } from "../../cli-commands/package.js";
import { restoreDeployedPackage } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface PackageRestoreRequest {
  packageName: string;
  project?: string;
}

export interface PackageRestorePlannedEffect {
  type?: string;
  package?: string;
  from?: string;
  to?: string;
  targetPath?: string;
  fileCount?: number;
  reason?: string;
}

export interface PackageRestoreLiveRefresh {
  status?: string;
  reason?: string;
  command?: string;
  path?: string;
  code?: string;
  message?: string;
  result?: AnyJson;
}

export interface PackageRestoreResult {
  action: string;
  status: string;
  projectPath: string;
  manifestPath: string;
  packageName: string;
  targetPath: string;
  backupPath?: string;
  restoredFromBackup: boolean;
  removedDeployedPackage: boolean;
  copiedFileCount: number;
  dryRun: boolean;
  plannedEffects: PackageRestorePlannedEffect[];
  metadataPath: string;
  liveRefresh?: PackageRestoreLiveRefresh;
  nextActions?: string[];
}

/** 按 package.deploy metadata 还原或删除部署目录；安全确认由统一 dispatcher 完成。 */
async function runPackageRestore(request: PackageRestoreRequest, context: ToolContext): Promise<PackageRestoreResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  // 文件恢复与 Editor refresh 消耗同一个绝对 deadline，禁止内部重置等待窗口。
  const absoluteDeadlineMs = context.absoluteDeadlineMs ?? Date.now() + Math.max(1, context.execution.timeoutMs ?? 120_000);
  const prepared = await restoreDeployedPackage(projectPath, request.packageName, { dryRun: true });
  if (context.execution.dryRun) return { action: "restore", status: "dry_run", ...prepared };
  await context.commitEffect?.();
  const restored = await restoreDeployedPackage(projectPath, request.packageName);

  const refreshRequestId = context.localJournalActive ? `${context.requestId}_package_restore_refresh` : context.requestId;
  const liveRefresh = await refreshPackageEditor(refreshRequestId, {
    dataDir: context.dataDir ?? context.projectPath,
    bridgeExecute: context.bridgeExecute
  }, projectPath, Math.max(1, absoluteDeadlineMs - Date.now()), absoluteDeadlineMs);
  if (liveRefresh.status === "failed") {
    throw new StructuredToolError(
      typeof liveRefresh.code === "string" ? liveRefresh.code : "E_PACKAGE_REFRESH_FAILED",
      typeof liveRefresh.message === "string" ? liveRefresh.message : "Unity did not reach a terminal package refresh state after restore.",
      ["Inspect editor.state and Console diagnostics before retrying; the package files were already restored."],
      { projectPath, targetPath: restored.targetPath, packageFilesRestored: true }
    );
  }

  return {
    action: "restore",
    status: "restored",
    ...restored,
    liveRefresh,
    nextActions: liveRefresh.status === "completed"
      ? ["Wait for Unity to finish importing and compiling, then inspect editor.state or console logs."]
      : ["Open or refresh the Unity project so it imports the restored package, then inspect console diagnostics."]
  };
}

export default defineTool<PackageRestoreRequest, PackageRestoreResult>({
  name: "package.restore",
  summary: "Restore a package directory from the latest package.deploy backup.",
  description: "Restore a package previously deployed by package.deploy. If package.deploy backed up an existing package directory, package.restore replaces Packages/<packageName> with that backup. If deploy created a new package with no previous directory, restore removes the deployed package directory. Destructive: writes/deletes project package files and can trigger Unity import/compile. Supports --dry-run to preview effects without writing.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["project-file-write", "project-file-delete", "session-affecting"],
  playModeSupport: "session-affecting",
  handler: "hybrid",
  supportStatus: "bridge-job",
  timeoutMs: 120_000,
  examples: [
    { description: "Preview restoring a deployed package", argv: ["tools", "run", "package.restore", "--input", "{\"packageName\":\"com.acme.tool\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    return runPackageRestore(request, context);
  }
});
