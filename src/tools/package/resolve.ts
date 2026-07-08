import { handlePackage } from "../../cli-commands/package.js";
import type { Runtime } from "../../cli-commands/types.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface PackageResolveRequest {
  project?: string;
  unity?: string;
  wait?: boolean;
  waitTimeoutMs?: number;
  timeoutMs?: number;
  job?: boolean;
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

type PackageResolveFlags = Record<string, string | boolean>;

// 将 defineTool request 映射回旧 package.resolve handler 使用的 CLI flag 名称。
function buildPackageResolveFlags(request: PackageResolveRequest): PackageResolveFlags {
  const flags: PackageResolveFlags = {};
  if (request.project !== undefined) flags.project = request.project;
  if (request.unity !== undefined) flags.unity = request.unity;
  if (request.wait !== undefined) flags.wait = request.wait;
  if (request.waitTimeoutMs !== undefined) flags["wait-timeout-ms"] = String(request.waitTimeoutMs);
  if (request.timeoutMs !== undefined) flags["timeout-ms"] = String(request.timeoutMs);
  if (request.job !== undefined) flags.job = request.job;
  return flags;
}

// 复用旧 package.resolve 所需 runtime 字段，保持 live refresh 与 batch job 路径一致。
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

// 只包装旧 package.resolve 分支，避免复制 manifest/live-refresh/batch-job 语义。
async function runPackageResolve(request: PackageResolveRequest, context: ToolContext): Promise<PackageResolveResult> {
  const result = await handlePackage("resolve", [], buildPackageResolveFlags(request), legacyRuntimeFromContext(context), context.requestId);
  if (!result) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Tool \"package.resolve\" is not supported by the package handler.", [
      "Run unity_cli tools describe package.resolve for input requirements."
    ]);
  }
  if (!result.ok) {
    throw new StructuredToolError(
      result.error?.code ?? "PACKAGE_RESOLVE_FAILED",
      result.error?.message ?? "package.resolve failed.",
      result.error?.nextActions ?? ["Run unity_cli tools describe package.resolve for input requirements."],
      result.error?.details as Record<string, unknown> | undefined
    );
  }
  return result.data as PackageResolveResult;
}

export default defineTool<PackageResolveRequest, PackageResolveResult>({
  name: "package.resolve",
  summary: "Resolve all packages in the project manifest by running Unity PackageManager (batch job).",
  description: "Resolve all packages in the project manifest by running Unity PackageManager as a batch job. Use this after manual manifest edits or install/remove when no live Editor refresh is available; it is long-running and can trigger import, compile or domain reload. If a live Editor Bridge is reachable, the CLI may trigger a live asset refresh instead of starting batchmode. Without a Unity executable and without job=true, the tool returns a skipped success with nextActions.",
  group: "package",
  destructiveHint: false,
  sideEffects: ["session-affecting"],
  playModeSupport: "session-affecting",
  handler: "hybrid",
  supportStatus: "bridge-job",
  examples: [
    { description: "Start a package resolve job", argv: ["tools", "run", "package.resolve", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\",\"wait\":false}"] },
    { description: "Run and wait for package resolve", argv: ["tools", "run", "package.resolve", "--input", "{\"project\":\"D:/workspace/alps-unity-cli/Dev/UnityProject\",\"wait\":true}"] }
  ],
  async execute(request, context) {
    return runPackageResolve(request, context);
  }
});
