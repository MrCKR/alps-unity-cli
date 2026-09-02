import { getString } from "./args.js";
import { executeBridgePayload, type BridgeRuntime } from "./bridge/index.js";
import { readInstalledExtensions } from "./cli-commands/extensions.js";
import { extensionAvailabilityFlags } from "./extension-availability.js";
import { ExtensionRegistry, extensionAvailabilitySummary } from "./extensions/index.js";
import { builtinExtensionMetadata } from "./extensions/metadata/index.js";
import { readInstalledUnityPackages } from "./extensions/project-resolver.js";
import type { ProjectToolRuntime } from "./project-tool-discovery.js";
import { success } from "./output.js";
import { loadFullToolCatalogForDiagnostics } from "./tools/catalog.js";
import { toolSource, type ToolMetadata } from "./tools/metadata/index.js";
import type { CliResult } from "./types.js";

/** 公开 capability summary 只汇总当前实例完整目录，不按实现来源拆成不同发现入口。 */
export async function handleCapabilitiesCommand(
  flags: Record<string, string | boolean>,
  runtime: BridgeRuntime & ProjectToolRuntime,
  requestId: string
): Promise<CliResult> {
  const toolCatalog = await loadFullToolCatalogForDiagnostics(flags, runtime, `${requestId}_tools`);
  if (toolCatalog.ok === false) return toolCatalog;

  const catalogSummary = buildCatalogSummary(toolCatalog.tools);
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
    if (typeof value.ready !== "boolean" || typeof value.toolCount !== "number") return undefined;
    return { bridge: { ready: value.ready, toolCount: value.toolCount } };
  } catch {
    return undefined;
  }
}

/** catalog summary 只汇总唯一 Tool 目录；source 仅作说明维度。 */
function buildCatalogSummary(tools: ToolMetadata[]): Record<string, unknown> {
  const groups: Record<string, number> = {};
  const sources: Record<string, number> = {};
  for (const tool of tools) {
    groups[tool.group] = (groups[tool.group] ?? 0) + 1;
    const source = toolSource(tool);
    sources[source] = (sources[source] ?? 0) + 1;
  }
  return {
    toolCount: tools.length,
    groupCount: Object.keys(groups).length,
    groups: Object.fromEntries(Object.entries(groups).sort(([left], [right]) => left.localeCompare(right))),
    sources: Object.fromEntries(Object.entries(sources).sort(([left], [right]) => left.localeCompare(right))),
  };
}
