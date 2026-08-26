/**
 * Extension registry: discovers available extensions, reads installation
 * status, and resolves Unity package dependency mappings.
 *
 * The registry is the central point of truth for which extensions the CLI
 * knows about and whether they are available in a given project.
 */

import { BUILTIN_EXTENSION_MANIFESTS } from "./.generated-registry-data.js";
import type {
  ExtensionId,
  ExtensionManifest,
  ExtensionAvailability,
  ExtensionAvailabilityResult,
  PackageResolution,
  RegistryExtension,
  UnityPackageRequirement
} from "./types.js";

/**
 * Extension registry — discovers and resolves CLI extensions.
 *
 * Extensions declare required Unity packages; the registry resolves
 * availability by checking a project's manifest/lock files. The core
 * package never imports optional Unity packages at compile time.
 */
export class ExtensionRegistry {
  private readonly extensions = new Map<ExtensionId, RegistryExtension>();

  /**
   * Create a registry pre-loaded with built-in extension manifests.
   *
   * @param additionalManifests - Optional extra manifests to register
   * alongside the built-in ones (for testing or future plugin loading).
   */
  public constructor(additionalManifests: ExtensionManifest[] = []) {
    const allManifests = [...BUILTIN_EXTENSION_MANIFESTS, ...additionalManifests];
    for (const manifest of allManifests) {
      this.register(manifest);
    }
  }

  /**
   * Register an extension manifest.
   *
   * @throws {Error} If an extension with the same ID is already registered.
   */
  public register(manifest: ExtensionManifest): void {
    validateManifest(manifest);

    if (this.extensions.has(manifest.id)) {
      throw new ExtensionRegistryError(
        "DUPLICATE_EXTENSION",
        `Extension already registered: ${manifest.id}`,
        ["Each extension ID must be unique. Remove the duplicate or use a different ID."]
      );
    }

    this.extensions.set(manifest.id, {
      manifest,
      hasBuiltinBridgeDomain: false // Set externally after bridge registry is available
    });
  }

  /**
   * List all registered extension IDs.
   */
  public extensionIds(): ExtensionId[] {
    return [...this.extensions.keys()].sort();
  }

  /**
   * List all registered extensions with their manifests.
   */
  public listExtensions(): RegistryExtension[] {
    return [...this.extensions.values()].sort((a, b) =>
      a.manifest.id.localeCompare(b.manifest.id)
    );
  }

  /**
   * Look up a single extension by ID.
   */
  public getExtension(id: ExtensionId): RegistryExtension | undefined {
    return this.extensions.get(id);
  }

  /**
   * Check whether an extension is registered.
   */
  public hasExtension(id: ExtensionId): boolean {
    return this.extensions.has(id);
  }

  /**
   * Resolve the availability of an extension against a project's installed
   * packages.
   *
   * @param extensionId - The extension to check.
   * @param installedPackages - Map of package ID → installed version (from
   *   the project's manifest.json / packages-lock.json). Pass an empty map
   *   or omit to get "unknown" status.
   */
  public resolveAvailability(
    extensionId: ExtensionId,
    installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
  ): ExtensionAvailabilityResult {
    const ext = this.extensions.get(extensionId);
    if (!ext) {
      throw new ExtensionRegistryError(
        "UNKNOWN_EXTENSION",
        `Extension not found in registry: ${extensionId}`,
        [`Available extensions: ${this.extensionIds().join(", ")}`]
      );
    }

    return resolveExtensionAvailability(ext.manifest, installedPackages);
  }

  /**
   * Resolve availability for ALL registered extensions against a project.
   */
  public resolveAllAvailability(
    installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
  ): ExtensionAvailabilityResult[] {
    return this.extensionIds().map((id) =>
      this.resolveAvailability(id, installedPackages)
    );
  }

  /**
   * List extensions that are available in the given project.
   */
  public availableExtensions(
    installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
  ): ExtensionAvailabilityResult[] {
    return this.resolveAllAvailability(installedPackages)
      .filter((r) => r.status === "available");
  }

  /**
   * List extensions that are unavailable in the given project.
   */
  public unavailableExtensions(
    installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
  ): ExtensionAvailabilityResult[] {
    return this.resolveAllAvailability(installedPackages)
      .filter((r) => r.status === "unavailable");
  }

