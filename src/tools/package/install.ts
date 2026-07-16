import { resolve } from "node:path";
import { resolvePackageChanges } from "../../cli-commands/package.js";
import { installPackage, parsePackageSpec, validatePackageName } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageInstallRequest {
  name: string;
  version?: string;
  project?: string;
  unity?: string;
}

export interface PackageInstallObject {}

export interface PackageInstallResult {
  action?: string;
  package?: string;
  projectPath?: string;
  manifestPath?: string;
  installedVersion?: string;
  previousVersion?: string;
  changed?: boolean;
  status?: string;
  resolve?: string;
  liveRefresh?: PackageInstallObject;
  nextActions?: string[];
  bridge?: PackageInstallObject;
  jobId?: string;
  kind?: string;
  requestId?: string;
  unityPath?: string;
  logPath?: string;
  pid?: number;
  exitCode?: number;
  createdAt?: string;
  updatedAt?: string;
  startedAt?: string;
  finishedAt?: string;
  command?: string[];
  result?: PackageInstallObject;
  error?: PackageInstallObject;
  cancellation?: PackageInstallObject;
  logTail?: string[];
}

/** 写入 manifest dependency，并按运行环境执行 resolve。 */
async function runPackageInstall(request: PackageInstallRequest, context: ToolContext): Promise<PackageInstallResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  const spec = parsePackageSpec(validatePackageName(request.name), request.version);
  const installed = await installPackage(projectPath, spec);
  return resolvePackageChanges("install", {
    projectPath,
    unity: request.unity,
    wait: context.execution.wait,
    waitTimeoutMs: context.execution.waitTimeoutMs,
    timeoutMs: context.execution.timeoutMs,
    job: context.execution.job
  }, {
    dataDir: context.dataDir ?? context.projectPath,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute,
    bridgeEndpoint: context.bridgeEndpoint
  }, context.requestId, { action: "install", package: spec.name, ...installed }) as Promise<PackageInstallResult>;
}

export default defineTool<PackageInstallRequest, PackageInstallResult>({
  name: "package.install",
  summary: "Install or update a package in the project manifest and optionally resolve.",
  description: "Install or update a package in the project manifest and optionally resolve. Destructive: manifest change + possible Unity resolve/domain reload. Requires an explicit version and explicit confirmation; refuses numeric downgrades to avoid accidental package rollback. If no live Editor Bridge is reachable, pass unity/job options to run Unity PackageManager resolve, or accept a skipped resolve success and let Unity import on next open.",
  group: "package",
  destructiveHint: true,
  sideEffects: ["manifest-change", "session-affecting"],
  playModeSupport: "session-affecting",
  handler: "hybrid",
  supportStatus: "bridge-job",
  examples: [
    { description: "Install a package", argv: ["tools", "run", "package.install", "--input", "{\"name\":\"com.unity.textmeshpro\",\"version\":\"3.0.7\"}"] }
  ],
  async execute(request, context) {
    return runPackageInstall(request, context);
  }
});
