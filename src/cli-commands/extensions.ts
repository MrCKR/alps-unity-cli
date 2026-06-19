/**
 * CLI sub-commands: extensions list, extensions install, extensions remove,
 * extensions doctor.
 *
 * P5-T02: extensions command surface built on top of the P5-T01 registry /
 * descriptor loader.
 *
 * `extensions list`     — list available extensions, install state, Unity
 *                         package mappings, availability and next_actions.
 * `extensions install`  — install (enable) an extension in the current project.
 * `extensions remove`   — uninstall (disable) an extension, removing its
 *                         descriptor contributions from discovery.
 * `extensions doctor`   — diagnose extension install state, Unity package
 *                         dependency consistency, missing deps, and descriptor
 *                         load errors.
 *
 * Install state is persisted per-project under
 *   <project>/Packages/com.unity_cli/extensions.json
 * as a list of enabled extension IDs. The core package never depends on
 * optional Unity packages at compile time; enabling an extension only flips
 * whether its descriptors participate in tool/context discovery.
 */

import { readFile, writeFile, mkdir } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { getBoolean, getString } from "../args.js";
import { failure, success } from "../output.js";
import type { CliResult } from "../types.js";
import type { Runtime } from "./types.js";
import {
  ExtensionRegistry,
  ExtensionRegistryError,
  loadExtensionDescriptor,
  extensionAvailabilitySummary,
} from "../extensions/index.js";
import { readInstalledUnityPackages } from "../extensions/project-resolver.js";
import type { ExtensionId } from "../extensions/types.js";

// ── Install state store ────────────────────────────────────────────────────

/** Location of the per-project extension install state file. */
function extensionsStatePath(projectPath: string): string {
  return join(resolve(projectPath), "Packages", "com.unity_cli", "extensions.json");
}

/**
 * Read the set of enabled (installed) extension IDs for a project.
 *
 * Returns an empty Set when the state file does not exist yet (nothing
 * installed). A malformed state file is treated as empty rather than
 * fatal, because doctor should report such corruption, not list.
 */
export async function readInstalledExtensions(projectPath: string): Promise<Set<ExtensionId>> {
  const statePath = extensionsStatePath(projectPath);
  try {
    const content = await readFile(statePath, "utf8");
    const parsed = JSON.parse(content) as { extensions?: string[] };
    return new Set(parsed.extensions ?? []);
  } catch {
    return new Set();
  }
}

/**
 * Write the set of enabled extension IDs for a project.
 */
export async function writeInstalledExtensions(projectPath: string, ids: Set<ExtensionId>): Promise<void> {
  const statePath = extensionsStatePath(projectPath);
  const dir = resolve(statePath, "..");
  if (!existsSync(dir)) {
    await mkdir(dir, { recursive: true });
  }
  const payload = { extensions: [...ids].sort() };
  await writeFile(statePath, JSON.stringify(payload, null, 2) + "\n", "utf8");
}

// ── Command handler ────────────────────────────────────────────────────────

export async function handleExtensionsCommand(
  command: string | undefined,
  args: string[],
  flags: Record<string, string | boolean>,
  _runtime: Runtime,
  requestId: string
): Promise<CliResult> {
  if (!command || command === "list") {
    return handleExtensionsList(flags, requestId);
  }
  if (command === "install") {
    return handleExtensionsInstall(args[0] ?? getString(flags, "name"), flags, requestId);
  }
  if (command === "remove") {
    return handleExtensionsRemove(args[0] ?? getString(flags, "name"), flags, requestId);
  }
  if (command === "doctor") {
    return handleExtensionsDoctor(flags, requestId);
  }
  return failure(requestId, "UNKNOWN_EXTENSIONS_COMMAND", `Expected extensions list, install, remove or doctor (got "${command ?? ""}").`, [
    "Run unity_cli extensions list to see available extensions.",
    "Run unity_cli extensions install <name> to enable an extension.",
    "Run unity_cli extensions remove <name> to disable an extension.",
    "Run unity_cli extensions doctor to diagnose extension state."
  ]);
}

// ── extensions list ────────────────────────────────────────────────────────

