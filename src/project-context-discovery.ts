/**
 * TS-side discovery and descriptor mapping for project custom contexts.
 *
 * Mirrors the project-tool-discovery pattern: determine whether bridge
 * discovery should run, call the bridge `context.list` command, and map
 * raw C# bridge descriptors into unified ContextDescriptor objects.
 *
 * Key invariants enforced here:
 * - Built-in contexts always have source="built-in".
 * - Project contexts that share a name with a built-in context are rejected
 *   (duplicate guard) rather than silently overriding.
 * - Contexts never enter the `tools` execution path.
 */

import { getBoolean, getString } from "./args.js";
import { executeBridgeCommand, type BridgeRuntime } from "./bridge/index.js";
import type { CliResult } from "./types.js";

// ─── Context Descriptor ────────────────────────────────────────────────

/** Fingerprint information attached to a context descriptor. */
export interface ContextFingerprint {
  algorithm?: string;
  value?: string;
  status?: string;
  pinnedFingerprint?: string;
  descriptorSha256?: string;
  handlerSha256?: string;
}

/** Unified context descriptor — the TS-side representation of a context. */
export interface ContextDescriptor {
  name: string;
  title: string;
  description: string;
  category: string;
  version?: string;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
  /** Always "context" for any context. */
  kind: "context";
  /** "built-in" for built-in contexts, "project" for custom project contexts. */
  source: "built-in" | "project";
  /** True for project contexts (they require a live Unity bridge). */
  requiresUnity: boolean;
  target?: string;
  availabilityScope?: string;
  playModeSupport?: "supported" | "runtime-only" | "session-affecting" | "editor-only" | "unknown";
  refreshBehavior?: string;
  resourceUri?: string;
  requiresPackages?: string[];
  equivalentUnityMcp?: string;
  equivalentUnityMcpNote?: string;
  handler?: string;
  supportStatus?: string;
  cliCommand?: string;
  docsTopic?: string;
  descriptorSource?: string;
  fingerprint?: ContextFingerprint;
}

// ─── Runtime / Discovery types ─────────────────────────────────────────

export interface ProjectContextRuntime extends Partial<BridgeRuntime> {
  projectContexts?: ContextDescriptor[];
}

export type ProjectContextDiscovery =
  | { ok: true; contexts: ContextDescriptor[]; mode: "runtime" | "bridge" | "skipped"; builtInCount: number; projectCustomCount: number }
  | (CliResult & { ok: false });

// ─── Discovery functions ───────────────────────────────────────────────

export function shouldDiscoverContexts(flags: Record<string, string | boolean>, runtime: ProjectContextRuntime): boolean {
  return getBoolean(flags, "include-custom") ||
    runtime.projectContexts !== undefined ||
    runtime.bridgeExecute !== undefined ||
    typeof getString(flags, "bridge-endpoint") === "string" ||
    typeof getString(flags, "target") === "string" ||
    typeof getString(flags, "instance") === "string" ||
    typeof runtime.bridgeEndpoint === "string" ||
    typeof process.env.UNITY_CLI_BRIDGE_ENDPOINT === "string";
}

export async function discoverProjectContexts(
  flags: Record<string, string | boolean>,
  runtime: ProjectContextRuntime,
  requestId: string,
  options: { requireBridge?: boolean } = {}
): Promise<ProjectContextDiscovery> {
  // 1. Pre-populated runtime contexts
  if (runtime.projectContexts) {
    const contexts = runtime.projectContexts.map(ensureContextDefaults);
    const builtInCount = contexts.filter((c) => c.source === "built-in").length;
    const projectCustomCount = contexts.filter((c) => c.source === "project").length;
    return { ok: true, contexts, mode: "runtime", builtInCount, projectCustomCount };
  }

  // 2. Skip if no bridge hints
  if (!options.requireBridge && !shouldDiscoverContexts(flags, runtime)) {
    return { ok: true, contexts: [], mode: "skipped", builtInCount: 0, projectCustomCount: 0 };
  }

  // 3. Call bridge context.list
  const result = await executeBridgeCommand(requestId, "context.list", flags, undefined, runtime);
  if (result.ok === false) {
    return result as CliResult & { ok: false };
  }

  const data = result.data as { contexts?: unknown; builtInCount?: number; projectCustomCount?: number } | undefined;
  const rawContexts = data?.contexts;
  if (!Array.isArray(rawContexts)) {
    return { ok: true, contexts: [], mode: "bridge", builtInCount: 0, projectCustomCount: 0 };
  }

  // Map raw descriptors to ContextDescriptor, filtering out invalid ones
  const contexts = rawContexts.map(rawDescriptorToContextDescriptor).filter(isContextDescriptor);

  // Duplicate guard: reject project contexts with names already in built-in set
  const builtInNames = new Set(contexts.filter((c) => c.source === "built-in").map((c) => c.name));
  const deduplicated = contexts.filter((c) => {
    if (c.source === "project" && builtInNames.has(c.name)) {
      // Project context shadows a built-in — reject (do not silently override)
      return false;
    }
    return true;
  });

  const builtInCount = data?.builtInCount ?? deduplicated.filter((c) => c.source === "built-in").length;
  const projectCustomCount = data?.projectCustomCount ?? deduplicated.filter((c) => c.source === "project").length;

  return { ok: true, contexts: deduplicated, mode: "bridge", builtInCount, projectCustomCount };
}

