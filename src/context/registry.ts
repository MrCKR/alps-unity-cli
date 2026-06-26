/**
 * Context registry: manages built-in and custom context descriptors.
 *
 * Provides lookup, listing, search, and discovery functions for the context
 * command family. Built-in contexts are static; custom contexts are discovered
 * at runtime from the Unity Bridge (context.list custom descriptor endpoint)
 * and from installed extensions.
 */

import { builtinContextMetadata } from "./metadata/index.js";
import { collectAvailableContexts, ExtensionRegistry, readInstalledUnityPackages } from "../extensions/index.js";
import { getString } from "../args.js";
import { scoreSearch } from "../util.js";
import type { ContextDescriptor, ContextListEntry, CustomContextDescriptor } from "./types.js";
import type { ContextMetadata } from "./metadata/types.js";
import type { ExtensionContextContribution } from "../extensions/types.js";

// ── Built-in query functions ──────────────────────────────────────────────

/** Return all built-in context descriptors from the canonical metadata registry. */
export function builtinContextDescriptors(): ContextDescriptor[] {
  return builtinContextMetadata().map(contextMetadataToDescriptor);
}

/** Return distinct category names from built-in contexts. */
export function builtinContextCategories(): string[] {
  const categories = new Set<string>();
  for (const ctx of builtinContextDescriptors()) {
    categories.add(ctx.category);
  }
  return [...categories].sort();
}

/** Find a single built-in context by dotted name. */
export function findBuiltinContextByName(name: string): ContextDescriptor | undefined {
  return builtinContextDescriptors().find((c) => c.name === name);
}

/** Filter built-in contexts by category. */
export function filterContextsByCategory(category: string): ContextDescriptor[] {
  return builtinContextDescriptors().filter((c) => c.category === category);
}

/** Search built-in contexts by keyword. */
export function searchContexts(keyword: string): ContextDescriptor[] {
  const lower = keyword.toLowerCase();
  return builtinContextDescriptors().filter((c) =>
    c.name.toLowerCase().includes(lower) ||
    c.title.toLowerCase().includes(lower) ||
    c.description.toLowerCase().includes(lower) ||
    c.category.toLowerCase().includes(lower)
  ).sort((a, b) => {
    const aScore = scoreSearch(a.name.toLowerCase(), lower) + scoreSearch(a.title.toLowerCase(), lower);
    const bScore = scoreSearch(b.name.toLowerCase(), lower) + scoreSearch(b.title.toLowerCase(), lower);
    return bScore - aScore;
  });
}

// ── Extension context contributions ───────────────────────────────────────

/** Collect context descriptors from installed extensions. */
export async function extensionContextDescriptors(
  flags: Record<string, string | boolean>
): Promise<ContextDescriptor[]> {
  const registry = new ExtensionRegistry();
  const projectPath = getString(flags, "project");
  if (!projectPath) {
    return [];
  }

  try {
    const installedPackages = await readInstalledUnityPackages(projectPath);
    return collectAvailableContexts(registry, installedPackages).map((ec) => extensionContributionToDescriptor(ec));
  } catch {
    return [];
  }
}

function contextMetadataToDescriptor(metadata: ContextMetadata): ContextDescriptor {
  return {
    name: metadata.name,
    title: metadata.title,
    description: metadata.description,
    category: metadata.category,
    source: metadata.source === "built-in" ? "built-in" : "custom",
    target: metadata.target === "runtime" ? "runtime" : "editor",
    availabilityScope: metadata.availabilityScope === "playModeSession" || metadata.availabilityScope === "always" ? metadata.availabilityScope : "editorSession",
    playModeSupport: metadata.playModeSupport ?? "supported",
    requiresUnity: metadata.handler === "bridge" || metadata.handler === "bridge-runtime",
    available: metadata.available,
    inputSchema: metadata.inputSchema,
    outputSchema: metadata.outputSchema ?? { type: "object", properties: {} },
    destructiveHint: false,
    bridgeCommand: "context.read",
    cliCommand: metadata.cliCommand ?? `context read ${metadata.name}`,
    requiresPackages: metadata.requiresPackages,
    docsTopic: metadata.category,
    resourceUri: metadata.resourceUri,
    equivalentUnityMcp: metadata.equivalentUnityMcp,
    equivalentUnityMcpNote: metadata.equivalentUnityMcpNote,
    fingerprint: metadata.fingerprint
  };
}

