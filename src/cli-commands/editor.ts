import { getBoolean, getNumber, getString } from "../args.js";
import { resolve } from "node:path";
import { bridgeCommandRegistry, executeBridgePayload } from "../bridge/index.js";
import { dismissUnityLaunchDialog, DISMISS_BUTTON_LABEL, LAUNCH_DIALOG_TITLE_FRAGMENTS } from "../launch-dialog-dismiss.js";
import { durationMsFrom, failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { runUnityJob, startUnityJob } from "./unity-job.js";
import { ensurePackageBridge } from "../package-manager.js";
import type { BridgeRuntime } from "../bridge/index.js";
import type { JobRunner } from "../job-runner.js";
import { StructuredToolError } from "../tools/errors.js";

export interface AssetRefreshServiceRequest {
  path?: string;
  forceUpdate?: boolean;
  importRecursive?: boolean;
  project?: string;
  unity?: string;
  wait?: boolean;
  waitTimeoutMs?: number;
  bridgeEndpoint?: string;
  instance?: string;
  job?: boolean;
  dryRun?: boolean;
}

export interface AssetRefreshRuntime extends BridgeRuntime {
  dataDir: string;
  runner?: JobRunner;
}

/** 根据 job 选项直接执行 live Bridge 或 batchmode Bridge job。 */
export async function executeAssetRefresh(request: AssetRefreshServiceRequest, runtime: AssetRefreshRuntime, requestId: string): Promise<Record<string, unknown>> {
  const input = compact({ path: request.path, forceUpdate: request.forceUpdate, importRecursive: request.importRecursive, dryRun: request.dryRun });
  if (!request.job) {
    const result = await executeBridgePayload(requestId, "project-tool.run", { name: "asset.refresh", input }, compact({
      "bridge-endpoint": request.bridgeEndpoint ?? runtime.bridgeEndpoint,
      instance: request.instance,
      "dry-run": request.dryRun
    }) as Record<string, string | boolean>, runtime);
    if (!result.ok) {
      throw StructuredToolError.from(result.error ?? { code: "ASSET_REFRESH_FAILED", message: "asset.refresh failed." });
    }
    return (result.data ?? {}) as Record<string, unknown>;
  }

  if (!runtime.runner) {
    throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "Tool \"asset.refresh\" requires a job runner runtime for batchmode execution.");
  }
  const projectPath = resolve(request.project ?? process.cwd());
  const bridge = await ensurePackageBridge(projectPath);
  const payload = {
    command: "project-tool.run",
    requestId,
    input: { name: "asset.refresh", input },
    ...(request.dryRun ? { execution: { dryRun: true } } : {})
  };
  return runUnityJob({
    kind: "bridge",
    requestId,
    project: projectPath,
    unity: request.unity,
    args: ["-executeMethod", "UnityCli.UnityCliBatch.RunBridgeCommand", "-unityCliBridgeRequest", JSON.stringify(payload)],
    result: {
      command: "asset.refresh",
      bridgeCommand: "project-tool.run",
      mode: "bridge-job",
      execution: { channel: "batchmode", exitState: "queued" },
      supportsJob: true,
      args: input,
      dryRun: request.dryRun === true,
      bridge
    },
    wait: request.wait,
    waitTimeoutMs: request.waitTimeoutMs
  }, runtime.runner);
}

const DEFAULT_COMPILE_WAIT_TIMEOUT_MS = 45_000;
const MAX_COMPILE_WAIT_TIMEOUT_MS = 180_000;
const DEFAULT_COMPILE_POLL_INTERVAL_MS = 1_000;
const COMPILE_DIAGNOSTIC_RETRY_CODES = new Set(["BRIDGE_CONNECTION_FAILED", "E_TARGET_STALE", "E_TARGET_UNREACHABLE", "E_UNITY_DOMAIN_RELOAD"]);

