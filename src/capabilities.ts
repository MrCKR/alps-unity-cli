import { getString } from "./args.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { builtinContextDescriptors, loadContextCatalog, shouldLoadLiveContextCatalog, type ContextCatalogRuntime } from "./contexts/catalog.js";
import type { ContextDescriptor } from "./contexts/types.js";
import { readInstalledExtensions } from "./cli-commands/extensions.js";
import { extensionAvailabilityFlags } from "./extension-availability.js";
import { ExtensionRegistry, extensionAvailabilitySummary } from "./extensions/index.js";
import { builtinExtensionMetadata } from "./extensions/metadata/index.js";
import { readInstalledUnityPackages } from "./extensions/project-resolver.js";
import { discoverProjectTools, shouldDiscoverProjectTools, type ProjectToolRuntime } from "./project-tool-discovery.js";
import { success } from "./output.js";
import { staticBridgeToolMetadata } from "./tools/catalog.js";
import { builtinToolMetadata, type ToolMetadata } from "./tools/metadata/index.js";
import type { CliResult } from "./types.js";

/** 公开 capability summary 只返回计数和分组，不复制完整 descriptor 或内部健康载荷。 */
export async function handleCapabilitiesCommand(
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime & ProjectToolRuntime & ContextCatalogRuntime,
  requestId: string
): Promise<CliResult> {
  let liveTools: ToolMetadata[] = [];
  let projectToolSummary: Record<string, unknown> | undefined;
  if (shouldDiscoverProjectTools(flags, runtime)) {
    const discovery = await discoverProjectTools(flags, runtime, requestId);
    if (discovery.ok) {
      liveTools = discovery.tools;
      projectToolSummary = { available: true, count: liveTools.length };
    } else {
      projectToolSummary = { available: false, code: discovery.error?.code, message: discovery.error?.message };
    }
  }

  let customContexts: ContextDescriptor[] = [];
  let customContextSummary: Record<string, unknown> | undefined;
  if (await shouldLoadLiveContextCatalog(flags, runtime)) {
    const catalog = await loadContextCatalog(flags, runtime, requestId, { requireLive: false });
    if (catalog.ok) {
      customContexts = catalog.contexts.filter((context) => context.source === "project");
      customContextSummary = {
        available: true,
        count: customContexts.length,
        categories: [...new Set(customContexts.map((context) => context.category))].sort(),
      };
    } else {
      customContextSummary = { available: false, code: catalog.error?.code, message: catalog.error?.message };
    }
  }

  const tools = uniqueTools([...builtinToolMetadata(), ...staticBridgeToolMetadata(), ...liveTools]);
  const contexts = uniqueContexts([...builtinContextDescriptors(), ...customContexts]);
  const catalogSummary = buildCatalogSummary(tools, contexts, liveTools.filter((tool) => tool.source === "project").length);
  const extensionSummary = await buildExtensionSummary(flags, runtime);
  const runtimeSummary = await readRuntimeSummary(flags, runtime, requestId);

  return success(requestId, {
    catalog: catalogSummary,
    ...(projectToolSummary ? { projectTools: projectToolSummary } : {}),
    ...(customContextSummary ? { customContexts: customContextSummary } : {}),
    ...(runtimeSummary ? { runtime: runtimeSummary } : {}),
    extensions: extensionSummary,
  });
}

/** selected instance 是 Tool 路径唯一项目来源；doctor 仍可直接传 project 控制面参数。 */
async function buildExtensionSummary(
  flags: Record<string, string | boolean>,
  runtime: { dataDir?: string }
): Promise<Record<string, unknown>> {
  const resolvedFlags = await extensionAvailabilityFlags(flags, runtime);
  const projectPath = getString(resolvedFlags, "project");
  const total = builtinExtensionMetadata().length;
  if (!projectPath) return { total, physicalStateKnown: false };
  try {
    const registry = new ExtensionRegistry();
    const installedPackages = await readInstalledUnityPackages(projectPath);
    const summary = extensionAvailabilitySummary(registry, installedPackages);
    const enabled = await readInstalledExtensions(projectPath);
    return {
      total: summary.total,
      available: summary.available,
      unavailable: summary.unavailable,
      unknown: summary.unknown,
      enabled: enabled.size,
      physicalStateKnown: true,
    };
  } catch {
    return { total, physicalStateKnown: false };
  }
}

/** Live preflight 只投影 Bridge 健康与公开能力计数。 */
async function readRuntimeSummary(
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime,
  requestId: string
): Promise<Record<string, unknown> | undefined> {
  if (!runtime.bridgeExecute && !getString(flags, "instance")) return undefined;
  try {
    const result = await executeBridgePayload(requestId, "diagnose.preflight", {}, flags, runtime, undefined, { mode: "read", mutation: false });
    if (!result.ok || !result.data || typeof result.data !== "object" || Array.isArray(result.data)) return undefined;
    const bridge = (result.data as Record<string, unknown>).bridge;
    if (!bridge || typeof bridge !== "object" || Array.isArray(bridge)) return undefined;
    const value = bridge as Record<string, unknown>;
    if (typeof value.ready !== "boolean" || typeof value.toolCount !== "number" || typeof value.contextCount !== "number") return undefined;
    return { bridge: { ready: value.ready, toolCount: value.toolCount, contextCount: value.contextCount } };
  } catch {
    return undefined;
  }
}

/** catalog summary 使用闭集计数；具体名称、schema 与 availability 由 discovery 查询。 */
function buildCatalogSummary(tools: ToolMetadata[], contexts: ContextDescriptor[], projectToolCount: number): Record<string, unknown> {
  const groups: Record<string, number> = {};
  for (const tool of tools) groups[tool.group] = (groups[tool.group] ?? 0) + 1;
  const categories: Record<string, number> = {};
  for (const context of contexts) categories[context.category] = (categories[context.category] ?? 0) + 1;
  return {
    toolCount: tools.length,
    contextCount: contexts.length,
    groupCount: Object.keys(groups).length,
    contextCategoryCount: Object.keys(categories).length,
    groups: Object.fromEntries(Object.entries(groups).sort(([left], [right]) => left.localeCompare(right))),
    contextCategories: Object.fromEntries(Object.entries(categories).sort(([left], [right]) => left.localeCompare(right))),
    sources: {
      tsLocalTools: builtinToolMetadata().length,
      bridgeTools: staticBridgeToolMetadata().length,
      projectTools: projectToolCount,
      projectContexts: contexts.filter((context) => context.source === "project").length,
    },
  };
}

function uniqueTools(tools: ToolMetadata[]): ToolMetadata[] {
  return [...new Map(tools.map((tool) => [tool.name, tool])).values()];
}

function uniqueContexts(contexts: ContextDescriptor[]): ContextDescriptor[] {
  return [...new Map(contexts.map((context) => [context.name, context])).values()];
}
