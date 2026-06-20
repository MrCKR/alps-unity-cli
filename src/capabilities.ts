/**
 * CLI command: capabilities [--json]
 *
 * Returns a capability manifest that combines:
 * - Static built-in capabilities (tool groups, tool count, schema features)
 * - Runtime capabilities from the connected Unity instance (if bridge is reachable)
 */

import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { builtinContextDescriptors, builtinContextCategories } from "./context/index.js";
import { getString } from "./args.js";
import { discoverProjectTools, shouldDiscoverProjectTools, type ProjectToolRuntime } from "./project-tool-discovery.js";
import { discoverProjectContexts, shouldDiscoverContexts, type ProjectContextRuntime, type ContextDescriptor } from "./project-context-discovery.js";
import { builtinToolGroups, builtinToolMetadata, extensionToolMetadata, allRegisteredToolMetadata, EXTENSION_GROUPS, type ToolMetadata } from "./tools/metadata/index.js";
import { builtinContextMetadata, type ContextMetadata } from "./context/metadata/index.js";
import { builtinExtensionMetadata, type ExtensionMetadata } from "./extensions/metadata/index.js";
import { supportStatusForTool } from "./tools/metadata/status.js";
import { ExtensionRegistry, extensionAvailabilitySummary, collectAvailableTools, collectAllTools } from "./extensions/index.js";
import { readInstalledUnityPackages } from "./extensions/project-resolver.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export async function handleCapabilitiesCommand(
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime & ProjectToolRuntime & ProjectContextRuntime,
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

  // ── Extension availability ──
  const projectPath = getString(flags, "project");
  let extensionInfo: Record<string, unknown> | undefined;
  // Determine which groups belong to extensions (not core built-in).
  // When a project path is available, we can determine which extensions
  // are enabled and separate their groups from the core `groups` map.
  const extensionGroupFilter = new Set<string>();
  const registry = new ExtensionRegistry();
  // All registered extension tool groups are extension groups
  for (const extId of registry.extensionIds()) {
    const ext = registry.getExtension(extId)!;
    const toolGroups = [...new Set(ext.manifest.contributions.tools?.map((t) => t.group) ?? [])];
    for (const group of toolGroups) {
      extensionGroupFilter.add(group);
    }
  }
  if (projectPath) {
    try {
      const installedPackages = await readInstalledUnityPackages(projectPath);
      const summary = extensionAvailabilitySummary(registry, installedPackages);
      const { readInstalledExtensions } = await import("./cli-commands/extensions.js");
      const enabledExtensions = await readInstalledExtensions(projectPath);
      extensionInfo = {
        total: summary.total,
        available: summary.available,
        unavailable: summary.unavailable,
        unknown: summary.unknown,
        extensions: summary.extensions.map((e) => ({
          ...e,
          enabled: enabledExtensions.has(e.id)
        })),
        extensionToolCount: collectAvailableTools(registry, installedPackages).length,
        allExtensionToolCount: collectAllTools(registry, installedPackages).length
      };
    } catch {
      // Extension info is best-effort; failures should not break capabilities.
    }
  }

  // Custom context discovery (P3)
  let projectContextDiagnostics: Record<string, unknown> | undefined;
  let projectContexts: ContextDescriptor[] = [];
  if (shouldDiscoverContexts(flags, runtime)) {
    const contextDiscovery = await discoverProjectContexts(flags, runtime, requestId, { requireBridge: false });
    if (contextDiscovery.ok) {
      projectContexts = contextDiscovery.contexts;
      const customContexts = projectContexts.filter((c) => c.source === "project");
      const categories = [...new Set(customContexts.map((c) => c.category))];
      projectContextDiagnostics = { mode: contextDiscovery.mode, count: customContexts.length, categories, builtInCount: contextDiscovery.builtInCount };
    } else {
      projectContextDiagnostics = { mode: "error", code: contextDiscovery.error?.code, message: contextDiscovery.error?.message };
    }
  }

  const staticCapabilities = buildStaticCapabilities(projectTools, projectContexts, extensionGroupFilter.size > 0 ? extensionGroupFilter : undefined);

  // P1: Context and extension base counts
  const contextCount = builtinContextMetadata().length;
  const contextCategories = builtinContextCategories();
  const extensions = builtinExtensionMetadata();
  const installedExtensions = extensions.filter((e) => e.status === "installed").length;
  const availableExtensions = extensions.filter((e) => e.status === "available").length;

  let runtimeCapabilities: Record<string, unknown> | undefined;
  let bridgeCustomContexts: Record<string, unknown> | undefined;
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
        // Extract customContexts from bridge preflight for runtime section
        const customCtx = runtimeCapabilities.customContexts;
        if (customCtx && typeof customCtx === "object") {
          bridgeCustomContexts = customCtx as Record<string, unknown>;
        }
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
    ...(projectContextDiagnostics ? { customContexts: projectContextDiagnostics } : {}),
    ...(runtimeCapabilities ? { runtime: runtimeCapabilities } : {}),
 ...(bridgeCustomContexts ? { runtimeCustomContexts: bridgeCustomContexts } : {}),
    ...(runtimeCapabilities ? { bridgeEndpoint: endpoint ?? runtime.bridgeEndpoint } : {}),
    contextDiscovery: {
      enabled: true,
      contextCount,
      categories: contextCategories,
      customContext: projectContexts.some((c) => c.source === "project")
    },
    toolsGroupFirstDiscovery: true,
    extensions: extensionInfo ?? {
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

function buildStaticCapabilities(projectTools: ToolMetadata[] = [], projectContexts: ContextDescriptor[] = [], extensionGroupFilter?: Set<string>): Record<string, unknown> {
  // P10-R3: builtinToolMetadata() returns core tools only.
  // Extension tools are loaded from extensionToolMetadata() and merged based
  // on availability (extensionGroupFilter determines which are active).
  const coreTools = builtinToolMetadata();
  const allExtensionTools = extensionToolMetadata();
  const activeExtensionTools = extensionGroupFilter
    ? allExtensionTools.filter((t) => extensionGroupFilter.has(t.group))
    : allExtensionTools;
  const tools = [...coreTools, ...activeExtensionTools, ...projectTools];
  const coreGroups = [...new Set([...coreTools, ...projectTools].map((tool) => tool.group))].sort();

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

  const coreGroupCounts: Record<string, number> = {};
  for (const group of coreGroups) {
    coreGroupCounts[group] = [...coreTools, ...projectTools].filter((t) => t.group === group).length;
  }

  // Full group counts (core + extension + project)
  const groupCounts: Record<string, number> = {};
  const allGroups = [...new Set(tools.map((tool) => tool.group))].sort();
  for (const group of allGroups) {
    groupCounts[group] = tools.filter((t) => t.group === group).length;
  }

  // Extension group counts (e.g. probuilder=36, cinemachine=10) are surfaced separately
  // so that capabilities consumers can distinguish core from extension.
  const extensionGroupCounts: Record<string, number> = {};
  if (activeExtensionTools.length > 0) {
    for (const group of [...new Set(activeExtensionTools.map((t) => t.group))].sort()) {
      extensionGroupCounts[group] = activeExtensionTools.filter((t) => t.group === group).length;
    }
  }

  return {
    toolCount: tools.length,
    groups: extensionGroupFilter ? coreGroupCounts : groupCounts,
    ...(Object.keys(extensionGroupCounts).length > 0 ? { extensionGroups: extensionGroupCounts } : {}),
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
      errorCode: true,
      customContextDescriptor: true,
      contextFingerprint: true
    }
  };
}


function buildContextCapabilities(): Record<string, unknown> {
  const contexts = builtinContextDescriptors();
  const categories = builtinContextCategories();

  return {
    contextCount: contexts.length,
    categories,
    categoryCounts: Object.fromEntries(
      categories.map((cat) => [cat, contexts.filter((c) => c.category === cat).length])
    ),
    contextNames: contexts.map((c) => c.name),
    allReadOnly: true,
    customContextSupport: true,
    bridgeCommand: "context.read",
    discoveryCommands: ["context list", "context describe <name>", "context read <name>"]
  };
}
