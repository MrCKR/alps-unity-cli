/**
 * Context system types for the Unity CLI.
 *
 * Contexts are read-only data sources that expose Unity Editor / project state
 * without side effects. This is the CLI-first equivalent of MCP Resources.
 *
 * Key rules:
 * - All contexts are read-only (readOnlyHint=true, destructiveHint=false).
 * - Output uses the unified envelope.
 * - Built-in contexts are declared statically in the CLI package.
 * - Custom contexts are discovered at runtime via the Unity Bridge.
 * - Contexts have fingerprints for descriptor/handler integrity verification.
 */

/** Context source: built into the CLI package, or contributed by a project extension. */
export type ContextSource = "built-in" | "custom";

/** Execution target for a context read. */
export type ContextTarget = "editor" | "runtime";

/** Availability scope for a context. */
export type ContextAvailabilityScope = "editorSession" | "playModeSession" | "always";

/** Descriptor for a single context entry. */
export interface ContextDescriptor {
  /** Dotted context name, e.g. "project.info", "editor.state", "scene.cameras". */
  name: string;
  /** Human-readable title. */
  title: string;
  /** One-paragraph description. */
  description: string;
  /** Logical category: "project", "editor", "scene", "prefab", "menu", "tests", or custom. */
  category: string;
  /** Whether this context is built-in or from a project extension. */
  source: ContextSource;
  /** Execution target: editor (default) or runtime. */
  target: ContextTarget;
  /** Availability scope. */
  availabilityScope: ContextAvailabilityScope;
  /** Whether the context requires a live Unity Bridge connection. */
  requiresUnity: boolean;
  /** Whether the context is currently available (depends on Unity state). */
  available?: boolean;
  /** JSON Schema for context read input parameters. */
  inputSchema: Record<string, unknown>;
  /** JSON Schema for context read output. */
  outputSchema: Record<string, unknown>;
  /** Always true for contexts. */
  readOnlyHint: true;
  /** Always false for contexts. */
  destructiveHint: false;
  /** Bridge command name for reading this context, e.g. "context.read". */
  bridgeCommand: string;
  /** CLI command for reading this context, e.g. "context read project.info". */
  cliCommand: string;
  /** Unity package names required for this context to be available. */
  requiresPackages?: string[];
  /** Extension ID if this context comes from an extension. */
  extensionId?: string;
  /** Fingerprint hash of the descriptor for integrity verification. */
  fingerprint?: string;
  /** Dependencies on other contexts by name. */
  dependencies?: string[];
  /** Documentation topic key. */
  docsTopic?: string;
  /** C# handler class name for custom contexts. */
  handlerClass?: string;
  /** C# handler method name for custom contexts. */
  handlerMethod?: string;
}

/** Summary entry returned by `context list` — lightweight view of a context. */
export interface ContextListEntry {
  name: string;
  title: string;
  description: string;
  category: string;
  source: ContextSource;
  target: ContextTarget;
  available?: boolean;
}

/** Detailed descriptor returned by `context describe`.
 * @planned Will be used as the explicit return type of handleContextDescribe. */
export interface ContextDescribeEntry extends ContextDescriptor {
  /** Support status label for display. */
  supportStatusLabel?: string;
}

/** Result of reading a context value.
 * @planned Will be used as the explicit return type of handleContextRead. */
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
  /** Human-readable title. */
  title: string;
  /** One-paragraph description. */
  description: string;
  /** Logical category. */
  category: string;
  /** C# handler class name. */
  handlerClass?: string;
  /** C# handler method name. */
  handlerMethod?: string;
  /** Input schema. */
  inputSchema?: Record<string, unknown>;
  /** Output schema. */
  outputSchema?: Record<string, unknown>;
  /** Extension ID or assembly source. */
  extensionId?: string;
  /** Fingerprint of the handler implementation. */
  fingerprint?: string;
}
