/**
 * Extension availability checker for tools describe / tools run.
 *
 * When a tool declares `requiresPackages` (indicating it depends on an
 * optional Unity package), this module resolves whether the extension
 * is available against a target project. If not, it returns a structured
 * unavailable result with next_actions instead of attempting execution.
 */

import { ExtensionRegistry } from "./extensions/index.js";
import { readInstalledUnityPackages } from "./extensions/project-resolver.js";
import { collectAllTools } from "./extensions/descriptor-loader.js";
import type { ExtensionAvailabilityResult, ExtensionId } from "./extensions/types.js";
import type { ToolMetadata } from "./tools/metadata/types.js";
import { getString } from "./args.js";

/**
 * Result of checking extension availability for a tool.
 */
export interface ExtensionAvailabilityCheck {
  /** Whether the tool's extension dependencies are satisfied. */
  available: boolean;
  /** The extension ID that provides this tool, if applicable. */
  extensionId?: ExtensionId;
  /** Detailed availability result from the registry. */
  availabilityResult?: ExtensionAvailabilityResult;
  /** Next actions when the tool is unavailable. */
  nextActions: string[];
  /** Error code for the unavailable case. */
  errorCode: string;
  /** Human-readable message for the unavailable case. */
  message: string;
}

/**
 * Check whether a tool's extension dependencies are available.
 *
 * For tools that declare `requiresPackages`, this resolves the extension
 * manifest against the target project's installed Unity packages.
 *
 * For tools without `requiresPackages`, always returns available.
 *
 * @param tool - The tool metadata to check.
 * @param flags - CLI flags (may contain --project path).
 * @returns Availability check result.
 */
export async function checkToolExtensionAvailability(
  tool: ToolMetadata,
  flags: Record<string, string | boolean>
): Promise<ExtensionAvailabilityCheck> {
  // Tools without package requirements are always available.
  if (!tool.requiresPackages || tool.requiresPackages.length === 0) {
    return { available: true, nextActions: [], errorCode: "", message: "" };
  }

  const registry = new ExtensionRegistry();

  // Find which extension provides this tool.
  const extensionId = findExtensionForTool(registry, tool.name);

  // No project path → we cannot determine availability, but we
  // don't block execution either; we report "unknown" and let
  // the bridge attempt fail naturally if the package is truly missing.
  const projectPath = getString(flags, "project");
  if (!projectPath) {
    return {
      available: true,
      extensionId,
      nextActions: [],
      errorCode: "",
      message: ""
    };
  }

  // Resolve installed packages from the target project.
  let installedPackages: Map<string, string>;
  try {
    installedPackages = await readInstalledUnityPackages(projectPath);
  } catch {
    return {
      available: true,
      extensionId,
      nextActions: [],
      errorCode: "",
      message: ""
    };
  }

  const missingPackages = tool.requiresPackages.filter((pkg) => !installedPackages.has(pkg));
  const availability = extensionId && registry.hasExtension(extensionId)
    ? packageAvailabilityForTool(registry, extensionId, installedPackages, missingPackages)
    : undefined;

  if (missingPackages.length === 0) {
    return {
      available: true,
      extensionId,
      availabilityResult: availability,
      nextActions: [],
      errorCode: "",
      message: ""
    };
  }

  const extensionName = extensionId && registry.hasExtension(extensionId)
    ? registry.getExtension(extensionId)!.manifest.name
    : "unknown";

  return {
    available: false,
    extensionId,
    availabilityResult: availability,
    nextActions: buildToolUnavailableNextActions(tool, extensionName, missingPackages),
    errorCode: "E_EXTENSION_UNAVAILABLE",
    message: `Tool "${tool.name}" requires Unity package(s) not installed: ${missingPackages.join(", ")}.`
  };
}

/**
 * Check whether a context's extension dependencies are available.
 *
 * Contexts contributed by extensions (e.g. probuilder.scene, vfx.assets)
 * need their dependencies satisfied before they can be read.
 *
 * @param contextKey - The context key (e.g. "probuilder.scene").
 * @param flags - CLI flags (may contain --project path).
 * @returns Availability check result.
 */
export async function checkContextExtensionAvailability(
  contextKey: string,
  flags: Record<string, string | boolean>
): Promise<ExtensionAvailabilityCheck> {
  const registry = new ExtensionRegistry();
  const extensionId = findExtensionForContext(registry, contextKey);

  if (!extensionId) {
    return { available: true, nextActions: [], errorCode: "", message: "" };
  }

  const projectPath = getString(flags, "project");
  if (!projectPath) {
    return {
      available: true,
      extensionId,
      nextActions: [],
      errorCode: "",
      message: ""
    };
  }

  let installedPackages: Map<string, string>;
  try {
    installedPackages = await readInstalledUnityPackages(projectPath);
  } catch {
    return {
      available: true,
      extensionId,
      nextActions: [],
      errorCode: "",
      message: ""
    };
  }

  if (!registry.hasExtension(extensionId)) {
    return {
      available: true,
      extensionId,
      nextActions: [],
      errorCode: "",
      message: ""
    };
  }

  const availability = registry.resolveAvailability(extensionId, installedPackages);

  if (availability.status === "available") {
    return {
      available: true,
      extensionId,
      availabilityResult: availability,
      nextActions: [],
      errorCode: "",
      message: ""
    };
  }

  if (availability.status === "unavailable") {
    const extension = registry.getExtension(extensionId)!;
    return {
      available: false,
      extensionId,
      availabilityResult: availability,
      nextActions: buildContextUnavailableNextActions(contextKey, extension.manifest.name, availability.missingPackageIds),
      errorCode: "E_EXTENSION_UNAVAILABLE",
      message: `Context "${contextKey}" requires the ${extension.manifest.name} extension, but required Unity package(s) are not installed: ${availability.missingPackageIds.join(", ")}.`
    };
  }

  // "unknown" — can't determine; allow proceed.
  return {
    available: true,
    extensionId,
    availabilityResult: availability,
    nextActions: [],
    errorCode: "",
    message: ""
  };
}

