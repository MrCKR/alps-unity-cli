import { getString } from "./args.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { loadContextCatalog, type ContextCatalogRuntime } from "./contexts/catalog.js";
import type { ContextDescriptor } from "./contexts/types.js";
import { readInstalledExtensions } from "./cli-commands/extensions.js";
import { extensionAvailabilityFlags } from "./extension-availability.js";
import { ExtensionRegistry, extensionAvailabilitySummary } from "./extensions/index.js";
import { builtinExtensionMetadata } from "./extensions/metadata/index.js";
import { readInstalledUnityPackages } from "./extensions/project-resolver.js";
import type { ProjectToolRuntime } from "./project-tool-discovery.js";
import { success } from "./output.js";
import { loadToolCatalog } from "./tools/catalog.js";
import { toolSource, type ToolMetadata } from "./tools/metadata/index.js";
import type { CliResult } from "./types.js";

/** 公开 capability summary 只汇总当前实例完整目录，不按实现来源拆成不同发现入口。 */
export async function handleCapabilitiesCommand(
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime & ProjectToolRuntime & ContextCatalogRuntime,
  requestId: string
): Promise<CliResult> {
  const toolCatalog = await loadToolCatalog(flags, runtime, `${requestId}_tools`);
  if (toolCatalog.ok === false) return toolCatalog;
  const contextCatalog = await loadContextCatalog(flags, runtime, `${requestId}_contexts`);
  if (contextCatalog.ok === false) return contextCatalog;

  const catalogSummary = buildCatalogSummary(toolCatalog.tools, contextCatalog.contexts);
  const extensionSummary = await buildExtensionSummary(flags, runtime);
  const runtimeSummary = await readRuntimeSummary(flags, runtime, `${requestId}_runtime`);

  return success(requestId, {
    catalog: catalogSummary,
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

/** catalog summary 使用统一计数；source 只作为说明维度，不改变公开路由。 */
function buildCatalogSummary(tools: ToolMetadata[], contexts: ContextDescriptor[]): Record<string, unknown> {
  const groups: Record<string, number> = {};
  const toolSources: Record<string, number> = {};
  for (const tool of tools) {
    groups[tool.group] = (groups[tool.group] ?? 0) + 1;
    const source = toolSource(tool);
    toolSources[source] = (toolSources[source] ?? 0) + 1;
  }
  const categories: Record<string, number> = {};
  const contextSources: Record<string, number> = {};
  for (const context of contexts) {
    categories[context.category] = (categories[context.category] ?? 0) + 1;
    contextSources[context.source] = (contextSources[context.source] ?? 0) + 1;
  }
  return {
    toolCount: tools.length,
    contextCount: contexts.length,
    groupCount: Object.keys(groups).length,
    contextCategoryCount: Object.keys(categories).length,
    groups: Object.fromEntries(Object.entries(groups).sort(([left], [right]) => left.localeCompare(right))),
    contextCategories: Object.fromEntries(Object.entries(categories).sort(([left], [right]) => left.localeCompare(right))),
    sources: {
      tools: Object.fromEntries(Object.entries(toolSources).sort(([left], [right]) => left.localeCompare(right))),
      contexts: Object.fromEntries(Object.entries(contextSources).sort(([left], [right]) => left.localeCompare(right))),
    },
  };
}
