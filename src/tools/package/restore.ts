import { resolve } from "node:path";
import { refreshPackageEditor } from "../../cli-commands/package.js";
import { restoreDeployedPackage } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

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
  endpoint?: string;
  command?: string;
  path?: string;
  code?: string;
  message?: string;
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
  const restored = await restoreDeployedPackage(projectPath, request.packageName, { dryRun: context.execution.dryRun === true });

  if (restored.dryRun) {
    return { action: "restore", status: "dry_run", ...restored };
  }

  const liveRefresh = await refreshPackageEditor(context.requestId, context.bridgeEndpoint, {
    dataDir: context.dataDir ?? context.projectPath,
    bridgeExecute: context.bridgeExecute,
    bridgeEndpoint: context.bridgeEndpoint
  });

  return {
    action: "restore",
    status: "restored",
    ...restored,
    liveRefresh,
    nextActions: liveRefresh.status === "triggered"
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
  examples: [
    { description: "Preview restoring a deployed package", argv: ["tools", "run", "package.restore", "--input", "{\"packageName\":\"com.acme.tool\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    return runPackageRestore(request, context);
  }
});