export function handleEditor(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> | undefined {
  if (command === "refresh") {
    return startUnityJob("refresh", flags, runtime, requestId, refreshArgs(flags), { action: "refresh", compile: getBoolean(flags, "compile") });
  }

  if (command === "compile-diagnostics") {
    return handleCompileDiagnosticsCli(flags, runtime, requestId);
  }

  if (command === "launch-dialog.dismiss") {
    return handleLaunchDialogDismiss(flags, requestId);
  }

  return undefined;
}

/** 处理 Unity 启动错误/Safe Mode 桌面弹窗 dismiss；不依赖 live Bridge。 */
async function handleLaunchDialogDismiss(flags: Record<string, string | boolean>, requestId: string): Promise<CliResult> {
  const processId = getNumber(flags, "process-id") ?? getNumber(flags, "processId") ?? getNumber(flags, "pid");
  if (getBoolean(flags, "dry-run")) {
    return success(requestId, {
      command: "editor.launch-dialog.dismiss",
      dryRun: true,
      status: "dry_run",
      dismissed: false,
      processId,
      plannedEffects: [{ type: "desktop-dialog-click", process: "Unity", titleFragments: LAUNCH_DIALOG_TITLE_FRAGMENTS, button: DISMISS_BUTTON_LABEL }],
      nextActions: ["Rerun without --dry-run to poll the Windows desktop and click Ignore when a matching Unity launch dialog is visible."]
    });
  }

  return success(requestId, await dismissUnityLaunchDialog({
    timeoutMs: getNumber(flags, "timeout-ms") ?? getNumber(flags, "timeoutMs"),
    intervalMs: getNumber(flags, "interval-ms") ?? getNumber(flags, "intervalMs"),
    processId
  }));
}

interface BridgeStepResult {
  result: CliResult;
  attempts: number;
}

interface ConsoleItem {
  index?: number;
  type?: string;
  message?: string;
  file?: string;
  line?: number;
}

export interface CompileDiagnosticsRequest {
  refresh?: boolean;
  path?: string;
  forceUpdate?: boolean;
  importRecursive?: boolean;
  waitReady?: boolean;
  waitTimeoutMs?: number;
  pollIntervalMs?: number;
  type?: string;
  filter?: string;
  count?: number;
  dryRun?: boolean;
  job?: boolean;
  bridgeEndpoint?: string;
  instance?: string;
  retry?: number;
}

/** 编排 live Editor 编译诊断：刷新资产、等待空闲、读取 Console。 */
export async function runCompileDiagnostics(input: CompileDiagnosticsRequest, runtime: Runtime, requestId: string): Promise<Record<string, unknown>> {
  if (input.job) {
    throw new StructuredToolError("E_TOOL_JOB_UNSUPPORTED", "editor.compile-diagnostics is a live Editor orchestration tool and does not support --job.", [
      "Remove --job and run against a live Unity Editor instance.",
      "Use editor.refresh for batchmode refresh jobs when you do not need live Console diagnostics."
    ]);
  }
  const waitTimeoutMs = clampInteger(input.waitTimeoutMs, DEFAULT_COMPILE_WAIT_TIMEOUT_MS, 0, MAX_COMPILE_WAIT_TIMEOUT_MS);
  const pollIntervalMs = clampInteger(input.pollIntervalMs, DEFAULT_COMPILE_POLL_INTERVAL_MS, 0, 5_000);
  const count = clampInteger(input.count, 100, 1, 500);
  const refresh = input.refresh ?? true;
  const waitReady = input.waitReady ?? true;
  const path = input.path;
  const filter = input.filter;
  const consoleType = input.type ?? "error";
  const forceUpdate = input.forceUpdate;
  const importRecursive = input.importRecursive;
  const execution = compact({ "bridge-endpoint": input.bridgeEndpoint, instance: input.instance, retry: input.retry === undefined ? undefined : String(input.retry) }) as Record<string, string | boolean>;

  if (input.dryRun) {
    return {
      command: "editor.compile-diagnostics",
      dryRun: true,
      status: "dry_run",
      plannedEffects: compileDiagnosticsPlannedEffects({ refresh, waitReady, path, forceUpdate, importRecursive, waitTimeoutMs, consoleType, filter, count }),
      nextActions: ["Rerun without --dry-run to refresh assets, wait for Unity to become idle, and read Console compile diagnostics."]
    };
  }

  const modeStep = await executeBridgeWithTransientRetry("playmode-guard", "context.read", { name: "editor.state", input: {} }, execution, runtime, requestId, waitTimeoutMs, pollIntervalMs);
  if (!modeStep.result.ok) {
    const error = modeStep.result.error;
    throw error ? StructuredToolError.from(error) : new StructuredToolError("E_EDITOR_STATE_UNAVAILABLE", "Unable to read Editor state before compile diagnostics.");
  }
  const editorState = recordData(bridgeStepData(modeStep.result.data).state);
  const playMode = recordData(editorState.playMode);
  if (playMode.isPlaying === true || playMode.isPlayingOrWillChangePlaymode === true) {
    throw new StructuredToolError("E_TOOL_PLAYMODE_UNAVAILABLE", "Tool editor.compile-diagnostics is editor-only and cannot run while Unity is in Play Mode.", [
      "Exit Play Mode before refreshing assets and collecting compile diagnostics."
    ], { toolName: "editor.compile-diagnostics", playModeSupport: "editor-only", isPlaying: true });
  }

  let refreshStep: BridgeStepResult | undefined;
  if (refresh) {
    refreshStep = await executeBridgeWithTransientRetry("refresh", "asset.refresh", compact({ path, forceUpdate, importRecursive }), execution, runtime, requestId, waitTimeoutMs, pollIntervalMs);
    if (!refreshStep.result.ok) {
      compileDiagnosticsStepFailure("refresh", refreshStep.result, { attempts: { refresh: refreshStep.attempts } });
    }
  }

  let waitStep: BridgeStepResult | undefined;
  if (waitReady) {
    waitStep = await executeBridgeWithTransientRetry("wait", "context.read", { name: "editor.state", input: { waitReady: true } }, execution, runtime, requestId, waitTimeoutMs, pollIntervalMs);
    if (!waitStep.result.ok) {
      compileDiagnosticsStepFailure("wait", waitStep.result, { refresh: summarizeResult(refreshStep?.result), attempts: { refresh: refreshStep?.attempts ?? 0, wait: waitStep.attempts } });
    }
  }

  const consoleStep = await executeBridgeWithTransientRetry("console", "console.get", compact({ type: consoleType, filter, count, cursor: 0 }), execution, runtime, requestId, waitTimeoutMs, pollIntervalMs);
  if (!consoleStep.result.ok) {
    compileDiagnosticsStepFailure("console", consoleStep.result, { refresh: summarizeResult(refreshStep?.result), wait: summarizeResult(waitStep?.result), attempts: { refresh: refreshStep?.attempts ?? 0, wait: waitStep?.attempts ?? 0, console: consoleStep.attempts } });
  }

  const consoleData = bridgeStepData(consoleStep.result.data);
  const items = consoleItems(consoleData.items);
  const compileDiagnostics = items.filter(isCompileDiagnostic).map(normalizeConsoleItem);
  const consoleErrorCount = items.filter((item) => stringValue(item.type).toLowerCase() === "error").length;
  const waitTimedOut = waitStep?.result.ok ? bridgeStepData(waitStep.result.data).timedOut === true : false;
  const total = numberValue(consoleData.total) ?? numberValue(recordData(consoleData.pageInfo).total) ?? items.length;
  const status = compileDiagnostics.length > 0 ? "compile_errors" : waitTimedOut ? "wait_timeout" : consoleErrorCount > 0 ? "console_errors" : "clean";

  return {
    command: "editor.compile-diagnostics",
    status,
    refresh: refresh ? summarizeResult(refreshStep?.result) : { skipped: true },
    wait: waitReady ? summarizeResult(waitStep?.result) : { skipped: true },
    console: {
      ok: true,
      type: consoleType,
      filter: filter ?? null,
      total,
      returned: items.length,
      pageInfo: consoleData.pageInfo,
      items: items.map(normalizeConsoleItem)
    },
    compile: {
      ok: compileDiagnostics.length === 0 && !waitTimedOut,
      hasErrors: compileDiagnostics.length > 0,
      errorCount: compileDiagnostics.length,
      consoleErrorCount,
      diagnostics: compileDiagnostics
    },
    attempts: { refresh: refreshStep?.attempts ?? 0, wait: waitStep?.attempts ?? 0, console: consoleStep.attempts },
    nextActions: compileDiagnosticsNextActions(status, total, items.length)
  };
}

async function handleCompileDiagnosticsCli(flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const startedAt = Date.now();
  try {
    const data = await runCompileDiagnostics({
      refresh: optionalBoolean(flags, "refresh"),
      path: getString(flags, "path") ?? getString(flags, "asset-path") ?? getString(flags, "assetPath"),
      forceUpdate: optionalBoolean(flags, "force-update") ?? optionalBoolean(flags, "forceUpdate"),
      importRecursive: optionalBoolean(flags, "import-recursive") ?? optionalBoolean(flags, "importRecursive"),
      waitReady: optionalBoolean(flags, "wait-ready") ?? optionalBoolean(flags, "waitReady"),
      waitTimeoutMs: getNumber(flags, "wait-timeout-ms") ?? getNumber(flags, "waitTimeoutMs") ?? getNumber(flags, "timeout-ms") ?? getNumber(flags, "timeoutMs"),
      pollIntervalMs: getNumber(flags, "poll-interval-ms") ?? getNumber(flags, "pollIntervalMs"),
      type: getString(flags, "type") ?? getString(flags, "level"),
      filter: getString(flags, "filter") ?? getString(flags, "search"),
      count: getNumber(flags, "count") ?? getNumber(flags, "console-count") ?? getNumber(flags, "consoleCount"),
      dryRun: getBoolean(flags, "dry-run"),
      job: getBoolean(flags, "job"),
      bridgeEndpoint: getString(flags, "bridge-endpoint"),
      instance: getString(flags, "instance"),
      retry: getNumber(flags, "retry")
    }, runtime, requestId);
    return success(requestId, data, { durationMs: durationMsFrom(startedAt) });
  } catch (error) {
    if (error instanceof StructuredToolError) return failure(requestId, error.code, error.message, error.nextActions, error.details);
    throw error;
  }
}

/** 对 transient Unity busy/compile/import/domain reload 结果做 CLI 侧重试。 */
async function executeBridgeWithTransientRetry(label: string, command: string, args: Record<string, unknown>, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, timeoutMs: number, pollIntervalMs: number): Promise<BridgeStepResult> {
  const startedAt = Date.now();
  let attempts = 0;
  let lastResult: CliResult | undefined;
  do {
    attempts += 1;
    const routed = routeBridgeStep(command, args);
    lastResult = await executeBridgePayload(`${requestId}_${label}_${attempts}`, routed.command, routed.args, flags, runtime);
    const retryable = !lastResult.ok && (lastResult.error.transient === true || COMPILE_DIAGNOSTIC_RETRY_CODES.has(lastResult.error.code));
    if (lastResult.ok || !retryable || timeoutMs === 0) {
      return { result: lastResult, attempts };
    }
    const remainingMs = timeoutMs - durationMsFrom(startedAt);
    if (remainingMs <= 0) {
      return { result: lastResult, attempts };
    }
    await sleep(Math.min(pollIntervalMs, remainingMs));
  } while (true);
}

/** 按 Bridge registry 将工具名转换为实际命令与 typed input。 */
function routeBridgeStep(command: string, args: Record<string, unknown>): { command: string; args: Record<string, unknown> } {
  const spec = bridgeCommandRegistry.specs().find((entry) => entry.toolName === command || entry.bridgeCommand === command);
  if (!spec?.toolName) return { command, args };
  return { command: spec.bridgeCommand, args: { name: spec.toolName, input: args } };
}

/** 提取 project-tool.run 或直接 Bridge 调用的业务数据。 */
function bridgeStepData(value: unknown): Record<string, unknown> {
  const data = recordData(value);
  return data.command === "project-tool.run" || data.command === "context.read" ? recordData(data.result) : data;
}

/** 将子步骤失败转换为编排级结构化错误。 */
function compileDiagnosticsStepFailure(step: string, result: CliResult, partial: Record<string, unknown>): never {
  const cause = result.ok ? result.data : result.error;
  const message = result.ok ? "unknown error" : result.error.message;
  throw new StructuredToolError(`E_COMPILE_DIAGNOSTICS_${step.toUpperCase()}_FAILED`, `editor.compile-diagnostics failed during ${step}: ${message}`, [
    "Run tools run editor.state or context read editor.state to inspect current Unity busy state.",
    "Fix Bridge connectivity or Unity compile/import errors, then rerun editor.compile-diagnostics."
  ], { step, cause, ...partial }, result.ok ? undefined : result.error.transient, result.ok ? undefined : result.error.retryAfterMs);
}

/** 生成 dry-run plannedEffects，明确不会清空 Console 或保存项目文件。 */
function compileDiagnosticsPlannedEffects(input: { refresh: boolean; waitReady: boolean; path?: string; forceUpdate?: boolean; importRecursive?: boolean; waitTimeoutMs: number; consoleType: string; filter?: string; count: number }): Array<Record<string, unknown>> {
  const effects: Array<Record<string, unknown>> = [];
  if (input.refresh) {
    effects.push({ type: input.path ? "asset.import" : "asset.refresh", path: input.path ?? null, forceUpdate: input.forceUpdate ?? false, importRecursive: input.importRecursive ?? false });
  }
  if (input.waitReady) {
    effects.push({ type: "editor.wait-ready", timeoutMs: input.waitTimeoutMs });
  }
  effects.push({ type: "console.get", level: input.consoleType, filter: input.filter ?? null, count: input.count });
  return effects;
}

/** 按闭环结果生成 AI 可执行的下一步建议。 */
function compileDiagnosticsNextActions(status: string, total: number, returned: number): string[] {
  const actions: string[] = [];
  if (status === "compile_errors") {
    actions.push("Fix the listed C# compiler diagnostics, then rerun editor.compile-diagnostics.");
  } else if (status === "wait_timeout") {
    actions.push("Unity did not become idle before waitTimeoutMs; rerun with a larger waitTimeoutMs or inspect editor.state.");
  } else if (status === "console_errors") {
    actions.push("Recent Console errors were found but no C# compiler diagnostics matched; inspect console.items before running tests/builds.");
  } else {
    actions.push("No C# compiler diagnostics were found in the returned Console page; continue with the next validation step.");
  }
  if (total > returned) {
    actions.push("Console results were paged; rerun with a larger count or use console.get cursor pagination if older diagnostics are needed.");
  }
  return actions;
}

/** 压缩子步骤 envelope，保留 requestId 与主要 data/error。 */
function summarizeResult(result: CliResult | undefined): Record<string, unknown> {
  if (!result) return { skipped: true };
  return result.ok ? { ok: true, requestId: result.requestId, data: result.data } : { ok: false, requestId: result.requestId, error: result.error };
}

/** 从 console.get data.items 中提取机器可读条目数组。 */
function consoleItems(value: unknown): ConsoleItem[] {
  return Array.isArray(value) ? value.filter((item): item is ConsoleItem => typeof item === "object" && item !== null) : [];
}

/** 将 Console 条目规整成编译诊断输出字段。 */
function normalizeConsoleItem(item: ConsoleItem): Record<string, unknown> {
  return compact({
    index: numberValue(item.index),
    type: stringValue(item.type),
    id: compileDiagnosticId(item),
    message: stringValue(item.message),
    file: stringValue(item.file),
    line: numberValue(item.line)
  });
}

/** 判断 Console error 是否像 C# compiler diagnostic。 */
function isCompileDiagnostic(item: ConsoleItem): boolean {
  const message = stringValue(item.message);
  const file = stringValue(item.file);
  const type = stringValue(item.type).toLowerCase();
  return type === "error" && (/\bCS\d{4}\b/.test(message) || /\.cs$/i.test(file));
}

/** 从 Console message 中提取 CS#### 错误码。 */
function compileDiagnosticId(item: ConsoleItem): string | undefined {
  return /\b(CS\d{4})\b/.exec(stringValue(item.message))?.[1];
}

/** 安全读取对象型 data，避免 undefined/array 进入后续逻辑。 */
function recordData(value: unknown): Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

/** 解析可选 boolean flag，区分未传与 false。 */
function optionalBoolean(flags: Record<string, string | boolean>, key: string): boolean | undefined {
  const value = flags[key];
  if (value === true || value === "true") return true;
  if (value === false || value === "false") return false;
  return undefined;
}

/** 仅接受有限 number，过滤字符串或 NaN。 */
function numberValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/** 将可选字符串字段规整为空串。 */
function stringValue(value: unknown): string {
  return typeof value === "string" ? value : "";
}

/** 规整用户输入的毫秒/数量参数，限制上下界。 */
function clampInteger(value: number | undefined, fallback: number, min: number, max: number): number {
  if (value === undefined || !Number.isFinite(value)) return fallback;
  return Math.min(max, Math.max(min, Math.floor(value)));
}

/** 删除 undefined 字段，避免传给 Bridge 侧参数解析。 */
function compact(input: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(input).filter(([, value]) => value !== undefined));
}

/** CLI 侧 transient 重试等待。 */
function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** editor.refresh batch job 的 Unity executeMethod 参数。 */
function refreshArgs(flags: Record<string, string | boolean>): string[] {
  return ["-executeMethod", getBoolean(flags, "compile") ? "UnityCli.UnityCliBatch.RefreshAndCompile" : "UnityCli.UnityCliBatch.Refresh"];
}
