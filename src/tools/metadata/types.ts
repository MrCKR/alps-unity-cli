export interface ToolExample {
  description: string;
  argv: string[];
  output?: string;
}

export type ToolHandler = "bridge" | "job" | "offline" | "hybrid";
export type ToolSupportStatus = "live-bridge" | "bridge-job" | "batch-job" | "cli-only" | "planned";

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
  examples?: ToolExample[];
  docsTopic?: string;
  handler?: ToolHandler;
  supportStatus?: ToolSupportStatus;
  cliCommand?: string;
}
