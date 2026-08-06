import type { BridgeRuntime } from "./bridge/index.js";
import { loadContextCatalog, parseLiveDescriptor, type ContextCatalogRuntime } from "./contexts/catalog.js";
import type { ContextDescriptor } from "./contexts/types.js";
import type { CliResult } from "./types.js";

export type { ContextDescriptor } from "./contexts/types.js";
export type ProjectContextRuntime = ContextCatalogRuntime & Partial<BridgeRuntime>;

export type ProjectContextDiscovery =
  | { ok: true; contexts: ContextDescriptor[]; mode: "runtime" | "bridge"; builtInCount: number; projectCustomCount: number }
  | (CliResult & { ok: false });

/** 返回诊断所需计数，descriptor 真源与校验只有 catalog 一套。 */
export async function discoverProjectContexts(
  flags: Record<string, string | boolean>,
  runtime: ProjectContextRuntime,
  requestId: string,
  _options: { requireBridge?: boolean } = {}
): Promise<ProjectContextDiscovery> {
  const catalog = await loadContextCatalog(flags, runtime, requestId);
  if (catalog.ok === false) return catalog;
  const contexts = catalog.liveContexts;
  return {
    ok: true,
    contexts,
    mode: runtime.projectContexts ? "runtime" : "bridge",
    builtInCount: contexts.filter((context) => context.source === "built-in").length,
    projectCustomCount: contexts.filter((context) => context.source === "project").length
  };
}

/** 严格映射 live descriptor；未知 source 与缺失 schema 不再默认成 built-in/空对象。 */
export function rawDescriptorToContextDescriptor(value: unknown): ContextDescriptor | undefined {
  const parsed = parseLiveDescriptor(value);
  return typeof parsed === "string" ? undefined : parsed;
}
