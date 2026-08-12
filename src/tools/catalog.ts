import {
  discoverProjectTools,
  discoverRuntimeTools,
  projectDescriptorToToolMetadata,
  type ProjectToolRuntime
} from "../project-tool-discovery.js";
import { staticCapabilityCatalog } from "../static-capability-catalog.js";
import { failure } from "../output.js";
import type { CliResult } from "../types.js";
import { GENERATED_TOOL_COMPLETION_CONTRACTS } from "./.generated-completion-registry.js";
import {
  builtinToolMetadata,
  toolAvailabilityScope,
  toolContractProfile,
  toolSource,
  type ToolMetadata
} from "./metadata/index.js";

export type ToolCatalogRuntime = ProjectToolRuntime & { dataDir?: string };
export type ToolCatalogResult = { ok: true; tools: ToolMetadata[] } | (CliResult & { ok: false });

const OFFICIAL_ATTRIBUTE_PREFIX = "UnityPipeline_";
const OFFICIAL_ATTRIBUTE_GROUP = "UnityPipeline";

/** package snapshot 中的 C# built-in/Extension descriptor，仅供生成、契约和离线审计使用。 */
export function staticBridgeToolMetadata(): ToolMetadata[] {
  const bridgeContracts = new Map(GENERATED_TOOL_COMPLETION_CONTRACTS
    .filter((contract) => contract.descriptorOwner === "built-in" || contract.descriptorOwner.startsWith("extension:"))
    .map((contract) => [contract.toolName, contract]));
  return staticCapabilityCatalog().tools.filter((descriptor) => bridgeContracts.has(String(descriptor.name ?? ""))).map((descriptor) => {
    const name = String(descriptor.name ?? "");
    const tool = projectDescriptorToToolMetadata({ ...descriptor, contractProfile: "native", completionContract: bridgeContracts.get(name) });
    if (!tool) throw new Error(`Malformed static Tool descriptor: ${String(descriptor.name ?? "unknown")}`);
    return tool;
  });
}

/**
 * 读取当前实例的唯一完整 Tool catalog。
 * project-tool.list 返回全部注册能力；runtime-tool.list 只补充同一注册在当前 PlayMode 下的 availability。
 */
export async function loadToolCatalog(
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
): Promise<ToolCatalogResult> {
  const editor = await discoverProjectTools(flags, runtime, `${requestId}_editor_catalog`, { requireBridge: true });
  if (editor.ok === false && editor.error?.code !== "E_INSTANCE_NOT_RUNNING") return editor;

  const runtimeCatalog = await discoverRuntimeTools(flags, runtime, `${requestId}_runtime_catalog`, { requireBridge: true });
  if (runtimeCatalog.ok === false && runtimeCatalog.error?.code !== "E_RUNTIME_NOT_RUNNING") return runtimeCatalog;
  if (editor.ok === false && runtimeCatalog.ok === false) return editor;

  // 一个 target 离线不应隐藏另一个 target 的 catalog；descriptor 仍只有 live owner，不建立静态副本。
  return mergeLiveToolCatalog(requestId, builtinToolMetadata(), editor.ok ? editor.tools : [], runtimeCatalog.ok ? runtimeCatalog.tools : []);
}

/** 按名称从同一实例完整目录解析 Tool；本地 Tool 也不能绕过实例连接。 */
export async function resolveCatalogTool(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string
): Promise<{ ok: true; tool?: ToolMetadata } | (CliResult & { ok: false })> {
  const catalog = await loadToolCatalog(flags, runtime, requestId);
  return catalog.ok ? { ok: true, tool: catalog.tools.find((tool) => tool.name === name) } : catalog;
}

/**
 * package 本地 Tool 与 live Tool 必须名称唯一；runtime 列表只能覆盖同一 live registration 的动态 availability。
 * 未经证明的同名覆盖会让来源影响路由，因此必须 fail closed。
 */
function mergeLiveToolCatalog(
  requestId: string,
  localTools: ToolMetadata[],
  editorTools: ToolMetadata[],
  runtimeTools: ToolMetadata[],
): ToolCatalogResult {
  const tools = new Map<string, ToolMetadata>();
  const liveNames = new Set<string>();

  for (const tool of [...localTools, ...editorTools, ...runtimeTools]) {
    const namespaceViolation = officialAttributeNamespaceViolation(tool);
    if (namespaceViolation) return toolCatalogConflict(requestId, tool.name, namespaceViolation);
  }

  for (const tool of localTools) tools.set(tool.name, tool);
  for (const tool of editorTools) {
    if (tools.has(tool.name)) return toolCatalogConflict(requestId, tool.name, "package-local and live registrations use the same name");
    tools.set(tool.name, tool);
    liveNames.add(tool.name);
  }

  for (const runtimeTool of runtimeTools) {
    const current = tools.get(runtimeTool.name);
    if (!current) {
      tools.set(runtimeTool.name, runtimeTool);
      liveNames.add(runtimeTool.name);
      continue;
    }
    if (!liveNames.has(runtimeTool.name) || !sameLiveRegistration(current, runtimeTool)) {
      return toolCatalogConflict(requestId, runtimeTool.name, "runtime registration does not match the instance Tool registry");
    }
    if (current.target === "runtime") {
      tools.set(runtimeTool.name, {
        ...current,
        available: runtimeTool.available,
        ...(runtimeTool.unavailableReason ? { unavailableReason: runtimeTool.unavailableReason } : {}),
      });
    }
  }

  return { ok: true, tools: [...tools.values()].sort((left, right) => left.name.localeCompare(right.name)) };
}

/** 保留namespace与contract profile必须双向一致，禁止native或伪attribute descriptor借名路由。 */
function officialAttributeNamespaceViolation(tool: ToolMetadata): string | undefined {
  const officialAttribute = toolContractProfile(tool) === "official-attribute";
  const usesPrefix = tool.name.startsWith(OFFICIAL_ATTRIBUTE_PREFIX);
  const usesGroup = tool.group === OFFICIAL_ATTRIBUTE_GROUP;
  if (officialAttribute && (!usesPrefix || !usesGroup)) return "official-attribute registration must use the UnityPipeline_ prefix and UnityPipeline group";
  if (!officialAttribute && (usesPrefix || usesGroup)) return "native registration cannot use the reserved UnityPipeline_ prefix or UnityPipeline group";
  return undefined;
}

/** runtime availability 必须来自同一份 live descriptor，不能借同名切换 owner。 */
function sameLiveRegistration(left: ToolMetadata, right: ToolMetadata): boolean {
  if (left.group !== right.group || left.target !== right.target || toolContractProfile(left) !== toolContractProfile(right)) return false;
  if (left.liveDescriptorFingerprint || right.liveDescriptorFingerprint) {
    return left.liveDescriptorFingerprint === right.liveDescriptorFingerprint;
  }
  return JSON.stringify(left.inputSchema) === JSON.stringify(right.inputSchema)
    && JSON.stringify(left.outputSchema) === JSON.stringify(right.outputSchema);
}

/** 返回稳定的 catalog ownership 冲突，不允许静默覆盖。 */
function toolCatalogConflict(requestId: string, name: string, reason: string): CliResult & { ok: false } {
  return failure(requestId, "E_TOOL_SCHEMA_CONFLICT", `Tool catalog conflict for ${name}: ${reason}.`, [
    "Fix the duplicate Tool registration in the selected Unity project, then retry discovery."
  ], { toolName: name, reason }) as CliResult & { ok: false };
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
    contractProfile: toolContractProfile(tool),
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
