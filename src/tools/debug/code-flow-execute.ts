import { executeRuntimeFlow } from "../../runtime-flow.js";
import type { AnyJson } from "../../schema/any-json.js";
import { defineTool, type ToolContext } from "../define.js";
import type { CodeExecuteAsyncDryRunResult, CodeExecuteAsyncRequest, CodeFlowResult } from "./code-flow-types.js";

/** 读取命令层 instance flag；它不进入流工具的业务 Request。 */
function instanceFlag(context: ToolContext): string | undefined {
  const value = context.flags?.instance;
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

const frameWaitExample: CodeExecuteAsyncRequest = {
  code: "yield return DebugFlow.NextFrame(3); yield return DebugFlow.Return(UnityEngine.Time.frameCount);",
  waitMs: 15000,
};

const conditionExample: CodeExecuteAsyncRequest = {
  code: "for (var i = 0; i < 10; i++) { yield return DebugFlow.Until(() => UnityEditor.EditorApplication.isCompiling == false, 3000); if (DebugFlow.Last.TimedOut) break; } yield return DebugFlow.Return(true);",
  waitMs: 20000,
  flowBudgetMs: 60000,
};

async function runFlow(request: CodeExecuteAsyncRequest, context: ToolContext): Promise<CodeFlowResult | CodeExecuteAsyncDryRunResult> {
  return executeRuntimeFlow({
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
  }, context.requestId) as unknown as Promise<CodeFlowResult | CodeExecuteAsyncDryRunResult>;
}

export default defineTool<CodeExecuteAsyncRequest, CodeFlowResult | CodeExecuteAsyncDryRunResult>({
  name: "debug.code.flow.execute",
  summary: "Submit trusted C# that runs across frames, returning its terminal result or a flowId to wait on.",
  description: "Compile trusted C# 8 once and run it as a flow that advances on the single central editor tick, in both Edit mode and Play mode. The body becomes `public static IEnumerator Run(string argsJson)` inside `namespace UnityCli.RuntimeCode { public static class Script { } }`; usings for System, System.Collections, System.Collections.Generic, System.IO, System.Linq, UnityEngine, UnityEditor and UnityCli are already present, so argsJson is the only pre-declared identifier. Cross frames only through UnityCli.DebugFlow: DebugFlow.NextFrame(n), DebugFlow.After(ms, timeoutMs), DebugFlow.Until(condition, timeoutMs), DebugFlow.UntilSignal(name, timeoutMs), DebugFlow.UntilLog(pattern, timeoutMs, level), DebugFlow.All(...), DebugFlow.Any(...) and DebugFlow.Return(value). UntilLog matches Unity log messages by case-insensitive substring, optionally with a minimum level of log, warning or error, considers only messages emitted after the wait starts, and keeps a bounded 256-entry ring with 512-byte messages, so a log flood costs one substring test per message and cannot grow memory without bound. Every timeout is required and has no default; conditions are C# delegates, not strings. Each wait settles as a DebugFlowWaitResult whose C# properties are Satisfied, TimedOut, ElapsedMs, Samples and LastObserved, readable as DebugFlow.Last right after the wait resumes, so a timed-out wait is a value the flow decides on instead of an exception; yielding null waits one tick. NextFrame(n) waits until Unity Time.frameCount has advanced by n while the Editor is playing, and in Edit mode, where no frame counter advances, it falls back to n editor ticks. A Play-mode frame wait that cannot advance within 4000 ms settles as timedOut with lastObserved set to the current frameCount, so the flow decides instead of consuming its whole budget. Only a completed flow answers ok:true. A flow that is still running answers E_DEBUG_FLOW_PENDING with flowId and the stepped facts, and failed, timed-out, cancelled and aborted flows answer E_DEBUG_FLOW_FAILED, E_DEBUG_FLOW_TIMED_OUT, E_DEBUG_FLOW_CANCELLED and E_DEBUG_FLOW_ABORTED; every one of those errors carries flowId, status and the terminal facts. Timing: waitMs bounds only this call and its expiry returns status running with the same flowId, resubmitting creates a second flow and is never the recovery path; flowBudgetMs bounds the flow itself from its first tick, excludes compilation and does not count time while a debug pause holds the main thread. Mode gate: requiresPlayMode accepts any (default), play or edit and is checked before compilation, so a wrong mode answers E_DEBUG_FLOW_MODE_MISMATCH without spending compile or artifact budget; a flow never spans a mode change because entering or leaving Play reloads the domain, so cross-mode orchestration belongs to the caller. Concurrency: at most 4 active flows per session when sessionId is supplied and 8 per Unity instance, refused immediately rather than queued, because every flow samples on the main thread and extra concurrency makes the observed timing less trustworthy. A flow submitted from inside a running flow is refused with E_DEBUG_FLOW_NESTED. Terminal states are completed, failed, timed-out, cancelled and aborted and never change once published; a Domain Reload, an Editor restart or a Play mode exit leaves the flow unobservable. A condition that throws fails the flow with failureTag predicate-exception instead of being retried as false. Dry-run reports the source hash and planned effects only; it neither compiles nor executes.",
  group: "debug",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["user-code-execution", "scene-modify", "asset-modify", "debug-activation"],
  playModeSupport: "supported",
  permissions: ["execute-user-code", "unity-editor-api"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "editor",
  timeoutMs: 140_000,
  examples: [{
    description: "Wait three frames and return the frame count.",
    argv: ["tools", "run", "debug.code.flow.execute", "--input", JSON.stringify(frameWaitExample)],
    input: frameWaitExample as unknown as AnyJson,
  }, {
    description: "Poll a condition across frames and report whether it settled.",
    argv: ["tools", "run", "debug.code.flow.execute", "--input", JSON.stringify(conditionExample)],
    input: conditionExample as unknown as AnyJson,
  }],
  async execute(request, context) { return runFlow(request, context); },
});
