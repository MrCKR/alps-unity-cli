/**
 * Context registry: manages built-in and custom context descriptors.
 *
 * Provides lookup, listing, search, and discovery functions for the context
 * command family. Built-in contexts are static; custom contexts are discovered
 * at runtime from the Unity Bridge and from installed extensions.
 */

import { builtinContextMetadata } from "./metadata/index.js";
import { collectAvailableContexts, ExtensionRegistry, loadAllExtensionDescriptors, readInstalledUnityPackages } from "../extensions/index.js";
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
    c.description.toLowerCase().includes(lower) ||
    c.category.toLowerCase().includes(lower)
  ).sort((a, b) => {
    const aScore = scoreSearch(a.name.toLowerCase(), lower) + scoreSearch(a.description.toLowerCase(), lower);
    const bScore = scoreSearch(b.name.toLowerCase(), lower) + scoreSearch(b.description.toLowerCase(), lower);
    return bScore - aScore;
  });
}

// ── Extension context contributions ───────────────────────────────────────

/** Collect context descriptors from extension manifests; project availability is checked at describe/read time. */
export async function extensionContextDescriptors(
  flags: Record<string, string | boolean>
): Promise<ContextDescriptor[]> {
  const registry = new ExtensionRegistry();
  const projectPath = getString(flags, "project");
  if (!projectPath) {
    return loadAllExtensionDescriptors(registry).flatMap((desc) =>
      desc.contexts.map((context) => extensionContributionToDescriptor(context))
    );
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
    description: metadata.description,
    category: metadata.category,
    source: metadata.source,
    target: metadata.target ?? "editor",
    availabilityScope: metadata.availabilityScope === "playModeSession" || metadata.availabilityScope === "always" ? metadata.availabilityScope : "editorSession",
    playModeSupport: metadata.playModeSupport ?? "supported",
    available: metadata.available,
    inputSchema: metadata.inputSchema,
    outputSchema: metadata.outputSchema ?? { type: "object", properties: {} },
    cliCommand: metadata.cliCommand ?? `context read ${metadata.name}`,
    supportStatus: metadata.handler === "bridge-runtime" ? "live-bridge-runtime" : metadata.handler === "offline" ? "cli-only" : "live-bridge",
    requiresPackages: metadata.requiresPackages,
    refreshBehavior: metadata.refreshBehavior
  };
}

function extensionContributionToDescriptor(ec: ExtensionContextContribution): ContextDescriptor {
  return {
    name: ec.key,
    description: ec.description,
    category: ec.category ?? extensionContextCategory(ec.key),
    source: "extension",
    target: ec.target ?? "editor",
    availabilityScope: ec.availabilityScope ?? "editorSession",
    playModeSupport: ec.playModeSupport ?? "supported",
    supportStatus: "live-bridge",
    cliCommand: `context read ${ec.key}`,
    inputSchema: ec.inputSchema ?? { type: "object", properties: {} },
    outputSchema: ec.outputSchema ?? { type: "object", properties: {} },
    refreshBehavior: ec.refreshBehavior,
  };
}

/** 扩展 Context 默认按 dotted key 前缀归类，便于 AI 用领域 category 发现能力。 */
function extensionContextCategory(key: string): string {
  const separator = key.indexOf(".");
  return separator > 0 ? key.slice(0, separator) : "extension";
}

// ── Custom context discovery */
export interface ContextBridgeRuntime {
  bridgeExecute?: (payload: { requestId: string; command: string; args: Record<string, unknown> }) => Promise<{ ok?: boolean; data?: Record<string, unknown>; error?: { code?: string; message?: string } }>;
  bridgeEndpoint?: string;
}

/** Discover custom context descriptors from a connected Unity instance. */
export async function discoverCustomContexts(
  runtime: ContextBridgeRuntime,
  requestId: string,
  flags: Record<string, string | boolean> = {}
): Promise<CustomContextDescriptor[]> {
  try {
    const { executeBridgeCommand } = await import("../bridge/index.js");
    const result = await executeBridgeCommand(
      requestId,
      "context.list",
      flags,
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

    const contexts: CustomContextDescriptor[] = [];
    for (const ctx of data.contexts) {
      const c = ctx as Record<string, unknown>;
      const name = String(c.name ?? "");
      const description = String(c.description ?? "");
      if (!name || !description) {
        continue;
      }
      contexts.push({
        name,
        description,
        category: String(c.category ?? "custom"),
        inputSchema: typeof c.inputSchema === "object" && c.inputSchema !== null ? c.inputSchema as Record<string, unknown> : undefined,
        outputSchema: typeof c.outputSchema === "object" && c.outputSchema !== null ? c.outputSchema as Record<string, unknown> : undefined,
        extensionId: c.extensionId ? String(c.extensionId) : undefined,
      });
    }
    return contexts;
  } catch {
    return [];
  }
}

/** Convert a CustomContextDescriptor into a ContextDescriptor for uniform handling. */
export function customContextToDescriptor(custom: CustomContextDescriptor): ContextDescriptor {
  return {
    name: custom.name,
    description: custom.description,
    category: custom.category,
    source: "project",
    target: "editor",
    availabilityScope: "editorSession",
    playModeSupport: "supported",
    supportStatus: "live-bridge",
    cliCommand: `context read ${custom.name} --include-custom`,
    inputSchema: custom.inputSchema ?? { type: "object", properties: {} },
    outputSchema: custom.outputSchema ?? { type: "object", properties: {} },
    extensionId: custom.extensionId,
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
    const customDescs = await discoverCustomContexts(runtime, requestId, flags);
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
    description: ctx.description,
    category: ctx.category,
    source: ctx.source,
    target: ctx.target,
    playModeSupport: ctx.playModeSupport,
    supportStatus: ctx.supportStatus,
    cliCommand: ctx.cliCommand,
    available: ctx.available,
    requiresPackages: ctx.requiresPackages,
  };
}
