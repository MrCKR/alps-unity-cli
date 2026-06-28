/**
 * Context discovery: discovers custom project contexts from the Unity bridge.
 */

import { getBoolean, getString } from "./args.js";
import { executeBridgeCommand, type BridgeRuntime } from "./bridge/index.js";
import type { ContextMetadata } from "./context/metadata/types.js";
import type { CliResult } from "./types.js";

export interface ContextDiscoveryRuntime extends Partial<BridgeRuntime> {
  projectContexts?: ContextMetadata[];
  dataDir?: string;
}

export type ContextDiscoveryResult =
  | { ok: true; contexts: ContextMetadata[]; mode: "runtime" | "bridge" | "skipped" }
  | (CliResult & { ok: false });

export function shouldDiscoverContexts(flags: Record<string, string | boolean>, runtime: ContextDiscoveryRuntime): boolean {
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
  runtime: ContextDiscoveryRuntime,
  requestId: string,
  options: { requireBridge?: boolean } = {}
): Promise<ContextDiscoveryResult> {
  if (runtime.projectContexts) {
    return { ok: true, contexts: runtime.projectContexts.map(ensureProjectContextDefaults), mode: "runtime" };
  }

  if (!options.requireBridge && !shouldDiscoverContexts(flags, runtime)) {
    return { ok: true, contexts: [], mode: "skipped" };
  }

  const result = await executeBridgeCommand(requestId, "context.list", flags, undefined, runtime);
  if (result.ok === false) {
    return result as CliResult & { ok: false };
  }

  const rawContexts = (result.data as { contexts?: unknown } | undefined)?.contexts;
  if (!Array.isArray(rawContexts)) {
    return { ok: true, contexts: [], mode: "bridge" };
  }
  return { ok: true, contexts: rawContexts.map(projectDescriptorToContextMetadata).filter(isContextMetadata), mode: "bridge" };
}

function projectDescriptorToContextMetadata(value: unknown): ContextMetadata | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  const descriptor = value as Record<string, unknown>;
  const name = stringProp(descriptor, "name");
  const description = stringProp(descriptor, "description");
  if (!name || !description) {
    return undefined;
  }
  const category = stringProp(descriptor, "category") ?? categoryFromName(name);
  const target = targetProp(descriptor, "target") ?? "editor";

  return ensureProjectContextDefaults({
    name,
    description,
    source: "project",
    category,
    inputSchema: objectProp(descriptor, "inputSchema") ?? {},
    outputSchema: objectProp(descriptor, "outputSchema"),
    refreshBehavior: stringProp(descriptor, "refreshBehavior") as ContextMetadata["refreshBehavior"],
    target,
    availabilityScope: stringProp(descriptor, "availabilityScope") ?? (target === "runtime" ? "playModeSession" : "editorSession"),
    available: booleanProp(descriptor, "available"),
    requiresPackages: stringArrayProp(descriptor, "requiresPackages"),
    fingerprint: stringProp(descriptor, "fingerprint"),
    cliCommand: stringProp(descriptor, "cliCommand") ?? `context read ${name}`,
    handler: "bridge"
  });
}

function ensureProjectContextDefaults(context: ContextMetadata): ContextMetadata {
  return {
    ...context,
    source: context.source ?? "project",
    category: context.category ?? categoryFromName(context.name),
    target: context.target ?? "editor",
    availabilityScope: context.availabilityScope ?? "editorSession",
    cliCommand: context.cliCommand ?? `context read ${context.name}`
  };
}

function isContextMetadata(value: ContextMetadata | undefined): value is ContextMetadata {
  return value !== undefined;
}

function categoryFromName(name: string): string {
  const [category] = name.split(".");
  return category || "custom";
}

function stringProp(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function booleanProp(record: Record<string, unknown>, key: string): boolean | undefined {
  return typeof record[key] === "boolean" ? record[key] : undefined;
}

function targetProp(record: Record<string, unknown>, key: string): ContextMetadata["target"] | undefined {
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
