import { existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { preflightPackageResolveDelivery, resolvePackageChanges } from "../../cli-commands/package.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface PackageResolveRequest {
  project?: string;
  unity?: string;
}

export interface PackageResolveObject { [key: string]: AnyJson; }

export interface PackageResolveResult {
  action?: string;
  projectPath?: string;
  status?: string;
  resolve?: string;
  liveRefresh?: PackageResolveObject;
  nextActions?: string[];
  bridge?: PackageResolveObject;
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
  result?: PackageResolveObject;
  error?: PackageResolveObject;
  cancellation?: PackageResolveObject;
  logTail?: string[];
}

/** 直接选择 live refresh、跳过或 batchmode resolve。 */
async function runPackageResolve(request: PackageResolveRequest, context: ToolContext): Promise<PackageResolveResult> {
  const projectPath = resolve(request.project ?? context.projectPath);
  const manifestPath = join(projectPath, "Packages", "manifest.json");
  if (!existsSync(manifestPath)) {
    throw new StructuredToolError("MANIFEST_NOT_FOUND", `Unity manifest not found: ${manifestPath}`, [
      "Run the command from a Unity project root or pass --project <path>."
    ], { projectPath, manifestPath, resolve: "blocked_missing_manifest" });
  }
  const delivery = {
    projectPath,
    unity: request.unity,
    wait: context.execution.wait,
    waitTimeoutMs: context.execution.waitTimeoutMs,
    timeoutMs: context.execution.timeoutMs,
    absoluteDeadlineMs: context.absoluteDeadlineMs,
    job: context.execution.job
  };
  const runtime = {
    dataDir: context.dataDir ?? context.projectPath,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute
  };
  await preflightPackageResolveDelivery(delivery, runtime);
  await context.commitEffect?.();
  const resolveRequestId = context.localJournalActive ? `${context.requestId}_package_resolve` : context.requestId;
  return resolvePackageChanges("resolve", delivery, runtime, resolveRequestId, { action: "resolve" }) as Promise<PackageResolveResult>;
}

export default defineTool<PackageResolveRequest, PackageResolveResult>({
  name: "package.resolve",
  summary: "Resolve all packages in the project manifest by running Unity PackageManager (batch job).",
  description: "Resolve all packages in the project manifest through a reachable live Editor session. Without a reachable session, ordinary direct execution fails before effects; pass --job explicitly to use the frozen Unity batch job delivery. Resolve can trigger import, compile or domain reload and never reports an intermediate job state as direct success.",
  group: "package",
  destructiveHint: true,
  sideEffects: ["session-affecting"],
  playModeSupport: "session-affecting",
  handler: "hybrid",
  supportStatus: "bridge-job",
  examples: [
    { description: "Start a package resolve job", argv: ["tools", "run", "package.resolve", "--project", ".", "--job", "--yes"] },
    { description: "Run and wait for package resolve", argv: ["tools", "run", "package.resolve", "--project", ".", "--job", "--wait", "--yes"] }
  ],
  async execute(request, context) {
    return runPackageResolve(request, context);
  }
});
