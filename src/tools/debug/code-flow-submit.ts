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
  description: "Compile trusted C# 8 once, register it as a flow that advances on the single central editor tick, and return a receipt without waiting. The body becomes `public static IEnumerator Run(string argsJson)` inside `namespace UnityCli.RuntimeCode { public static class Script { } }`; usings for System, System.Collections, System.Collections.Generic, System.IO, System.Linq, UnityEngine, UnityEditor and UnityCli are already present. " + REFERENCE_SET_DISCLOSURE + " Cross frames only through UnityCli.DebugFlow: NextFrame(n), After(ms, timeoutMs), Until(condition, timeoutMs), UntilSignal(name, timeoutMs), UntilLog(pattern, timeoutMs, level), All(...), Any(...) and Return(value); every timeout is required and conditions are C# delegates. Completion meaning: registration is the terminal outcome of this Tool and it is not a result. The receipt carries registered, flowId, sessionId, budgetMs, startedMonotonicMs, the current concurrency usage and the editorSessionId, and deliberately carries no status field, because a half-finished flow is not an acceptable success of a submit. Read the flow with debug.code-flow-wait using the returned flowId; never resubmit to obtain a result, because that creates a second flow. Use this Tool when a single long request would be killed by the caller's own tool timeout, and use debug.code-flow-run when one call should own the whole wait. Timing: flowBudgetMs defaults to 300000 ms and is bounded to 3600000 ms, counted from the first tick and excluding compilation; this Tool's request only needs to cover compilation, so it answers in seconds even for a long flow. Mode gate: requiresPlayMode accepts any (default), play or edit and is checked before compilation. Concurrency: at most 4 active flows per session when sessionId is supplied and 8 per Unity instance, refused immediately rather than queued. Rejections: E_CODE_SOURCE_INVALID, E_CODE_COMPILE_FAILED, E_REFERENCE_RESOLUTION_FAILED, E_CODE_ENTRYPOINT_INVALID, E_UNITY_BUSY, the compiler budget code, E_DEBUG_FLOW_LIMIT_EXCEEDED, E_DEBUG_FLOW_NESTED, E_DEBUG_FLOW_MODE_MISMATCH and E_SCHEMA_INVALID.",
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
