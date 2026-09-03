import {
  discoverProjectTools,
  discoverRuntimeTools,
  projectDescriptorToToolMetadata,
  type ProjectToolRuntime
} from "../project-tool-discovery.js";
import { readEditorCatalogDescriptor, readRuntimeCatalogDescriptor } from "../catalog-protocol.js";
import { staticCapabilityCatalog } from "../static-capability-catalog.js";
import { failure } from "../output.js";
import type { CliResult } from "../types.js";
import { GENERATED_TOOL_COMPLETION_CONTRACTS } from "./.generated-completion-registry.js";
import {
  builtinToolMetadata,
  findToolByName,
  toolContractProfile,
  type ToolExample,
  type ToolMetadata
} from "./metadata/index.js";

export type ToolCatalogRuntime = ProjectToolRuntime & { dataDir?: string };
export type ToolCatalogResult = { ok: true; tools: ToolMetadata[] } | (CliResult & { ok: false });

const OFFICIAL_ATTRIBUTE_PREFIX = "UnityPipeline_";
const OFFICIAL_ATTRIBUTE_GROUP = "UnityPipeline";
const CATALOG_INDEPENDENT_OPERABILITY_TOOLS = new Set(["editor.state", "editor.dialogs", "editor.dialog.click"]);

/** 统一状态观察与弹窗恢复必须在Live catalog被transition或modal阻断时仍进入原dispatcher。 */
export function resolveCatalogIndependentOperabilityTool(name: string): ToolMetadata | undefined {
  if (!CATALOG_INDEPENDENT_OPERABILITY_TOOLS.has(name)) return undefined;
  const tool = findToolByName(name);
  return tool?.handler === "offline" ? tool : undefined;
}

/** 只有三个 exact operability Tool 可绕过 live catalog。 */
export function resolveCatalogIndependentInvocationTool(name: string): ToolMetadata | undefined {
  return resolveCatalogIndependentOperabilityTool(name);
}

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
 * 显式诊断聚合使用的完整 Tool catalog；公开discovery与执行解析禁止调用。
 * project-tool.list 返回全部注册能力；runtime-tool.list 只补充同一注册在当前 PlayMode 下的 availability。
 */
export async function loadFullToolCatalogForDiagnostics(
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  absoluteDeadlineMs?: number,
): Promise<ToolCatalogResult> {
  const editorAdmission = runtime.rootAdmissions?.editor ?? (runtime.rootAdmission?.target === "editor" ? runtime.rootAdmission : undefined);
  const runtimeAdmission = runtime.rootAdmissions?.runtime ?? (runtime.rootAdmission?.target === "runtime" ? runtime.rootAdmission : undefined);
  const editorRuntime = { ...runtime, rootAdmission: editorAdmission };
  const runtimeTargetRuntime = { ...runtime, rootAdmission: runtimeAdmission };
  const editorRequestId = editorRuntime.rootAdmission?.requestId ?? `${requestId}_editor_catalog`;
  const runtimeRequestId = runtimeTargetRuntime.rootAdmission?.requestId ?? `${requestId}_runtime_catalog`;
  const catalogDeadlineMs = absoluteDeadlineMs ?? editorAdmission?.executionAbsoluteDeadlineMs ?? runtimeAdmission?.executionAbsoluteDeadlineMs;
  const editor = await discoverProjectTools(flags, editorRuntime, editorRequestId, { requireBridge: true, absoluteDeadlineMs: catalogDeadlineMs });
  if (editor.ok === false && editor.error?.code !== "E_INSTANCE_NOT_RUNNING") return editor;

  const runtimeCatalog = await discoverRuntimeTools(flags, runtimeTargetRuntime, runtimeRequestId, { requireBridge: true, absoluteDeadlineMs: catalogDeadlineMs });
  if (runtimeCatalog.ok === false && runtimeCatalog.error?.code !== "E_RUNTIME_NOT_RUNNING") return runtimeCatalog;
  if (editor.ok === false && runtimeCatalog.ok === false) return editor;

  // 一个 target 离线不应隐藏另一个 target 的 catalog；descriptor 仍只有 live owner，不建立静态副本。
  return mergeLiveToolCatalog(requestId, builtinToolMetadata(), editor.ok ? editor.tools : [], runtimeCatalog.ok ? runtimeCatalog.tools : []);
}

