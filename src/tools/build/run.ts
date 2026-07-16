import { resolve } from "node:path";
import { startUnityJob } from "../../cli-commands/unity-job.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";

const BUILD_TARGETS = ["StandaloneWindows64", "StandaloneWindows", "StandaloneOSX", "StandaloneLinux64", "WebGL", "iOS", "Android"] as const;

type BuildTarget = typeof BUILD_TARGETS[number];
const BUILD_TARGET_SET = new Set<string>(BUILD_TARGETS);

export interface BuildRunRequest {
  target?: BuildTarget;
  output?: string;
  targets?: string;
  project?: string;
  unity?: string;
}

export interface BuildRunObject {}

export interface BuildRunError {
  code: string;
  message: string;
  details?: AnyJson;
}

export interface BuildRunCancellation {
  mode: string;
  description: string;
  cancelCommand: string;
  domainReloadObserved: boolean;
}

export interface BuildRunJobResult {
  action?: string;
  target?: string;
  outputPath?: string;
  resultPath?: string;
}

export interface BuildRunJobSummary {
  jobId: string;
  kind: string;
  status: string;
  requestId: string;
  projectPath: string;
  unityPath: string;
  logPath: string;
  pid?: number;
  exitCode?: number;
  createdAt: string;
  updatedAt: string;
  startedAt?: string;
  finishedAt?: string;
  command?: string[];
  result?: BuildRunJobResult;
  error?: BuildRunError;
  cancellation: BuildRunCancellation;
  logTail?: string[];
}

export interface BuildRunBatchSummary {
  queued: number;
  failedToQueue: number;
}

export interface BuildRunResult {
  command?: AnyJson;
  jobId?: string;
  kind?: string;
  status?: string;
  requestId?: string;
  projectPath?: string;
  unityPath?: string;
  logPath?: string;
  pid?: number;
  exitCode?: number;
  createdAt?: string;
  updatedAt?: string;
  startedAt?: string;
  finishedAt?: string;
  result?: BuildRunJobResult;
  error?: BuildRunError;
  cancellation?: BuildRunCancellation;
  logTail?: string[];
  mode?: string;
  targetCount?: number;
  targets?: string[];
  summary?: BuildRunBatchSummary;
  jobs?: BuildRunJobSummary[];
}

type JobFlags = Record<string, string | boolean>;

// 将 defineTool request 映射回现有 batchmode job helper 使用的 CLI flag 名称。
function buildRunFlags(request: BuildRunRequest, execution: ToolContext["execution"]): JobFlags {
  const flags: JobFlags = {};
  if (request.target !== undefined) flags.target = request.target;
  if (request.output !== undefined) flags.output = request.output;
  if (request.targets !== undefined) flags.targets = request.targets;
  if (request.project !== undefined) flags.project = request.project;
  if (request.unity !== undefined) flags.unity = request.unity;
  if (execution.wait !== undefined) flags.wait = execution.wait;
  if (execution.timeoutMs !== undefined) flags["timeout-ms"] = String(execution.timeoutMs);
  if (execution.waitTimeoutMs !== undefined) flags["wait-timeout-ms"] = String(execution.waitTimeoutMs);
  return flags;
}

// 对 targets 逗号列表逐项套用 target 同一组枚举,避免无效平台被排进 Unity job。
function assertBuildTarget(target: string, field: "target" | "targets"): BuildTarget {
  if (BUILD_TARGET_SET.has(target)) return target as BuildTarget;
  const allowed = BUILD_TARGETS.join(", ");
  throw new StructuredToolError(
    "E_SCHEMA_INVALID",
    `Invalid input value(s): ${field} entries must be one of ${allowed}.`,
    [
      "Run unity_cli tools describe build.run to see allowed input values.",
      `${field} entries must be one of ${allowed}.`
    ],
    { field, value: target, allowedValues: [...BUILD_TARGETS] }
  );
}

