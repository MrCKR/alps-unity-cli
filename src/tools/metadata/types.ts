import type { AnyJson } from "../../schema/any-json.js";

export interface ToolExample {
  description: string;
  argv: string[];
  input?: AnyJson;
  execution?: ToolExampleExecution;
  output?: string;
}

/** 示例执行提示属于 CLI 命令层，不属于业务 inputSchema。 */
export interface ToolExampleExecution {
  dryRun?: boolean;
  timeoutMs?: number;
}

export type ToolHandler = "bridge" | "bridge-runtime" | "job" | "offline" | "hybrid";
export type ToolSupportStatus = "live-bridge" | "live-bridge-runtime" | "bridge-job" | "batch-job" | "cli-only" | "planned";
export type ToolSource = "built-in" | "project" | "extension";
export type ToolRiskLevel = "safe" | "low" | "medium" | "high" | "critical";
export type ToolCancellation = "none" | "cooperative" | "job";
export type PlayModeSupport = "supported" | "runtime-only" | "session-affecting" | "editor-only" | "unknown";

export interface ToolMetadata {
  /** Optional origin override when a live bridge descriptor already knows whether this tool is built-in/extension/project. */
  source?: ToolSource;
  name: string;
  summary: string;
  description: string;
  /** Logical group for discovery and `tools group list <group>`. */
  group: string;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
  destructiveHint: boolean;
  /** Side effect categories this tool may produce, e.g. ['file-write', 'scene-modify']. */
  sideEffects?: string[];
  /** Play Mode execution semantics for AI routing and runtime safety. */
  playModeSupport?: PlayModeSupport;
  supportsDryRun?: boolean;
  timeoutMs?: number;
  permissions?: string[];
  examples?: ToolExample[];
  /** Internal execution route override. Omit when the route can be inferred from the bridge registry or tool name. */
  handler?: ToolHandler;
  supportStatus?: ToolSupportStatus;
  /** Execution target: "editor" (default), "runtime", or "both". */
  target?: string;
  /** Whether the tool is currently available based on session state (runtime tools: only in Play Mode). */
  available?: boolean;
  /** Unity package names required by this tool at runtime. When absent the tool has no package prerequisites. */
  requiresPackages?: string[];
  /** Live Bridge 计算的 handler+descriptor 指纹；破坏性 proof 必须绑定此值。 */
  liveDescriptorFingerprint?: string;
}

export type ResolvedToolMetadata = ToolMetadata & {
  source: ToolSource;
  handler: ToolHandler;
  playModeSupport: PlayModeSupport;
  supportStatus: ToolSupportStatus;
  cliCommand: string;
  availabilityScope: string;
  riskLevel: ToolRiskLevel;
  longRunning?: boolean;
};
