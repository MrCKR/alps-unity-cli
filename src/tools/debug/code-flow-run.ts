import { executeRuntimeFlowRun } from "../../runtime-flow.js";
import type { AnyJson } from "../../schema/any-json.js";
import { defineTool, type ToolContext } from "../define.js";
import type { CodeFlowDryRunResult, CodeFlowRequest, CodeFlowResult } from "./code-flow-types.js";
import { REFERENCE_SET_DISCLOSURE } from "./code-reference-set.js";

/** 读取命令层 instance flag；它不进入流工具的业务 Request。 */
function instanceFlag(context: ToolContext): string | undefined {
  const value = context.flags?.instance;
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

const frameRunExample: CodeFlowRequest = {
  code: "var start = UnityEngine.Time.frameCount; yield return DebugFlow.Until(() => UnityEngine.Time.frameCount >= start + 3, 5000); yield return DebugFlow.Return(new object[] { start, UnityEngine.Time.frameCount });",
  flowBudgetMs: 30000,
};

const conditionRunExample: CodeFlowRequest = {
  code: "yield return DebugFlow.Until(() => UnityEditor.EditorApplication.isCompiling == false, 5000); yield return DebugFlow.Return(DebugFlow.Last.Satisfied);",
};

async function runFlow(request: CodeFlowRequest, context: ToolContext): Promise<CodeFlowResult | CodeFlowDryRunResult> {
  return executeRuntimeFlowRun({
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
  }, context.requestId) as unknown as Promise<CodeFlowResult | CodeFlowDryRunResult>;
}

export default defineTool<CodeFlowRequest, CodeFlowResult | CodeFlowDryRunResult>({
  name: "debug.code-flow-run",
  summary: "Submit trusted C# that runs across frames and wait for its terminal result in the same call.",
  description: "Compile trusted C# 8 once and run it as a flow that advances on the single central editor tick, then wait for its terminal state. The body becomes public static IEnumerator Run(string argsJson) inside amespace UnityCli.RuntimeCode { public static class Script }. Cross frames only through UnityCli.DebugFlow (NextFrame, After, Until, UntilSignal, UntilLog, All, Any, Return); every timeout is required and conditions are C# delegates. flowBudgetMs defaults to 300000 ms and is bounded to 3600000 ms. This Tool extends its own request deadline to cover flowBudgetMs plus a 60000 ms margin so it always reaches a terminal state; if that cannot fit the transport deadline it refuses before compiling with E_SCHEMA_INVALID. Output: status, flowId, elapsedMs, steps, the terminal result or failure tag, the last observed wait value and the budget. Only a completed flow answers ok:true; other terminal states answer E_DEBUG_FLOW_FAILED, E_DEBUG_FLOW_TIMEOUT, E_DEBUG_FLOW_CANCELLED or E_DEBUG_FLOW_ABORTED. Rejections: E_CODE_SOURCE_INVALID, E_CODE_COMPILE_FAILED, E_CODE_ENTRYPOINT_INVALID, E_UNITY_BUSY, E_DEBUG_FLOW_LIMIT_EXCEEDED, E_DEBUG_FLOW_NESTED or E_SCHEMA_INVALID.",
  notes: ["Each wait settles as a DebugFlowWaitResult whose C# properties are Satisfied, TimedOut, ElapsedMs, Samples and LastObserved, readable as DebugFlow.Last right after the wait resumes. NextFrame waits real Unity Time.frameCount increments while the Editor is playing and falls back to editor ticks in Edit mode, where no frame counter advances; a Play-mode frame wait that cannot advance within 4000 ms settles as timedOut. A condition that throws fails the flow with failureTag predicate-exception. Terminal states never change once published, and a Domain Reload, Editor restart or Play mode exit makes the flow unobservable."],
  group: "debug",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["user-code-execution", "scene-modify", "asset-modify", "debug-activation"],
  playModeSupport: "supported",
  permissions: ["execute-user-code", "unity-editor-api"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "editor",
  timeoutMs: 360_000,
  examples: [{
    description: "Wait for three real frames and return the frame numbers seen before and after.",
    argv: ["tools", "run", "debug.code-flow-run", "--input", JSON.stringify(frameRunExample)],
    input: frameRunExample as unknown as AnyJson,
  }, {
    description: "Wait until the Editor finishes compiling and return whether it settled.",
    argv: ["tools", "run", "debug.code-flow-run", "--input", JSON.stringify(conditionRunExample)],
    input: conditionRunExample as unknown as AnyJson,
  }],
  async execute(request, context) { return runFlow(request, context); },
});
