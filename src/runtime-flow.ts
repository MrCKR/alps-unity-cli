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

/** 提交一条跨帧流：等待预算内完成则直接返回结果，否返回 flowId 供后续续等。 */
export interface RuntimeFlowRequest extends RuntimeCodeRequest {
  waitMs?: number;
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

/** 提交流体；源码来源与一次性执行共用同一套读取与预算规则。 */
export async function executeRuntimeFlow(request: RuntimeFlowRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  const timeoutMs = normalizeTimeout(request.timeoutMs);
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
  if (request.waitMs !== undefined) payload.waitMs = request.waitMs;
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
        waitMs: request.waitMs ?? null,
        flowBudgetMs: request.flowBudgetMs ?? null,
        args: parsedArgs,
      },
      plannedEffects: [{ type: "debug.code.flow.execute", sideEffects: "user-code-not-executed" }],
    };
  }

  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  // 源码读取与引用校验都通过后，编译并执行流体才是本 Tool 的首个业务 effect。
  await runtime.commitEffect?.();
  const result = await executeBridgePayload(requestId, "debug.code.flow.execute", payload, {
    instance: request.instance ?? "",
    "timeout-ms": String(timeoutMs),
    __destructiveConfirmed: true,
  }, runtime, { mode: "execute", mutation: true, defaultTimeoutMs: timeoutMs, absoluteDeadlineMs });
  if (!result.ok) throwCliFailure(result);
  return (result.data ?? {}) as Record<string, unknown>;
}

/** 观察一条流；到期返回当前状态，不取消流。 */
export async function executeRuntimeFlowWait(request: RuntimeFlowWaitRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  return dispatchFlowObservation("debug.code.flow.wait", request, runtime, requestId);
}

/** 取消一条流；已终态时返回同一终态。 */
export async function executeRuntimeFlowCancel(request: RuntimeFlowCancelRequest, runtime: RuntimeCodeRuntime, requestId: string): Promise<Record<string, unknown>> {
  return dispatchFlowObservation("debug.code.flow.cancel", request, runtime, requestId);
}

// 观察与取消都不编译、不执行用户代码，因此走只读派发并使用有界 deadline。
async function dispatchFlowObservation(
  command: string,
  request: RuntimeFlowWaitRequest,
  runtime: RuntimeCodeRuntime,
  requestId: string,
): Promise<Record<string, unknown>> {
  const flowId = typeof request.flowId === "string" ? request.flowId.trim() : "";
  if (flowId.length === 0)
    throw new StructuredToolError("E_SCHEMA_INVALID", `${command} requires a non-empty flowId.`, ["Pass the flowId returned by debug.code.flow.execute."]);
  const timeoutMs = normalizeTimeout(request.timeoutMs);
  const absoluteDeadlineMs = request.absoluteDeadlineMs ?? Date.now() + timeoutMs;
  requireRuntimeCodeDeadline(absoluteDeadlineMs);
  const payload: Record<string, unknown> = { flowId };
  if (request.waitMs !== undefined) payload.waitMs = request.waitMs;
  const result = await executeBridgePayload(requestId, command, payload, {
    instance: request.instance ?? "",
    "timeout-ms": String(timeoutMs),
  }, runtime, { mode: "read", mutation: false, defaultTimeoutMs: timeoutMs, absoluteDeadlineMs });
  if (!result.ok) throwCliFailure(result);
  return (result.data ?? {}) as Record<string, unknown>;
}
