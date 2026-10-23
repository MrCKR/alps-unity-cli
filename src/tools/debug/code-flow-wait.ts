import { executeRuntimeFlowWait } from "../../runtime-flow.js";
import type { AnyJson } from "../../schema/any-json.js";
import { defineTool, type ToolContext } from "../define.js";
import type { CodeFlowResult, CodeFlowWaitRequest } from "./code-flow-types.js";

/** 读取命令层 instance flag；它不进入流工具的业务 Request。 */
function instanceFlag(context: ToolContext): string | undefined {
  const value = context.flags?.instance;
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

const waitExample: CodeFlowWaitRequest = { flowId: "flow-1", waitMs: 10000 };
const readExample: CodeFlowWaitRequest = { flowId: "flow-1", waitMs: 0 };

export default defineTool<CodeFlowWaitRequest, CodeFlowResult>({
  name: "debug.code-flow-wait",
  summary: "Observe one flow until it is terminal or a bounded wait expires, returning the same terminal facts every time.",
  description: "Read the current facts of exactly one flowId, optionally waiting a bounded time for it to become terminal. This Tool never compiles, never re-executes the body, never creates a flow and never cancels anything, so it is safe to call repeatedly while a flow is running. Inputs: flowId (required) and an optional waitMs that bounds only this call: waitMs 0 returns the current facts immediately, a small waitMs is a short window, and omitting waitMs waits until this request own deadline, which is the longest window this Tool can hold. A wait window is one in-flight read request, so other sessions' mutation requests cannot start until it returns; keep waitMs short and repeat this read instead of asking for one long window. A wait window that expires is a normal observation, not a failure: the result reports status running with the current steps and elapsed time, which is what separates it from a host request execution timeout. Terminal states are completed, failed, timed-out, cancelled and aborted, and repeating a read after the terminal returns the same facts because a terminal state never changes. flowBudgetMs is the flow's own lifetime budget counted from its first tick excluding compilation, while waitMs bounds only this call; a flow that keeps running past waitMs is not cancelled and not penalised. Only a completed flow answers ok:true. A flow that is still running answers E_DEBUG_FLOW_PENDING with flowId and the stepped facts, and failed, timed-out, cancelled and aborted flows answer E_DEBUG_FLOW_FAILED, E_DEBUG_FLOW_TIMEOUT, E_DEBUG_FLOW_CANCELLED and E_DEBUG_FLOW_ABORTED; every one of those errors carries flowId, status and the terminal facts. Rejections: E_DEBUG_FLOW_NOT_FOUND for a flowId that does not exist in the current Editor session, and E_SCHEMA_INVALID for a waitMs outside the declared budget. A flow does not survive a Domain Reload, an Editor restart or a Play mode exit, and terminal records are kept only for a bounded number of flows.",
  group: "debug",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "editor",
  timeoutMs: 140_000,
  examples: [{
    description: "Wait up to ten seconds for a flow to become terminal.",
    argv: ["tools", "run", "debug.code-flow-wait", "--input", JSON.stringify(waitExample)],
    input: waitExample as unknown as AnyJson,
  }, {
    description: "Read the current facts of a flow without waiting by asking for a zero-length window; a flow that is still running answers E_DEBUG_FLOW_PENDING with its stepped facts.",
    argv: ["tools", "run", "debug.code-flow-wait", "--input", JSON.stringify(readExample)],
    input: readExample as unknown as AnyJson,
  }],
  async execute(request, context) {
    return executeRuntimeFlowWait({
      flowId: request.flowId,
      waitMs: request.waitMs,
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
