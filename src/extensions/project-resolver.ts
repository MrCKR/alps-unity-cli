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

/**
 * Read installed Unity packages from a project's manifest.json and
 * packages-lock.json.
 *
 * Returns a Map of package ID → locked version string.
 *
 * @param projectPath - Root of the Unity project.
 * @returns Map of installed package IDs to their locked versions.
 */
export async function readInstalledUnityPackages(projectPath: string): Promise<Map<string, string>> {
  const packages = new Map<string, string>();

  // Read manifest.json for declared dependencies
  const manifestPath = join(resolve(projectPath), "Packages", "manifest.json");
  try {
    const manifestContent = await readFile(manifestPath, "utf8");
    const manifest = JSON.parse(stripBom(manifestContent)) as { dependencies?: Record<string, string> };
    if (manifest.dependencies) {
      for (const [id, version] of Object.entries(manifest.dependencies)) {
        if (typeof version === "string") {
          packages.set(id, version);
        }
      }
    }
  } catch {
    // manifest.json may not exist; continue with empty
  }

  // Read packages-lock.json for resolved versions (overrides manifest versions)
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
    // packages-lock.json may not exist; use manifest-only data
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

function stripBom(value: string): string {
  return value.charCodeAt(0) === 0xfeff ? value.slice(1) : value;
}
