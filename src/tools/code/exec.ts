import { executeRuntimeCode } from "../../runtime-code.js";
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
  runtime: { returnValue: AnyJson; stdout: string; stderr: string };
  completion: CompletionProbeReadback | null;
}
export interface CodeExecDryRunResult {
  dryRun: true;
  preview: { sourceKind: string; sourcePath?: string; sourceSha256: string; sourceSizeBytes: number; timeoutMs: number; args: CodeExecJsonObject };
  plannedEffects: Array<{ type: "code.exec"; sideEffects: string }>;
}

/** 读取命令层 instance flag；它不进入 code.exec 业务 Request。 */
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
    context: "scene.gameobject",
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
    dryRun: context.execution.dryRun,
    instance: instanceFlag(context),
  }, {
    dataDir: context.dataDir ?? context.projectPath,
    bridgeExecute: context.bridgeExecute,
  }, context.requestId) as unknown as Promise<CodeExecResult | CodeExecDryRunResult>;
}

export default defineTool<CodeExecRequest, CodeExecResult | CodeExecDryRunResult>({
  name: "code.exec",
  summary: "Execute trusted synchronous C# in the live Editor and optionally prove declared state.",
  description: "The snippet is injected as the body of public static object Run(string argsJson) inside UnityCli.RuntimeCode.Script (common usings: System, System.IO, System.Linq, System.Collections.Generic, UnityEngine, UnityEditor are pre-imported). It must end with a return statement; the returned value is surfaced via runtime.returnValue. If the source already contains a full public static object Run( definition it is compiled as-is. argsJson carries the --input args JSON. Compiler: Unity-bundled Roslyn csc (C# 8), the same compiler Unity uses for project scripts; local functions, out var, pattern matching, and similar features work. completionProbe is optional: omit it for plain execute-and-return usage such as logging, reading or writing runtime state, and Editor API calls; provide it only when you need to assert an Editor state change. A call that times out before terminal proof returns E_EXECUTION_TIMEOUT; inspect state and decide whether to submit a new call. Each execution loads one small in-memory assembly into the Editor AppDomain which cannot be unloaded until the next domain reload; avoid very high-frequency loops. Use dry-run to inspect the source hash before execution.",
  group: "code",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["user-code-execution", "scene-modify", "asset-modify", "session-affecting"],
  playModeSupport: "session-affecting",
  permissions: ["execute-user-code", "unity-editor-api"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "editor",
  examples: [{
    description: "Execute C# and return a value (no completion probe needed).",
    argv: ["tools", "run", "code.exec", "--input", JSON.stringify(returnValueExample)],
    input: returnValueExample as unknown as AnyJson,
  }, {
    description: "Move a controlled scene object and prove its X position.",
    argv: ["tools", "run", "code.exec", "--input", JSON.stringify(moveProbeExample)],
    input: moveProbeExample as unknown as AnyJson,
  }],
  async execute(request, context) { return runCodeExec(request, context); },
});
