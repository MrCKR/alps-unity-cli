import { getBoolean, getString } from "./args.js";
import { executeBridgeCommand, type BridgeRuntime } from "./bridge/index.js";
import type { ToolMetadata } from "./tools/metadata/index.js";
import type { CliResult } from "./types.js";

export interface CustomToolRuntime extends Partial<BridgeRuntime> {
  customTools?: ToolMetadata[];
}

export type CustomToolDiscovery = { ok: true; tools: ToolMetadata[]; mode: "runtime" | "bridge" | "skipped" } | (CliResult & { ok: false });

export function shouldDiscoverCustomTools(flags: Record<string, string | boolean>, runtime: CustomToolRuntime): boolean {
  return getBoolean(flags, "include-custom") ||
    runtime.customTools !== undefined ||
    runtime.bridgeExecute !== undefined ||
    typeof getString(flags, "bridge-endpoint") === "string" ||
    typeof runtime.bridgeEndpoint === "string" ||
    typeof process.env.UNITY_CLI_BRIDGE_ENDPOINT === "string";
}

export async function discoverCustomTools(
  flags: Record<string, string | boolean>,
  runtime: CustomToolRuntime,
  requestId: string,
  options: { requireBridge?: boolean } = {}
): Promise<CustomToolDiscovery> {
  if (runtime.customTools) {
    return { ok: true, tools: runtime.customTools.map(ensureCustomToolDefaults), mode: "runtime" };
  }

  if (!options.requireBridge && !shouldDiscoverCustomTools(flags, runtime)) {
    return { ok: true, tools: [], mode: "skipped" };
  }

  const result = await executeBridgeCommand(requestId, "custom-tool.list", flags, undefined, runtime);
  if (result.ok === false) {
    return result as CliResult & { ok: false };
  }

  const rawTools = (result.data as { tools?: unknown } | undefined)?.tools;
  if (!Array.isArray(rawTools)) {
    return { ok: true, tools: [], mode: "bridge" };
  }
  return { ok: true, tools: rawTools.map(customDescriptorToToolMetadata).filter(isToolMetadata), mode: "bridge" };
}

export function customDescriptorToToolMetadata(value: unknown): ToolMetadata | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  const descriptor = value as Record<string, unknown>;
  const name = stringProp(descriptor, "name");
  if (!name) {
    return undefined;
  }
  const title = stringProp(descriptor, "title") ?? stringProp(descriptor, "displayName") ?? name;
  const group = stringProp(descriptor, "group") ?? groupFromName(name);
  return ensureCustomToolDefaults({
    name,
    title,
    description: stringProp(descriptor, "description") ?? title,
    group,
    inputSchema: objectProp(descriptor, "inputSchema") ?? {},
    outputSchema: objectProp(descriptor, "outputSchema"),
    readOnlyHint: booleanProp(descriptor, "readOnlyHint") ?? false,
    destructiveHint: booleanProp(descriptor, "destructiveHint") ?? false,
    timeoutMs: numberProp(descriptor, "defaultTimeoutMs") ?? numberProp(descriptor, "timeoutMs"),
    permissions: stringArrayProp(descriptor, "permissions"),
    architectureSensitive: booleanProp(descriptor, "architectureSensitive"),
    frozenDecision: stringProp(descriptor, "frozenDecision"),
    coverageScope: stringArrayProp(descriptor, "coverageScope"),
    docsTopic: stringProp(descriptor, "docsTopic") ?? "custom-tool",
    handler: "bridge",
    supportStatus: "live-bridge",
    cliCommand: stringProp(descriptor, "cliCommand") ?? `custom-tool run ${name}`
  });
}

function ensureCustomToolDefaults(tool: ToolMetadata): ToolMetadata {
  return {
    ...tool,
    handler: tool.handler ?? "bridge",
    supportStatus: tool.supportStatus ?? "live-bridge",
    cliCommand: tool.cliCommand ?? `custom-tool run ${tool.name}`,
    docsTopic: tool.docsTopic ?? "custom-tool"
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

function objectProp(record: Record<string, unknown>, key: string): Record<string, unknown> | undefined {
  const value = record[key];
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function stringArrayProp(record: Record<string, unknown>, key: string): string[] | undefined {
  const value = record[key];
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : undefined;
}

function groupFromName(name: string): string {
  const [group] = name.split(".");
  return group || "custom";
}
