import { getBoolean, getString } from "./args.js";
import { executeBridgeCommand, type BridgeRuntime } from "./bridge/index.js";
import { inferPlayModeSupport, normalizeToolMetadata, type PlayModeSupport, type ResolvedToolMetadata, type ToolHandler, type ToolMetadata, type ToolSupportStatus } from "./tools/metadata/index.js";
import type { CliResult } from "./types.js";

export interface ProjectToolRuntime extends Partial<BridgeRuntime> {
  projectTools?: ToolMetadata[];
}

export type ProjectToolDiscovery = { ok: true; tools: ResolvedToolMetadata[]; mode: "runtime" | "bridge" | "skipped" } | (CliResult & { ok: false });

export function shouldDiscoverProjectTools(flags: Record<string, string | boolean>, runtime: ProjectToolRuntime): boolean {
  return getBoolean(flags, "include-custom") ||
    getBoolean(flags, "runtime") ||
    runtime.projectTools !== undefined ||
    runtime.bridgeExecute !== undefined ||
    typeof getString(flags, "bridge-endpoint") === "string" ||
    typeof getString(flags, "target") === "string" ||
    typeof getString(flags, "instance") === "string" ||
    typeof runtime.bridgeEndpoint === "string" ||
    typeof process.env.UNITY_CLI_BRIDGE_ENDPOINT === "string";
}

export async function discoverProjectTools(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string,
  options: { requireBridge?: boolean } = {}
): Promise<ProjectToolDiscovery> {
  if (runtime.projectTools) {
    return { ok: true, tools: runtime.projectTools.map(ensureProjectToolDefaults), mode: "runtime" };
  }

  if (!options.requireBridge && !shouldDiscoverProjectTools(flags, runtime)) {
    return { ok: true, tools: [], mode: "skipped" };
  }

  const result = await executeBridgeCommand(requestId, "project-tool.list", flags, undefined, runtime);
  if (result.ok === false) {
    return result as CliResult & { ok: false };
  }

  const rawTools = (result.data as { tools?: unknown } | undefined)?.tools;
  if (!Array.isArray(rawTools)) {
    return { ok: true, tools: [], mode: "bridge" };
  }
  return { ok: true, tools: rawTools.map(projectDescriptorToToolMetadata).filter(isResolvedToolMetadata), mode: "bridge" };
}

export function projectDescriptorToToolMetadata(value: unknown): ResolvedToolMetadata | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  const descriptor = value as Record<string, unknown>;
  const name = stringProp(descriptor, "name");
  if (!name) {
    return undefined;
  }
  const summary = stringProp(descriptor, "summary");
  if (!summary) {
    return undefined;
  }
  const group = stringProp(descriptor, "group") ?? groupFromName(name);
  const target = stringProp(descriptor, "target") ?? "editor";
  return ensureProjectToolDefaults({
    name,
    summary,
    description: stringProp(descriptor, "description") ?? summary,
    group,
    inputSchema: objectProp(descriptor, "inputSchema") ?? {},
    outputSchema: objectProp(descriptor, "outputSchema"),
    destructiveHint: booleanProp(descriptor, "destructiveHint") ?? false,
    playModeSupport: playModeSupportProp(descriptor, "playModeSupport"),
    timeoutMs: numberProp(descriptor, "defaultTimeoutMs") ?? numberProp(descriptor, "timeoutMs"),
    permissions: stringArrayProp(descriptor, "permissions"),
    handler: toolHandlerProp(descriptor, "handler"),
    supportStatus: toolSupportStatusProp(descriptor, "supportStatus"),
    target,
    available: booleanProp(descriptor, "available") ?? undefined,
    source: toolSourceProp(descriptor, "source")
  });
}