// ─── Descriptor mapping ────────────────────────────────────────────────

/**
 * Map a raw bridge descriptor object to a unified ContextDescriptor.
 * Handles both built-in and project custom descriptors.
 */
export function rawDescriptorToContextDescriptor(value: unknown): ContextDescriptor | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  const d = value as Record<string, unknown>;
  const name = stringProp(d, "name");
  if (!name) {
    return undefined;
  }

  const source = stringProp(d, "source") === "project" ? "project" as const : "built-in" as const;
  const isProject = source === "project";

  return ensureContextDefaults({
    name,
    title: stringProp(d, "title") ?? name,
    description: stringProp(d, "description") ?? stringProp(d, "title") ?? name,
    category: stringProp(d, "category") ?? (isProject ? "custom" : "built-in"),
    version: stringProp(d, "version"),
    inputSchema: objectProp(d, "inputSchema") ?? {},
    outputSchema: objectProp(d, "outputSchema"),
    kind: "context" as const,
    source,
    requiresUnity: isProject,              // Project contexts require Unity bridge
    target: stringProp(d, "target") ?? "editor",
    availabilityScope: stringProp(d, "availabilityScope"),
    playModeSupport: playModeSupportProp(d, "playModeSupport"),
    refreshBehavior: stringProp(d, "refreshBehavior"),
    resourceUri: stringProp(d, "resourceUri"),
    requiresPackages: stringArrayProp(d, "requiresPackages"),
    equivalentUnityMcp: stringProp(d, "equivalentUnityMcp"),
    equivalentUnityMcpNote: stringProp(d, "equivalentUnityMcpNote"),
    handler: isProject ? "bridge" : undefined,
    supportStatus: isProject ? "live-bridge" : undefined,
    cliCommand: isProject ? `context read ${name}` : undefined,
    docsTopic: isProject ? "project-context" : undefined,
    descriptorSource: stringProp(d, "descriptorSource"),
    fingerprint: mapFingerprint(d)
  });
}

function mapFingerprint(d: Record<string, unknown>): ContextFingerprint | undefined {
  const fp = d.fingerprint;
  if (!fp || typeof fp !== "object" || Array.isArray(fp)) {
    return undefined;
  }
  const f = fp as Record<string, unknown>;
  return {
    algorithm: stringProp(f, "algorithm"),
    value: stringProp(f, "value"),
    status: stringProp(f, "status"),
    pinnedFingerprint: stringProp(f, "pinnedFingerprint"),
    descriptorSha256: stringProp(f, "descriptorSha256"),
    handlerSha256: stringProp(f, "handlerSha256")
  };
}

function ensureContextDefaults(context: ContextDescriptor): ContextDescriptor {
  return {
    ...context,
    kind: "context" as const,
    target: context.target ?? "editor",
    availabilityScope: context.availabilityScope ?? "editorSession",
    playModeSupport: context.playModeSupport ?? (context.target === "runtime" ? "runtime-only" : "supported"),
    ...(context.source === "project" ? {
      handler: context.handler ?? "bridge",
      supportStatus: context.supportStatus ?? "live-bridge",
      cliCommand: context.cliCommand ?? `context read ${context.name}`,
      docsTopic: context.docsTopic ?? "project-context"
    } : {})
  };
}

function isContextDescriptor(value: ContextDescriptor | undefined): value is ContextDescriptor {
  return value !== undefined;
}

// ─── Property accessors ────────────────────────────────────────────────

function stringProp(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function playModeSupportProp(record: Record<string, unknown>, key: string): ContextDescriptor["playModeSupport"] | undefined {
  const value = record[key];
  return value === "supported" || value === "runtime-only" || value === "session-affecting" || value === "editor-only" || value === "unknown" ? value : undefined;
}

function objectProp(record: Record<string, unknown>, key: string): Record<string, unknown> | undefined {
  const value = record[key];
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function stringArrayProp(record: Record<string, unknown>, key: string): string[] | undefined {
  const value = record[key];
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : undefined;
}
