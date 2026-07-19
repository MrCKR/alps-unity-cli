import { resolve } from "node:path";
import { refreshPackageEditor } from "../../cli-commands/package.js";
import { deployPackageSource } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface PackageDeployRequest {
  source: string;
  packageName?: string;
  project?: string;
}

export interface PackageDeployPlannedEffect {
  type?: string;
  package?: string;
  from?: string;
  to?: string;
  targetPath?: string;
  packageVersion?: string | null;
  fileCount?: number;
  reason?: string;
}

export interface PackageDeployLiveRefresh {
  status?: string;
  reason?: string;
  command?: string;
  path?: string;
  code?: string;
  message?: string;
  result?: AnyJson;
}

export interface PackageDeployResult {
  action: string;
  status: string;
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
  plannedEffects: PackageDeployPlannedEffect[];
  metadataPath: string;
  liveRefresh?: PackageDeployLiveRefresh;
  nextActions?: string[];
}

/** 部署本地 package 源目录；安全确认由统一 dispatcher 完成。 */
async function runPackageDeploy(request: PackageDeployRequest, context: ToolContext): Promise<PackageDeployResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  // 文件复制与 Editor refresh 共享调用方冻结的总预算，不能在进入 supervisor 时重置 timeout。
  const absoluteDeadlineMs = context.absoluteDeadlineMs ?? Date.now() + Math.max(1, context.execution.timeoutMs ?? 120_000);
  const prepared = await deployPackageSource(projectPath, request.source, {
    packageName: request.packageName,
    dryRun: true
  });
  if (context.execution.dryRun) return { action: "deploy", status: "dry_run", ...prepared };
  await context.commitEffect?.();
  const deployed = await deployPackageSource(projectPath, request.source, { packageName: request.packageName });

  const refreshRequestId = context.localJournalActive ? `${context.requestId}_package_deploy_refresh` : context.requestId;
  const liveRefresh = await refreshPackageEditor(refreshRequestId, {
    dataDir: context.dataDir ?? context.projectPath,
    bridgeExecute: context.bridgeExecute
  }, projectPath, Math.max(1, absoluteDeadlineMs - Date.now()), absoluteDeadlineMs);
  if (liveRefresh.status === "failed") {
    throw new StructuredToolError(
      typeof liveRefresh.code === "string" ? liveRefresh.code : "E_PACKAGE_REFRESH_FAILED",
      typeof liveRefresh.message === "string" ? liveRefresh.message : "Unity did not reach a terminal package refresh state after deployment.",
      ["Inspect editor.state and Console diagnostics before retrying; the package files were already deployed."],
      { projectPath, targetPath: deployed.targetPath, packageFilesDeployed: true }
    );
  }

  return {
    action: "deploy",
    status: "deployed",
    ...deployed,
    liveRefresh,
    nextActions: liveRefresh.status === "completed"
      ? ["Wait for Unity to finish importing and compiling, then inspect editor.state or console logs."]
      : ["Open or refresh the Unity project so it imports the deployed package, then inspect console diagnostics."]
  };
}

export default defineTool<PackageDeployRequest, PackageDeployResult>({
  name: "package.deploy",
  summary: "Deploy a local Unity package source folder into Packages/<packageName> with an automatic restore backup.",
  description: "Deploy a local Unity package source folder into the target project's Packages/<packageName> directory. The source folder must contain package.json with name/version and must be outside the target project's Packages directory. If a package directory already exists, package.deploy backs it up under Packages/.unity_cli_deploy_backups before copying the source. Destructive: writes project package files and can trigger Unity import/compile. Supports --dry-run to preview backup/copy effects without writing.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["project-file-write", "project-file-delete", "session-affecting"],
  playModeSupport: "session-affecting",
  handler: "hybrid",
  supportStatus: "bridge-job",
  timeoutMs: 120_000,
  examples: [
    { description: "Preview deploying a local package", argv: ["tools", "run", "package.deploy", "--input", "{\"source\":\"./Packages/my-package\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    return runPackageDeploy(request, context);
  }
});
