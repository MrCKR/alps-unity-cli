import { handleEditor } from "../../cli-commands/editor.js";
import type { Runtime } from "../../cli-commands/types.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface EditorCompileDiagnosticsRequest {
  refresh?: boolean;
  path?: string;
  assetPath?: string;
  forceUpdate?: boolean;
  importRecursive?: boolean;
  waitReady?: boolean;
  waitTimeoutMs?: number;
  pollIntervalMs?: number;
  type?: "error" | "warning" | "log" | "assert" | "all";
  filter?: string;
  search?: string;
  count?: number;
  dryRun?: boolean;
  job?: boolean;
}

export interface EditorCompileDiagnosticsObject {}

export interface EditorCompileDiagnosticsCompile {
  ok?: boolean;
  hasErrors?: boolean;
  errorCount?: number;
  consoleErrorCount?: number;
  diagnostics?: EditorCompileDiagnosticsObject[];
}

export interface EditorCompileDiagnosticsResult {
  command: string;
  dryRun?: boolean;
  status: "clean" | "compile_errors" | "console_errors" | "wait_timeout" | "dry_run";
  refresh?: EditorCompileDiagnosticsObject;
  wait?: EditorCompileDiagnosticsObject;
  console?: EditorCompileDiagnosticsObject;
  compile?: EditorCompileDiagnosticsCompile;
  attempts?: EditorCompileDiagnosticsObject;
  plannedEffects?: EditorCompileDiagnosticsObject[];
  nextActions?: string[];
}

type CompileDiagnosticsFlags = Record<string, string | boolean>;

// 将 defineTool request 映射回旧 live compile-diagnostics 编排入口使用的 CLI flag 名称。
function buildCompileDiagnosticsFlags(request: EditorCompileDiagnosticsRequest): CompileDiagnosticsFlags {
  const flags: CompileDiagnosticsFlags = {};
  if (request.refresh !== undefined) flags.refresh = request.refresh;
  if (request.path !== undefined) flags.path = request.path;
  if (request.assetPath !== undefined) flags["asset-path"] = request.assetPath;
  if (request.forceUpdate !== undefined) flags["force-update"] = request.forceUpdate;
  if (request.importRecursive !== undefined) flags["import-recursive"] = request.importRecursive;
  if (request.waitReady !== undefined) flags["wait-ready"] = request.waitReady;
  if (request.waitTimeoutMs !== undefined) flags["wait-timeout-ms"] = String(request.waitTimeoutMs);
  if (request.pollIntervalMs !== undefined) flags["poll-interval-ms"] = String(request.pollIntervalMs);
  if (request.type !== undefined) flags.type = request.type;
  if (request.filter !== undefined) flags.filter = request.filter;
  if (request.search !== undefined) flags.search = request.search;
  if (request.count !== undefined) flags.count = String(request.count);
  if (request.dryRun !== undefined) flags["dry-run"] = request.dryRun;
  if (request.job !== undefined) flags.job = request.job;
  return flags;
}

// 复用旧 handler 所需 runtime 字段；本工具不启动 job,但保留类型兼容与测试注入入口。
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

// 只包装旧 live 编排 handler,确保 transient retry、dry-run、子步骤失败 envelope 不漂移。
async function runCompileDiagnostics(request: EditorCompileDiagnosticsRequest, context: ToolContext): Promise<EditorCompileDiagnosticsResult> {
  const result = await handleEditor("compile-diagnostics", buildCompileDiagnosticsFlags(request), legacyRuntimeFromContext(context), context.requestId);
  if (!result) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Tool \"editor.compile-diagnostics\" is not supported by the editor handler.", [
      "Run unity_cli tools describe editor.compile-diagnostics for input requirements."
    ]);
  }
  if (!result.ok) {
    throw new StructuredToolError(
      result.error?.code ?? "EDITOR_COMPILE_DIAGNOSTICS_FAILED",
      result.error?.message ?? "editor.compile-diagnostics failed.",
      result.error?.nextActions ?? ["Run unity_cli tools describe editor.compile-diagnostics for input requirements."],
      result.error?.details as Record<string, unknown> | undefined
    );
  }
  return result.data as EditorCompileDiagnosticsResult;
}

export default defineTool<EditorCompileDiagnosticsRequest, EditorCompileDiagnosticsResult>({
  name: "editor.compile-diagnostics",
  summary: "Refresh/wait live Editor, then return Console compile diagnostics for C# edit loops.",
  description: "Refresh the live Unity Editor AssetDatabase, wait for the Editor to become idle, and read recent Console errors in one call. This is the live-bridge compile diagnostics loop for AI workflows after editing C# files: it does not clear Console entries, does not save project files, and does not start a batchmode Editor. The CLI orchestrates existing live Bridge calls (asset.refresh, context read editor.state, console.get), retrying transient compile/import/domain-reload states within waitTimeoutMs. Use refresh=false to only wait/read Console, or pass path/assetPath to import a specific Assets/* path before collecting diagnostics. Use dryRun=true or --dry-run to preview the planned refresh/wait/console steps without calling the Bridge. job=true/--job is rejected with E_TOOL_JOB_UNSUPPORTED because live Console diagnostics require a live Editor; use editor.refresh for batchmode refresh jobs.",
  group: "editor",
  destructiveHint: false,
  supportsDryRun: true,
  playModeSupport: "session-affecting",
  sideEffects: ["asset-refresh", "script-compile", "domain-reload", "console-read"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Refresh, wait, and read recent compiler diagnostics", argv: ["tools", "run", "editor.compile-diagnostics", "--input", "{\"waitTimeoutMs\":45000,\"count\":100}"] },
    { description: "Import one changed script before reading diagnostics", argv: ["tools", "run", "editor.compile-diagnostics", "--input", "{\"path\":\"Assets/Scripts/Player.cs\",\"forceUpdate\":true}"] },
    { description: "Only wait and read Console without triggering refresh", argv: ["tools", "run", "editor.compile-diagnostics", "--input", "{\"refresh\":false,\"count\":50}"] },
    { description: "Preview planned refresh/wait/console steps", argv: ["tools", "run", "editor.compile-diagnostics", "--dry-run", "--input", "{\"path\":\"Assets/Scripts/Player.cs\"}"] }
  ],
  async execute(request, context) {
    return runCompileDiagnostics(request, context);
  }
});
