import { startUnityJob } from "../../cli-commands/unity-job.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface EditorRefreshRequest {
  compile?: boolean;
  project?: string;
  unity?: string;
}

export interface EditorRefreshJobResult {
  action?: string;
  compile?: boolean;
}

export interface EditorRefreshError {
  code: string;
  message: string;
  details?: AnyJson;
}

export interface EditorRefreshCancellation {
  mode: string;
  description: string;
  cancelCommand: string;
  domainReloadObserved: boolean;
}

export interface EditorRefreshResult {
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
  result?: EditorRefreshJobResult;
  error?: EditorRefreshError;
  cancellation: EditorRefreshCancellation;
  logTail?: string[];
}

type JobFlags = Record<string, string | boolean>;

// 将 defineTool request 映射回现有 batchmode job helper 使用的 CLI flag 名称。
function buildEditorRefreshFlags(request: EditorRefreshRequest, execution: ToolContext["execution"]): JobFlags {
  const flags: JobFlags = {};
  if (request.compile !== undefined) flags.compile = request.compile;
  if (request.project !== undefined) flags.project = request.project;
  if (request.unity !== undefined) flags.unity = request.unity;
  if (execution.wait !== undefined) flags.wait = execution.wait;
  if (execution.timeoutMs !== undefined) flags["timeout-ms"] = String(execution.timeoutMs);
  if (execution.waitTimeoutMs !== undefined) flags["wait-timeout-ms"] = String(execution.waitTimeoutMs);
  return flags;
}

// editor.refresh 只包装旧 batch refresh job,确保 executeMethod、wait 和错误 envelope 不漂移。
async function startEditorRefreshJob(request: EditorRefreshRequest, context: ToolContext): Promise<EditorRefreshResult> {
  if (!context.jobStore || !context.jobRunner || !context.dataDir) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Tool \"editor.refresh\" requires a job runner runtime.", [
      "Run unity_cli tools describe editor.refresh for details."
    ]);
  }

  const compile = request.compile === true;
  const result = await startUnityJob("refresh", buildEditorRefreshFlags(request, context.execution), {
    dataDir: context.dataDir,
    store: context.jobStore,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute,
    bridgeEndpoint: context.bridgeEndpoint
  }, context.requestId, ["-executeMethod", compile ? "UnityCli.UnityCliBatch.RefreshAndCompile" : "UnityCli.UnityCliBatch.Refresh"], {
    action: "refresh",
    compile
  });

  if (!result.ok) {
    throw new StructuredToolError(
      result.error?.code ?? "EDITOR_REFRESH_FAILED",
      result.error?.message ?? "Unity refresh job failed to start.",
      result.error?.nextActions ?? ["Run unity_cli tools describe editor.refresh for input requirements."],
      result.error?.details as Record<string, unknown> | undefined
    );
  }
  return result.data as EditorRefreshResult;
}

export default defineTool<EditorRefreshRequest, EditorRefreshResult>({
  name: "editor.refresh",
  summary: "Closed-project batch refresh job; prefer asset.refresh for an already open live Editor.",
  description: "Trigger AssetDatabase.Refresh in a separate Unity batch job, optionally requesting script compilation and waiting for readiness. Prefer asset.refresh for an already open live Editor; do not use this against a project already open in Unity because open projects can return UNITY_BUSY due to UnityLockfile.",
  group: "editor",
  destructiveHint: false,
  playModeSupport: "editor-only",
  sideEffects: ["asset-refresh", "script-compile", "domain-reload"],
  handler: "job",
  supportStatus: "batch-job",
  examples: [
    { description: "Refresh assets in batchmode", argv: ["tools", "run", "editor.refresh", "--project", ".", "--wait"] },
    { description: "Refresh and request compilation", argv: ["tools", "run", "editor.refresh", "--project", "./Dev/UnityProject", "--wait", "--input", "{\"compile\":true}"] }
  ],
  async execute(request, context) {
    return startEditorRefreshJob(request, context);
  }
});
