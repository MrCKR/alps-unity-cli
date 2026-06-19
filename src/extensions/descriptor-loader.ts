/**
 * Extension descriptor loader: loads extension contributions (tool/context
 * descriptors, bridge commands, C# entries, docs fragments) with resolved
 * availability status.
 *
 * The descriptor loader bridges the extension registry with the CLI's
 * tool discovery system, producing LoadedExtensionDescriptor objects that
 * include both the extension's contributions and its resolved availability
 * against a specific Unity project.
 */

import { ExtensionRegistry } from "./registry.js";
import type {
  ExtensionAvailabilityResult,
  ExtensionCSharpEntry,
  ExtensionContextContribution,
  ExtensionDocsContribution,
  ExtensionId,
  ExtensionToolContribution,
  LoadedExtensionDescriptor
} from "./types.js";

/**
 * Load all extension descriptors with resolved availability.
 *
 * @param registry - The extension registry to load from.
 * @param installedPackages - Map of package ID → installed version for the
 *   target project. Used to determine availability of each extension.
 * @returns Loaded descriptors for all registered extensions, sorted by ID.
 */
export function loadAllExtensionDescriptors(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): LoadedExtensionDescriptor[] {
  return registry.extensionIds().map((id) =>
    loadExtensionDescriptor(registry, id, installedPackages)
  );
}

/**
 * Load a single extension descriptor with resolved availability.
 *
 * @param registry - The extension registry.
 * @param extensionId - The extension to load.
 * @param installedPackages - Map of package ID → installed version.
 * @returns The loaded descriptor with availability and contributions.
 * @throws {Error} If the extension is not registered.
 */
export function loadExtensionDescriptor(
  registry: ExtensionRegistry,
  extensionId: ExtensionId,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): LoadedExtensionDescriptor {
  const ext = registry.getExtension(extensionId);
  if (!ext) {
    throw new Error(`Extension not found in registry: ${extensionId}`);
  }

  const availability = registry.resolveAvailability(extensionId, installedPackages);
  const { contributions } = ext.manifest;

  return {
    manifest: ext.manifest,
    availability,
    tools: contributions.tools ?? [],
    contexts: contributions.contexts ?? [],
    bridgeCommands: contributions.bridgeCommands ?? [],
    csharpEntries: contributions.csharpEntries ?? [],
    docs: contributions.docs ?? []
  };
}

/**
 * Load only available extension descriptors (all required packages installed).
 */
export function loadAvailableDescriptors(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): LoadedExtensionDescriptor[] {
  return loadAllExtensionDescriptors(registry, installedPackages)
    .filter((desc) => desc.availability.status === "available");
}

/**
 * Load only unavailable extension descriptors (missing required packages).
 */
export function loadUnavailableDescriptors(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): LoadedExtensionDescriptor[] {
  return loadAllExtensionDescriptors(registry, installedPackages)
    .filter((desc) => desc.availability.status === "unavailable");
}

/**
 * Collect all tool contributions across available extensions.
 *
 * This is the primary entry point for tools list / tools search to discover
 * extension-provided tools without scanning unavailable extensions.
 */
export function collectAvailableTools(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): ExtensionToolContribution[] {
  const available = loadAvailableDescriptors(registry, installedPackages);
  return available.flatMap((desc) => desc.tools);
}

/**
 * Collect all tool contributions across ALL extensions (including unavailable).
 *
 * Used by `tools list` when the user explicitly wants to see all tools
 * regardless of availability.
 */
export function collectAllTools(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): Array<ExtensionToolContribution & { extensionId: ExtensionId; available: boolean }> {
  const all = loadAllExtensionDescriptors(registry, installedPackages);
  return all.flatMap((desc) =>
    desc.tools.map((tool) => ({
      ...tool,
      extensionId: desc.manifest.id,
      available: desc.availability.status === "available"
    }))
  );
}

/**
 * Collect all context contributions across available extensions.
 */
export function collectAvailableContexts(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): ExtensionContextContribution[] {
  const available = loadAvailableDescriptors(registry, installedPackages);
  return available.flatMap((desc) => desc.contexts);
}

/**
 * Collect all docs topic contributions across available extensions.
 */
export function collectAvailableDocs(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): ExtensionDocsContribution[] {
  const available = loadAvailableDescriptors(registry, installedPackages);
  return available.flatMap((desc) => desc.docs);
}

/**
 * Collect all C# implementation entry points across available extensions.
 */
export function collectAvailableCSharpEntries(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): ExtensionCSharpEntry[] {
  const available = loadAvailableDescriptors(registry, installedPackages);
  return available.flatMap((desc) => desc.csharpEntries);
}

/**
 * Build a summary of extension availability for diagnostics / doctor output.
 *
 * Returns a structured summary suitable for JSON or text rendering.
 */
export function extensionAvailabilitySummary(
  registry: ExtensionRegistry,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): {
  total: number;
  available: number;
  unavailable: number;
  unknown: number;
  extensions: Array<{
    id: ExtensionId;
    name: string;
    status: string;
    missingPackages: string[];
    toolCount: number;
    nextActions: string[];
  }>;
} {
  const results = loadAllExtensionDescriptors(registry, installedPackages);

  return {
    total: results.length,
    available: results.filter((r) => r.availability.status === "available").length,
    unavailable: results.filter((r) => r.availability.status === "unavailable").length,
    unknown: results.filter((r) => r.availability.status === "unknown").length,
    extensions: results.map((desc) => ({
      id: desc.manifest.id,
      name: desc.manifest.name,
      status: desc.availability.status,
      missingPackages: desc.availability.missingPackageIds,
      toolCount: desc.tools.length,
      nextActions: desc.availability.nextActions
    }))
  };
}