/** exact-name resolution 每个 target 只读取一份 full descriptor，不构造完整 descriptor graph。 */
export async function resolveCatalogTool(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolCatalogRuntime,
  requestId: string,
  absoluteDeadlineMs?: number,
  fallbackName?: string,
): Promise<{ ok: true; tool?: ToolMetadata } | (CliResult & { ok: false })> {
  const operabilityTool = resolveCatalogIndependentOperabilityTool(name);
  if (operabilityTool) return { ok: true, tool: operabilityTool };

  const localTool = findToolByName(name);
  const localFallbackTool = fallbackName ? findToolByName(fallbackName) : undefined;
  const editorAdmission = runtime.rootAdmissions?.editor ?? (runtime.rootAdmission?.target === "editor" ? runtime.rootAdmission : undefined);
  const runtimeAdmission = runtime.rootAdmissions?.runtime ?? (runtime.rootAdmission?.target === "runtime" ? runtime.rootAdmission : undefined);
  const editorRuntime = { ...runtime, rootAdmission: editorAdmission };
  const runtimeTargetRuntime = { ...runtime, rootAdmission: runtimeAdmission };
  const editor = await readEditorCatalogDescriptor(flags, editorRuntime, editorAdmission?.requestId ?? `${requestId}_editor_descriptor`, name, absoluteDeadlineMs, fallbackName);
  if (!editor.ok && editor.error.code !== "E_INSTANCE_NOT_RUNNING") return editor;
  const runtimeDescriptor = await readRuntimeCatalogDescriptor(flags, runtimeTargetRuntime, runtimeAdmission?.requestId ?? `${requestId}_runtime_descriptor`, name, absoluteDeadlineMs);
  if (!runtimeDescriptor.ok && runtimeDescriptor.error.code !== "E_RUNTIME_NOT_RUNNING") return runtimeDescriptor;
  if (!editor.ok && !runtimeDescriptor.ok) return editor;

  const candidates = [localTool, localFallbackTool, editor.ok ? editor.tool : undefined, runtimeDescriptor.ok ? runtimeDescriptor.tool : undefined];
  const selectedName = candidates.some((tool) => tool?.name === name) ? name : fallbackName;
  const selected = (tool: ToolMetadata | undefined): ToolMetadata[] => tool && tool.name === selectedName ? [tool] : [];
  const merged = mergeLiveToolCatalog(
    requestId,
    selected(localTool).concat(selected(localFallbackTool)),
    selected(editor.ok ? editor.tool : undefined),
    selected(runtimeDescriptor.ok ? runtimeDescriptor.tool : undefined),
  );
  return merged.ok ? { ok: true, tool: merged.tools[0] } : merged;
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

/** runtime availability 必须来自同一published table entry，动态availability不参与identity比较。 */
function sameLiveRegistration(left: ToolMetadata, right: ToolMetadata): boolean {
  return JSON.stringify(liveRegistrationIdentity(left)) === JSON.stringify(liveRegistrationIdentity(right));
}

function liveRegistrationIdentity(tool: ToolMetadata): Record<string, unknown> {
  return {
    name: tool.name,
    summary: tool.summary,
    description: tool.description,
    group: tool.group,
    source: tool.source,
    inputSchema: tool.inputSchema,
    outputSchema: tool.outputSchema,
    destructiveHint: tool.destructiveHint,
    sideEffects: tool.sideEffects ?? [],
    supportsDryRun: tool.supportsDryRun ?? false,
    permissions: tool.permissions ?? [],
    requiresPackages: tool.requiresPackages ?? [],
    examples: tool.examples ?? [],
    target: tool.target,
    availabilityScope: tool.availabilityScope,
    playModeSupport: tool.playModeSupport,
    contractProfile: toolContractProfile(tool),
    timeoutMs: tool.timeoutMs,
    completionContract: tool.completionContract,
  };
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

/** 输出调用所需的精简 descriptor；完整结果 schema 只保留在内部 metadata。 */
export function publicToolDescriptor(tool: ToolMetadata): Record<string, unknown> {
  if (!tool.outputSchema) throw new Error(`Tool ${tool.name} is missing outputSchema.`);
  const available = tool.available ?? true;
  if (!available && !tool.unavailableReason) throw new Error(`Unavailable Tool ${tool.name} is missing unavailableReason.`);
  return {
    name: tool.name,
    description: tool.description,
    inputSchema: tool.inputSchema,
    destructiveHint: tool.destructiveHint,
    ...(tool.sideEffects?.length ? { sideEffects: tool.sideEffects } : {}),
    playModeSupport: tool.playModeSupport ?? "supported",
    supportsDryRun: tool.supportsDryRun ?? false,
    ...(tool.examples?.length ? { examples: tool.examples.map((example) => publicToolExample(tool.name, example)) } : {}),
    target: tool.target ?? "editor",
    ...(!available ? { available: false, unavailableReason: tool.unavailableReason } : {}),
    ...(tool.requiresPackages?.length ? { requiresPackages: tool.requiresPackages } : {})
  };
}

/** 将内部 argv authoring 形态投影为无 CLI 语法重复的业务示例。 */
function publicToolExample(toolName: string, example: ToolExample): Record<string, unknown> {
  const execution = publicExampleExecution(toolName, example);
  return {
    description: example.description,
    input: publicExampleInput(toolName, example),
    ...(execution ? { execution } : {})
  };
}

/** 优先使用 typed input；旧 authoring 仅有 argv 时解析同一个 --input JSON。 */
function publicExampleInput(toolName: string, example: ToolExample): unknown {
  if (example.input !== undefined) return example.input;
  const inputIndex = example.argv.indexOf("--input");
  if (inputIndex < 0) return {};
  try {
    const input = JSON.parse(example.argv[inputIndex + 1] ?? "");
    if (!input || typeof input !== "object" || Array.isArray(input)) throw new Error("input must be an object");
    return input;
  } catch (error) {
    throw new Error(`Tool ${toolName} has an invalid example input: ${error instanceof Error ? error.message : String(error)}`);
  }
}

/** 从 authoring metadata 或 argv 提取 dry-run/timeout 控制，不混入业务 input。 */
function publicExampleExecution(toolName: string, example: ToolExample): Record<string, unknown> | undefined {
  const dryRun = example.execution?.dryRun === true || example.argv.includes("--dry-run");
  const timeoutIndex = example.argv.indexOf("--timeout-ms");
  const attachedTimeout = example.argv.find((token) => token.startsWith("--timeout-ms="))?.slice("--timeout-ms=".length);
  const timeoutRaw = example.execution?.timeoutMs ?? (timeoutIndex >= 0 ? Number(example.argv[timeoutIndex + 1]) : attachedTimeout === undefined ? undefined : Number(attachedTimeout));
  if (timeoutRaw !== undefined && (!Number.isFinite(timeoutRaw) || timeoutRaw <= 0)) {
    throw new Error(`Tool ${toolName} has an invalid example timeoutMs.`);
  }
  if (!dryRun && timeoutRaw === undefined) return undefined;
  return { ...(dryRun ? { dryRun: true } : {}), ...(timeoutRaw !== undefined ? { timeoutMs: timeoutRaw } : {}) };
}
