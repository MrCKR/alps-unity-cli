import type { AnyJson } from "../schema/any-json.js";

/** Context 公开来源；Bridge 必须明确返回其中一个值。 */
export type ContextSource = "built-in" | "extension" | "project";

export type ContextTarget = "editor" | "runtime" | "both";
export type ContextAvailabilityScope = "editorSession" | "playModeSession" | "always";
export type ContextPlayModeSupport = "supported" | "runtime-only" | "session-affecting" | "editor-only" | "unknown";

export interface ContextExample {
  description: string;
  argv: string[];
  /** 内部 authoring 输入，公开投影时移除。 */
  input?: Record<string, AnyJson>;
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
  requiresPackages?: string[];
  extensionId?: string;
  available?: boolean;
  unavailableReason?: string;
  /** 内部 descriptor 一致性校验字段，不进入公开投影。 */
  descriptorFingerprint?: string;
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
  available?: boolean;
  unavailableReason?: string;
  requiresPackages?: string[];
  extensionId?: string;
}