// 保留旧 build.run 的 targets 解析语义:字符串逗号分隔,结构化数组也兼容。
function buildTargets(request: BuildRunRequest): BuildTarget[] {
  const rawTargets = (request as { targets?: unknown }).targets;
  if (Array.isArray(rawTargets)) {
    const targets = rawTargets.filter((item): item is string => typeof item === "string" && item.length > 0);
    return targets.length > 0 ? targets.map((target) => assertBuildTarget(target, "targets")) : [request.target ?? "StandaloneWindows64"];
  }
  if (typeof rawTargets === "string" && rawTargets.length > 0) {
    const targets = rawTargets.split(",").map((target) => target.trim()).filter(Boolean);
    return targets.length > 0 ? targets.map((target) => assertBuildTarget(target, "targets")) : [request.target ?? "StandaloneWindows64"];
  }
  return [request.target ?? "StandaloneWindows64"];
}

// 多 target 构建先替换输出路径中的 target 标记，否则按 target 追加子目录。
function outputForTarget(output: string | undefined, target: string): string | undefined {
  if (!output) return undefined;
  return output.includes("{target}") ? output.replace(/\{target\}/g, target) : `${output.replace(/[\\/]$/, "")}/${target}`;
}

// 启动单个 build job,保持 executeMethod、outputPath/resultPath 和 wait 语义不漂移。
async function startBuildJob(target: string, request: BuildRunRequest, context: ToolContext, output?: string): Promise<BuildRunJobSummary> {
  if (!context.jobStore || !context.jobRunner || !context.dataDir) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Tool \"build.run\" requires a job runner runtime.", [
      "Run unity_cli tools describe build.run for details."
    ]);
  }

  const outputPath = resolve(output ?? "Builds/Windows/UnityCliBuild.exe");
  const result = await startUnityJob("build", buildRunFlags(request, context.execution), {
    dataDir: context.dataDir,
    store: context.jobStore,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute,
    bridgeEndpoint: context.bridgeEndpoint
  }, context.requestId, ["-buildTarget", target, "-executeMethod", "UnityCli.UnityCliBatch.BuildProject", "-unityCliBuildOutput", outputPath], {
    action: "build",
    target,
    outputPath,
    resultPath: outputPath
  });

  if (!result.ok) {
    throw new StructuredToolError(
      result.error?.code ?? "BUILD_RUN_FAILED",
      result.error?.message ?? "Unity build job failed to start.",
      result.error?.nextActions ?? ["Run unity_cli tools describe build.run for input requirements."],
      result.error?.details as Record<string, unknown> | undefined
    );
  }
  return result.data as BuildRunJobSummary;
}

// 复用旧 build.run 的单 job / multi-target batch 输出形态。
async function startBuildRun(request: BuildRunRequest, context: ToolContext): Promise<BuildRunResult> {
  const targets = buildTargets(request);
  if (targets.length <= 1) {
    return startBuildJob(targets[0] ?? "StandaloneWindows64", request, context, request.output);
  }

  const jobs: BuildRunJobSummary[] = [];
  for (const target of targets) {
    jobs.push(await startBuildJob(target, request, context, outputForTarget(request.output, target)));
  }

  return {
    command: "build.run",
    mode: "batch",
    targetCount: targets.length,
    targets,
    summary: { queued: jobs.length, failedToQueue: 0 },
    jobs
  };
}

export default defineTool<BuildRunRequest, BuildRunResult>({
  name: "build.run",
  summary: "Trigger a Unity batchmode player build as a long-running job.",
  description: "Trigger a Unity batchmode player build as a long-running job. Returns a job ID; poll job status/logs instead of rerunning. This writes build output and can import/compile assets.",
  group: "build",
  destructiveHint: true,
  sideEffects: ["build-output-write", "asset-import", "compile"],
  playModeSupport: "editor-only",
  handler: "job",
  supportStatus: "batch-job",
  examples: [
    { description: "Start a Windows build job", argv: ["tools", "run", "build.run", "--project", ".", "--input", "{\"target\":\"StandaloneWindows64\",\"output\":\"Builds/App.exe\"}", "--yes"] },
    { description: "Start multi-platform build jobs", argv: ["tools", "run", "build.run", "--input", "{\"targets\":\"StandaloneWindows64,WebGL\",\"output\":\"Builds/{target}/Player\"}", "--yes"] }
  ],
  async execute(request, context) {
    return startBuildRun(request, context);
  }
});
