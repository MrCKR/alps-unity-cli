/**
 * Context metadata type for the Unity CLI.
 *
 * Context represents a read-only observation capability: project info,
 * editor state, scene data, selections, etc. Context never modifies
 * Unity state; if a capability has side effects it MUST be a Tool.
 *
 * Defines the typed descriptor surface for built-in, project, and extension contexts.
 */

export type ContextSource = "built-in" | "project" | "extension";

export interface ContextMetadata {
  /** Dotted context identifier, e.g. 'project.info'. Must be unique across all contexts. */
  name: string;
  /** Short human-readable title. */
  title: string;
  /** One-paragraph explanation of what the context provides. */
  description: string;
  /** Discriminator: always 'context' for ContextMetadata. */
  kind: "context";
  /** Origin: built-in (shipped with CLI), project (custom context in Unity project), extension (from an extension package). */
  source: ContextSource;
  /** Logical grouping for discovery, e.g. 'project', 'editor', 'scene'. */
  category: string;
  /** JSON Schema for the context's input parameters. */
  inputSchema: Record<string, unknown>;
  /** JSON Schema for the context's output. */
  outputSchema?: Record<string, unknown>;
  /** URI template for resource identification, e.g. 'unity-cli://project/info'. Optional. */
  resourceUri?: string;
  /** Refresh behavior: 'static' (never changes), 'on-demand' (can be re-read), 'live' (subscribable). */
  refreshBehavior?: "static" | "on-demand" | "live";
  /** Execution target: 'editor' (default), 'runtime', or 'both'. */
  target?: "editor" | "runtime" | "both";
  /** Availability scope for the context. */
  availabilityScope?: string;
  /** Whether the context is currently available based on session state. */
  available?: boolean;
  /** Unity package names required by this context at runtime. */
  requiresPackages?: string[];
  /** Fingerprint material for detecting descriptor/handler drift. Project contexts only. */
  fingerprint?: string;
  /** Mapping to equivalent unity-mcp resource URI, for parity tracking. */
  equivalentUnityMcp?: string;
  /** Note explaining semantic difference when equivalentUnityMcp mapping is not exact. */
  equivalentUnityMcpNote?: string;
  /** CLI command to read this context, e.g. 'context read project.info'. */
  cliCommand?: string;
  /** Handler type for bridge routing. */
  handler?: "bridge" | "bridge-runtime" | "offline";
}
