import { executeBridgePayload } from "./bridge/index.js";
import {
  maxBridgePayloadBytes,
  normalizeTimeout,
  readCodeSource,
  requireRuntimeCodeDeadline,
  throwCliFailure,
  validateExtraReferences,
  type RuntimeCodeRequest,
  type RuntimeCodeRuntime,
} from "./runtime-code.js";
import { StructuredToolError } from "./tools/errors.js";

/** 流请求：源码来源与一次性执行共用同一套读取与预算规则。 */
export interface RuntimeFlowRequest extends RuntimeCodeRequest {
  flowBudgetMs?: number;
  sessionId?: string;
  requiresPlayMode?: "any" | "play" | "edit";
}

/** 观察一条已存在的流；不重新执行、不创建新流、不取消。 */
export interface RuntimeFlowWaitRequest {
  flowId: string;
  waitMs?: number;
  timeoutMs?: number;
  absoluteDeadlineMs?: number;
  instance?: string;
}

/** 取消一条已存在的流。 */
export interface RuntimeFlowCancelRequest extends RuntimeFlowWaitRequest {}

// run 的请求超时由流预算推导：流的花费上限决定要等多久，不由另一个写死的数字决定。
const flowRunMarginMs = 60_000;
const flowBudgetDefaultMs = 300_000;

/** 提交一条流并立即返回句柄；它不等待，因此请求超时只需要覆盖编译。 */
export async function executeRuntimeFlowSubmit(request: RuntimeFlowRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  const result = await dispatchFlowStart("debug.code-flow-submit", request, runtime, requestId, 90_000);
  if (result.dryRun === true) return result;
  return { ...result, receipt: { type: "accepted", complete: false, followUpTool: "debug.code-flow-wait" } };
}

/** 提交一条流并等到终态；请求超时覆盖流预算与余量，因此不会退回半途状态。 */
export async function executeRuntimeFlowRun(request: RuntimeFlowRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  const budget = request.flowBudgetMs ?? flowBudgetDefaultMs;
  return dispatchFlowStart("debug.code-flow-run", request, runtime, requestId, budget + flowRunMarginMs);
}

// 两个提交入口共用同一份源码读取、payload 组装与 dry-run 语义。
async function dispatchFlowStart(
  command: string,
  request: RuntimeFlowRequest,
  runtime: RuntimeCodeRuntime,
  requestId: string,
  minimumTimeoutMs: number,
): Promise<Record<string, unknown>> {
  // 调用方显式给了更长的超时则尊重它；否则用推导值，保证命令契约能兑现。
  const timeoutMs = Math.max(normalizeTimeout(request.timeoutMs), Math.round(minimumTimeoutMs));
  const absoluteDeadlineMs = request.absoluteDeadlineMs ?? Date.now() + timeoutMs;
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  const source = await readCodeSource(request, absoluteDeadlineMs);
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  const parsedArgs = request.args ?? {};
  const refs = await validateExtraReferences(request.refs, absoluteDeadlineMs);
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  // 只发送流工具声明过的字段：未知属性会被 schema 校验拒绝。
  const payload: Record<string, unknown> = {
    sourceText: source.text,
    argsJson: JSON.stringify(parsedArgs),
    defines: request.define?.join(",") ?? "",
    refs,
  };
  if (request.flowBudgetMs !== undefined) payload.flowBudgetMs = request.flowBudgetMs;
  if (request.sessionId !== undefined) payload.sessionId = request.sessionId;
  if (request.requiresPlayMode !== undefined) payload.requiresPlayMode = request.requiresPlayMode;
  if (Buffer.byteLength(JSON.stringify(payload), "utf8") > maxBridgePayloadBytes)
    throw new StructuredToolError("E_CODE_INPUT_TOO_LARGE", `Flow Bridge payload exceeds the ${maxBridgePayloadBytes}-byte local limit.`);
  requireRuntimeCodeDeadline(absoluteDeadlineMs);

  if (request.dryRun) {
    return {
      dryRun: true,
      preview: {
        sourceKind: source.kind,
        ...(source.path ? { sourcePath: source.path } : {}),
        sourceSha256: source.sha256,
        sourceSizeBytes: source.sizeBytes,
        timeoutMs,
        flowBudgetMs: request.flowBudgetMs ?? null,
        args: parsedArgs,
      },
      plannedEffects: [{ type: command, sideEffects: "user-code-not-executed" }],
    };
  }

  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  // 源码读取与引用校验都通过后，编译并执行流体才是本 Tool 的首个业务 effect。
  await runtime.commitEffect?.();
  const result = await executeBridgePayload(requestId, command, payload, {
    instance: request.instance ?? "",
    "timeout-ms": String(timeoutMs),
    __destructiveConfirmed: true,
  }, runtime, { mode: "execute", mutation: true, defaultTimeoutMs: timeoutMs, absoluteDeadlineMs });
  if (!result.ok) throwCliFailure(result);
  return (result.data ?? {}) as Record<string, unknown>;
}

/** 观察一条流；省略 waitMs 时观察到本次请求超时为止，不取消流。 */
export async function executeRuntimeFlowWait(request: RuntimeFlowWaitRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  return dispatchFlowObservation("debug.code-flow-wait", request, runtime, requestId, true);
}

/** 取消一条流；已终态时返回同一终态。 */
export async function executeRuntimeFlowCancel(request: RuntimeFlowCancelRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  return dispatchFlowObservation("debug.code-flow-cancel", request, runtime, requestId, false);
}

// 观察与取消都不编译、不执行用户代码，因此走只读派发并使用有界 deadline。
async function dispatchFlowObservation(
  command: string,
  request: RuntimeFlowWaitRequest,
  runtime: RuntimeCodeRuntime,
  requestId: string,
  acceptsWaitMs: boolean,
): Promise<Record<string, unknown>> {
  const flowId = typeof request.flowId === "string" ? request.flowId.trim() : "";
  if (flowId.length === 0)
    throw new StructuredToolError("E_SCHEMA_INVALID", `${command} requires a non-empty flowId.`, ["Pass the flowId returned by debug.code-flow-submit."]);
  const timeoutMs = normalizeTimeout(request.timeoutMs);
  const absoluteDeadlineMs = request.absoluteDeadlineMs ?? Date.now() + timeoutMs;
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  const payload: Record<string, unknown> = { flowId };
  if (acceptsWaitMs && request.waitMs !== undefined) payload.waitMs = request.waitMs;
  const result = await executeBridgePayload(requestId, command, payload, {
    instance: request.instance ?? "",
    "timeout-ms": String(timeoutMs),
  }, runtime, { mode: "read", mutation: false, defaultTimeoutMs: timeoutMs, absoluteDeadlineMs });
  if (!result.ok) throwCliFailure(result);
  return (result.data ?? {}) as Record<string, unknown>;
}
