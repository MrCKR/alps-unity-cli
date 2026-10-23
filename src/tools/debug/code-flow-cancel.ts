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
  name: "debug.code-flow-cancel",
  summary: "Cancel one running flow and return its resulting terminal facts.",
  description: "Cancel exactly one flowId. Cancellation is terminal: the flow stops advancing on the central editor tick and reports status cancelled with the steps it completed and the time it consumed. Cancelling a flow that already reached a terminal state changes nothing and returns that same state, so a cancel is safe to repeat. An unknown flowId is refused with E_DEBUG_FLOW_NOT_FOUND. Cancelling frees one slot of the per-session and per-instance concurrency budgets. A flow also stops on its own when flowBudgetMs elapses (status timed-out), and a Domain Reload, Editor restart or Play mode exit makes it unobservable. Only a completed flow answers ok:true; running answers E_DEBUG_FLOW_PENDING, and failed, timed-out, cancelled and aborted flows answer E_DEBUG_FLOW_FAILED, E_DEBUG_FLOW_TIMEOUT, E_DEBUG_FLOW_CANCELLED and E_DEBUG_FLOW_ABORTED with flowId, status and terminal facts.",
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
    argv: ["tools", "run", "debug.code-flow-cancel", "--input", JSON.stringify(cancelExample)],
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
