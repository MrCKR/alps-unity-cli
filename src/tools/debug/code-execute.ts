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
  description: "Compile and execute trusted C# 8 synchronously on Unity's main thread. Code, file or stdin becomes the body of `public static object Run(string argsJson)` inside `namespace UnityCli.RuntimeCode { public static class Script }`; usings System, System.IO, System.Linq, System.Collections.Generic, UnityEngine and UnityEditor are already present, argsJson is the only pre-declared identifier, and the body must return a value or throw. A source containing `public static object Run(` is inserted as-is and must declare that complete method. Returns diagnostics, streams and the runtime value with returnValueTruncated and returnValueByteBudget readback; completionProbe optionally asserts declared Editor state. Timeout stops only CLI waiting and cannot interrupt Run. A main-thread pause withholds the terminal result as ok:false with error.code E_DEBUG_MAIN_THREAD_PAUSED, outcome and dispatch \"may-have-started\" plus pauseId, which is neither a failure nor a proven unknown outcome; release it with debug.pause-list then debug.pause-resume. Dry-run reports the source hash and planned effects only and never compiles or executes.",
  notes: ["References: every locatable assembly already loaded in the Editor AppDomain is referenced for the whole domain generation and refs only appends more; nothing can be removed, so a type name that another loaded assembly also declares is a CS0433 ambiguity (System.Tuple exists in both mscorlib and ExCSS.Unity) and you must use object[] or a type you declare.", "Scope: the whole body is one method-body scope, so a name declared at its top level cannot be redeclared in a nested block, loop, lambda or local function (CS0136).", "No capability reduction: infinite loops, long sleeps, blocking waits and lock contention are not rejected. Timeout stops only CLI waiting and cannot interrupt Run, so state may be partial or outcome-unknown rather than proof that execution stopped, and recovery may require terminating the Editor. While the main thread is paused, each off-main control wait is bounded to 30000 ms, so a pause longer than the remaining --timeout-ms yields the partial E_DEBUG_MAIN_THREAD_PAUSED outcome. External command hosts must wait for this CLI process to exit and allow at least 5000 ms beyond --timeout-ms."],
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
