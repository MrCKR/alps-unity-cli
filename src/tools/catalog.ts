import { getBoolean } from "../args.js";
import { hasInstanceForFlags } from "../instances.js";
import {
  discoverProjectTools,
  discoverRuntimeTools,
  projectDescriptorToToolMetadata,
  type ProjectToolRuntime
} from "../project-tool-discovery.js";
import { staticCapabilityCatalog } from "../static-capability-catalog.js";
import type { CliResult } from "../types.js";
import { GENERATED_TOOL_COMPLETION_CONTRACTS } from "./.generated-completion-registry.js";
import {
  builtinToolMetadata,
  toolAvailabilityScope,
  toolSource,
  type ToolMetadata
} from "./metadata/index.js";

export type ToolCatalogRuntime = ProjectToolRuntime & { dataDir?: string };
export type ToolCatalogResult = { ok: true; tools: ToolMetadata[] } | (CliResult & { ok: false });

const STATIC_BRIDGE_TOOL_NAMES = new Set(GENERATED_TOOL_COMPLETION_CONTRACTS
  .filter((contract) => contract.descriptorOwner === "built-in" || contract.descriptorOwner.startsWith("extension:"))
  .map((contract) => contract.toolName));

/** package snapshot 中的 C# built-in/Extension descriptor；ownership 不匹配或结构异常时 fail closed。 */
export function staticBridgeToolMetadata(): ToolMetadata[] {
  const bridgeContracts = new Map(GENERATED_TOOL_COMPLETION_CONTRACTS
    .filter((contract) => contract.descriptorOwner === "built-in" || contract.descriptorOwner.startsWith("extension:"))
    .map((contract) => [contract.toolName, contract]));
  return staticCapabilityCatalog().tools.filter((descriptor) => bridgeContracts.has(String(descriptor.name ?? ""))).map((descriptor) => {
    const name = String(descriptor.name ?? "");
    const tool = projectDescriptorToToolMetadata({ ...descriptor, completionContract: bridgeContracts.get(name) });
    if (!tool) throw new Error(`Malformed static Tool descriptor: ${String(descriptor.name ?? "unknown")}`);
    return tool;
  });
}

/** 静态 discovery 永不因 ready instance 自动进入 supervisor；只有显式 custom/runtime 请求才读取 live catalog。 */
export async function shouldLoadLiveCatalog(flags: Record<string, string | boolean>, runtime: ToolCatalogRuntime): Promise<boolean> {
  return getBoolean(flags, "include-custom")
    || getBoolean(flags, "runtime")
    || runtime.projectTools !== undefined
    || runtime.bridgeExecute !== undefined;
}

