import { getBoolean, getString } from "./args.js";
import type { BridgeRuntime } from "./bridge/index.js";
import { builtinContextDescriptors, loadContextCatalog, parseLiveDescriptor, type ContextCatalogRuntime } from "./contexts/catalog.js";
import type { ContextDescriptor } from "./contexts/types.js";
import type { CliResult } from "./types.js";

export type { ContextDescriptor } from "./contexts/types.js";
export type ProjectContextRuntime = ContextCatalogRuntime & Partial<BridgeRuntime>;

export type ProjectContextDiscovery =
  | { ok: true; contexts: ContextDescriptor[]; mode: "runtime" | "bridge" | "skipped"; builtInCount: number; projectCustomCount: number }
  | (CliResult & { ok: false });

/** 保留同步目标探测 API；实际 descriptor 加载统一交给 plural catalog。 */
export function shouldDiscoverContexts(flags: Record<string, string | boolean>, runtime: ProjectContextRuntime): boolean {
  return getBoolean(flags, "include-custom") ||
    runtime.projectContexts !== undefined ||
    runtime.bridgeExecute !== undefined ||
    typeof getString(flags, "bridge-endpoint") === "string" ||
    typeof getString(flags, "target") === "string" ||
    typeof getString(flags, "instance") === "string" ||
    typeof runtime.bridgeEndpoint === "string" ||
    typeof process.env.UNITY_CLI_BRIDGE_ENDPOINT === "string";
}

/** 返回诊断所需计数，descriptor 真源与校验只有 catalog 一套。 */
export async function discoverProjectContexts(
  flags: Record<string, string | boolean>,
  runtime: ProjectContextRuntime,
  requestId: string,
  options: { requireBridge?: boolean } = {}
): Promise<ProjectContextDiscovery> {
  if (!options.requireBridge && !shouldDiscoverContexts(flags, runtime)) {
    return { ok: true, contexts: [], mode: "skipped", builtInCount: 0, projectCustomCount: 0 };
  }
  const catalog = await loadContextCatalog(flags, runtime, requestId, { requireLive: options.requireBridge });
  if (catalog.ok === false) return catalog;
  const localNames = new Set(builtinContextDescriptors().map((context) => context.name));
  const contexts = catalog.contexts.filter((context) => !localNames.has(context.name));
  return {
    ok: true,
    contexts,
    mode: runtime.projectContexts ? "runtime" : catalog.live ? "bridge" : "skipped",
    builtInCount: contexts.filter((context) => context.source === "built-in").length,
    projectCustomCount: contexts.filter((context) => context.source === "project").length
  };
}

/** 严格映射 live descriptor；未知 source 与缺失 schema 不再默认成 built-in/空对象。 */
export function rawDescriptorToContextDescriptor(value: unknown): ContextDescriptor | undefined {
  const parsed = parseLiveDescriptor(value);
  return typeof parsed === "string" ? undefined : parsed;
}
