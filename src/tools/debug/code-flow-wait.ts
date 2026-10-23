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
  description: "Read the current facts of exactly one flowId, optionally waiting a bounded time for it to become terminal. It never compiles, re-executes, creates or cancels a flow, so it is safe to repeat while a flow runs. waitMs bounds only this call: 0 returns immediately, a small value is a short window, and omitting it waits until this request's own deadline. A wait window is one in-flight read, so other sessions' mutations cannot start until it returns; keep waitMs short and repeat instead of holding one long window. An expired window is a normal observation that reports status running, which separates it from a request execution timeout. flowBudgetMs is the flow's own lifetime budget. Terminal states completed, failed, timed-out, cancelled and aborted never change, so a repeated read returns the same facts. Only a completed flow answers ok:true; running answers E_DEBUG_FLOW_PENDING, and the other terminal states answer E_DEBUG_FLOW_FAILED, E_DEBUG_FLOW_TIMEOUT, E_DEBUG_FLOW_CANCELLED or E_DEBUG_FLOW_ABORTED with flowId, status and terminal facts. A flow does not survive a Domain Reload, Editor restart or Play mode exit, and terminal records are bounded.",
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
