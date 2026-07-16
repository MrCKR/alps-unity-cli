/** Context 公开来源；Bridge 必须明确返回其中一个值。 */
export type ContextSource = "built-in" | "extension" | "project";

export type ContextTarget = "editor" | "runtime" | "both";
export type ContextAvailabilityScope = "editorSession" | "playModeSession" | "always";
export type ContextPlayModeSupport = "supported" | "runtime-only" | "session-affecting" | "editor-only" | "unknown";

export interface ContextFingerprint {
  algorithm?: string;
  value?: string;
  status?: string;
  pinnedFingerprint?: string;
  descriptorSha256?: string;
  handlerSha256?: string;
}

export interface ContextExample {
  description: string;
  argv: string[];
  input?: Record<string, unknown>;
}

/** list、search、describe 与 read 共用的完整 descriptor。 */
export interface ContextDescriptor {
  name: string;
  summary: string;
  description: string;
  category: string;
  source: ContextSource;
  target: ContextTarget;
  availabilityScope: ContextAvailabilityScope;
  playModeSupport: ContextPlayModeSupport;
  inputSchema: Record<string, unknown>;
  outputSchema: Record<string, unknown>;
  examples: ContextExample[];
  cliCommand: string;
  supportStatus?: string;
  requiresPackages?: string[];
  extensionId?: string;
  refreshBehavior?: string;
  available?: boolean;
  version?: string;
  fingerprint?: ContextFingerprint;
}

export interface ContextListEntry {
  name: string;
  summary: string;
  description: string;
  category: string;
  source: ContextSource;
  target: ContextTarget;
  availabilityScope: ContextAvailabilityScope;
  playModeSupport: ContextPlayModeSupport;
  supportStatus?: string;
  cliCommand: string;
  available?: boolean;
  requiresPackages?: string[];
  extensionId?: string;
}
