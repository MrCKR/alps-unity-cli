/**
 * TS-side discovery and descriptor mapping for project custom contexts.
 *
 * Mirrors the project-tool-discovery pattern: determine whether bridge
 * discovery should run, call the bridge `context.list` command, and map
 * raw C# bridge descriptors into unified ContextDescriptor objects.
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
  summary?: string;
  description: string;
  category: string;
  version?: string;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown>;
  source: "built-in" | "project";
  target?: "editor" | "runtime" | "both";
  availabilityScope?: string;
  playModeSupport?: "supported" | "runtime-only" | "session-affecting" | "editor-only" | "unknown";
  refreshBehavior?: string;
  requiresPackages?: string[];
  supportStatus?: string;
  cliCommand?: string;
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
  if (runtime.projectContexts) {
    const contexts = runtime.projectContexts.map(ensureContextDefaults);
    const builtInCount = contexts.filter((c) => c.source === "built-in").length;
    const projectCustomCount = contexts.filter((c) => c.source === "project").length;
    return { ok: true, contexts, mode: "runtime", builtInCount, projectCustomCount };
  }

  if (!options.requireBridge && !shouldDiscoverContexts(flags, runtime)) {
    return { ok: true, contexts: [], mode: "skipped", builtInCount: 0, projectCustomCount: 0 };
  }

  const result = await executeBridgeCommand(requestId, "context.list", flags, undefined, runtime);
  if (result.ok === false) {
    return result as CliResult & { ok: false };
  }

  const data = result.data as { contexts?: unknown; builtInCount?: number; projectCustomCount?: number } | undefined;
  const rawContexts = data?.contexts;
  if (!Array.isArray(rawContexts)) {
    return { ok: true, contexts: [], mode: "bridge", builtInCount: 0, projectCustomCount: 0 };
  }

  const contexts = rawContexts.map(rawDescriptorToContextDescriptor).filter(isContextDescriptor);

  const builtInNames = new Set(contexts.filter((c) => c.source === "built-in").map((c) => c.name));
  const deduplicated = contexts.filter((c) => !(c.source === "project" && builtInNames.has(c.name)));

  const builtInCount = data?.builtInCount ?? deduplicated.filter((c) => c.source === "built-in").length;
  const projectCustomCount = data?.projectCustomCount ?? deduplicated.filter((c) => c.source === "project").length;

  return { ok: true, contexts: deduplicated, mode: "bridge", builtInCount, projectCustomCount };
}

// ─── Descriptor mapping ────────────────────────────────────────────────

/** Map a raw bridge descriptor object to a unified ContextDescriptor. */
export function rawDescriptorToContextDescriptor(value: unknown): ContextDescriptor | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  const d = value as Record<string, unknown>;
  const name = stringProp(d, "name");
  const description = stringProp(d, "description");
  if (!name || !description) {
    return undefined;
  }

  const source = stringProp(d, "source") === "project" ? "project" as const : "built-in" as const;
  const isProject = source === "project";

  return ensureContextDefaults({
    name,
    summary: stringProp(d, "summary") ?? description,
    description,
    category: stringProp(d, "category") ?? (isProject ? "custom" : "built-in"),
    version: stringProp(d, "version"),
    inputSchema: objectProp(d, "inputSchema") ?? {},
    outputSchema: objectProp(d, "outputSchema"),
    source,
    target: targetProp(d, "target") ?? "editor",
    availabilityScope: stringProp(d, "availabilityScope"),
    playModeSupport: playModeSupportProp(d, "playModeSupport"),
    refreshBehavior: stringProp(d, "refreshBehavior"),
    requiresPackages: stringArrayProp(d, "requiresPackages"),
    supportStatus: isProject ? "live-bridge" : stringProp(d, "supportStatus"),
    cliCommand: isProject ? `context read ${name}` : stringProp(d, "cliCommand"),
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
    target: context.target ?? "editor",
    availabilityScope: context.availabilityScope ?? "editorSession",
    playModeSupport: context.playModeSupport ?? (context.target === "runtime" ? "runtime-only" : "supported"),
    summary: context.summary ?? context.description,
    supportStatus: context.supportStatus ?? (context.target === "runtime" ? "live-bridge-runtime" : "live-bridge"),
    cliCommand: context.cliCommand ?? `context read ${context.name}`
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

function targetProp(record: Record<string, unknown>, key: string): ContextDescriptor["target"] | undefined {
  const value = record[key];
  return value === "editor" || value === "runtime" || value === "both" ? value : undefined;
}

function objectProp(record: Record<string, unknown>, key: string): Record<string, unknown> | undefined {
  const value = record[key];
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function stringArrayProp(record: Record<string, unknown>, key: string): string[] | undefined {
  const value = record[key];
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : undefined;
}
