import { getBoolean, getString } from "./args.js";
import { executeBridgeCommand, type BridgeRuntime } from "./bridge/index.js";
import { inferPlayModeSupport, type PlayModeSupport, type ToolMetadata } from "./tools/metadata/index.js";
import type { CliResult } from "./types.js";

export interface ProjectToolRuntime extends Partial<BridgeRuntime> {
  projectTools?: ToolMetadata[];
}

export type ProjectToolDiscovery = { ok: true; tools: ToolMetadata[]; mode: "runtime" | "bridge" | "skipped" } | (CliResult & { ok: false });

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
  return { ok: true, tools: rawTools.map(projectDescriptorToToolMetadata).filter(isToolMetadata), mode: "bridge" };
}

export function projectDescriptorToToolMetadata(value: unknown): ToolMetadata | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  const descriptor = value as Record<string, unknown>;
  const name = stringProp(descriptor, "name");
  if (!name) {
    return undefined;
  }
  const title = stringProp(descriptor, "title") ?? name;
  const group = stringProp(descriptor, "group") ?? groupFromName(name);
  const target = stringProp(descriptor, "target") ?? "editor";
  const availabilityScope = stringProp(descriptor, "availabilityScope") ?? (target === "runtime" ? "playModeSession" : "editorSession");
  return ensureProjectToolDefaults({
    name,
    title,
    description: stringProp(descriptor, "description") ?? title,
    group,
    inputSchema: objectProp(descriptor, "inputSchema") ?? {},
    outputSchema: objectProp(descriptor, "outputSchema"),
    destructiveHint: booleanProp(descriptor, "destructiveHint") ?? false,
    playModeSupport: playModeSupportProp(descriptor, "playModeSupport"),
    timeoutMs: numberProp(descriptor, "defaultTimeoutMs") ?? numberProp(descriptor, "timeoutMs"),
    permissions: stringArrayProp(descriptor, "permissions"),
    docsTopic: stringProp(descriptor, "docsTopic") ?? "project-tool",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: stringProp(descriptor, "cliCommand") ?? `tools run ${name}`,
    target,
    availabilityScope,
    available: booleanProp(descriptor, "available") ?? undefined
  });
}

function ensureProjectToolDefaults(tool: ToolMetadata): ToolMetadata {
  return {
    ...tool,
    handler: tool.handler ?? (tool.target === "runtime" ? "bridge-runtime" : "bridge"),
    supportStatus: tool.supportStatus ?? (tool.target === "runtime" ? "live-bridge-runtime" : "live-bridge"),
    cliCommand: tool.cliCommand ?? `tools run ${tool.name}`,
    docsTopic: tool.docsTopic ?? (tool.target === "runtime" ? "runtime-tool" : "project-tool"),
    target: tool.target ?? "editor",
    availabilityScope: tool.availabilityScope ?? (tool.target === "runtime" ? "playModeSession" : "editorSession"),
    playModeSupport: tool.playModeSupport ?? inferPlayModeSupport(tool)
  };
}

function isToolMetadata(value: ToolMetadata | undefined): value is ToolMetadata {
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

function objectProp(record: Record<string, unknown>, key: string): Record<string, unknown> | undefined {
  const value = record[key];
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function stringArrayProp(record: Record<string, unknown>, key: string): string[] | undefined {
  const value = record[key];
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : undefined;
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
  return { ok: true, tools: rawTools.map(projectDescriptorToToolMetadata).filter(isToolMetadata), mode: "bridge" };
}