  /**
   * Get the set of all Unity package IDs required by any registered extension.
   */
  public allRequiredUnityPackageIds(): string[] {
    const ids = new Set<string>();
    for (const ext of this.extensions.values()) {
      for (const pkg of requiredUnityPackagesForManifest(ext.manifest)) {
        ids.add(pkg.packageId);
      }
    }
    return [...ids].sort();
  }
}

// ── Availability resolution ─────────────────────────────────────────────────

/**
 * Resolve an extension's availability against a project's installed packages.
 */
export function resolveExtensionAvailability(
  manifest: ExtensionManifest,
  installedPackages?: ReadonlyMap<string, string> | Readonly<Record<string, string>>
): ExtensionAvailabilityResult {
  const requiredPackages = requiredUnityPackagesForManifest(manifest);
  // Only `undefined` (no project context) yields "unknown". An empty
  // collection means "we checked the project, nothing is installed" → unavailable.
  if (installedPackages === undefined) {
    return {
      extensionId: manifest.id,
      status: "unknown",
      packages: requiredPackages.map((pkg) => ({
        packageId: pkg.packageId,
        displayName: pkg.displayName,
        installed: false,
        version: undefined
      })),
      missingPackageIds: requiredPackages.map((pkg) => pkg.packageId),
      nextActions: [
        `Provide a project path or installed packages map to resolve availability for ${manifest.name}.`
      ]
    };
  }

  const getPackage = (id: string): PackageResolution => {
    if (installedPackages instanceof Map) {
      const version = installedPackages.get(id);
      return { installed: version !== undefined, version };
    }
    const version = (installedPackages as Record<string, string>)[id];
    return { installed: version !== undefined, version };
  };

  const packages = requiredPackages.map((pkg) => {
    const resolved = getPackage(pkg.packageId);
    return {
      packageId: pkg.packageId,
      displayName: pkg.displayName,
      version: resolved.version,
      installed: resolved.installed && (pkg.versionRange === undefined || resolved.version === pkg.versionRange)
    };
  });

  const missingPackageIds = packages
    .filter((p) => !p.installed)
    .map((p) => p.packageId);

  const allInstalled = missingPackageIds.length === 0;
  const status: ExtensionAvailability = allInstalled ? "available" : "unavailable";

  const nextActions = allInstalled
    ? []
    : buildNextActions(manifest, missingPackageIds);

  return {
    extensionId: manifest.id,
    status,
    packages,
    missingPackageIds,
    nextActions
  };
}

function requiredUnityPackagesForManifest(manifest: ExtensionManifest): UnityPackageRequirement[] {
  const byId = new Map<string, UnityPackageRequirement>();
  for (const pkg of manifest.unityPackages) {
    byId.set(pkg.packageId, pkg);
  }
  for (const tool of manifest.contributions.tools ?? []) {
    for (const packageId of tool.requiresPackages ?? []) {
      if (!byId.has(packageId)) {
        byId.set(packageId, { packageId, displayName: packageId });
      }
    }
  }
  return [...byId.values()];
}

/**
 * Build nextActions for an unavailable extension.
 */
function buildNextActions(
  manifest: ExtensionManifest,
  missingPackageIds: string[]
): string[] {
  const actions: string[] = [];

  const requirements = requiredUnityPackagesForManifest(manifest)
    .filter((pkg) => missingPackageIds.includes(pkg.packageId))
    .map((pkg) => pkg.versionRange ? `${pkg.packageId}@${pkg.versionRange}` : pkg.packageId);
  actions.push(
    `Install the required Unity package(s) manually via Unity Package Manager, or run package.install with explicit package versions: ${requirements.join(", ")}.`
  );

  actions.push(
    `Do not write \"latest\" into manifest.json; Unity requires a SemVer/package source such as \"<version>\", \"file:<path>\" or a Git URL.`
  );

  actions.push(
    `After Unity resolves packages, run 'unitycli extensions install ${manifest.id} --project <path>'.`
  );

  actions.push(
    `Run 'unitycli extensions doctor --project <path>' to diagnose all extension availability.`
  );

  return actions;
}

// ── Validation ──────────────────────────────────────────────────────────────

/**
 * Validate an extension manifest for required fields and constraints.
 */
