import { resolve } from "node:path";
import { resolvePackageChanges } from "../../cli-commands/package.js";
import { PackageManagerError, removePackage, validatePackageName } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface PackageRemoveRequest {
  name: string;
  force?: boolean;
  project?: string;
  unity?: string;
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

export interface PackageRemoveObject { [key: string]: AnyJson; }

export interface PackageRemoveResult {
  action?: string;
  package?: string;
  projectPath?: string;
  manifestPath?: string;
  dependencyRemoved?: boolean;
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

/** 删除 manifest dependency；dry-run 返回计划，实际删除后执行 resolve。 */
async function runPackageRemove(request: PackageRemoveRequest, context: ToolContext): Promise<PackageRemoveResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  const name = validatePackageName(request.name);
  const removal = await removePackage(projectPath, name, { force: request.force === true, dryRun: true });
  if (context.execution.dryRun) {
    return {
      action: "remove",
      package: name,
      projectPath,
      ...removal,
      status: removal.blocked ? "blocked" : "dry_run"
    };
  }
  if (removal.blocked) {
    throw new PackageManagerError("PACKAGE_DEPENDENTS_BLOCKED", `Package ${name} is required by: ${removal.dependents.map((entry) => entry.name).join(", ")}.`, ["Remove dependent packages first, or pass force=true only when the dependency break is intentional."]);
  }

  await context.commitEffect?.();
  const committedRemoval = await removePackage(projectPath, name, { force: request.force === true });
  const resolveRequestId = context.localJournalActive ? `${context.requestId}_package_remove_resolve` : context.requestId;
  return resolvePackageChanges("remove", {
    projectPath,
    unity: request.unity,
    wait: context.execution.wait,
    waitTimeoutMs: context.execution.waitTimeoutMs,
    timeoutMs: context.execution.timeoutMs,
    absoluteDeadlineMs: context.absoluteDeadlineMs,
    job: context.execution.job
  }, {
    dataDir: context.dataDir ?? context.projectPath,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute
  }, resolveRequestId, { action: "remove", package: name, ...committedRemoval }) as Promise<PackageRemoveResult>;
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
