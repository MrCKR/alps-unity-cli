import { resolve } from "node:path";
import { resolvePackageChanges } from "../../cli-commands/package.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageResolveRequest {
  project?: string;
  unity?: string;
}

export interface PackageResolveObject {}

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
  return resolvePackageChanges("resolve", {
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
  }, context.requestId, { action: "resolve" }) as Promise<PackageResolveResult>;
}

export default defineTool<PackageResolveRequest, PackageResolveResult>({
  name: "package.resolve",
  summary: "Resolve all packages in the project manifest by running Unity PackageManager (batch job).",
  description: "Resolve all packages in the project manifest by running Unity PackageManager as a batch job. Use this after manual manifest edits or install/remove when no live Editor refresh is available; it is long-running and can trigger import, compile or domain reload. If a live Editor Bridge is reachable, the CLI may trigger a live asset refresh instead of starting batchmode. Without a Unity executable and without --job, the tool returns a skipped success with nextActions.",
  group: "package",
  destructiveHint: true,
  sideEffects: ["session-affecting"],
  playModeSupport: "session-affecting",
  handler: "hybrid",
  supportStatus: "bridge-job",
  examples: [
    { description: "Start a package resolve job", argv: ["tools", "run", "package.resolve", "--project", ".", "--yes"] },
    { description: "Run and wait for package resolve", argv: ["tools", "run", "package.resolve", "--project", ".", "--wait", "--yes"] }
  ],
  async execute(request, context) {
    return runPackageResolve(request, context);
  }
});
