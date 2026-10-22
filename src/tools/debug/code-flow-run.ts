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
  name: "debug.code.flow.run",
  summary: "Submit trusted C# that runs across frames and wait for its terminal result in the same call.",
  description: "Compile trusted C# 8 once and run it as a flow that advances on the single central editor tick, then wait for its terminal state. The body becomes `public static IEnumerator Run(string argsJson)` inside `namespace UnityCli.RuntimeCode { public static class Script { } }`; usings for System, System.Collections, System.Collections.Generic, System.IO, System.Linq, UnityEngine, UnityEditor and UnityCli are already present, so argsJson is the only pre-declared identifier. " + REFERENCE_SET_DISCLOSURE + " Cross frames only through UnityCli.DebugFlow: DebugFlow.NextFrame(n), DebugFlow.After(ms, timeoutMs), DebugFlow.Until(condition, timeoutMs), DebugFlow.UntilSignal(name, timeoutMs), DebugFlow.UntilLog(pattern, timeoutMs, level), DebugFlow.All(...), DebugFlow.Any(...) and DebugFlow.Return(value). Every timeout is required and has no default; conditions are C# delegates, not strings; yielding null waits one tick. Each wait settles as a DebugFlowWaitResult whose C# properties are Satisfied, TimedOut, ElapsedMs, Samples and LastObserved, readable as DebugFlow.Last right after the wait resumes. NextFrame waits real Unity Time.frameCount increments while the Editor is playing and falls back to editor ticks in Edit mode, where no frame counter advances; a Play-mode frame wait that cannot advance within 4000 ms settles as timedOut. Timing and completion: flowBudgetMs defaults to 300000 ms and is bounded to 3600000 ms, counted from the first tick and excluding compilation, and it does not count time while a debug pause holds the main thread. This Tool extends its own request deadline to cover flowBudgetMs plus a 60000 ms margin, so it always reaches a terminal state instead of returning a half-finished handle; if the resulting budget cannot fit the transport deadline it refuses before compiling with E_SCHEMA_INVALID and suggests debug.code.flow.submit plus debug.code.flow.wait. Output: status, flowId, elapsedMs, steps, the terminal result or the failure tag plus error code and message, the last observed wait value, the sample count, the budget and its consumed share, and the current concurrency usage. Only a completed flow answers ok:true; failed, timed-out, cancelled and aborted answer E_DEBUG_FLOW_FAILED, E_DEBUG_FLOW_TIMEOUT, E_DEBUG_FLOW_CANCELLED and E_DEBUG_FLOW_ABORTED with flowId, status and the terminal facts. Mode gate: requiresPlayMode accepts any (default), play or edit and is checked before compilation, so a wrong mode answers E_DEBUG_FLOW_MODE_MISMATCH without spending compile or artifact budget. Concurrency: at most 4 active flows per session when sessionId is supplied and 8 per Unity instance, refused immediately rather than queued. Terminal states never change once published; a Domain Reload, an Editor restart or a Play mode exit makes the flow unobservable. A condition that throws fails the flow with failureTag predicate-exception. Rejections: E_CODE_SOURCE_INVALID, E_CODE_COMPILE_FAILED, E_REFERENCE_RESOLUTION_FAILED, E_CODE_ENTRYPOINT_INVALID, E_UNITY_BUSY, the compiler budget code, E_DEBUG_FLOW_LIMIT_EXCEEDED, E_DEBUG_FLOW_NESTED and E_SCHEMA_INVALID.",
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
    argv: ["tools", "run", "debug.code.flow.run", "--input", JSON.stringify(frameRunExample)],
    input: frameRunExample as unknown as AnyJson,
  }, {
    description: "Wait until the Editor finishes compiling and return whether it settled.",
    argv: ["tools", "run", "debug.code.flow.run", "--input", JSON.stringify(conditionRunExample)],
    input: conditionRunExample as unknown as AnyJson,
  }],
  async execute(request, context) { return runFlow(request, context); },
});
