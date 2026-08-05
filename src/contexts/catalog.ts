import { getBoolean } from "../args.js";
import { executeBridgePayload, type BridgeRuntime } from "../bridge/index.js";
import { ExtensionRegistry } from "../extensions/index.js";
import { hasInstanceForFlags } from "../instances.js";
import { staticCapabilityCatalog } from "../static-capability-catalog.js";
import { failure } from "../output.js";
import type { CliResult } from "../types.js";
import { scoreSearch } from "../util.js";
import { validateSchema } from "../schema/validator.js";
import "./.generated-registry.js";
import { defineContextMetadata } from "./define.js";
import type { ContextDescriptor, ContextListEntry, ContextSource } from "./types.js";

export interface ContextCatalogRuntime extends Partial<BridgeRuntime> {
  dataDir?: string;
  projectContexts?: unknown[];
}

export type ContextCatalogResult = { ok: true; contexts: ContextDescriptor[]; live: boolean; liveContexts: ContextDescriptor[] } | (CliResult & { ok: false });

/** 无 live 目标时只暴露生成 registry 中的 TS-local Context。 */
export async function shouldLoadLiveContextCatalog(flags: Record<string, string | boolean>, runtime: ContextCatalogRuntime): Promise<boolean> {
  return getBoolean(flags, "include-custom")
    || runtime.projectContexts !== undefined
    || runtime.bridgeExecute !== undefined;
}

/** 加载唯一 catalog view；任何 malformed descriptor 或 duplicate 都使 registry fail closed。 */
export async function loadContextCatalog(
  flags: Record<string, string | boolean>,
  runtime: ContextCatalogRuntime,
  requestId: string,
  options: { requireLive?: boolean } = {}
): Promise<ContextCatalogResult> {
  let local: ContextDescriptor[];
  try {
    local = builtinContextDescriptors();
  } catch (error) {
    return unhealthy(requestId, error instanceof Error ? error.message : String(error));
  }
  const loadLive = options.requireLive === true || await shouldLoadLiveContextCatalog(flags, runtime);
  if (!loadLive) return { ok: true, contexts: local, live: false, liveContexts: [] };

  let live: ContextDescriptor[];
  if (runtime.projectContexts) {
    const parsed = parseLiveDescriptors(runtime.projectContexts, requestId);
    if (parsed.ok === false) return parsed;
    live = parsed.contexts;
  } else {
    const result = await executeBridgePayload(requestId, "context.list", {}, flags, runtime, undefined, { mode: "read", mutation: false });
    if (result.ok === false) return result as CliResult & { ok: false };
    const raw = (result.data as { contexts?: unknown } | undefined)?.contexts;
    if (!Array.isArray(raw)) return unhealthy(requestId, "context.list response must contain a contexts array.");
    const parsed = parseLiveDescriptors(raw, requestId);
    if (parsed.ok === false) return parsed;
    live = parsed.contexts;
  }

  const liveNames = new Set(live.map((context) => context.name));
  const merged = [...local.filter((context) => !liveNames.has(context.name)), ...live];
  const duplicate = duplicateNames(merged);
  if (duplicate.length > 0) return unhealthy(requestId, `Duplicate context registration: ${duplicate.join(", ")}.`, { duplicates: duplicate });
  return { ok: true, contexts: merged, live: true, liveContexts: live };
}

export async function resolveCatalogContext(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ContextCatalogRuntime,
  requestId: string
): Promise<{ ok: true; context?: ContextDescriptor; live: boolean } | (CliResult & { ok: false })> {
  // editor.state 由 TS 聚合 identity/session/Windows UI，live catalog 不得覆盖其唯一 owner。
  if (name === "editor.state") return { ok: true, context: builtinContextDescriptors().find((context) => context.name === name), live: false };
  if (runtime.projectContexts !== undefined || runtime.bridgeExecute !== undefined) {
    const supplied = await loadContextCatalog(flags, runtime, requestId, { requireLive: true });
    if (supplied.ok === false) return supplied;
    const context = supplied.contexts.find((candidate) => candidate.name === name);
    if (context) return { ok: true, context, live: true };
  }
  const local = builtinContextDescriptors().find((context) => context.name === name);
  if (local) return { ok: true, context: local, live: false };
  const shouldLoad = await shouldLoadLiveContextCatalog(flags, runtime)
    || (typeof runtime.dataDir === "string" && await hasInstanceForFlags(flags, runtime.dataDir));
  if (!shouldLoad) return { ok: true, context: undefined, live: false };
  const catalog = await loadContextCatalog(flags, runtime, requestId, { requireLive: true });
  return catalog.ok ? { ok: true, context: catalog.contexts.find((context) => context.name === name), live: true } : catalog;
}