/** 合并 TS-local 与 Bridge 返回的 built-in、extension、project、runtime descriptor。 */
export async function loadToolCatalog(
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  options: { requireLive?: boolean } = {}
): Promise<ToolCatalogResult> {
  const tools = [...builtinToolMetadata(), ...staticBridgeToolMetadata()];
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

/**
 * 显式选中 Live Editor 时，用该 generation 的真实 registry 覆盖静态条件 Tool 可用性。
 * 静态 descriptor 仍负责跨版本发现；当前版本未注册时必须在执行前暴露 available=false。
 */
export async function resolveLiveToolRegistration(
  tool: ToolMetadata,
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
): Promise<ToolMetadata> {
  return (await resolveLiveToolRegistrations([tool], flags, runtime, requestId))[0];
}

/** 为 group/search 批量投影当前 generation；每类 registry 最多读取一次。 */
export async function resolveLiveToolRegistrations(
  tools: ToolMetadata[],
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
): Promise<ToolMetadata[]> {
  const conditional = tools.filter((tool) => STATIC_BRIDGE_TOOL_NAMES.has(tool.name));
  if (conditional.length === 0 || !await hasLiveCatalogTarget(flags, runtime)) return tools;

  const isRuntimeTarget = (tool: ToolMetadata) => tool.target === "runtime" || (tool.target === "both" && getBoolean(flags, "runtime"));
  const editorTools = conditional.filter((tool) => !isRuntimeTarget(tool));
  const runtimeTools = conditional.filter(isRuntimeTarget);
  const editor = editorTools.length > 0 ? await discoverProjectTools(flags, runtime, `${requestId}_editor`, { requireBridge: true }) : undefined;
  const runtimeCatalog = runtimeTools.length > 0 ? await discoverRuntimeTools(flags, runtime, `${requestId}_runtime`, { requireBridge: true }) : undefined;

  return tools.map((tool) => {
    if (!STATIC_BRIDGE_TOOL_NAMES.has(tool.name)) return tool;
    const discovered = isRuntimeTarget(tool) ? runtimeCatalog : editor;
    if (!discovered || discovered.ok === false) {
      const error = discovered?.ok === false ? discovered.error : undefined;
      const code = error?.code ?? "E_TOOL_UNAVAILABLE";
      const message = error?.message ?? `Tool ${tool.name} availability could not be verified in the selected Unity generation.`;
      return { ...tool, available: false, unavailableReason: `${code}: ${message}` };
    }
    return discovered.tools.find((candidate) => candidate.name === tool.name) ?? {
      ...tool,
      available: false,
      unavailableReason: `E_TOOL_UNAVAILABLE: Tool ${tool.name} is not registered in the selected Unity generation.`,
    };
  });
}

async function hasLiveCatalogTarget(flags: Record<string, string | boolean>, runtime: ToolCatalogRuntime): Promise<boolean> {
  return runtime.projectTools !== undefined
    || runtime.bridgeExecute !== undefined
    || (typeof runtime.dataDir === "string" && await hasInstanceForFlags(flags, runtime.dataDir));
}

/** 按名称解析 catalog descriptor，并在需要时加载 live catalog。 */
export async function resolveCatalogTool(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string
): Promise<{ ok: true; tool?: ToolMetadata } | (CliResult & { ok: false })> {
  // editor.state is a Context-only observation capability.
  if (name === "editor.state") return { ok: true, tool: undefined };
  if (runtime.projectTools) {
    const supplied = await discoverProjectTools(flags, runtime, requestId, { requireBridge: true });
    if (supplied.ok === false) return supplied;
    const tool = supplied.tools.find((candidate) => candidate.name === name);
    if (tool) return { ok: true, tool };
  }
  const local = [...builtinToolMetadata(), ...staticBridgeToolMetadata()].find((tool) => tool.name === name);
  if (local) return { ok: true, tool: local };
  const shouldLoad = await shouldLoadLiveCatalog(flags, runtime)
    || (typeof runtime.dataDir === "string" && await hasInstanceForFlags(flags, runtime.dataDir));
  if (!shouldLoad) return { ok: true, tool: undefined };
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

/** 输出完整公开 descriptor；内部 routing、fingerprint 与展示级状态不会越过此边界。 */
export function publicToolDescriptor(tool: ToolMetadata): Record<string, unknown> {
  if (!tool.outputSchema) throw new Error(`Tool ${tool.name} is missing outputSchema.`);
  const available = tool.available ?? true;
  return {
    name: tool.name,
    summary: tool.summary,
    description: tool.description,
    group: tool.group,
    inputSchema: tool.inputSchema,
    outputSchema: tool.outputSchema,
    destructiveHint: tool.destructiveHint,
    ...(tool.sideEffects?.length ? { sideEffects: tool.sideEffects } : {}),
    playModeSupport: tool.playModeSupport ?? "supported",
    supportsDryRun: tool.supportsDryRun ?? false,
    ...(tool.permissions?.length ? { permissions: tool.permissions } : {}),
    ...(tool.examples?.length ? { examples: tool.examples.map(({ description, argv }) => ({ description, argv })) } : {}),
    source: toolSource(tool),
    target: tool.target ?? "editor",
    availabilityScope: toolAvailabilityScope(tool),
    available,
    ...(!available && tool.unavailableReason ? { unavailableReason: tool.unavailableReason } : {}),
    ...(tool.requiresPackages?.length ? { requiresPackages: tool.requiresPackages } : {})
  };
}

/** 将 Tool 请求中的目标字段转换为 discovery 与实例选择共用 flags。 */
export function catalogFlags(request: Record<string, unknown>, base: Record<string, string | boolean> = {}): Record<string, string | boolean> {
  const flags = { ...base };
  const mappings: Record<string, string> = {
    includeCustom: "include-custom",
    instance: "instance",
    runtime: "runtime",
    project: "project"
  };
  for (const [property, flag] of Object.entries(mappings)) {
    const value = request[property];
    if (typeof value === "string" || typeof value === "boolean") flags[flag] = value;
  }
  return flags;
}
