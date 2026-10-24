import { executeRuntimeFlowSubmit } from "../../runtime-flow.js";
import type { AnyJson } from "../../schema/any-json.js";
import { defineTool, type ToolContext } from "../define.js";
import type { CodeFlowDryRunResult, CodeFlowRequest, CodeFlowSubmitResult } from "./code-flow-types.js";
import { REFERENCE_SET_DISCLOSURE } from "./code-reference-set.js";

/** 读取命令层 instance flag；它不进入流工具的业务 Request。 */
function instanceFlag(context: ToolContext): string | undefined {
  const value = context.flags?.instance;
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

const submitExample: CodeFlowRequest = {
  code: "yield return DebugFlow.NextFrame(60); yield return DebugFlow.Return(UnityEngine.Time.frameCount);",
  flowBudgetMs: 120000,
};

async function submitFlow(request: CodeFlowRequest, context: ToolContext): Promise<CodeFlowSubmitResult | CodeFlowDryRunResult> {
  return executeRuntimeFlowSubmit({
    ...request,
    args: request.args as Record<string, unknown> | undefined,
    timeoutMs: context.execution.timeoutMs,
    absoluteDeadlineMs: context.absoluteDeadlineMs,
    dryRun: context.execution.dryRun,
    instance: instanceFlag(context),
  }, {
    dataDir: context.dataDir ?? context.projectPath,
    rootAdmission: context.rootAdmission,
    bridgeExecute: context.bridgeExecute,
    commitEffect: context.commitEffect,
  }, context.requestId) as unknown as Promise<CodeFlowSubmitResult | CodeFlowDryRunResult>;
}

export default defineTool<CodeFlowRequest, CodeFlowSubmitResult | CodeFlowDryRunResult>({
  name: "debug.code-flow-submit",
  summary: "Submit trusted C# that runs across frames and return its flowId immediately, without waiting for a result.",
  description: "Compile trusted C# 8 once, register it as a flow that advances on the single central editor tick, and return a machine-readable receipt with type=accepted and complete=false without waiting. The body becomes `public static IEnumerator Run(string argsJson)` inside `namespace UnityCli.RuntimeCode { public static class Script }`, with usings for System, System.Collections, System.Linq, UnityEngine, UnityEditor and UnityCli already present. Cross frames only through UnityCli.DebugFlow; every timeout is required. The receipt carries flowId, sessionId, budgetMs, concurrency usage, editorSessionId and followUpTool=debug.code-flow-wait; registration is this call's terminal outcome, not a result. Read the flow with debug.code-flow-wait using that flowId and never resubmit to obtain a result; use debug.code-flow-run when one call should own the whole wait. flowBudgetMs defaults to 300000 ms and is bounded to 3600000 ms. At most 4 active flows per session and 8 per instance; overflow is refused. Fails with E_CODE_SOURCE_INVALID, E_CODE_COMPILE_FAILED, E_REFERENCE_RESOLUTION_FAILED, E_CODE_ENTRYPOINT_INVALID, E_UNITY_BUSY, E_DEBUG_FLOW_LIMIT_EXCEEDED or E_DEBUG_FLOW_NESTED.",
  group: "debug",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["user-code-execution", "scene-modify", "asset-modify", "debug-activation"],
  playModeSupport: "supported",
  permissions: ["execute-user-code", "unity-editor-api"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "editor",
  timeoutMs: 90_000,
  examples: [{
    description: "Submit a flow and return its handle for later reading.",
    argv: ["tools", "run", "debug.code-flow-submit", "--input", JSON.stringify(submitExample)],
    input: submitExample as unknown as AnyJson,
  }],
  async execute(request, context) { return submitFlow(request, context); },
});