export function builtinContextDescriptors(): ContextDescriptor[] {
  return [...defineContextMetadata(), ...staticBridgeContextDescriptors()];
}

/** 将 package snapshot 映射为当前严格 Context descriptor；任一 malformed entry 都 fail closed。 */
export function staticBridgeContextDescriptors(): ContextDescriptor[] {
  return staticCapabilityCatalog().contexts.filter((descriptor) => descriptor.name !== "editor.state").map((descriptor) => {
    const parsed = parseLiveDescriptor(descriptor);
    if (typeof parsed === "string") throw new Error(`Malformed static Context descriptor ${String(descriptor.name ?? "unknown")}: ${parsed}`);
    return parsed;
  });
}

export function builtinContextCategories(): string[] {
  return [...new Set(builtinContextDescriptors().map((context) => context.category))].sort();
}

export function findBuiltinContextByName(name: string): ContextDescriptor | undefined {
  return builtinContextDescriptors().find((context) => context.name === name);
}

export function filterContextsByCategory(category: string): ContextDescriptor[] {
  return builtinContextDescriptors().filter((context) => context.category === category);
}

export function searchContexts(keyword: string, contexts = builtinContextDescriptors()): ContextDescriptor[] {
  const query = keyword.toLowerCase();
  return contexts.filter((context) => `${context.name}\n${context.summary}\n${context.description}\n${context.category}`.toLowerCase().includes(query))
    .sort((left, right) => scoreSearch(right.name.toLowerCase(), query) - scoreSearch(left.name.toLowerCase(), query));
}

export function toContextListEntry(context: ContextDescriptor): ContextListEntry {
  const { name, summary, description, category, source, target, availabilityScope, playModeSupport, unavailableReason, requiresPackages, extensionId } = context;
  const available = context.available ?? true;
  return { name, summary, description, category, source, target, availabilityScope, playModeSupport, available, ...(!available && unavailableReason ? { unavailableReason } : {}), ...(requiresPackages?.length ? { requiresPackages } : {}), ...(extensionId ? { extensionId } : {}) };
}

function parseLiveDescriptors(values: unknown[], requestId: string): { ok: true; contexts: ContextDescriptor[] } | (CliResult & { ok: false }) {
  const contexts: ContextDescriptor[] = [];
  for (let index = 0; index < values.length; index += 1) {
    const parsed = parseLiveDescriptor(values[index]);
    if (typeof parsed === "string") return unhealthy(requestId, `context.list contexts[${index}] ${parsed}`, { index });
    contexts.push(parsed);
  }
  const duplicate = duplicateNames(contexts);
  if (duplicate.length > 0) return unhealthy(requestId, `Duplicate live context registration: ${duplicate.join(", ")}.`, { duplicates: duplicate });
  return { ok: true, contexts };
}

/** Bridge descriptor 不完整时拒绝整个 catalog，禁止补造 schema 或 source。 */
export function parseLiveDescriptor(value: unknown): ContextDescriptor | string {
  if (!value || typeof value !== "object" || Array.isArray(value)) return "must be an object.";
  const raw = value as Record<string, unknown>;
  const requiredStrings = ["name", "description", "category", "source", "target", "availabilityScope", "playModeSupport"] as const;
  for (const field of requiredStrings) if (typeof raw[field] !== "string" || raw[field].length === 0) return `is missing required field ${field}.`;
  if (!isSource(raw.source)) return "has invalid source.";
  if (!isTarget(raw.target)) return "has invalid target.";
  if (!isAvailabilityScope(raw.availabilityScope)) return "has invalid availabilityScope.";
  if (!isPlayModeSupport(raw.playModeSupport)) return "has invalid playModeSupport.";
  if (!isSchema(raw.inputSchema)) return "is missing inputSchema.";
  if (!isSchema(raw.outputSchema)) return "is missing outputSchema.";
  const examples = contextExamplesProp(raw, String(raw.name), raw.inputSchema);
  if (!examples) return "is missing valid examples.";

  const ownership = extensionOwnership(String(raw.name));
  if (raw.source === "extension" && !ownership) return "declares source=extension without a manifest owner.";
  if (raw.source !== "extension" && ownership) return `is claimed by extension ${ownership.id} but declares source=${String(raw.source)}.`;
  const requiresPackages = stringArray(raw.requiresPackages) ?? ownership?.requiresPackages;
  return {
    name: String(raw.name),
    summary: typeof raw.summary === "string" && raw.summary.length > 0 ? raw.summary : String(raw.description),
    description: String(raw.description),
    category: String(raw.category),
    source: raw.source,
    target: raw.target,
    availabilityScope: raw.availabilityScope,
    playModeSupport: raw.playModeSupport,
    inputSchema: raw.inputSchema,
    outputSchema: raw.outputSchema,
    examples,
    ...(typeof raw.internalDescriptorFingerprint === "string" ? { descriptorFingerprint: raw.internalDescriptorFingerprint } : {}),
    ...(requiresPackages?.length ? { requiresPackages } : {}),
    ...(ownership ? { extensionId: ownership.id } : {}),
    ...(typeof raw.available === "boolean" ? { available: raw.available } : {}),
    ...(typeof raw.unavailableReason === "string" ? { unavailableReason: raw.unavailableReason } : {})
  };
}

