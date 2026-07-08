import { handleCode } from "../../runtime-code.js";
import type { CliResult } from "../../types.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface CodeExecRequest {
  file?: string;
  stdin?: boolean;
  code?: string;
  channel?: string;
  refs?: string[];
  define?: string[];
  args?: CodeExecJsonObject;
  timeoutMs?: number;
  artifactDir?: string;
  project?: string;
  unity?: string;
  job?: boolean;
  wait?: boolean;
  waitTimeoutMs?: number;
  bridgeEndpoint?: string;
  target?: string;
  instance?: string;
  retry?: number;
}

export interface CodeExecJsonObject {}

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

export interface CodeExecResult {
  command?: string;
  dryRun?: boolean;
  preview?: CodeExecPreview;
  plannedEffects?: CodeExecPlannedEffect[];
  artifactDir?: string;
  manifestPath?: string;
  status?: string;
  jobId?: string;
}

type CodeExecFlags = Record<string, string | boolean>;

// 将 defineTool request 和原始 CLI flags 合并回旧 Runtime Code handler 使用的 flag 形态。
function buildCodeExecFlags(request: CodeExecRequest, context: ToolContext): CodeExecFlags {
  const rawRequest = request as Record<string, unknown>;
  const flags: CodeExecFlags = { ...(context.flags ?? {}) };

  setFlag(flags, "file", stringValue(rawRequest.file));
  setFlag(flags, "stdin", booleanValue(rawRequest.stdin));
  setFlag(flags, "code", stringValue(rawRequest.code));
  setFlag(flags, "channel", stringValue(rawRequest.channel));
  setFlag(flags, "refs", stringListValue(rawRequest.refs));
  setFlag(flags, "define", stringListValue(rawRequest.define));
  setFlag(flags, "args", argsValue(rawRequest.args));
  setFlag(flags, "timeout-ms", numberValue(rawRequest.timeoutMs));
  setFlag(flags, "artifact-dir", stringValue(rawRequest.artifactDir ?? rawRequest["artifact-dir"]));
  setFlag(flags, "project", stringValue(rawRequest.project));
  setFlag(flags, "unity", stringValue(rawRequest.unity));
  setFlag(flags, "job", booleanValue(rawRequest.job));
  setFlag(flags, "wait", booleanValue(rawRequest.wait));
  setFlag(flags, "wait-timeout-ms", numberValue(rawRequest.waitTimeoutMs ?? rawRequest["wait-timeout-ms"]));
  setFlag(flags, "bridge-endpoint", stringValue(rawRequest.bridgeEndpoint ?? rawRequest["bridge-endpoint"]));
  setFlag(flags, "target", stringValue(rawRequest.target));
  setFlag(flags, "instance", stringValue(rawRequest.instance));
  setFlag(flags, "retry", numberValue(rawRequest.retry));

  return flags;
}

function setFlag(flags: CodeExecFlags, key: string, value: string | boolean | undefined): void {
  if (value === undefined || key in flags) return;
  flags[key] = value;
}

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function booleanValue(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}

function numberValue(value: unknown): string | undefined {
  return typeof value === "number" && Number.isFinite(value) ? String(value) : undefined;
}

function stringListValue(value: unknown): string | undefined {
  if (typeof value === "string") return value;
  if (!Array.isArray(value)) return undefined;
  const parts = value.map((part) => typeof part === "string" ? part.trim() : "").filter(Boolean);
  return parts.length > 0 ? parts.join(",") : undefined;
}

function argsValue(value: unknown): string | undefined {
  if (typeof value === "string") return value;
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  return JSON.stringify(value);
}

// 旧 handler 返回完整 CLI envelope；defineTool 只暴露 data，错误转为结构化异常交由框架包 envelope。
function unwrapCodeExecResult(result: CliResult): CodeExecResult {
  if (!result.ok) {
    throw new StructuredToolError(
      result.error?.code ?? "CODE_EXEC_FAILED",
      result.error?.message ?? "code.exec failed.",
      result.error?.nextActions ?? ["Run unity_cli tools describe code.exec for input requirements."],
      result.error?.details as Record<string, unknown> | undefined
    );
  }
  return result.data as CodeExecResult;
}

function legacyRuntimeFromContext(context: ToolContext, flags: CodeExecFlags): Parameters<typeof handleCode>[2] {
  if ((flags.channel === "batchmode" || flags.job === true) && !context.jobRunner) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Tool \"code.exec\" requires a job runner runtime for batchmode execution.", [
      "Run unity_cli tools describe code.exec for channel and job requirements."
    ]);
  }

  return {
    dataDir: context.dataDir ?? context.projectPath,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute,
    bridgeEndpoint: context.bridgeEndpoint,
    projectTools: context.projectTools,
    projectContexts: context.projectContexts
  } as Parameters<typeof handleCode>[2];
}

// 执行可信 C# Runtime Code；本包装只负责协议适配，校验/确认/artifact/job 继续由旧 handler 承担。
async function runCodeExec(request: CodeExecRequest, context: ToolContext): Promise<CodeExecResult> {
  const flags = buildCodeExecFlags(request, context);
  const runtime = legacyRuntimeFromContext(context, flags);
  return unwrapCodeExecResult(await handleCode("exec", flags, runtime, context.requestId));
}

export default defineTool<CodeExecRequest, CodeExecResult>({
  name: "code.exec",
  summary: "Execute trusted C# code in the Unity Editor live bridge or a Unity batchmode job.",
  description: "Execute trusted C# code in the Unity Editor live bridge or a Unity batchmode job. This is high risk: code can modify scenes/assets, call Unity APIs, write artifacts, hang the Editor, or trigger import/compile. Use --dry-run first to inspect source hash/channel/artifactDir; real execution requires --yes.",
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
    { description: "Dry-run inline code before execution", argv: ["tools", "run", "code.exec", "--code", "Debug.Log(\"hello\");", "--dry-run"] },
    { description: "Execute a trusted code file after review", argv: ["tools", "run", "code.exec", "--file", "script.cs", "--yes"] },
    { description: "Start a batchmode code job", argv: ["tools", "run", "code.exec", "--file", "script.cs", "--channel", "batchmode", "--project", ".", "--yes"] }
  ],
  async execute(request, context) {
    return runCodeExec(request, context);
  }
});
