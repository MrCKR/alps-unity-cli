/**
 * Context system types for the Unity CLI.
 *
 * Contexts are read-only data sources that expose Unity Editor / project state
 * without side effects.
 */

/** Context source: built into the CLI package, contributed by an extension, or defined in the target project. */
export type ContextSource = "built-in" | "extension" | "project";

/** Execution target for a context read. */
export type ContextTarget = "editor" | "runtime" | "both";

/** Availability scope for a context. */
export type ContextAvailabilityScope = "editorSession" | "playModeSession" | "always";

/** Play Mode read semantics for a context. */
export type ContextPlayModeSupport = "supported" | "runtime-only" | "session-affecting" | "editor-only" | "unknown";

/** Descriptor for a single context entry. */
export interface ContextDescriptor {
  /** Dotted context name, e.g. "project.info", "editor.state", "scene.cameras". */
  name: string;
  /** Short routing summary for discovery output. */
  summary?: string;
  /** One-paragraph description shown in discovery and describe output. */
  description: string;
  /** Logical category: "project", "editor", "scene", "prefab", "tools", "tests", or custom. */
  category: string;
  /** Whether this context is built-in, extension-contributed, or project-defined. */
  source: ContextSource;
  /** Execution target: editor, runtime, or both. */
  target: ContextTarget;
  /** Availability scope. */
  availabilityScope: ContextAvailabilityScope;
  /** Play Mode read semantics. */
  playModeSupport: ContextPlayModeSupport;
  /** Whether the context is currently available (depends on Unity state). */
  available?: boolean;
  /** JSON Schema for context read input parameters. */
  inputSchema: Record<string, unknown>;
  /** JSON Schema for context read output. */
  outputSchema: Record<string, unknown>;
  /** CLI command for reading this context, e.g. "context read project.info". */
  cliCommand: string;
  /** Current routing status for AI execution decisions. */
  supportStatus?: string;
  /** Unity package names required for this context to be available. */
  requiresPackages?: string[];
  /** Extension ID if this context comes from an extension. */
  extensionId?: string;
  /** Dependencies on other contexts by name. */
  dependencies?: string[];
  /** Refresh behavior for read cost / staleness decisions. */
  refreshBehavior?: string;
}

/** Summary entry returned by `context list` — lightweight view of a context. */
export interface ContextListEntry {
  name: string;
  summary: string;
  description: string;
  category: string;
  source: ContextSource;
  target: ContextTarget;
  playModeSupport: ContextPlayModeSupport;
  supportStatus?: string;
  cliCommand: string;
  available?: boolean;
  requiresPackages?: string[];
}

/** Detailed descriptor returned by `context describe`. */
export type ContextDescribeEntry = ContextDescriptor;

/** Result of reading a context value. */
export interface ContextReadResult {
  context: string;
  data: unknown;
  fingerprint?: string;
  readAt: string;
}

/** Custom context descriptor discovered from the Unity Bridge at runtime. */
export interface CustomContextDescriptor {
  /** Dotted context name. */
  name: string;
  /** Short routing summary. */
  summary?: string;
  /** One-paragraph description. */
  description: string;
  /** Logical category. */
  category: string;
  /** Whether this runtime-discovered context is built-in, extension, or project-defined. */
  source?: ContextSource;
  /** Execution target. */
  target?: ContextTarget;
  /** Availability scope. */
  availabilityScope?: ContextAvailabilityScope;
  /** Play Mode read semantics. */
  playModeSupport?: ContextPlayModeSupport;
  /** Current routing status for AI execution decisions. */
  supportStatus?: string;
  /** CLI command for reading this context. */
  cliCommand?: string;
  /** Input schema. */
  inputSchema?: Record<string, unknown>;
  /** Output schema. */
  outputSchema?: Record<string, unknown>;
  /** Unity package names required for this context to be available. */
  requiresPackages?: string[];
  /** Refresh behavior for read cost / staleness decisions. */
  refreshBehavior?: string;
  /** Whether the context is currently available. */
  available?: boolean;
  /** Extension ID or assembly source. */
  extensionId?: string;
}