function ensureProjectToolDefaults(tool: ToolMetadata): ResolvedToolMetadata {
  const target = tool.target ?? "editor";
  // target=runtime 是公开契约不变量；即使旧桥返回 bridge/live-bridge，也必须按 runtime 路由归一化。
  const handler = target === "runtime" ? "bridge-runtime" : tool.handler ?? "bridge";
  const supportStatus = target === "runtime" ? "live-bridge-runtime" : tool.supportStatus;
  const source = tool.source === "built-in" || tool.source === "extension" || tool.source === "project" ? tool.source : "project";
  return normalizeToolMetadata({
    ...tool,
    handler,
    supportStatus,
    target,
    playModeSupport: tool.playModeSupport ?? inferPlayModeSupport(tool)
  }, source);
}

function isResolvedToolMetadata(value: ResolvedToolMetadata | undefined): value is ResolvedToolMetadata {
  return value !== undefined;
}

function stringProp(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function booleanProp(record: Record<string, unknown>, key: string): boolean | undefined {
  return typeof record[key] === "boolean" ? record[key] : undefined;
}

function numberProp(record: Record<string, unknown>, key: string): number | undefined {
  const value = record[key];
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function playModeSupportProp(record: Record<string, unknown>, key: string): PlayModeSupport | undefined {
  const value = record[key];
  return value === "supported" || value === "runtime-only" || value === "session-affecting" || value === "editor-only" || value === "unknown" ? value : undefined;
}

/** 读取 Unity descriptor 已给出的执行路由，避免 runtime 工具被误归类为普通 bridge。 */
function toolHandlerProp(record: Record<string, unknown>, key: string): ToolHandler | undefined {
  const value = record[key];
  return value === "bridge" || value === "bridge-runtime" || value === "job" || value === "offline" || value === "hybrid" ? value : undefined;
}

/** 读取 Unity descriptor 已给出的支持状态，保持 describe/group list 与 runtime discovery 一致。 */
function toolSupportStatusProp(record: Record<string, unknown>, key: string): ToolSupportStatus | undefined {
  const value = record[key];
  return value === "live-bridge" || value === "live-bridge-runtime" || value === "bridge-job" || value === "batch-job" || value === "cli-only" || value === "planned" ? value : undefined;
}

function objectProp(record: Record<string, unknown>, key: string): Record<string, unknown> | undefined {
  const value = record[key];
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function stringArrayProp(record: Record<string, unknown>, key: string): string[] | undefined {
  const value = record[key];
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : undefined;
}

function toolSourceProp(record: Record<string, unknown>, key: string): ToolMetadata["source"] | undefined {
  const value = record[key];
  return value === "built-in" || value === "extension" || value === "project" ? value : undefined;
}

export function groupFromName(name: string): string {
  const [group] = name.split(".");
  return group || "custom";
}

/**
 * Discover runtime tools via runtime-tool.list bridge command.
 * This path is exempt from the Play Mode busy guard, so it works
 * correctly in Play Mode where project-tool.list would be blocked.
 */
export async function discoverRuntimeTools(
  flags: Record<string, string | boolean>,
  runtime: ProjectToolRuntime,
  requestId: string
): Promise<ProjectToolDiscovery> {
  if (runtime.projectTools) {
    const runtimeTools = runtime.projectTools
      .map(ensureProjectToolDefaults)
      .filter((tool) => tool.target === "runtime" || tool.target === "both");
    return { ok: true, tools: runtimeTools, mode: "runtime" };
  }

  if (!shouldDiscoverProjectTools(flags, runtime)) {
    return { ok: true, tools: [], mode: "skipped" };
  }

  const result = await executeBridgeCommand(requestId, "runtime-tool.list", flags, undefined, runtime);
  if (result.ok === false) {
    return result as CliResult & { ok: false };
  }

  const rawTools = (result.data as { tools?: unknown } | undefined)?.tools;
  if (!Array.isArray(rawTools)) {
    return { ok: true, tools: [], mode: "bridge" };
  }
  return { ok: true, tools: rawTools.map(projectDescriptorToToolMetadata).filter(isResolvedToolMetadata), mode: "bridge" };
}