function extensionContributionToDescriptor(ec: ExtensionContextContribution): ContextDescriptor {
  return {
    name: ec.key,
    title: ec.key,
    description: ec.description,
    category: "extension",
    source: "custom",
    target: "editor",
    availabilityScope: "editorSession",
    playModeSupport: "supported",
    requiresUnity: true,
    destructiveHint: false,
    bridgeCommand: "context.read",
    cliCommand: `context read ${ec.key}`,
    inputSchema: { type: "object", properties: {} },
    outputSchema: { type: "object", properties: {} },
  };
}

// ── Custom context discovery */
export interface ContextBridgeRuntime {
  bridgeExecute?: (payload: { requestId: string; command: string; args: Record<string, unknown> }) => Promise<{ ok?: boolean; data?: Record<string, unknown>; error?: { code?: string; message?: string } }>;
  bridgeEndpoint?: string;
}

/** Discover custom context descriptors from a connected Unity instance. */
export async function discoverCustomContexts(
  runtime: ContextBridgeRuntime,
  requestId: string
): Promise<CustomContextDescriptor[]> {
  if (!runtime.bridgeExecute && !runtime.bridgeEndpoint && !process.env.UNITY_CLI_BRIDGE_ENDPOINT) {
    return [];
  }

  try {
    const { executeBridgeCommand } = await import("../bridge/index.js");
    const result = await executeBridgeCommand(
      requestId,
      "context.list",
      {},
      undefined,
      runtime as import("../bridge/types.js").BridgeRuntime
    );

    if (!result.ok || !result.data) {
      return [];
    }

    const data = result.data as { contexts?: unknown[] };
    if (!Array.isArray(data.contexts)) {
      return [];
    }

    return data.contexts.map((ctx: unknown) => {
      const c = ctx as Record<string, unknown>;
      return {
        name: String(c.name ?? ""),
        title: String(c.title ?? c.name ?? ""),
        description: String(c.description ?? ""),
        category: String(c.category ?? "custom"),
        handlerClass: c.handlerClass ? String(c.handlerClass) : undefined,
        handlerMethod: c.handlerMethod ? String(c.handlerMethod) : undefined,
        inputSchema: typeof c.inputSchema === "object" && c.inputSchema !== null ? c.inputSchema as Record<string, unknown> : undefined,
        outputSchema: typeof c.outputSchema === "object" && c.outputSchema !== null ? c.outputSchema as Record<string, unknown> : undefined,
        extensionId: c.extensionId ? String(c.extensionId) : undefined,
        fingerprint: c.fingerprint ? String(c.fingerprint) : undefined,
      };
    });
  } catch {
    return [];
  }
}

/** Convert a CustomContextDescriptor into a ContextDescriptor for uniform handling. */
export function customContextToDescriptor(custom: CustomContextDescriptor): ContextDescriptor {
  return {
    name: custom.name,
    title: custom.title,
    description: custom.description,
    category: custom.category,
    source: "custom",
    target: "editor",
    availabilityScope: "editorSession",
    playModeSupport: "supported",
    requiresUnity: true,
    destructiveHint: false,
    bridgeCommand: "context.read",
    cliCommand: `context read ${custom.name}`,
    inputSchema: custom.inputSchema ?? { type: "object", properties: {} },
    outputSchema: custom.outputSchema ?? { type: "object", properties: {} },
    extensionId: custom.extensionId,
    fingerprint: custom.fingerprint,
    handlerClass: custom.handlerClass,
    handlerMethod: custom.handlerMethod,
  };
}

// ── Unified query ─────────────────────────────────────────────────────────

/** Collect ALL context descriptors: built-in + extension + custom. */
export async function allContextDescriptors(
  flags: Record<string, string | boolean>,
  runtime: ContextBridgeRuntime,
  requestId: string,
  options?: { includeCustom?: boolean }
): Promise<ContextDescriptor[]> {
  const builtin = builtinContextDescriptors();
  const extension = await extensionContextDescriptors(flags);

  let custom: ContextDescriptor[] = [];
  if (options?.includeCustom) {
    const customDescs = await discoverCustomContexts(runtime, requestId);
    custom = customDescs.map(customContextToDescriptor);
  }

  // Deduplicate by name (built-in takes precedence, then extension, then custom)
  const seen = new Set<string>();
  const result: ContextDescriptor[] = [];

  for (const ctx of [...builtin, ...extension, ...custom]) {
    if (!seen.has(ctx.name)) {
      seen.add(ctx.name);
      result.push(ctx);
    }
  }

  return result;
}

/** Convert a ContextDescriptor to a lightweight list entry. */
export function toListEntry(ctx: ContextDescriptor): ContextListEntry {
  return {
    name: ctx.name,
    title: ctx.title,
    description: ctx.description,
    category: ctx.category,
    source: ctx.source,
    target: ctx.target,
    playModeSupport: ctx.playModeSupport,
    available: ctx.available,
  };
}
