export interface ToolExample {
  description: string;
  argv: string[];
  output?: string;
}

export type ToolHandler = "bridge" | "bridge-runtime" | "job" | "offline" | "hybrid";
export type ToolSupportStatus = "live-bridge" | "live-bridge-runtime" | "bridge-job" | "batch-job" | "cli-only" | "planned";
export type ToolSource = "built-in" | "project" | "extension";
export type ToolRiskLevel = "safe" | "low" | "medium" | "high" | "critical";

export interface ToolMetadata {
  name: string;
  title: string;
  description: string;
  /** Discriminator: always 'tool' for ToolMetadata. Distinguishes from ContextMetadata (kind='context'). Defaults to 'tool' when omitted. */
  kind?: "tool";
  /** Origin: built-in (shipped with CLI), project (custom tool in Unity project), extension (from extension package). Defaults to 'built-in' when omitted. */
  source?: ToolSource;
  /** Logical group for discovery and `tools group list <group>`. */
  group: string;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
  destructiveHint: boolean;
  /** Side effect categories this tool may produce, e.g. ['file-write', 'scene-modify']. */
  sideEffects?: string[];
  /** Risk level classification for safety disclosure. */
  riskLevel?: ToolRiskLevel;
  /** Mapping to equivalent unity-mcp tool name, for parity tracking. */
  equivalentUnityMcp?: string;
  longRunning?: boolean;
  requiresMainThread?: boolean;
  supportsDryRun?: boolean;
  timeoutMs?: number;
  permissions?: string[];
  cancellationSemantics?: string;
  examples?: ToolExample[];
  docsTopic?: string;
  handler?: ToolHandler;
  supportStatus?: ToolSupportStatus;
  cliCommand?: string;
  /** Execution target: "editor" (default), "runtime", or "both". */
  target?: string;
  /** Availability scope for the tool. "playModeSession" for runtime tools, "editorSession" for editor tools. */
  availabilityScope?: string;
  /** Whether the tool is currently available based on session state (runtime tools: only in Play Mode). */
  available?: boolean;
  /** Unity package names required by this tool at runtime. When absent the tool has no package prerequisites. */
  requiresPackages?: string[];
}
