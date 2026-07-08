/**
 * Context registry: manages built-in and custom context descriptors.
 *
 * Provides lookup, listing, search, and discovery functions for the context
 * command family. TS-owned contexts are local descriptors; C# built-in and
 * project custom contexts are discovered at runtime from the Unity Bridge.
 */

import { defineContextMetadata } from "../contexts/registry.js";
import { collectAvailableContexts, ExtensionRegistry, loadAllExtensionDescriptors, readInstalledUnityPackages } from "../extensions/index.js";
import { getString } from "../args.js";
import { scoreSearch } from "../util.js";
import type { ContextDescriptor, ContextListEntry, CustomContextDescriptor } from "./types.js";
import type { ExtensionContextContribution } from "../extensions/types.js";

// ── Local built-in query functions ────────────────────────────────────────

/** Return CLI-owned local context descriptors; C# built-ins come from live Bridge discovery. */
export function builtinContextDescriptors(): ContextDescriptor[] {
  return defineContextMetadata();
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

function extensionContributionToDescriptor(ec: ExtensionContextContribution): ContextDescriptor {
  return {
    name: ec.key,
    summary: ec.description,
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
        summary: typeof c.summary === "string" && c.summary ? c.summary : description,
        description,
        category: String(c.category ?? "custom"),
        source: contextSource(c.source),
        target: contextTarget(c.target),
        availabilityScope: contextAvailabilityScope(c.availabilityScope),
        playModeSupport: contextPlayModeSupport(c.playModeSupport),
        supportStatus: typeof c.supportStatus === "string" ? c.supportStatus : undefined,
        cliCommand: typeof c.cliCommand === "string" ? c.cliCommand : undefined,
        inputSchema: typeof c.inputSchema === "object" && c.inputSchema !== null ? c.inputSchema as Record<string, unknown> : undefined,
        outputSchema: typeof c.outputSchema === "object" && c.outputSchema !== null ? c.outputSchema as Record<string, unknown> : undefined,
        requiresPackages: stringArray(c.requiresPackages),
        refreshBehavior: typeof c.refreshBehavior === "string" ? c.refreshBehavior : undefined,
        available: typeof c.available === "boolean" ? c.available : undefined,
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
  const source = custom.source ?? "project";
  const target = custom.target ?? "editor";
  return {
    name: custom.name,
    summary: custom.summary ?? custom.description,
    description: custom.description,
    category: custom.category,
    source,
    target,
    availabilityScope: custom.availabilityScope ?? (target === "runtime" ? "playModeSession" : "editorSession"),
    playModeSupport: custom.playModeSupport ?? (target === "runtime" ? "runtime-only" : "supported"),
    supportStatus: custom.supportStatus ?? (target === "runtime" ? "live-bridge-runtime" : "live-bridge"),
    cliCommand: custom.cliCommand ?? `context read ${custom.name}${source === "project" ? " --include-custom" : ""}`,
    inputSchema: custom.inputSchema ?? { type: "object", properties: {} },
    outputSchema: custom.outputSchema ?? { type: "object", properties: {} },
    requiresPackages: custom.requiresPackages,
    refreshBehavior: custom.refreshBehavior,
    available: custom.available,
    extensionId: custom.extensionId,
  };
}

function contextSource(value: unknown): CustomContextDescriptor["source"] {
  return value === "built-in" || value === "extension" || value === "project" ? value : undefined;
}

function contextTarget(value: unknown): CustomContextDescriptor["target"] {
  return value === "editor" || value === "runtime" || value === "both" ? value : undefined;
}

function contextAvailabilityScope(value: unknown): CustomContextDescriptor["availabilityScope"] {
  return value === "editorSession" || value === "playModeSession" || value === "always" ? value : undefined;
}

function contextPlayModeSupport(value: unknown): CustomContextDescriptor["playModeSupport"] {
  return value === "supported" || value === "runtime-only" || value === "session-affecting" || value === "editor-only" || value === "unknown" ? value : undefined;
}

function stringArray(value: unknown): string[] | undefined {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : undefined;
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
    summary: ctx.summary ?? ctx.description,
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
