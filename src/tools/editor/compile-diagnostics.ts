import { runCompileDiagnostics } from "../../cli-commands/editor.js";
import type { Runtime } from "../../cli-commands/types.js";
import { defineTool, type ToolContext } from "../define.js";

export interface EditorCompileDiagnosticsRequest {
  refresh?: boolean;
  path?: string;
  assetPath?: string;
  forceUpdate?: boolean;
  importRecursive?: boolean;
  waitReady?: boolean;
  pollIntervalMs?: number;
  type?: "error" | "warning" | "log" | "assert" | "all";
  filter?: string;
  search?: string;
  count?: number;
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

/** 从执行控制层读取当前 instance/target selector，不把它并入业务 input。 */
function selectedInstance(context: ToolContext): string | undefined {
  const target = context.flags?.target;
  const instance = context.flags?.instance;
  return typeof target === "string" && target.length > 0
    ? target
    : typeof instance === "string" && instance.length > 0 ? instance : undefined;
}

/** 使用 typed request 编排 refresh、wait-ready 与 Console 读取。 */
async function executeCompileDiagnostics(request: EditorCompileDiagnosticsRequest, context: ToolContext): Promise<EditorCompileDiagnosticsResult> {
  const runtime = {
    dataDir: context.dataDir ?? context.projectPath,
    store: context.jobStore,
    runner: context.jobRunner,
    bridgeExecute: context.bridgeExecute,
    bridgeEndpoint: context.bridgeEndpoint,
    projectTools: context.projectTools,
    projectContexts: context.projectContexts
  } as Runtime;
  return runCompileDiagnostics({
    refresh: request.refresh,
    path: request.path ?? request.assetPath,
    forceUpdate: request.forceUpdate,
    importRecursive: request.importRecursive,
    waitReady: request.waitReady,
    waitTimeoutMs: context.execution.waitTimeoutMs,
    pollIntervalMs: request.pollIntervalMs,
    type: request.type,
    filter: request.filter ?? request.search,
    count: request.count,
    dryRun: context.execution.dryRun,
    job: context.execution.job,
    bridgeEndpoint: context.bridgeEndpoint,
    instance: selectedInstance(context)
  }, runtime, context.requestId) as unknown as Promise<EditorCompileDiagnosticsResult>;
}

export default defineTool<EditorCompileDiagnosticsRequest, EditorCompileDiagnosticsResult>({
  name: "editor.compile-diagnostics",
  summary: "Refresh/wait live Editor, then return Console compile diagnostics for C# edit loops.",
  description: "Refresh the live Unity Editor AssetDatabase, wait for the Editor to become idle, and read recent Console errors in one call. This editor-only live-bridge loop is unavailable in PlayMode because its default AssetDatabase refresh can trigger import, compile, and domain reload. It does not clear Console entries, save project files, or start a batchmode Editor. The CLI orchestrates asset.refresh, context read editor.state, and console.get, retrying transient states within --wait-timeout-ms. Use refresh=false to only wait/read Console, or pass path/assetPath to import a specific Assets/* path. Use --dry-run to preview the steps. --job is rejected with E_TOOL_JOB_UNSUPPORTED; use editor.refresh for closed-project batch jobs.",
  group: "editor",
  destructiveHint: false,
  supportsDryRun: true,
  playModeSupport: "editor-only",
  sideEffects: ["asset-refresh", "script-compile", "domain-reload", "console-read"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Refresh, wait, and read recent compiler diagnostics", argv: ["tools", "run", "editor.compile-diagnostics", "--wait-timeout-ms", "45000", "--input", "{\"count\":100}"] },
    { description: "Import one changed script before reading diagnostics", argv: ["tools", "run", "editor.compile-diagnostics", "--wait-timeout-ms", "180000", "--input", "{\"path\":\"Assets/Scripts/Player.cs\",\"forceUpdate\":true}"] },
    { description: "Only wait and read Console without triggering refresh", argv: ["tools", "run", "editor.compile-diagnostics", "--input", "{\"refresh\":false,\"count\":50}"] },
    { description: "Preview planned refresh/wait/console steps", argv: ["tools", "run", "editor.compile-diagnostics", "--dry-run", "--input", "{\"path\":\"Assets/Scripts/Player.cs\"}"] }
  ],
  async execute(request, context) {
    return executeCompileDiagnostics(request, context);
  }
});
