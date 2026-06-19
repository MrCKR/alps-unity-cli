/**
 * Init auto-detect: reads the target Unity project's installed packages,
 * matches known Unity package IDs to CLI extensions, and auto-installs
 * matching extensions.
 *
 * P5-T03: init auto-detect Unity packages and install matching extensions.
 *
 * Rules:
 * - Only installs an extension when its required Unity packages are all
 *   present in the project's manifest/lock.
 * - Does NOT install Unity packages themselves.
 * - Does NOT guess based on file names, directories, or assembly names.
 * - Already-enabled extensions are not re-installed.
 * - In --check / dry-run mode, shows what would be installed without writing.
 * - On detection failure (missing manifest, etc.), returns clear next_actions.
 */

import { ExtensionRegistry } from "../extensions/registry.js";
import { readInstalledUnityPackages } from "../extensions/project-resolver.js";
import { readInstalledExtensions, writeInstalledExtensions } from "./extensions.js";
import type { ExtensionAvailabilityResult, ExtensionId } from "../extensions/types.js";

export interface AutoDetectResult {
  /** Extensions that were auto-installed (newly enabled). */
  installed: Array<{
    extensionId: ExtensionId;
    name: string;
  }>;
  /** Extensions that were skipped because they were already enabled. */
  skipped: Array<{
    extensionId: ExtensionId;
    name: string;
    reason: string;
  }>;
  /** Extensions whose Unity packages were not found in the project. */
  unavailable: Array<{
    extensionId: ExtensionId;
    name: string;
    missingPackageIds: string[];
    nextActions: string[];
  }>;
  /** Extensions whose availability could not be determined. */
  unknown: Array<{
    extensionId: ExtensionId;
    name: string;
    reason: string;
  }>;
}

/**
 * Auto-detect and auto-install CLI extensions based on a Unity project's
 * installed packages.
 *
 * @param projectPath - Root of the Unity project.
 * @param dryRun - If true, compute the plan but do not write install state.
 * @returns Structured result showing what was/would be installed, skipped,
 *   unavailable, or unknown.
 */
export async function autoDetectExtensions(
  projectPath: string,
  dryRun = false
): Promise<AutoDetectResult> {
  const registry = new ExtensionRegistry();
  const installedPackages = await readInstalledUnityPackages(projectPath);

  // When we can't read any packages at all (empty result), we can still
  // report unavailable — this handles the "no manifest.json" case.
  const allAvailability = registry.resolveAllAvailability(installedPackages.size > 0 ? installedPackages : new Map());

  const enabled = dryRun
    ? await readInstalledExtensions(projectPath)
    : await readInstalledExtensions(projectPath);

  const result: AutoDetectResult = {
    installed: [],
    skipped: [],
    unavailable: [],
    unknown: []
  };

  for (const avail of allAvailability) {
    const ext = registry.getExtension(avail.extensionId);
    if (!ext) continue;
    const { manifest } = ext;

    if (avail.status === "unknown") {
      result.unknown.push({
        extensionId: avail.extensionId,
        name: manifest.name,
        reason: "Could not resolve availability (project packages not readable)."
      });
      continue;
    }

    if (avail.status === "unavailable") {
      result.unavailable.push({
        extensionId: avail.extensionId,
        name: manifest.name,
        missingPackageIds: avail.missingPackageIds,
        nextActions: avail.nextActions
      });
      continue;
    }

    // Available — check if already enabled
    if (enabled.has(avail.extensionId)) {
      result.skipped.push({
        extensionId: avail.extensionId,
        name: manifest.name,
        reason: "Extension is already enabled."
      });
      continue;
    }

    // Auto-install
    enabled.add(avail.extensionId);
    result.installed.push({
      extensionId: avail.extensionId,
      name: manifest.name
    });
  }

  // Write state (only if not dry-run and something was installed)
  if (!dryRun && result.installed.length > 0) {
    await writeInstalledExtensions(projectPath, enabled);
  }

  return result;
}

/**
 * Build a summary of auto-detect results for CLI output.
 */
export function autoDetectSummary(result: AutoDetectResult): {
  total: number;
  installed: number;
  skipped: number;
  unavailable: number;
  unknown: number;
  summary: string;
} {
  const total = result.installed.length + result.skipped.length + result.unavailable.length + result.unknown.length;
  const parts: string[] = [];

  if (result.installed.length > 0) {
    parts.push(`installed: ${result.installed.map((e) => e.name).join(", ")}`);
  }
  if (result.skipped.length > 0) {
    parts.push(`skipped: ${result.skipped.map((e) => `${e.name} (${e.reason})`).join(", ")}`);
  }
  if (result.unavailable.length > 0) {
    parts.push(`unavailable: ${result.unavailable.map((e) => `${e.name} (missing: ${e.missingPackageIds.join(", ")})`).join(", ")}`);
  }
  if (result.unknown.length > 0) {
    parts.push(`unknown: ${result.unknown.map((e) => e.name).join(", ")}`);
  }

  return {
    total,
    installed: result.installed.length,
    skipped: result.skipped.length,
    unavailable: result.unavailable.length,
    unknown: result.unknown.length,
    summary: parts.length > 0 ? parts.join("; ") : "No extensions detected."
  };
}
