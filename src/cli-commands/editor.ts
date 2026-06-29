import { getBoolean, getNumber, getString } from "../args.js";
import { executeBridgePayload } from "../bridge/index.js";
import { dismissUnityLaunchDialog, DISMISS_BUTTON_LABEL, LAUNCH_DIALOG_TITLE_FRAGMENTS } from "../launch-dialog-dismiss.js";
import { durationMsFrom, failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import { startUnityJob } from "./unity-job.js";

const DEFAULT_COMPILE_WAIT_TIMEOUT_MS = 45_000;
const MAX_COMPILE_WAIT_TIMEOUT_MS = 55_000;
const DEFAULT_COMPILE_POLL_INTERVAL_MS = 1_000;

export function handleEditor(command: string | undefined, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> | undefined {
  if (command === "refresh") {
    return startUnityJob("refresh", flags, runtime, requestId, refreshArgs(flags), { action: "refresh", compile: getBoolean(flags, "compile") });
  }

  if (command === "compile-diagnostics") {
    return handleCompileDiagnostics(flags, runtime, requestId);
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

/** 编排 live Editor 编译诊断：刷新资产、等待空闲、读取 Console。 */
async function handleCompileDiagnostics(flags: Record<string, string | boolean>, runtime: Runtime, requestId: string): Promise<CliResult> {
  const startedAt = Date.now();
  if (getBoolean(flags, "job")) {
    return failure(requestId, "E_TOOL_JOB_UNSUPPORTED", "editor.compile-diagnostics is a live Editor orchestration tool and does not support --job.", [
      "Remove --job and run against a live Unity Editor instance.",
      "Use editor.refresh for batchmode refresh jobs when you do not need live Console diagnostics."
    ]);
  }
  const waitTimeoutMs = clampInteger(getNumber(flags, "wait-timeout-ms") ?? getNumber(flags, "waitTimeoutMs") ?? getNumber(flags, "timeout-ms") ?? getNumber(flags, "timeoutMs"), DEFAULT_COMPILE_WAIT_TIMEOUT_MS, 0, MAX_COMPILE_WAIT_TIMEOUT_MS);
  const pollIntervalMs = clampInteger(getNumber(flags, "poll-interval-ms") ?? getNumber(flags, "pollIntervalMs"), DEFAULT_COMPILE_POLL_INTERVAL_MS, 0, 5_000);
  const count = clampInteger(getNumber(flags, "count") ?? getNumber(flags, "console-count") ?? getNumber(flags, "consoleCount"), 100, 1, 500);
  const refresh = optionalBoolean(flags, "refresh") ?? true;
  const waitReady = optionalBoolean(flags, "wait-ready") ?? optionalBoolean(flags, "waitReady") ?? true;
  const path = getString(flags, "path") ?? getString(flags, "asset-path") ?? getString(flags, "assetPath");
  const filter = getString(flags, "filter") ?? getString(flags, "search");
  const consoleType = getString(flags, "type") ?? getString(flags, "level") ?? "error";
  const forceUpdate = optionalBoolean(flags, "force-update") ?? optionalBoolean(flags, "forceUpdate");
  const importRecursive = optionalBoolean(flags, "import-recursive") ?? optionalBoolean(flags, "importRecursive");

  if (getBoolean(flags, "dry-run")) {
    return success(requestId, {
      command: "editor.compile-diagnostics",
      dryRun: true,
      status: "dry_run",
      plannedEffects: compileDiagnosticsPlannedEffects({ refresh, waitReady, path, forceUpdate, importRecursive, waitTimeoutMs, consoleType, filter, count }),
      nextActions: ["Rerun without --dry-run to refresh assets, wait for Unity to become idle, and read Console compile diagnostics."]
    });
  }

  let refreshStep: BridgeStepResult | undefined;
  if (refresh) {
    refreshStep = await executeBridgeWithTransientRetry("refresh", "asset.refresh", compact({ path, forceUpdate, importRecursive }), flags, runtime, requestId, waitTimeoutMs, pollIntervalMs);
    if (!refreshStep.result.ok) {
      return compileDiagnosticsStepFailure(requestId, "refresh", refreshStep.result, { attempts: { refresh: refreshStep.attempts } });
    }
  }

  let waitStep: BridgeStepResult | undefined;
  if (waitReady) {
    waitStep = await executeBridgeWithTransientRetry("wait", "context.read", { name: "editor.state", input: { waitReady: true, timeoutMs: waitTimeoutMs }, waitReady: true, timeoutMs: waitTimeoutMs }, flags, runtime, requestId, waitTimeoutMs, pollIntervalMs);
    if (!waitStep.result.ok) {
      return compileDiagnosticsStepFailure(requestId, "wait", waitStep.result, { refresh: summarizeResult(refreshStep?.result), attempts: { refresh: refreshStep?.attempts ?? 0, wait: waitStep.attempts } });
    }
  }

  const consoleStep = await executeBridgeWithTransientRetry("console", "console.get", compact({ type: consoleType, filter, count, cursor: 0 }), flags, runtime, requestId, waitTimeoutMs, pollIntervalMs);
  if (!consoleStep.result.ok) {
    return compileDiagnosticsStepFailure(requestId, "console", consoleStep.result, { refresh: summarizeResult(refreshStep?.result), wait: summarizeResult(waitStep?.result), attempts: { refresh: refreshStep?.attempts ?? 0, wait: waitStep?.attempts ?? 0, console: consoleStep.attempts } });
  }

  const consoleData = recordData(consoleStep.result.data);
  const items = consoleItems(consoleData.items);
  const compileDiagnostics = items.filter(isCompileDiagnostic).map(normalizeConsoleItem);
  const consoleErrorCount = items.filter((item) => stringValue(item.type).toLowerCase() === "error").length;
  const waitTimedOut = recordData(waitStep?.result.data).timedOut === true;
  const total = numberValue(consoleData.total) ?? numberValue(recordData(consoleData.pageInfo).total) ?? items.length;
  const status = compileDiagnostics.length > 0 ? "compile_errors" : waitTimedOut ? "wait_timeout" : consoleErrorCount > 0 ? "console_errors" : "clean";

  return success(requestId, {
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
  }, { durationMs: durationMsFrom(startedAt) });
}

/** 对 transient Unity busy/compile/import/domain reload 结果做 CLI 侧重试。 */
async function executeBridgeWithTransientRetry(label: string, command: string, args: Record<string, unknown>, flags: Record<string, string | boolean>, runtime: Runtime, requestId: string, timeoutMs: number, pollIntervalMs: number): Promise<BridgeStepResult> {
  const startedAt = Date.now();
  let attempts = 0;
  let lastResult: CliResult | undefined;
  do {
    attempts += 1;
    lastResult = await executeBridgePayload(`${requestId}_${label}_${attempts}`, command, args, flags, runtime);
    if (lastResult.ok || lastResult.error?.transient !== true || timeoutMs === 0) {
      return { result: lastResult, attempts };
    }
    const remainingMs = timeoutMs - durationMsFrom(startedAt);
    if (remainingMs <= 0) {
      return { result: lastResult, attempts };
    }
    await sleep(Math.min(pollIntervalMs, remainingMs));
  } while (true);
}

/** 将子步骤失败包装为 editor.compile-diagnostics 的结构化失败。 */
function compileDiagnosticsStepFailure(requestId: string, step: string, result: CliResult, partial: Record<string, unknown>): CliResult {
  return failure(requestId, `E_COMPILE_DIAGNOSTICS_${step.toUpperCase()}_FAILED`, `editor.compile-diagnostics failed during ${step}: ${result.error?.message ?? "unknown error"}`, [
    "Run tools run editor.state or context read editor.state to inspect current Unity busy state.",
    "Fix Bridge connectivity or Unity compile/import errors, then rerun editor.compile-diagnostics."
  ], { step, cause: result.error ?? result.data, ...partial });
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
