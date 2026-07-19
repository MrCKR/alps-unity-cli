import { executeRuntimeCode } from "../../runtime-code.js";
import type { AnyJson } from "../../schema/any-json.js";
import { defineTool, type ToolContext } from "../define.js";

export interface CodeExecRequest {
  file?: string;
  stdin?: boolean;
  code?: string;
  channel?: string;
  refs?: string[];
  define?: string[];
  args?: CodeExecJsonObject;
  artifactDir?: string;
  project?: string;
  unity?: string;
}

export interface CodeExecJsonObject {
  [key: string]: AnyJson;
}

export interface CodeExecPreview {
  sourceKind?: string;
  sourcePath?: string;
  sourceSha256?: string;
  sourceSizeBytes?: number;
  channel?: string;
  timeoutMs?: number;
  artifactDir?: string;
  projectPath?: string;
  args?: CodeExecJsonObject;
}

export interface CodeExecPlannedEffect {
  type?: string;
  channel?: string;
  artifactDir?: string;
  sideEffects?: string;
}

export type CodeExecResult = Record<string, AnyJson>;

/** 读取执行控制 flag；这些字段不进入 code.exec 业务 Request。 */
function executionFlag(context: ToolContext, name: string): string | undefined {
  const value = context.flags?.[name];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

/** 执行可信 C# Runtime Code；安全确认由统一 dispatcher 完成。 */
async function runCodeExec(request: CodeExecRequest, context: ToolContext): Promise<CodeExecResult> {
  return executeRuntimeCode({
    ...request,
    args: request.args as Record<string, unknown> | undefined,
    timeoutMs: context.execution.timeoutMs,
    job: context.execution.job,
    wait: context.execution.wait,
    waitTimeoutMs: context.execution.waitTimeoutMs,
    dryRun: context.execution.dryRun,
    instance: executionFlag(context, "instance")
  }, {
    dataDir: context.dataDir ?? context.projectPath,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute
  }, context.requestId) as Promise<CodeExecResult>;
}

export default defineTool<CodeExecRequest, CodeExecResult>({
  name: "code.exec",
  summary: "Execute trusted C# code through a deferred job or batchmode delivery.",
  description: "Execute trusted C# code only through --job, channel=batchmode, or the frozen batch delivery. Ordinary live direct execution is refused because arbitrary side effects have no independent business-state proof. This is high risk: code can modify scenes/assets, call Unity APIs, write artifacts, hang the Editor, or trigger import/compile. Use --dry-run first to inspect source hash/channel/artifactDir; real execution requires --yes.",
  group: "code",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["user-code-execution", "artifact-write", "asset-import", "scene-modify", "compile"],
  playModeSupport: "session-affecting",
  permissions: ["execute-user-code", "write-artifacts", "unity-editor-api"],
  handler: "hybrid",
  supportStatus: "bridge-job",
  target: "editor",
  examples: [
    { description: "Dry-run inline code before execution", argv: ["tools", "run", "code.exec", "--code", "Debug.Log(\"hello\"); return null;", "--dry-run"] },
    { description: "Start a deferred inline code job", argv: ["tools", "run", "code.exec", "--code", "return 1;", "--job", "--yes"] },
    { description: "Start a batchmode code job", argv: ["tools", "run", "code.exec", "--file", "script.cs", "--channel", "batchmode", "--project", ".", "--yes"] }
  ],
  async execute(request, context) {
    return runCodeExec(request, context);
  }
});
