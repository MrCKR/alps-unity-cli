import type { AnyJson } from "../../schema/any-json.js";

/** 提交流的请求：源码来源与一次性执行共用 file / stdin / code 三选一。 */
export interface CodeExecuteAsyncRequest {
  file?: string;
  stdin?: boolean;
  code?: string;
  refs?: string[];
  define?: string[];
  args?: CodeFlowJsonObject;
  /** 本次调用最多等待多久；到期返回 status running 与 flowId，流继续运行。 */
  waitMs?: number;
  /** 流自身生命周期预算；从首次 tick 起算，不含编译，暂停时段不计入。 */
  flowBudgetMs?: number;
  /** 可选 session 归集，用于每 session 并发额度。 */
  sessionId?: string;
  /** 模式门：any 不限制；play 要求已在 Play；edit 要求不在 Play。 */
  requiresPlayMode?: "any" | "play" | "edit";
}

export interface CodeFlowJsonObject { [key: string]: AnyJson; }

/** 观察一条已存在的流。 */
export interface CodeExecuteAsyncWaitRequest {
  flowId: string;
  waitMs?: number;
}

/** 取消一条已存在的流。 */
export interface CodeExecuteAsyncCancelRequest {
  flowId: string;
}

/** 三个流调用共享的结果视图；status 是唯一区分器。 */
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

export interface CodeExecuteAsyncDryRunResult {
  dryRun: true;
  preview: {
    sourceKind: string;
    sourcePath?: string;
    sourceSha256: string;
    sourceSizeBytes: number;
    timeoutMs: number;
    waitMs: number | null;
    flowBudgetMs: number | null;
    args: CodeFlowJsonObject;
  };
  plannedEffects: Array<{ type: "debug.code.flow.execute"; sideEffects: string }>;
}
