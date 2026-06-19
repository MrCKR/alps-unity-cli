/**
 * CLI command: extensions list / extensions install / extensions remove / extensions doctor
 *
 * Extensions are optional capability packs that depend on optional Unity packages.
 * They provide tools and contexts but must not bypass the descriptor/schema/envelope system.
 *
 * P1 baseline: list and doctor commands functional.
 * install/remove are declared but route to the package manager (P5 full flow).
 */

import { getString } from "./args.js";
import { builtinExtensionMetadata, findExtensionById, type ExtensionMetadata } from "./extensions/metadata/index.js";
import { failure, success } from "./output.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import type { CliResult } from "./types.js";

export type ExtensionsRuntime = BridgeRuntime;

export async function handleExtensionsCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ExtensionsRuntime,
  requestId: string
): Promise<CliResult> {
  if (!command || command === "list") {
    return handleExtensionsList(flags, runtime, requestId);
  }

  if (command === "doctor") {
    return handleExtensionsDoctor(flags, runtime, requestId);
  }

  if (command === "install") {
    const extensionId = positional[0] ?? getString(flags, "id") ?? getString(flags, "name");
    return handleExtensionsInstall(extensionId, flags, runtime, requestId);
  }

  if (command === "remove") {
    const extensionId = positional[0] ?? getString(flags, "id") ?? getString(flags, "name");
    return handleExtensionsRemove(extensionId, flags, runtime, requestId);
  }

  return failure(requestId, "UNKNOWN_EXTENSIONS_COMMAND", "Expected extensions list, extensions install <id>, extensions remove <id>, or extensions doctor.", [
    "Run unity_cli extensions list to see all available extensions.",
    "Run unity_cli extensions doctor to check extension status."
  ]);
}

