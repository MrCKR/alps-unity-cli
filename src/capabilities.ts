/**
 * CLI command: capabilities [--json]
 *
 * Returns a capability manifest that combines:
 * - Static built-in capabilities (tool groups, tool count, schema features)
 * - Runtime capabilities from the connected Unity instance (if bridge is reachable)
 */

import { executeBridgePayload, type BridgeRuntime } from "./bridge-commands.js";
import { getString } from "./args.js";
import { builtinToolGroups, builtinToolMetadata } from "./tool-metadata.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export async function handleCapabilitiesCommand(
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime,
  requestId: string
): Promise<CliResult> {
  const jsonFormat = getString(flags, "format") === "json" || getBoolean(flags, "json");

  const staticCapabilities = buildStaticCapabilities();

  let runtimeCapabilities: Record<string, unknown> | undefined;
  const endpoint = getString(flags, "bridge-endpoint") ?? runtime.bridgeEndpoint ?? process.env.UNITY_CLI_BRIDGE_ENDPOINT;

  if (runtime.bridgeExecute || endpoint) {
    try {
      const bridgeResult = await executeBridgePayload(
        requestId,
        "diagnose.preflight",
        {},
        flags,
        runtime
      );
      if (bridgeResult.ok) {
        runtimeCapabilities = bridgeResult.data as Record<string, unknown>;
      }
    } catch {
      // Bridge not reachable — runtime capabilities stay undefined
    }
  }

  const data: Record<string, unknown> = {
    command: "capabilities",
    mode: runtimeCapabilities ? "hybrid" : "offline",
    requiresUnity: false,
    static: staticCapabilities,
    ...(runtimeCapabilities ? { runtime: runtimeCapabilities } : {}),
    ...(runtimeCapabilities ? { bridgeEndpoint: endpoint ?? runtime.bridgeEndpoint } : {})
  };

  return success(requestId, data);
}

function getBoolean(flags: Record<string, string | boolean>, name: string): boolean {
  return flags[name] === true || flags[name] === "true";
}

function buildStaticCapabilities(): Record<string, unknown> {
  const tools = builtinToolMetadata();
  const groups = builtinToolGroups();

  const destructiveTools = tools.filter((t) => t.destructiveHint).map((t) => t.name);
  const readOnlyTools = tools.filter((t) => t.readOnlyHint).map((t) => t.name);
  const longRunningTools = tools.filter((t) => t.longRunning).map((t) => t.name);
  const bridgeTools = tools.filter((t) => t.handler === "bridge" || t.handler === "hybrid").map((t) => t.name);
  const offlineTools = tools.filter((t) => t.handler === "offline").map((t) => t.name);
  const jobTools = tools.filter((t) => t.handler === "job" || t.handler === "hybrid").map((t) => t.name);

  const groupCounts: Record<string, number> = {};
  for (const group of groups) {
    groupCounts[group] = tools.filter((t) => t.group === group).length;
  }

  return {
    toolCount: tools.length,
    groups: groupCounts,
    bridgeTools,
    offlineTools,
    jobTools,
    readOnlyTools,
    destructiveTools,
    longRunningTools,
    schemaFeatures: {
      toolMetadata: true,
      requestEnvelope: true,
      responseEnvelope: true,
      errorCode: true
    }
  };
}