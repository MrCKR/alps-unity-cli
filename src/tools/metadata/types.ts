export interface ToolExample {
  description: string;
  argv: string[];
  output?: string;
}

export type ToolHandler = "bridge" | "bridge-runtime" | "job" | "offline" | "hybrid";
export type ToolSupportStatus = "live-bridge" | "live-bridge-runtime" | "bridge-job" | "batch-job" | "cli-only" | "planned";

export interface ToolMetadata {
  name: string;
  title: string;
  description: string;
  group: string;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
  readOnlyHint: boolean;
  destructiveHint: boolean;
  longRunning?: boolean;
  requiresMainThread?: boolean;
  supportsDryRun?: boolean;
  timeoutMs?: number;
  permissions?: string[];
  architectureSensitive?: boolean;
  frozenDecision?: string;
  coverageScope?: readonly string[];
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