async function handleExtensionsList(
  flags: Record<string, string | boolean>,
  runtime: ExtensionsRuntime,
  requestId: string
): Promise<CliResult> {
  // Query bridge for runtime extension status (installed vs available)
  const extensions = builtinExtensionMetadata();
  const runtimeStatus = await queryExtensionStatus(flags, runtime, requestId);

  // Merge runtime status into static extension metadata
  const summaries = extensions.map((ext) => {
    const installed = runtimeStatus?.installed?.includes(ext.id) ?? false;
    const unityPackagePresent = runtimeStatus?.packageAvailability?.[ext.requiresUnityPackages?.[0] ?? ""] ?? null;
    return {
      id: ext.id,
      title: ext.title,
      description: ext.description,
      version: ext.version,
      status: installed ? "installed" : (ext.status ?? "available"),
      installMode: ext.installMode,
      requiresUnityPackages: ext.requiresUnityPackages,
      providesTools: ext.providesTools,
      providesContexts: ext.providesContexts,
      equivalentMcpGroup: ext.equivalentMcpGroup,
      unityPackagePresent,
      installCommand: ext.installCommand,
      nextActions: ext.nextActions
    };
  });

  const data: Record<string, unknown> = {
    command: "extensions.list",
    count: summaries.length,
    extensions: summaries
  };

  const format = getString(flags, "format");
  if (format === "text") {
    const lines = summaries.map((s) => `${s.id} [${s.status}] ${s.requiresUnityPackages?.join(", ") ?? ""} — ${s.title}`);
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

async function handleExtensionsDoctor(
  flags: Record<string, string | boolean>,
  runtime: ExtensionsRuntime,
  requestId: string
): Promise<CliResult> {
  const extensions = builtinExtensionMetadata();
  const runtimeStatus = await queryExtensionStatus(flags, runtime, requestId);

  const checks = extensions.map((ext) => {
    const installed = runtimeStatus?.installed?.includes(ext.id) ?? false;
    const requiredPackage = ext.requiresUnityPackages?.[0];
    const packagePresent = requiredPackage
      ? (runtimeStatus?.packageAvailability?.[requiredPackage] ?? null)
      : null;

    const issues: string[] = [];
    if (!installed) {
      issues.push(`Extension "${ext.id}" is not installed.`);
    }
    if (requiredPackage && packagePresent === false) {
      issues.push(`Required Unity package "${requiredPackage}" is not present in the project.`);
    }

    return {
      id: ext.id,
      title: ext.title,
      installed,
      status: installed ? "installed" : "available",
      requiresUnityPackages: ext.requiresUnityPackages,
      packagePresent,
      issues
    };
  });

  const allInstalled = checks.every((c) => c.installed);

  const data: Record<string, unknown> = {
    command: "extensions.doctor",
    summary: allInstalled ? "pass" : "warn",
    extensionCount: checks.length,
    installedCount: checks.filter((c) => c.installed).length,
    checks
  };

  return success(requestId, data);
}

async function handleExtensionsInstall(
  extensionId: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ExtensionsRuntime,
  requestId: string
): Promise<CliResult> {
  if (!extensionId) {
    return failure(requestId, "EXTENSIONS_INSTALL_ID_REQUIRED", "Pass an extension id: unity_cli extensions install <id>.", [
      "Run unity_cli extensions list to see all available extensions."
    ]);
  }

  const extension = findExtensionById(extensionId);
  if (!extension) {
    return failure(requestId, "E_EXTENSION_NOT_FOUND", `Extension not found: ${extensionId}.`, [
      "Run unity_cli extensions list to see all available extensions."
    ]);
  }

  // P1 baseline: extension install requires the underlying Unity package.
  // Full install flow (npm package + Unity package install) is P5.
  // For P1, route through bridge to check Unity package presence.
  const bridgeResult = await executeBridgePayload(
    requestId,
    "extension.status",
    { extensionId },
    flags,
    runtime
  ).catch(() => null);

  if (bridgeResult?.ok) {
    const packagePresent = (bridgeResult.data as { packagePresent?: boolean })?.packagePresent ?? false;
    if (!packagePresent) {
      return failure(requestId, "E_EXTENSION_DEPENDENCY_MISSING", `Extension "${extensionId}" requires Unity package "${extension.requiresUnityPackages?.[0]}" which is not present in the project.`, [
        `Install the Unity package first: ${extension.requiresUnityPackages?.[0] ?? "<unknown>"}`,
        "Run unity_cli extensions doctor to check extension status.",
        "P5 will implement automatic Unity package installation."
      ]);
    }
  }

  return failure(requestId, "EXTENSIONS_INSTALL_NOT_IMPLEMENTED", `Extension install for "${extensionId}" is declared but not yet implemented.`, [
    "P1 baseline: extension install is reserved for P5.",
    "Ensure the required Unity package is installed: " + (extension.requiresUnityPackages?.join(", ") ?? "none"),
    "Run unity_cli extensions doctor to check extension status."
  ]);
}

async function handleExtensionsRemove(
  extensionId: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ExtensionsRuntime,
  requestId: string
): Promise<CliResult> {
  if (!extensionId) {
    return failure(requestId, "EXTENSIONS_REMOVE_ID_REQUIRED", "Pass an extension id: unity_cli extensions remove <id>.", [
      "Run unity_cli extensions list to see all available extensions."
    ]);
  }

  const extension = findExtensionById(extensionId);
  if (!extension) {
    return failure(requestId, "E_EXTENSION_NOT_FOUND", `Extension not found: ${extensionId}.`, [
      "Run unity_cli extensions list to see all available extensions."
    ]);
  }

  return failure(requestId, "EXTENSIONS_REMOVE_NOT_IMPLEMENTED", `Extension remove for "${extensionId}" is declared but not yet implemented.`, [
    "P1 baseline: extension remove is reserved for P5.",
    "Run unity_cli extensions doctor to check extension status."
  ]);
}

async function queryExtensionStatus(
  flags: Record<string, string | boolean>,
  runtime: ExtensionsRuntime,
  requestId: string
): Promise<{ installed?: string[]; packageAvailability?: Record<string, boolean> } | null> {
  if (!runtime.bridgeExecute) {
    const endpoint = getString(flags, "bridge-endpoint") ?? runtime.bridgeEndpoint ?? process.env.UNITY_CLI_BRIDGE_ENDPOINT;
    if (!endpoint) {
      return null;
    }
  }
  try {
    const result = await executeBridgePayload(requestId, "extension.status", {}, flags, runtime);
    if (result.ok) {
      return result.data as { installed?: string[]; packageAvailability?: Record<string, boolean> };
    }
  } catch {
    // Bridge not reachable — runtime status unavailable
  }
  return null;
}
