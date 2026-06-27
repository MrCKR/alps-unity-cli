/**
 * Project-level resolver: reads installed Unity packages from a project's
 * manifest.json and packages-lock.json, then resolves extension availability.
 *
 * This module bridges the package-manager module (which reads Unity manifests)
 * with the extension registry (which declares package requirements).
 */

import { readFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { ExtensionRegistry } from "./registry.js";
import type { ExtensionAvailabilityResult } from "./types.js";
import { stripBom } from "../util.js";

/**
 * Read installed (resolved) Unity packages from a project's packages-lock.json.
 *
 * Only packages that appear in packages-lock.json are considered "installed" —
 * this is the authoritative record of what Unity's package manager has actually
 * resolved and made available. A package declared in manifest.json but absent
 * from packages-lock.json has NOT been resolved and cannot be used; reporting it
 * as installed would produce false-positive diagnostics.
 *
 * Returns a Map of package ID → resolved version string.
 *
 * @param projectPath - Root of the Unity project.
 * @returns Map of installed package IDs to their resolved versions.
 */
export async function readInstalledUnityPackages(projectPath: string): Promise<Map<string, string>> {
  const packages = new Map<string, string>();

  // Only read packages-lock.json for RESOLVED (installed) packages.
  // manifest.json declares intent but not resolution; a package listed in
  // manifest but absent from lock has not been resolved by Unity.
  const lockPath = join(resolve(projectPath), "Packages", "packages-lock.json");
  try {
    const lockContent = await readFile(lockPath, "utf8");
    const lock = JSON.parse(stripBom(lockContent)) as { dependencies?: Record<string, { version?: string }> };
    if (lock.dependencies) {
      for (const [id, entry] of Object.entries(lock.dependencies)) {
        if (entry.version) {
          packages.set(id, entry.version);
        }
      }
    }
  } catch {
    // packages-lock.json may not exist; return empty
  }

  return packages;
}

/**
 * Resolve extension availability for a specific Unity project.
 *
 * Reads the project's manifest/lock files and resolves all registered
 * extensions against the installed packages.
 *
 * @param registry - The extension registry.
 * @param projectPath - Root of the Unity project.
 * @returns Availability results for all extensions.
 */
export async function resolveExtensionAvailabilityForProject(
  registry: ExtensionRegistry,
  projectPath: string
): Promise<ExtensionAvailabilityResult[]> {
  const installedPackages = await readInstalledUnityPackages(projectPath);
  return registry.resolveAllAvailability(installedPackages);
}
