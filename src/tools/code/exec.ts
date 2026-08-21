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
  runtime: { returnValue: AnyJson; stdout: string; stderr: string; stdoutTruncated: boolean; stderrTruncated: boolean };
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
  }, context.requestId) as unknown as Promise<CodeExecResult | CodeExecDryRunResult>;
}

export default defineTool<CodeExecRequest, CodeExecResult | CodeExecDryRunResult>({
  name: "code.exec",
  summary: "Execute trusted synchronous C# in the live Editor; started code cannot be force-stopped by timeout.",
  description: "Synchronously compile and execute trusted C# 8 on Unity's main thread. Submit code, file, or stdin; snippets become Run(string argsJson) bodies and must return, while complete Run methods compile as-is. Returns diagnostics, streams/value, and optional completion readback; completionProbe is optional and only asserts declared Editor state. The Tool has no capability reduction and does not reject infinite loops, long sleeps, blocking waits, lock contention, process/network/file work, or long Unity API calls. Prefer a bounded alternative when possible; necessary code executes unchanged. Timeout stops only CLI waiting and cannot interrupt Run. State may be partial or outcome-unknown, not proof execution stopped; inspect before retrying. Recovery may require terminating the Editor. External command hosts must wait for this CLI process to exit and allow at least 5000ms beyond --timeout-ms. Dry-run reports source hash and planned effects only; it neither compiles nor executes or proves termination.",
  group: "code",
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
    argv: ["tools", "run", "code.exec", "--input", JSON.stringify(returnValueExample)],
    input: returnValueExample as unknown as AnyJson,
  }, {
    description: "Move a controlled scene object and prove its X position.",
    argv: ["tools", "run", "code.exec", "--input", JSON.stringify(moveProbeExample)],
    input: moveProbeExample as unknown as AnyJson,
  }],
  async execute(request, context) { return runCodeExec(request, context); },
});