async function handleExtensionsList(
  flags: Record<string, string | boolean>,
  requestId: string
): Promise<CliResult> {
  const projectPath = resolveProject(flags);
  const registry = new ExtensionRegistry();
  const installedPackages = projectPath
    ? await readInstalledUnityPackages(projectPath)
    : undefined;
  const enabledExtensions = projectPath
    ? await readInstalledExtensions(projectPath)
    : new Set<ExtensionId>();

  const descriptors = registry.extensionIds().map((id) =>
    loadExtensionDescriptor(registry, id, installedPackages)
  );

  const extensions = descriptors.map((desc) => {
    const availability = desc.availability;
    return {
      id: desc.manifest.id,
      name: desc.manifest.name,
      description: desc.manifest.description,
      version: desc.manifest.version,
      enabled: enabledExtensions.has(desc.manifest.id),
      availability: availability.status,
      unityPackages: desc.manifest.unityPackages.map((pkg) => ({
        packageId: pkg.packageId,
        displayName: pkg.displayName,
        installed: availability.packages.find((p) => p.packageId === pkg.packageId)?.installed ?? false,
        version: availability.packages.find((p) => p.packageId === pkg.packageId)?.version
      })),
      missingPackageIds: availability.missingPackageIds,
      nextActions: availability.nextActions,
      toolCount: desc.tools.length,
      contextCount: desc.contexts.length
    };
  });

  const data: Record<string, unknown> = {
    command: "extensions.list",
    projectPath: projectPath ?? process.cwd(),
    projectResolved: projectPath !== undefined,
    count: extensions.length,
    extensions
  };

  const format = getString(flags, "format");
  if (format === "text") {
    const lines = extensions.map((e) => {
      const state = e.enabled ? "enabled" : "disabled";
      const avail = e.availability === "available" ? "✓" : e.availability === "unavailable" ? "✗" : "?";
      return `${e.id} [${state}] ${avail} ${e.name} — ${e.unityPackages.map((p) => p.packageId).join(", ")}`;
    });
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

// ── extensions install ────────────────────────────────────────────────────

async function handleExtensionsInstall(
  extensionId: string | undefined,
  flags: Record<string, string | boolean>,
  requestId: string
): Promise<CliResult> {
  if (!extensionId) {
    return failure(requestId, "EXTENSION_NAME_REQUIRED", "Pass an extension id: unity_cli extensions install <name>.", [
      "Run unity_cli extensions list to see available extension ids."
    ]);
  }

  const registry = new ExtensionRegistry();
  if (!registry.hasExtension(extensionId)) {
    return failure(requestId, "UNKNOWN_EXTENSION", `Unknown extension: ${extensionId}`, [
      "Run unity_cli extensions list to see available extension ids.",
      `Available extensions: ${registry.extensionIds().join(", ")}`
    ]);
  }

  const projectPath = resolveProject(flags);
  if (!projectPath) {
    return failure(requestId, "PROJECT_PATH_REQUIRED", "A project path is required to install an extension. Pass --project <path>.", [
      "Run unity_cli extensions install <name> --project <path>.",
      "Run unity_cli extensions list --project <path> to see current state."
    ]);
  }

  const installedPackages = await readInstalledUnityPackages(projectPath);
  const availability = registry.resolveAvailability(extensionId, installedPackages);
  const enabled = await readInstalledExtensions(projectPath);

  if (enabled.has(extensionId)) {
    const manifest = registry.getExtension(extensionId)!.manifest;
    return success(requestId, {
      command: "extensions.install",
      extensionId,
      name: manifest.name,
      status: "already_installed",
      availability: availability.status,
      projectPath,
      nextActions: [`Extension "${extensionId}" is already enabled. Run 'unity_cli extensions remove ${extensionId}' to disable it.`]
    });
  }

  enabled.add(extensionId);
  await writeInstalledExtensions(projectPath, enabled);

  const manifest = registry.getExtension(extensionId)!.manifest;
  const data: Record<string, unknown> = {
    command: "extensions.install",
    extensionId,
    name: manifest.name,
    status: "installed",
    availability: availability.status,
    projectPath,
    unityPackages: manifest.unityPackages.map((pkg) => ({
      packageId: pkg.packageId,
      displayName: pkg.displayName,
      installed: availability.packages.find((p) => p.packageId === pkg.packageId)?.installed ?? false
    }))
  };

  if (availability.status === "unavailable") {
    data.warning = `Extension "${extensionId}" is enabled but its Unity package dependencies are not installed.`;
    data.nextActions = availability.nextActions;
  } else {
    data.nextActions = [`Extension "${extensionId}" is now enabled. Its tool/context descriptors participate in discovery.`];
  }

  return success(requestId, data);
}

// ── extensions remove ─────────────────────────────────────────────────────

async function handleExtensionsRemove(
  extensionId: string | undefined,
  flags: Record<string, string | boolean>,
  requestId: string
): Promise<CliResult> {
  if (!extensionId) {
    return failure(requestId, "EXTENSION_NAME_REQUIRED", "Pass an extension id: unity_cli extensions remove <name>.", [
      "Run unity_cli extensions list to see installed extension ids."
    ]);
  }

  const projectPath = resolveProject(flags);
  if (!projectPath) {
    return failure(requestId, "PROJECT_PATH_REQUIRED", "A project path is required to remove an extension. Pass --project <path>.", [
      "Run unity_cli extensions remove <name> --project <path>."
    ]);
  }

  const enabled = await readInstalledExtensions(projectPath);
  if (!enabled.has(extensionId)) {
    return failure(requestId, "EXTENSION_NOT_INSTALLED", `Extension is not installed: ${extensionId}`, [
      "Run unity_cli extensions list to see installed extensions."
    ]);
  }

  enabled.delete(extensionId);
  await writeInstalledExtensions(projectPath, enabled);

  return success(requestId, {
    command: "extensions.remove",
    extensionId,
    status: "removed",
    projectPath,
    nextActions: [`Extension "${extensionId}" is now disabled. Its descriptors no longer participate in tool/context discovery.`]
  });
}

// ── extensions doctor ──────────────────────────────────────────────────────

async function handleExtensionsDoctor(
  flags: Record<string, string | boolean>,
  requestId: string
): Promise<CliResult> {
  const projectPath = resolveProject(flags);
  const registry = new ExtensionRegistry();

  if (!projectPath) {
    return failure(requestId, "PROJECT_PATH_REQUIRED", "A project path is required to diagnose extensions. Pass --project <path>.", [
      "Run unity_cli extensions doctor --project <path>.",
      "Run unity_cli extensions list to see available extensions."
    ]);
  }

  const installedPackages = await readInstalledUnityPackages(projectPath);
  const enabledExtensions = await readInstalledExtensions(projectPath);

  const summary = extensionAvailabilitySummary(registry, installedPackages);

  const checks = registry.extensionIds().map((id) => {
    const desc = loadExtensionDescriptor(registry, id, installedPackages);
    const availability = desc.availability;
    const enabled = enabledExtensions.has(id);

    const issues: string[] = [];
    if (enabled && availability.status === "unavailable") {
      issues.push(`Extension "${id}" is enabled but required Unity packages are missing: ${availability.missingPackageIds.join(", ")}`);
    }
    if (!enabled && availability.status === "available") {
      issues.push(`Extension "${id}" has all dependencies installed but is not enabled.`);
    }
    if (availability.status === "unknown") {
      issues.push(`Extension "${id}" availability could not be resolved (no project packages read).`);
    }

    return {
      id,
      name: desc.manifest.name,
      enabled,
      availability: availability.status,
      missingPackageIds: availability.missingPackageIds,
      issues
    };
  });

  // Detect unknown enabled extensions (enabled ids not in registry)
  const unknownEnabled: string[] = [];
  for (const id of enabledExtensions) {
    if (!registry.hasExtension(id)) {
      unknownEnabled.push(id);
    }
  }
  if (unknownEnabled.length > 0) {
    checks.push(...unknownEnabled.map((id): typeof checks[number] => ({
      id,
      name: id,
      enabled: true,
      availability: "unknown" as const,
      missingPackageIds: [],
      issues: [`Extension "${id}" is enabled in project state but is not registered in the CLI extension registry.`]
    })));
  }

  const allIssues = checks.flatMap((c) => c.issues);
  const healthy = allIssues.length === 0;

  const data: Record<string, unknown> = {
    command: "extensions.doctor",
    projectPath,
    healthy,
    summary: {
      total: summary.total,
      available: summary.available,
      unavailable: summary.unavailable,
      unknown: summary.unknown,
      enabled: enabledExtensions.size
    },
    checks,
    issues: allIssues,
    nextActions: healthy
      ? ["All extensions are in a consistent state."]
      : allIssues
  };

  const format = getString(flags, "format");
  if (format === "text") {
    const lines: string[] = [`extensions doctor — project: ${projectPath}`, `healthy: ${healthy}`];
    for (const check of checks) {
      const icon = check.issues.length === 0 ? "✓" : "✗";
      lines.push(`  ${icon} ${check.id} [${check.enabled ? "enabled" : "disabled"}] (${check.availability}) ${check.name}`);
      for (const issue of check.issues) {
        lines.push(`      - ${issue}`);
      }
    }
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

// ── Helpers ───────────────────────────────────────────────────────────────

function resolveProject(flags: Record<string, string | boolean>): string | undefined {
  const project = getString(flags, "project");
  return project ? resolve(project) : undefined;
}
