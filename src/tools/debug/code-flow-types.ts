import type { AnyJson } from "../../schema/any-json.js";

/**
 * 提交一条流的请求。submit 与 run 共用同一输入形状：
 * 差别只在完成含义——submit 是"已被接受"，run 是"已到终态"。
 */
export interface CodeFlowRequest {
  file?: string;
  stdin?: boolean;
  code?: string;
  refs?: string[];
  define?: string[];
  args?: CodeFlowJsonObject;
  /** 流自身生命周期预算；从首次 tick 起算，不含编译，暂停时段不计入。 */
  flowBudgetMs?: number;
  /** 可选 session 归集，用于每 session 并发额度。 */
  sessionId?: string;
  /** 模式门：any 不限制；play 要求已在 Play；edit 要求不在 Play。 */
  requiresPlayMode?: "any" | "play" | "edit";
}

export interface CodeFlowJsonObject { [key: string]: AnyJson; }

/** 观察一条已存在的流。 */
export interface CodeFlowWaitRequest {
  flowId: string;
  waitMs?: number;
}

/** 取消一条已存在的流。 */
export interface CodeFlowCancelRequest {
  flowId: string;
}

/** submit 的收据：没有 status，因为它不是结论。 */
export interface CodeFlowSubmitResult {
  registered: true;
  flowId: string;
  sessionId: string;
  budgetMs: number;
  startedMonotonicMs: number;
  activeFlows: number;
  sessionActiveFlows: number;
  maxFlowsPerInstance: number;
  maxFlowsPerSession: number;
  editorSessionId: string;
  note: string;
}

/** run / wait / cancel 共享的结果视图；status 是唯一区分器。 */
export interface CodeFlowResult {
  status: "running" | "completed" | "failed" | "timed-out" | "cancelled" | "aborted";
  flowId: string;
  sessionId: string;
  elapsedMs: number;
  steps: number;
  waited: boolean;
  waitMs: number;
  result?: AnyJson;
  resultTruncated: boolean;
  failureTag: string;
  errorCode: string;
  errorMessage: string;
  lastObserved?: AnyJson;
  samples: number;
  budgetMs: number;
  budgetConsumedMs: number;
  activeFlows: number;
  sessionActiveFlows: number;
  maxFlowsPerInstance: number;
  maxFlowsPerSession: number;
  editorSessionId: string;
  note: string;
}

export interface CodeFlowDryRunResult {
  dryRun: true;
  preview: {
    sourceKind: string;
    sourcePath?: string;
    sourceSha256: string;
    sourceSizeBytes: number;
    timeoutMs: number;
    flowBudgetMs: number | null;
    args: CodeFlowJsonObject;
  };
  plannedEffects: Array<{ type: "debug.code.flow.run" | "debug.code.flow.submit"; sideEffects: string }>;
}
