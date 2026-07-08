import { resolve } from "node:path";
import { maybeResolvePackage } from "../../cli-commands/package.js";
import type { Runtime } from "../../cli-commands/types.js";
import { removePackage, validatePackageName } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface PackageRemoveRequest {
  name: string;
  force?: boolean;
  project?: string;
  unity?: string;
  wait?: boolean;
  waitTimeoutMs?: number;
  timeoutMs?: number;
  job?: boolean;
  dryRun?: boolean;
}

export interface PackageRemoveDependent {
  name?: string;
  requiredVersion?: string;
}

export interface PackageRemovePlannedEffect {
  type?: string;
  package?: string;
  removedVersion?: string;
  dependents?: PackageRemoveDependent[];
  forced?: boolean;
  blocked?: boolean;
}

export interface PackageRemoveObject {}

export interface PackageRemoveResult {
  action?: string;
  package?: string;
  projectPath?: string;
  manifestPath?: string;
  removedVersion?: string;
  dependents?: PackageRemoveDependent[];
  forced?: boolean;
  dryRun?: boolean;
  blocked?: boolean;
  plannedEffects?: PackageRemovePlannedEffect[];
  status?: string;
  resolve?: string;
  liveRefresh?: PackageRemoveObject;
  nextActions?: string[];
  bridge?: PackageRemoveObject;
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
  result?: PackageRemoveObject;
  error?: PackageRemoveObject;
  cancellation?: PackageRemoveObject;
  logTail?: string[];
}

type PackageRemoveFlags = Record<string, string | boolean>;

// 将 defineTool request 映射回 package resolve helper 使用的 CLI flag 名称。
function buildPackageRemoveFlags(request: PackageRemoveRequest): PackageRemoveFlags {
  const flags: PackageRemoveFlags = {};
  if (request.project !== undefined) flags.project = request.project;
  if (request.unity !== undefined) flags.unity = request.unity;
  if (request.wait !== undefined) flags.wait = request.wait;
  if (request.waitTimeoutMs !== undefined) flags["wait-timeout-ms"] = String(request.waitTimeoutMs);
  if (request.timeoutMs !== undefined) flags["timeout-ms"] = String(request.timeoutMs);
  if (request.job !== undefined) flags.job = request.job;
  if (request.dryRun === true) flags["dry-run"] = true;
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
function unwrapPackageRemoveResult(result: Awaited<ReturnType<typeof maybeResolvePackage>>): PackageRemoveResult {
  if (!result.ok) {
    throw new StructuredToolError(
      result.error?.code ?? "PACKAGE_REMOVE_FAILED",
      result.error?.message ?? "package.remove failed.",
      result.error?.nextActions ?? ["Run unity_cli tools describe package.remove for input requirements."],
      result.error?.details as Record<string, unknown> | undefined
    );
  }
  return result.data as PackageRemoveResult;
}

// 删除 manifest dependency；dry-run 直接返回计划，真实删除后沿用 resolve/live refresh/job 后续语义。
async function runPackageRemove(request: PackageRemoveRequest, context: ToolContext): Promise<PackageRemoveResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  const name = validatePackageName(request.name);
  const removal = await removePackage(projectPath, name, { force: request.force === true, dryRun: request.dryRun === true });
  if (removal.dryRun) {
    return {
      action: "remove",
      package: name,
      projectPath,
      ...removal,
      status: removal.blocked ? "blocked" : "dry_run"
    };
  }

  return unwrapPackageRemoveResult(await maybeResolvePackage(
    "remove",
    buildPackageRemoveFlags(request),
    legacyRuntimeFromContext(context),
    context.requestId,
    projectPath,
    { action: "remove", package: name, ...removal }
  ));
}

export default defineTool<PackageRemoveRequest, PackageRemoveResult>({
  name: "package.remove",
  summary: "Remove a package from the project manifest and optionally resolve.",
  description: "Remove a package from the project manifest and optionally resolve. Destructive: manifest change + possible Unity resolve/domain reload. Requires explicit confirmation; inspect with package.list first. By default it blocks when packages-lock.json shows other packages depending on the target; pass force=true only when you intentionally want Unity PackageManager to resolve that dependency break. Supports --dry-run to return plannedEffects without writing manifest.json.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["manifest-change", "session-affecting"],
  playModeSupport: "session-affecting",
  handler: "hybrid",
  supportStatus: "bridge-job",
  examples: [
    { description: "Preview removing a package", argv: ["tools", "run", "package.remove", "--input", "{\"name\":\"com.unity.textmeshpro\"}", "--dry-run"] }
  ],
  async execute(request, context) {
    return runPackageRemove(request, context);
  }
});
