import { resolve } from "node:path";
import { startUnityJob } from "../../cli-commands/unity-job.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface TestRunRequest {
  platform?: "EditMode" | "PlayMode";
  project?: string;
  unity?: string;
  wait?: boolean;
  timeoutMs?: number;
  waitTimeoutMs?: number;
}

export interface TestRunObject {}

export interface TestRunError {
  code: string;
  message: string;
  details?: unknown;
}

export interface TestRunCancellation {
  mode: string;
  description: string;
  cancelCommand: string;
  domainReloadObserved: boolean;
}

export interface TestRunResult {
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
  command: string[];
  result?: TestRunObject;
  error?: TestRunError;
  cancellation: TestRunCancellation;
  logTail?: string[];
}

type JobFlags = Record<string, string | boolean>;

// 将 defineTool request 映射回现有 job helper 使用的 CLI flag 名称。
function buildTestRunFlags(request: TestRunRequest): JobFlags {
  const flags: JobFlags = {};
  if (request.platform !== undefined) flags.platform = request.platform;
  if (request.project !== undefined) flags.project = request.project;
  if (request.unity !== undefined) flags.unity = request.unity;
  if (request.wait !== undefined) flags.wait = request.wait;
  if (request.timeoutMs !== undefined) flags["timeout-ms"] = String(request.timeoutMs);
  if (request.waitTimeoutMs !== undefined) flags["wait-timeout-ms"] = String(request.waitTimeoutMs);
  return flags;
}

// 复用旧 batchmode job 入口，确保 jobId、command、resultPath 和 wait 语义不漂移。
async function startTestRunJob(request: TestRunRequest, context: ToolContext): Promise<TestRunResult> {
  if (!context.jobStore || !context.jobRunner || !context.dataDir) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Tool \"test.run\" requires a job runner runtime.", [
      "Run unity_cli tools describe test.run for details."
    ]);
  }

  const flags = buildTestRunFlags(request);
  const platform = request.platform ?? "EditMode";
  const resultPath = resolve(context.dataDir, "results", `${context.requestId}-test-results.xml`);
  const result = await startUnityJob("test", flags, {
    dataDir: context.dataDir,
    store: context.jobStore,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute,
    bridgeEndpoint: context.bridgeEndpoint
  }, context.requestId, ["-runTests", "-testPlatform", platform, "-testResults", resultPath], {
    action: "test",
    platform,
    resultPath
  });

  if (!result.ok) {
    throw new StructuredToolError(
      result.error?.code ?? "TEST_RUN_FAILED",
      result.error?.message ?? "Unity test job failed to start.",
      result.error?.nextActions ?? ["Run unity_cli tools describe test.run for input requirements."],
      result.error?.details as Record<string, unknown> | undefined
    );
  }
  return result.data as TestRunResult;
}

export default defineTool<TestRunRequest, TestRunResult>({
  name: "test.run",
  summary: "Run Unity EditMode or PlayMode tests as a batch job.",
  description: "Run Unity EditMode or PlayMode tests as a batch job. Returns a job ID that can be polled for status.",
  group: "test",
  destructiveHint: false,
  sideEffects: ["session-affecting"],
  playModeSupport: "session-affecting",
  handler: "job",
  supportStatus: "batch-job",
  examples: [{ description: "Run EditMode tests", argv: ["tools", "run", "test.run", "--project", ".", "--platform", "EditMode"] }],
  async execute(request, context) {
    return startTestRunJob(request, context);
  }
});