/**
 * Build the full list of extension tool descriptors, including unavailable
 * extension tools alongside built-in tools. Used by tools group list/describe to surface
 * extension tools alongside built-in tools.
 */
export async function collectExtensionToolDescriptors(
  flags: Record<string, string | boolean>
): Promise<Array<ToolMetadata & { extensionId?: ExtensionId; extensionAvailable?: boolean }>> {
  const registry = new ExtensionRegistry();
  const projectPath = getString(flags, "project");

  let installedPackages: Map<string, string> | undefined;
  if (projectPath) {
    try {
      installedPackages = await readInstalledUnityPackages(projectPath);
    } catch {
      // Leave undefined → "unknown" availability
    }
  }

  const allTools = collectAllTools(registry, installedPackages);
  return allTools.map((extTool) => ({
    name: extTool.name,
    summary: extTool.summary,
    description: extTool.description,
    group: extTool.group,
    inputSchema: {},
    destructiveHint: false,
    handler: "bridge" as const,
    supportStatus: "live-bridge" as const,
    cliCommand: extTool.cliCommand,
    target: "editor",
    requiresPackages: [],
    extensionId: extTool.extensionId,
    extensionAvailable: extTool.available
  }));
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function packageAvailabilityForTool(
  registry: ExtensionRegistry,
  extensionId: ExtensionId,
  installedPackages: ReadonlyMap<string, string>,
  missingPackageIds: string[]
): ExtensionAvailabilityResult {
  const extension = registry.getExtension(extensionId)!;
  const requiredPackages = extension.manifest.unityPackages.filter((pkg) => installedPackages.has(pkg.packageId) || missingPackageIds.includes(pkg.packageId));
  const packages = requiredPackages.map((pkg) => ({
    packageId: pkg.packageId,
    displayName: pkg.displayName,
    installed: installedPackages.has(pkg.packageId),
    version: installedPackages.get(pkg.packageId)
  }));
  return {
    extensionId,
    status: missingPackageIds.length === 0 ? "available" : "unavailable",
    packages,
    missingPackageIds,
    nextActions: missingPackageIds.length === 0 ? [] : buildToolUnavailableNextActions({
      name: extensionId,
      summary: extension.manifest.name,
      description: extension.manifest.description,
      group: extensionId,
      inputSchema: {},
      destructiveHint: false
    }, extension.manifest.name, missingPackageIds)
  };
}

function findExtensionForTool(registry: ExtensionRegistry, toolName: string): ExtensionId | undefined {
  for (const id of registry.extensionIds()) {
    const ext = registry.getExtension(id)!;
    if (ext.manifest.contributions.tools?.some((t) => t.name === toolName)) {
      return id;
    }
  }
  return undefined;
}

function findExtensionForContext(registry: ExtensionRegistry, contextKey: string): ExtensionId | undefined {
  for (const id of registry.extensionIds()) {
    const ext = registry.getExtension(id)!;
    if (ext.manifest.contributions.contexts?.some((c) => c.key === contextKey)) {
      return id;
    }
  }
  return undefined;
}

function buildToolUnavailableNextActions(
  tool: ToolMetadata,
  extensionName: string,
  missingPackageIds: string[]
): string[] {
  const actions: string[] = [];
  actions.push(
    `Install the required Unity package(s) manually via Unity Package Manager, or run package.install with explicit package versions to enable ${extensionName}: ${missingPackageIds.join(", ")}.`
  );
  actions.push(
    `Do not write \"latest\" into manifest.json; Unity requires a SemVer/package source such as \"<version>\", \"file:<path>\" or a Git URL.`
  );
  actions.push(
    `After Unity resolves packages, run 'unity_cli extensions install <id> --project <path>' for the matching extension bundle.`
  );
  actions.push(
    `Run 'unity_cli extensions doctor --project <path>' to diagnose all extension availability.`
  );
  actions.push(
    `Run 'unity_cli tools describe ${tool.name}' to inspect tool requirements.`
  );
  return actions;
}

function buildContextUnavailableNextActions(
  contextKey: string,
  extensionName: string,
  missingPackageIds: string[]
): string[] {
  const actions: string[] = [];
  actions.push(
    `Install the required Unity package(s) manually via Unity Package Manager, or run package.install with explicit package versions to enable ${extensionName}: ${missingPackageIds.join(", ")}.`
  );
  actions.push(
    `Do not write \"latest\" into manifest.json; Unity requires a SemVer/package source such as \"<version>\", \"file:<path>\" or a Git URL.`
  );
  actions.push(
    `Run 'unity_cli extensions doctor --project <path>' to diagnose all extension availability.`
  );
  return actions;
}
