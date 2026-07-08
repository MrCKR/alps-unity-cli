import { resolve } from "node:path";
import { maybeResolvePackage } from "../../cli-commands/package.js";
import type { Runtime } from "../../cli-commands/types.js";
import { installPackage, parsePackageSpec, validatePackageName } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface PackageInstallRequest {
  name: string;
  version?: string;
  project?: string;
  unity?: string;
  wait?: boolean;
  waitTimeoutMs?: number;
  timeoutMs?: number;
  job?: boolean;
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

type PackageInstallFlags = Record<string, string | boolean>;

// 将 defineTool request 映射回 package resolve helper 使用的 CLI flag 名称。
function buildPackageInstallFlags(request: PackageInstallRequest): PackageInstallFlags {
  const flags: PackageInstallFlags = {};
  if (request.project !== undefined) flags.project = request.project;
  if (request.unity !== undefined) flags.unity = request.unity;
  if (request.wait !== undefined) flags.wait = request.wait;
  if (request.waitTimeoutMs !== undefined) flags["wait-timeout-ms"] = String(request.waitTimeoutMs);
  if (request.timeoutMs !== undefined) flags["timeout-ms"] = String(request.timeoutMs);
  if (request.job !== undefined) flags.job = request.job;
  return flags;
}

// 复用旧 package resolve 所需 runtime 字段，保持 live refresh 与 batch job 路径一致。
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

// 将 resolve helper 的 CliResult 转为 defineTool 成功数据或结构化错误。
function unwrapPackageInstallResult(result: Awaited<ReturnType<typeof maybeResolvePackage>>): PackageInstallResult {
  if (!result.ok) {
    throw new StructuredToolError(
      result.error?.code ?? "PACKAGE_INSTALL_FAILED",
      result.error?.message ?? "package.install failed.",
      result.error?.nextActions ?? ["Run unity_cli tools describe package.install for input requirements."],
      result.error?.details as Record<string, unknown> | undefined
    );
  }
  return result.data as PackageInstallResult;
}

// 写入 manifest dependency；后续沿用 resolve/live refresh/job 语义。
async function runPackageInstall(request: PackageInstallRequest, context: ToolContext): Promise<PackageInstallResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  const spec = parsePackageSpec(validatePackageName(request.name), request.version);
  const installed = await installPackage(projectPath, spec);
  return unwrapPackageInstallResult(await maybeResolvePackage(
    "install",
    buildPackageInstallFlags(request),
    legacyRuntimeFromContext(context),
    context.requestId,
    projectPath,
    { action: "install", package: spec.name, ...installed }
  ));
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
