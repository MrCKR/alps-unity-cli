import { resolve } from "node:path";
import { refreshOpenEditor } from "../../cli-commands/package.js";
import type { Runtime } from "../../cli-commands/types.js";
import { deployPackageSource } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageDeployRequest {
  source: string;
  packageName?: string;
  project?: string;
  dryRun?: boolean;
  bridgeEndpoint?: string;
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
  endpoint?: string;
  command?: string;
  path?: string;
  code?: string;
  message?: string;
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

type PackageDeployFlags = Record<string, string | boolean>;

// 构造旧 package live refresh helper 使用的 CLI flag 形态。
function buildPackageDeployFlags(request: PackageDeployRequest): PackageDeployFlags {
  const flags: PackageDeployFlags = {};
  if (request.project !== undefined) flags.project = request.project;
  if (request.bridgeEndpoint !== undefined) flags["bridge-endpoint"] = request.bridgeEndpoint;
  return flags;
}

// 复用旧 refreshOpenEditor 所需 runtime 字段，保持 live refresh 结果不漂移。
function legacyRuntimeFromContext(context: ToolContext): Runtime {
  return {
    dataDir: context.dataDir ?? context.projectPath,
    store: context.jobStore,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute,
    bridgeEndpoint: context.bridgeEndpoint,
    projectTools: context.projectTools,
    projectContexts: context.projectContexts
  } as Runtime;
}

// 部署本地 package 源目录；destructive 确认由 tools run 分发层在进入 defineTool 前完成。
async function runPackageDeploy(request: PackageDeployRequest, context: ToolContext): Promise<PackageDeployResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  const deployed = await deployPackageSource(projectPath, request.source, {
    packageName: request.packageName,
    dryRun: request.dryRun === true
  });

  if (deployed.dryRun) {
    return { action: "deploy", status: "dry_run", ...deployed };
  }

  const liveRefresh = await refreshOpenEditor(
    context.requestId,
    buildPackageDeployFlags(request),
    legacyRuntimeFromContext(context)
  );

  return {
    action: "deploy",
    status: "deployed",
    ...deployed,
    liveRefresh,
    nextActions: liveRefresh.status === "triggered"
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
  examples: [
    { description: "Preview deploying a local package", argv: ["tools", "run", "package.deploy", "--input", "{\"source\":\"D:/workspace/my-package\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    return runPackageDeploy(request, context);
  }
});
