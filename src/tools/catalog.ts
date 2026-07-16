import { getBoolean, getString } from "../args.js";
import { hasInstanceForFlags } from "../instances.js";
import {
  discoverProjectTools,
  discoverRuntimeTools,
  shouldDiscoverProjectTools,
  type ProjectToolRuntime
} from "../project-tool-discovery.js";
import type { CliResult } from "../types.js";
import {
  builtinToolMetadata,
  inferToolRiskLevel,
  isLongRunningTool,
  supportStatusForTool,
  toolAvailabilityScope,
  toolCancellation,
  toolCliCommand,
  toolSource,
  type ToolMetadata
} from "./metadata/index.js";

export type ToolCatalogRuntime = ProjectToolRuntime & { dataDir?: string };
export type ToolCatalogResult = { ok: true; tools: ToolMetadata[] } | (CliResult & { ok: false });

/** 判断当前调用是否具备 live catalog 目标；无目标时 catalog 严格保持 TS-local。 */
export async function shouldLoadLiveCatalog(flags: Record<string, string | boolean>, runtime: ToolCatalogRuntime): Promise<boolean> {
  if (getBoolean(flags, "include-custom") || shouldDiscoverProjectTools(flags, runtime)) return true;
  return typeof runtime.dataDir === "string" && hasInstanceForFlags(flags, runtime.dataDir);
}

/** 合并 TS-local 与 Bridge 返回的 built-in、extension、project、runtime descriptor。 */
export async function loadToolCatalog(
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  options: { requireLive?: boolean } = {}
): Promise<ToolCatalogResult> {
  const tools = [...builtinToolMetadata()];
  const loadLive = options.requireLive === true || await shouldLoadLiveCatalog(flags, runtime);
  if (!loadLive) return { ok: true, tools };

  const editor = await discoverProjectTools(flags, runtime, requestId, { requireBridge: options.requireLive });
  if (editor.ok === false) return editor;
  tools.push(...editor.tools);

  if (getBoolean(flags, "runtime")) {
    const runtimeTools = await discoverRuntimeTools(flags, runtime, requestId, { requireBridge: options.requireLive });
    if (runtimeTools.ok === false) return runtimeTools;
    tools.push(...runtimeTools.tools);
  }
  return { ok: true, tools: uniqueByName(tools) };
}

/** 按名称解析 catalog descriptor，并在需要时加载 live catalog。 */
export async function resolveCatalogTool(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string
): Promise<{ ok: true; tool?: ToolMetadata } | (CliResult & { ok: false })> {
  const local = builtinToolMetadata().find((tool) => tool.name === name);
  if (local) return { ok: true, tool: local };
  if (!await shouldLoadLiveCatalog(flags, runtime)) return { ok: true, tool: undefined };
  const runtimeCatalog = await discoverRuntimeTools(flags, runtime, requestId, { requireBridge: true });
  if (runtimeCatalog.ok === false) return runtimeCatalog;
  const runtimeTool = runtimeCatalog.tools.find((tool) => tool.name === name);
  if (runtimeTool) return { ok: true, tool: runtimeTool };
  const editorCatalog = await discoverProjectTools(flags, runtime, requestId, { requireBridge: true });
  if (editorCatalog.ok === false) return editorCatalog;
  return { ok: true, tool: editorCatalog.tools.find((tool) => tool.name === name) };
}

export function uniqueByName(tools: ToolMetadata[]): ToolMetadata[] {
  const result = new Map<string, ToolMetadata>();
  for (const tool of tools) result.set(tool.name, tool);
  return [...result.values()];
}

/** 输出完整公开 descriptor，保留 live Bridge 提供的安全和 schema 字段。 */
export function publicToolDescriptor(tool: ToolMetadata): Record<string, unknown> {
  const longRunning = isLongRunningTool(tool);
  const cancellation = toolCancellation(tool);
  return {
    name: tool.name,
    summary: tool.summary,
    description: tool.description,
    group: tool.group,
    inputSchema: tool.inputSchema,
    ...(tool.outputSchema ? { outputSchema: tool.outputSchema } : {}),
    destructiveHint: tool.destructiveHint,
    ...(tool.sideEffects ? { sideEffects: tool.sideEffects } : {}),
    ...(tool.playModeSupport ? { playModeSupport: tool.playModeSupport } : {}),
    ...(tool.supportsDryRun !== undefined ? { supportsDryRun: tool.supportsDryRun } : {}),
    ...(tool.permissions ? { permissions: tool.permissions } : {}),
    ...(tool.examples ? { examples: tool.examples } : {}),
    source: toolSource(tool),
    riskLevel: inferToolRiskLevel(tool),
    ...(longRunning ? { longRunning: true } : {}),
    ...(cancellation !== "none" ? { cancellation } : {}),
    cliCommand: toolCliCommand(tool),
    target: tool.target ?? "editor",
    availabilityScope: toolAvailabilityScope(tool),
    supportStatus: supportStatusForTool(tool),
    ...(tool.requiresPackages ? { requiresPackages: tool.requiresPackages } : {})
  };
}

/** 将 Tool 请求中的目标字段转换为 discovery 与实例选择共用 flags。 */
export function catalogFlags(request: Record<string, unknown>, base: Record<string, string | boolean> = {}): Record<string, string | boolean> {
  const flags = { ...base };
  const mappings: Record<string, string> = {
    includeCustom: "include-custom",
    bridgeEndpoint: "bridge-endpoint",
    target: "target",
    instance: "instance",
    runtime: "runtime",
    project: "project"
  };
  for (const [property, flag] of Object.entries(mappings)) {
    const value = request[property];
    if (typeof value === "string" || typeof value === "boolean") flags[flag] = value;
  }
  const endpoint = getString(flags, "bridge-endpoint");
  if (endpoint) flags["bridge-endpoint"] = endpoint;
  return flags;
}
