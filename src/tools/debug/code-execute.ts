import { executeRuntimeCode } from "../../runtime-code.js";
import { REFERENCE_SET_DISCLOSURE } from "./code-reference-set.js";
import type { AnyJson } from "../../schema/any-json.js";
import type { CompletionProbe, CompletionProbeReadback } from "../completion-probe.js";
import { defineTool, type ToolContext } from "../define.js";

export interface CodeExecRequest {
  file?: string;
  stdin?: boolean;
  code?: string;
  refs?: string[];
  define?: string[];
  args?: CodeExecJsonObject;
  completionProbe?: CompletionProbe;
}

export interface CodeExecJsonObject { [key: string]: AnyJson; }
export interface CodeExecDiagnostic { severity: string; id: string; message: string; file: string; line: number; column: number; span: AnyJson; }
export interface CodeExecResult {
  compile: { diagnostics: CodeExecDiagnostic[] };
  runtime: { returnValue: AnyJson; returnValueTruncated: boolean; returnValueByteBudget: number; stdout: string; stderr: string; stdoutTruncated: boolean; stderrTruncated: boolean };
  completion: CompletionProbeReadback | null;
}
export interface CodeExecDryRunResult {
  dryRun: true;
  preview: { sourceKind: string; sourcePath?: string; sourceSha256: string; sourceSizeBytes: number; timeoutMs: number; args: CodeExecJsonObject };
  plannedEffects: Array<{ type: "debug.code-execute"; sideEffects: string }>;
}

/** 读取命令层 instance flag；它不进入 debug.code-execute 业务 Request。 */
function instanceFlag(context: ToolContext): string | undefined {
  const value = context.flags?.instance;
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

const returnValueExample: CodeExecRequest = {
  code: "return UnityEngine.SceneManagement.SceneManager.GetActiveScene().rootCount;",
};

const moveProbeExample: CodeExecRequest = {
  code: "var go = GameObject.Find(\"ProbeTarget\"); go.transform.position = new Vector3(1f, go.transform.position.y, go.transform.position.z); return null;",
  completionProbe: {
    readTool: "scene.gameobject",
    input: { target: "ProbeTarget", includeComponents: false },
    assertions: [{ path: "/gameObject/transform/position/x", operator: "approx", before: 0, after: 1, tolerance: 0.0001 }],
  },
};

/** 仅在已打开 Editor 中执行一次可信 C# Runtime Code。 */
async function runCodeExec(request: CodeExecRequest, context: ToolContext): Promise<CodeExecResult | CodeExecDryRunResult> {
  return executeRuntimeCode({
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
  }, context.requestId) as unknown as Promise<CodeExecResult | CodeExecDryRunResult>;
}

export default defineTool<CodeExecRequest, CodeExecResult | CodeExecDryRunResult>({
  name: "debug.code-execute",
  summary: "Execute trusted synchronous C# in the live Editor; started code cannot be force-stopped by timeout.",
  description: "Synchronously compile and execute trusted C# 8 on Unity's main thread. Code, file or stdin become the body of exactly `public static object Run(string argsJson)` inside `namespace UnityCli.RuntimeCode { public static class Script { } }` with usings System, System.IO, System.Linq, System.Collections.Generic, UnityEngine, UnityEditor already present; the wrapper declares no locals, so argsJson is the only pre-declared identifier and the body must return a value or throw. " + REFERENCE_SET_DISCLOSURE + " The whole body is one method-body scope: a name declared at its top level cannot be redeclared in a nested block, loop, lambda or local function (CS0136). A source whose text contains `public static object Run(` is placed in the class as-is instead, so it must declare that complete method. Returns diagnostics, streams/value, readback; runtime.returnValueTruncated plus runtime.returnValueByteBudget state whether the returned value was cut and the applied UTF-8 byte budget for a returned string; completionProbe is optional and asserts declared Editor state. No capability reduction: it does not reject infinite loops, long sleeps, blocking waits, lock contention. Prefer a bounded alternative when possible; necessary code executes unchanged. Timeout stops only CLI waiting, cannot interrupt Run. A main-thread pause withholds the terminal result: ok:false with error.code E_DEBUG_MAIN_THREAD_PAUSED and error.details.outcome and .dispatch \"may-have-started\" plus pauseId, threadId, timeoutKind and remainingMs; that partial outcome is neither a failure nor an unknown outcome. Read and release with debug.pause-list then debug.pause-resume. Each off-main control wait is bounded to 30000 ms while the main thread is paused, so a caller that needs the terminal result must keep the declared pause under that bound or pause from a dispatch site whose creation result is answered before the pause starts; a pause longer than the remaining --timeout-ms yields this partial outcome. State may be partial or outcome-unknown, not proof execution stopped. Recovery may require terminating the Editor. External command hosts must wait for this CLI process to exit and allow at least 5000ms beyond --timeout-ms. Dry-run reports source hash and planned effects only; it neither compiles nor executes.",
  group: "debug",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["user-code-execution", "scene-modify", "asset-modify", "session-affecting"],
  playModeSupport: "session-affecting",
  permissions: ["execute-user-code", "unity-editor-api"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "editor",
  timeoutMs: 60_000,
  examples: [{
    description: "Execute C# and return a value (no completion probe needed).",
    argv: ["tools", "run", "debug.code-execute", "--input", JSON.stringify(returnValueExample)],
    input: returnValueExample as unknown as AnyJson,
  }, {
    description: "Move a controlled scene object and prove its X position.",
    argv: ["tools", "run", "debug.code-execute", "--input", JSON.stringify(moveProbeExample)],
    input: moveProbeExample as unknown as AnyJson,
  }],
  async execute(request, context) { return runCodeExec(request, context); },
});
