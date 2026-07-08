import { defineTool, type ToolRunInvocation } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { CliResult } from "../../types.js";

export interface AssetRefreshRequest {
  path?: string;
  assetPath?: string;
  forceUpdate?: boolean;
  importRecursive?: boolean;
  project?: string;
  unity?: string;
  wait?: boolean;
  waitTimeoutMs?: number;
  bridgeEndpoint?: string;
  instance?: string;
  job?: boolean;
  dryRun?: boolean;
  yes?: boolean;
  positionalTarget?: string;
}

export interface AssetRefreshPlannedEffect {
  type?: string;
  path?: string;
  options?: string;
  sideEffects?: string;
}

export interface AssetRefreshResult {
  command?: string;
  mode?: string;
  path?: string;
  options?: string;
  refreshed?: boolean;
  jobId?: string;
  status?: string;
  dryRun?: boolean;
  plannedEffects?: AssetRefreshPlannedEffect[];
  logPath?: string;
}

const ROUTER_ONLY_FLAGS = new Set(["input", "input-file", "stdin", "__structuredInput", "raw"]);

function setStringFlag(flags: Record<string, string | boolean>, key: string, value: unknown): void {
  if (typeof value === "string" && value.length > 0) flags[key] = value;
}

function setBooleanFlag(flags: Record<string, string | boolean>, key: string, value: unknown): void {
  if (typeof value === "boolean") flags[key] = value;
}

function setNumberFlag(flags: Record<string, string | boolean>, key: string, value: unknown): void {
  if (typeof value === "number" && Number.isFinite(value)) flags[key] = String(value);
}

function compactInput(input: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(input).filter(([, value]) => value !== undefined));
}

// 构造内部 router flags,保留 instance/target/job 等执行控制,只替换目标工具 input。
function buildInnerFlags(request: AssetRefreshRequest, contextFlags: Record<string, string | boolean> | undefined): Record<string, string | boolean> {
  const flags: Record<string, string | boolean> = {};
  for (const [key, value] of Object.entries(contextFlags ?? {})) {
    if (!ROUTER_ONLY_FLAGS.has(key)) flags[key] = value;
  }

  const input = compactInput({
    path: request.path ?? request.assetPath ?? request.positionalTarget,
    forceUpdate: request.forceUpdate,
    importRecursive: request.importRecursive,
    dryRun: request.dryRun
  });
  flags.input = JSON.stringify(input);

  setStringFlag(flags, "project", request.project);
  setStringFlag(flags, "unity", request.unity);
  setStringFlag(flags, "bridge-endpoint", request.bridgeEndpoint);
  setStringFlag(flags, "instance", request.instance);
  setBooleanFlag(flags, "job", request.job);
  setBooleanFlag(flags, "dry-run", request.dryRun);
  setBooleanFlag(flags, "yes", request.yes);
  setBooleanFlag(flags, "wait", request.wait);
  setNumberFlag(flags, "wait-timeout-ms", request.waitTimeoutMs);
  return flags;
}

function throwNestedFailure(result: CliResult): never {
  const error = result.error;
  if (!error) {
    throw new StructuredToolError("COMMAND_FAILED", "Nested asset.refresh routing failed without an error envelope.");
  }
  const { code, message, nextActions, requestId: _requestId, transient: _transient, retryAfterMs: _retryAfterMs, details, ...rest } = error as Record<string, unknown> & { code: string; message: string; nextActions?: string[] };
  const extraDetails = Object.keys(rest).length > 0 || details !== undefined ? { ...rest, ...(details !== undefined ? { details } : {}) } : undefined;
  throw new StructuredToolError(code, message, Array.isArray(nextActions) ? nextActions : [], extraDetails);
}

export default defineTool<AssetRefreshRequest, AssetRefreshResult>({
  name: "asset.refresh",
  summary: "Refresh AssetDatabase in a live Editor, or use --job for a closed-project batch refresh.",
  description: "Refresh the AssetDatabase in a live Editor or as a Unity batchmode job. Without --job, this runs through the open Editor bridge and may import one Assets/* path with refresh options. With --job, this opens a closed project in Unity batchmode and runs the same Bridge command, so the project must not already be open in Unity. Supports dry-run for planned effects.",
  group: "asset",
  destructiveHint: false,
  sideEffects: ["asset-import", "asset-refresh", "script-compile", "domain-reload"],
  playModeSupport: "session-affecting",
  supportsDryRun: true,
  handler: "hybrid",
  supportStatus: "bridge-job",
  target: "editor",
  examples: [
    { description: "Refresh the live Editor AssetDatabase", argv: ["tools", "run", "asset.refresh", "--input", "{}"] },
    { description: "Preview importing one asset in the live Editor", argv: ["tools", "run", "asset.refresh", "--input", "{\"path\":\"Assets/Generated/config.json\",\"forceUpdate\":true}", "--dry-run"] },
    { description: "Refresh a closed project in batchmode", argv: ["tools", "run", "asset.refresh", "--job", "--input", "{\"project\":\"./Dev/UnityProject\",\"wait\":true}"] }
  ],
  async execute(request, ctx) {
    if (!ctx.runTool) {
      throw new StructuredToolError("E_TOOL_HANDLER_UNSUPPORTED", "asset.refresh requires the CLI router context.", [
        "Run this through unity_cli tools run asset.refresh, not by calling the tool definition directly."
      ]);
    }

    const invocation: ToolRunInvocation = {
      name: "asset.refresh",
      flags: buildInnerFlags(request, ctx.flags),
      bypassDefineTool: true
    };
    const result = await ctx.runTool(invocation);
    if (!result.ok) throwNestedFailure(result);
    return (result.data ?? {}) as AssetRefreshResult;
  }
});