export function validateManifest(manifest: ExtensionManifest): void {
  if (!manifest.id || typeof manifest.id !== "string") {
    throw new ExtensionRegistryError(
      "INVALID_MANIFEST",
      "Extension manifest requires a non-empty 'id' field.",
      ["Use a kebab-case identifier, e.g. 'probuilder'."]
    );
  }

  if (!/^[a-z][a-z0-9]*(-[a-z0-9]+)*$/.test(manifest.id)) {
    throw new ExtensionRegistryError(
      "INVALID_MANIFEST",
      `Extension ID must be kebab-case (lowercase letters, digits, hyphens): ${manifest.id}`,
      ["Use a format like 'probuilder', 'vfx-graph', 'urp'."]
    );
  }

  if (!manifest.name || typeof manifest.name !== "string") {
    throw new ExtensionRegistryError(
      "INVALID_MANIFEST",
      `Extension '${manifest.id}' requires a non-empty 'name' field.`,
      ["Provide a human-readable name, e.g. 'ProBuilder'."]
    );
  }

  if (!manifest.description || typeof manifest.description !== "string") {
    throw new ExtensionRegistryError(
      "INVALID_MANIFEST",
      `Extension '${manifest.id}' requires a non-empty 'description' field.`,
      ["Provide a one-paragraph description."]
    );
  }

  if (!manifest.version || typeof manifest.version !== "string") {
    throw new ExtensionRegistryError(
      "INVALID_MANIFEST",
      `Extension '${manifest.id}' requires a non-empty 'version' field.`,
      ["Use semver format, e.g. '1.0.0'."]
    );
  }

  if (!Array.isArray(manifest.unityPackages)) {
    throw new ExtensionRegistryError(
      "INVALID_MANIFEST",
      `Extension '${manifest.id}' requires a 'unityPackages' array.`,
      ["List the Unity packages required by this extension."]
    );
  }

  for (const pkg of manifest.unityPackages) {
    if (!pkg.packageId || typeof pkg.packageId !== "string") {
      throw new ExtensionRegistryError(
        "INVALID_MANIFEST",
        `Extension '${manifest.id}' has a unityPackages entry missing 'packageId'.`,
        ["Each unityPackages entry must have a packageId, e.g. 'com.unity.probuilder'."]
      );
    }
    if (!pkg.displayName || typeof pkg.displayName !== "string") {
      throw new ExtensionRegistryError(
        "INVALID_MANIFEST",
        `Extension '${manifest.id}' has a unityPackages entry missing 'displayName'.`,
        [`Each unityPackages entry must have a displayName, e.g. 'ProBuilder'.`]
      );
    }
    if (pkg.versionRange !== undefined && (typeof pkg.versionRange !== "string" || !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(pkg.versionRange))) {
      throw new ExtensionRegistryError(
        "INVALID_MANIFEST",
        `Extension '${manifest.id}' has an invalid exact package version for '${pkg.packageId}'.`,
        ["Use an exact SemVer such as 5.2.4, or omit versionRange to accept any installed version."]
      );
    }
  }

  if (!manifest.contributions || typeof manifest.contributions !== "object") {
    throw new ExtensionRegistryError(
      "INVALID_MANIFEST",
      `Extension '${manifest.id}' requires a 'contributions' object.`,
      ["Provide tool capability IDs, context keys, and/or C# implementation entries."]
    );
  }

  // Validate tool contributions
  if (manifest.contributions.tools) {
    if (!Array.isArray(manifest.contributions.tools)) {
      throw new ExtensionRegistryError(
        "INVALID_MANIFEST",
        `Extension '${manifest.id}' contributions.tools must be an array.`,
        []
      );
    }
    for (const tool of manifest.contributions.tools) {
      if (!tool.name || typeof tool.name !== "string") {
        throw new ExtensionRegistryError(
          "INVALID_MANIFEST",
          `Extension '${manifest.id}' has an incomplete tool contribution: ${tool.name ?? "<missing name>"}.`,
          ["Each tool contribution must declare a stable tool name."]
        );
      }
    }
  }
}

// ── Error type ──────────────────────────────────────────────────────────────

export class ExtensionRegistryError extends Error {
  constructor(
    readonly code: "DUPLICATE_EXTENSION" | "UNKNOWN_EXTENSION" | "INVALID_MANIFEST",
    message: string,
    readonly nextActions: string[] = []
  ) {
    super(message);
  }
}