/** 同时归一 Bridge authoring input 与 static catalog 的 argv-only 公开示例。 */
function contextExamplesProp(record: Record<string, unknown>, name: string, inputSchema: Record<string, unknown>): ContextDescriptor["examples"] | undefined {
  const value = record.examples;
  if (!Array.isArray(value) || value.length === 0) return undefined;
  const examples = value.flatMap((item) => {
    if (!item || typeof item !== "object" || Array.isArray(item)) return [];
    const example = item as Record<string, unknown>;
    if (typeof example.description !== "string" || example.description.length === 0) return [];

    if (Array.isArray(example.argv) && example.argv.every((token) => typeof token === "string")) {
      const argv = example.argv as string[];
      if (argv[0] !== "context" || argv[1] !== "read" || argv[2] !== name) return [];
      const inputIndex = argv.indexOf("--input");
      try {
        const input = inputIndex >= 0 ? JSON.parse(argv[inputIndex + 1] ?? "") : {};
        if (!isExampleInput(input) || validateSchema(inputSchema, input).length > 0) return [];
      } catch {
        return [];
      }
      return [{ description: example.description, argv: [...argv] }];
    }

    if (!isExampleInput(example.input) || validateSchema(inputSchema, example.input).length > 0) return [];
    return [{ description: example.description, argv: ["context", "read", name, "--input", JSON.stringify(example.input)] }];
  });
  return examples.length === value.length ? examples : undefined;
}

function extensionOwnership(name: string): { id: string; requiresPackages: string[] } | undefined {
  const registry = new ExtensionRegistry();
  const owners = registry.extensionIds().filter((id) => registry.getExtension(id)?.manifest.contributions.contexts?.some((context) => context.key === name));
  if (owners.length !== 1) return undefined;
  const manifest = registry.getExtension(owners[0])!.manifest;
  return { id: owners[0], requiresPackages: manifest.unityPackages.map((pkg) => pkg.packageId) };
}

function unhealthy(requestId: string, message: string, details?: Record<string, unknown>): CliResult & { ok: false } {
  return failure(requestId, "E_CONTEXT_REGISTRY_UNHEALTHY", message, ["Fix the live context descriptor registry, then retry context discovery."], details) as CliResult & { ok: false };
}

function duplicateNames(contexts: ContextDescriptor[]): string[] {
  const counts = new Map<string, number>();
  for (const context of contexts) counts.set(context.name, (counts.get(context.name) ?? 0) + 1);
  return [...counts].filter(([, count]) => count > 1).map(([name]) => name).sort();
}

function isSchema(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === "object" && !Array.isArray(value) && Object.keys(value).length > 0;
}

/** 示例输入允许空 Request object；是否可调用由对应 inputSchema 决定。 */
function isExampleInput(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === "object" && !Array.isArray(value);
}
function isSource(value: unknown): value is ContextSource { return value === "built-in" || value === "extension" || value === "project"; }
function isTarget(value: unknown): value is ContextDescriptor["target"] { return value === "editor" || value === "runtime" || value === "both"; }
function isAvailabilityScope(value: unknown): value is ContextDescriptor["availabilityScope"] { return value === "editorSession" || value === "playModeSession" || value === "always"; }
function isPlayModeSupport(value: unknown): value is ContextDescriptor["playModeSupport"] { return value === "supported" || value === "runtime-only" || value === "session-affecting" || value === "editor-only" || value === "unknown"; }
function stringArray(value: unknown): string[] | undefined { return Array.isArray(value) && value.every((item) => typeof item === "string") ? value : undefined; }
