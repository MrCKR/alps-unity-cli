import { defineTool, type ToolRunInvocation } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { CliResult } from "../../types.js";

export interface ToolsRunRequest {
  name: string;
  input?: string;
  inputFile?: string;
  timeoutMs?: number;
  stdin?: boolean;
  fingerprintMode?: string;
  runtime?: boolean;
  target?: string;
  instance?: string;
  bridgeEndpoint?: string;
  project?: string;
  artifactDir?: string;
  job?: boolean;
  dryRun?: boolean;
  yes?: boolean;
  positionalTarget?: string;
}

export interface ToolsRunResult {
  command?: string;
}

const ROUTER_ONLY_FLAGS = new Set(["name", "tool", "input", "input-file", "stdin", "__structuredInput", "raw"]);

function setStringFlag(flags: Record<string, string | boolean>, key: string, value: unknown): void {
  if (typeof value === "string" && value.length > 0) flags[key] = value;
}

function setBooleanFlag(flags: Record<string, string | boolean>, key: string, value: unknown): void {
  if (typeof value === "boolean") flags[key] = value;
}

function setNumberFlag(flags: Record<string, string | boolean>, key: string, value: unknown): void {
  if (typeof value === "number" && Number.isFinite(value)) flags[key] = String(value);
}

function encodeTargetInput(value: unknown): string | undefined {
  if (value === undefined) return undefined;
  if (typeof value === "string") return value;
  return JSON.stringify(value);
}

// 从外层 tools.run 请求构造目标工具 flags,避免把外层 name/input 泄漏给目标工具。
function buildTargetFlags(request: ToolsRunRequest, contextFlags: Record<string, string | boolean> | undefined): Record<string, string | boolean> {
  const flags: Record<string, string | boolean> = {};
  for (const [key, value] of Object.entries(contextFlags ?? {})) {
    if (!ROUTER_ONLY_FLAGS.has(key)) flags[key] = value;
  }

  const rawRequest = request as unknown as Record<string, unknown>;
  const targetInput = encodeTargetInput(rawRequest.input);
  if (targetInput !== undefined) flags.input = targetInput;
  setStringFlag(flags, "input-file", request.inputFile);
  setBooleanFlag(flags, "stdin", request.stdin);
  setNumberFlag(flags, "timeout-ms", request.timeoutMs);
  setStringFlag(flags, "fingerprint-mode", request.fingerprintMode);
  setBooleanFlag(flags, "runtime", request.runtime);
  setStringFlag(flags, "target", request.target);
  setStringFlag(flags, "instance", request.instance);
  setStringFlag(flags, "bridge-endpoint", request.bridgeEndpoint);
  setStringFlag(flags, "project", request.project);
  setStringFlag(flags, "artifact-dir", request.artifactDir);
  setBooleanFlag(flags, "job", request.job);
  setBooleanFlag(flags, "dry-run", request.dryRun);
  setBooleanFlag(flags, "yes", request.yes);
  return flags;
}

function throwNestedFailure(result: CliResult): never {
  const error = result.error;
  if (!error) {
    throw new StructuredToolError("COMMAND_FAILED", "Nested tools.run failed without an error envelope.");
  }
  const { code, message, nextActions, requestId: _requestId, transient: _transient, retryAfterMs: _retryAfterMs, details, ...rest } = error as Record<string, unknown> & { code: string; message: string; nextActions?: string[] };
  const extraDetails = Object.keys(rest).length > 0 || details !== undefined ? { ...rest, ...(details !== undefined ? { details } : {}) } : undefined;
  throw new StructuredToolError(code, message, Array.isArray(nextActions) ? nextActions : [], extraDetails);
}

export default defineTool<ToolsRunRequest, ToolsRunResult>({
  name: "tools.run",
  summary: "Execute another tool by dotted name through the unified router.",
  description: "Execute another tool by dotted name through the unified tools router. This wrapper preserves the same routing as direct tools run <name> calls: migrated defineTool handlers, legacy offline handlers, Bridge tools, job handlers, runtime tools, and project custom tools. Put the target tool payload in input as a JSON string or object; outer router fields such as name, target, instance, runtime, job, dryRun, and yes control execution routing and safety. Bridge tools are scheduled onto Unity's main thread; if the Editor is compiling, importing, switching Play Mode, or domain reloading, inspect editor.state/waitReady and retry after the busy state clears. Runtime target tools require Play Mode; otherwise the CLI returns transient E_RUNTIME_NOT_READY.",
  group: "tools",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "both",
  examples: [
    { description: "Run an editor-domain tool", argv: ["tools", "run", "tools.run", "--input", "{\"name\":\"scene.hierarchy\",\"target\":\"my-editor\"}"] },
    { description: "Run a migrated offline tool", argv: ["tools", "run", "tools.run", "--input", "{\"name\":\"docs.topic\",\"input\":{\"topic\":\"scene\"}}"] }
  ],
  async execute(request, ctx) {
    if (!request.name) {
      throw new StructuredToolError("E_TOOL_NOT_FOUND_FOR_TARGET", "Tool name is required for tools.run.", [
        "Run unity_cli tools group to see tool groups.",
        "Pass name as the target dotted tool name, for example {\"name\":\"docs.list\"}."
      ]);
    }
    if (request.name === "tools.run") {
      throw new StructuredToolError("E_TOOL_RECURSION_UNSUPPORTED", "tools.run cannot execute itself recursively.", [
        "Pass the actual target tool name, for example docs.list or scene.hierarchy."
      ]);
    }
    if (!ctx.runTool) {
      throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "tools.run requires the CLI router context.", [
        "Run this through unity_cli tools run tools.run, not by calling the tool definition directly."
      ]);
    }

    const invocation: ToolRunInvocation = {
      name: request.name,
      flags: buildTargetFlags(request, ctx.flags),
      positionalTarget: request.positionalTarget
    };
    const result = await ctx.runTool(invocation);
    if (!result.ok) throwNestedFailure(result);
    return (result.data ?? {}) as ToolsRunResult;
  }
});
