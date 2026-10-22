import { executeRuntimeFlowCancel } from "../../runtime-flow.js";
import type { AnyJson } from "../../schema/any-json.js";
import { defineTool, type ToolContext } from "../define.js";
import type { CodeFlowCancelRequest, CodeFlowResult } from "./code-flow-types.js";

/** 读取命令层 instance flag；它不进入流工具的业务 Request。 */
function instanceFlag(context: ToolContext): string | undefined {
  const value = context.flags?.instance;
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

const cancelExample: CodeFlowCancelRequest = { flowId: "flow-1" };

export default defineTool<CodeFlowCancelRequest, CodeFlowResult>({
  name: "debug.code.flow.cancel",
  summary: "Cancel one running flow and return its resulting terminal facts.",
  description: "Cancel exactly one flowId. Cancellation is a terminal state: the flow stops advancing on the central editor tick and reports status cancelled with the steps it had completed and the elapsed time it had consumed. Cancelling a flow that already reached a terminal state changes nothing and returns that same terminal state, so a cancel is safe to repeat and never rewrites history. Cancelling an unknown flowId is refused with E_DEBUG_FLOW_NOT_FOUND rather than reported as success, because a missing flow is not evidence of a cancelled one. Cancelling frees one slot of the per-session and per-instance concurrency budgets. A flow also stops on its own when its flowBudgetMs elapses, reported as status timed-out, and when the Editor reloads the domain, restarts or leaves Play mode, after which the flow is unobservable. Only a completed flow answers ok:true. A flow that is still running answers E_DEBUG_FLOW_PENDING with flowId and the stepped facts, and failed, timed-out, cancelled and aborted flows answer E_DEBUG_FLOW_FAILED, E_DEBUG_FLOW_TIMEOUT, E_DEBUG_FLOW_CANCELLED and E_DEBUG_FLOW_ABORTED; every one of those errors carries flowId, status and the terminal facts.",
  group: "debug",
  destructiveHint: true,
  sideEffects: ["debug-activation"],
  playModeSupport: "supported",
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "editor",
  timeoutMs: 30_000,
  examples: [{
    description: "Cancel a flow that is still running.",
    argv: ["tools", "run", "debug.code.flow.cancel", "--input", JSON.stringify(cancelExample)],
    input: cancelExample as unknown as AnyJson,
  }],
  async execute(request, context) {
    return executeRuntimeFlowCancel({
      flowId: request.flowId,
      timeoutMs: context.execution.timeoutMs,
      absoluteDeadlineMs: context.absoluteDeadlineMs,
      instance: instanceFlag(context),
    }, {
      dataDir: context.dataDir ?? context.projectPath,
      rootAdmission: context.rootAdmission,
      bridgeExecute: context.bridgeExecute,
      commitEffect: context.commitEffect,
    }, context.requestId) as unknown as Promise<CodeFlowResult>;
  },
});
