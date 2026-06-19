/**
 * CLI command: capabilities [--json]
 *
 * Returns a capability manifest that combines:
 * - Static built-in capabilities (tool groups, tool count, schema features)
 * - Runtime capabilities from the connected Unity instance (if bridge is reachable)
 */

import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { getString } from "./args.js";
import { discoverProjectTools, shouldDiscoverProjectTools, type ProjectToolRuntime } from "./project-tool-discovery.js";
import { builtinToolGroups, builtinToolMetadata, type ToolMetadata } from "./tools/metadata/index.js";
import { builtinContextMetadata, builtinContextCategories, type ContextMetadata } from "./context/metadata/index.js";
import { builtinExtensionMetadata, type ExtensionMetadata } from "./extensions/metadata/index.js";
import { supportStatusForTool } from "./tools/metadata/status.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export async function handleCapabilitiesCommand(
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime & ProjectToolRuntime,
  requestId: string
): Promise<CliResult> {
  const jsonFormat = getString(flags, "format") === "json" || getBoolean(flags, "json");

  let projectTools: ToolMetadata[] = [];
  let projectToolDiagnostics: Record<string, unknown> | undefined;
  if (shouldDiscoverProjectTools(flags, runtime)) {
    const discovery = await discoverProjectTools(flags, runtime, requestId);
    if (discovery.ok) {
      projectTools = discovery.tools;
      projectToolDiagnostics = { mode: discovery.mode, count: projectTools.length };
    } else {
      projectToolDiagnostics = { mode: "error", code: discovery.error?.code, message: discovery.error?.message };
    }
  }

  const staticCapabilities = buildStaticCapabilities(projectTools);

  // P1: Context and extension base counts
  const contextCount = builtinContextMetadata().length;
  const contextCategories = builtinContextCategories();
  const extensions = builtinExtensionMetadata();
  const installedExtensions = extensions.filter((e) => e.status === "installed").length;
  const availableExtensions = extensions.filter((e) => e.status === "available").length;

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
    ...(projectToolDiagnostics ? { projectTools: projectToolDiagnostics } : {}),
    ...(runtimeCapabilities ? { runtime: runtimeCapabilities } : {}),
    ...(runtimeCapabilities ? { bridgeEndpoint: endpoint ?? runtime.bridgeEndpoint } : {}),
    contextDiscovery: {
      enabled: true,
      contextCount,
      categories: contextCategories,
      customContext: false
    },
    toolsGroupFirstDiscovery: true,
    extensions: {
      enabled: true,
      total: extensions.length,
      installed: installedExtensions,
      available: availableExtensions
    }
  };

  return success(requestId, data);
}

function getBoolean(flags: Record<string, string | boolean>, name: string): boolean {
  return flags[name] === true || flags[name] === "true";
}

function buildStaticCapabilities(projectTools: ToolMetadata[] = []): Record<string, unknown> {
  const builtinTools = builtinToolMetadata();
  const tools = [...builtinTools, ...projectTools];
  const groups = [...new Set([...builtinToolGroups(), ...projectTools.map((tool) => tool.group)])].sort();

  const destructiveTools = tools.filter((t) => t.destructiveHint).map((t) => t.name);
  const readOnlyTools = tools.filter((t) => t.readOnlyHint).map((t) => t.name);
  const longRunningTools = tools.filter((t) => t.longRunning).map((t) => t.name);
  const architectureSensitiveTools = tools.filter((t) => t.architectureSensitive).map((t) => ({
    name: t.name,
    frozenDecision: t.frozenDecision,
    coverageScope: t.coverageScope ?? []
  }));
  const liveBridgeTools = tools.filter((t) => supportStatusForTool(t) === "live-bridge").map((t) => t.name);
  const hybridTools = tools.filter((t) => supportStatusForTool(t) === "bridge-job").map((t) => t.name);
  const batchJobTools = tools.filter((t) => supportStatusForTool(t) === "batch-job").map((t) => t.name);
  const cliOnlyTools = tools.filter((t) => supportStatusForTool(t) === "cli-only").map((t) => t.name);
  const plannedTools = tools.filter((t) => supportStatusForTool(t) === "planned").map((t) => t.name);
  const bridgeTools = liveBridgeTools;
  const offlineTools = cliOnlyTools;
  const jobTools = [...batchJobTools, ...hybridTools];

  const groupCounts: Record<string, number> = {};
  for (const group of groups) {
    groupCounts[group] = tools.filter((t) => t.group === group).length;
  }

  return {
    toolCount: tools.length,
    groups: groupCounts,
    liveBridgeTools,
    hybridTools,
    batchJobTools,
    cliOnlyTools,
    plannedTools,
    bridgeTools,
    offlineTools,
    jobTools,
    readOnlyTools,
    destructiveTools,
    longRunningTools,
    architectureSensitiveTools,
    handlerDefinitions: {
      bridge: "Live Unity Editor Bridge /bridge/execute path.",
      hybrid: "Command supports more than one channel, typically live Bridge plus batch/job execution.",
      job: "Unity batchmode/job-runner path only.",
      offline: "Local CLI execution without Unity connection."
    },
    schemaFeatures: {
      toolMetadata: true,
      projectToolDescriptor: true,
      contextDescriptor: true,
      extensionDescriptor: true,
      unifiedDiscovery: true,
      groupFirstDiscovery: true,
      builtinDocs: true,
      requestEnvelope: true,
      responseEnvelope: true,
      errorCode: true
    }
  };
}