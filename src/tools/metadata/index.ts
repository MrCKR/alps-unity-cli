/**
 * Tool metadata index for TS-local tools.
 *
 * 设计原则:
 * - TS local Tool 的 metadata 真源是 defineTool registry。
 * - C# built-in、Extension 与 Project-defined descriptor 都从选定实例的 live registry 读取。
 * - static catalog 只供生成、契约和离线审计，不进入公开 discovery。
 * - 每项能力只有一个 descriptor owner，不维护 TS 手写重复 metadata。
 */

import { bridgeCommandRegistry } from "../../bridge/index.js";
import { validateBridgeRegistryContracts, validateToolMetadataContracts } from "../../contracts/command-contract.js";
import { scoreSearch } from "../../util.js";
import "../.generated-registry.js";
import { defineToolMetadata } from "../define.js";
import { builtinExtensionGroupNames } from "../../extensions/tool-manifest.js";
import type { PlayModeSupport, ResolvedToolMetadata, ToolMetadata, ToolSource } from "./types.js";
import { inferToolRiskLevel, isLongRunningTool, toolAvailabilityScope, toolPublicRoute, toolSource } from "./derived.js";
import { supportStatusForTool, toolHandler } from "./status.js";

export { supportStatusForTool, toolHandler } from "./status.js";
export { inferToolRiskLevel, isLongRunningTool, toolAvailabilityScope, toolDocsTopic, toolIsMutation, toolPublicRoute, toolSource } from "./derived.js";
export type { PlayModeSupport, ResolvedToolMetadata, ToolCompletionContract, ToolExample, ToolHandler, ToolMetadata, ToolSource, ToolRiskLevel, ToolSupportStatus } from "./types.js";

/** Extension groups are discovered from extension manifests, not TS tool metadata. */
export const EXTENSION_GROUPS = new Set(builtinExtensionGroupNames());

/** Normalize a ToolMetadata entry to include generated source and Play Mode defaults. */
export function normalizeToolMetadata(tool: ToolMetadata, source: ToolSource = "built-in"): ResolvedToolMetadata {
  const normalized: ResolvedToolMetadata = {
    ...tool,
    source: toolSource(tool, source),
    handler: toolHandler(tool),
    playModeSupport: tool.playModeSupport ?? inferPlayModeSupport(tool),
    availabilityScope: toolAvailabilityScope(tool),
    riskLevel: inferToolRiskLevel(tool),
  };
  return normalized;
}

/** 根据执行目标和副作用推导默认 Play Mode 语义。 */
export function inferPlayModeSupport(tool: ToolMetadata): PlayModeSupport {
  const handler = toolHandler(tool);
  if (tool.target === "runtime" || handler === "bridge-runtime") return "runtime-only";
  if (handler === "offline") return "editor-only";
  if (isLongRunningTool(tool) || tool.sideEffects?.some((effect) => ["asset-import", "file-write", "package-modify"].includes(effect))) return "session-affecting";
  if (tool.destructiveHint || tool.supportsDryRun) return "runtime-only";
  return "supported";
}

/** TS 本地工具(metadata 真源 = defineTool registry)；公开 discovery 仍要求绑定实例。 */
function localTsTools(): ToolMetadata[] {
  return defineToolMetadata();
}

// Validate bridge registry once at module level. TS 本地工具 metadata 在运行时从 defineTool registry 取。
if (process.env.UNITY_CLI_GENERATING_CONTRACT !== "1") {
  validateBridgeRegistryContracts(bridgeCommandRegistry, EXTENSION_GROUPS);
}

/** Return TS-local tool metadata only. C# built-in/extension/project tools require a live bridge. */
export function builtinToolMetadata(): ResolvedToolMetadata[] {
  const tools = localTsTools();
  validateBuiltinTools(tools, false);
  return tools.map((tool) => normalizeToolMetadata(tool, tool.source ?? "built-in"));
}

/** Return all locally registered metadata entries (TS-local only). */
export function allRegisteredToolMetadata(): ResolvedToolMetadata[] {
  return builtinToolMetadata();
}

/** Return distinct TS-local group names only. C# groups require a live bridge. */
export function builtinToolGroups(): string[] {
  const groups = new Set<string>();
  for (const tool of localTsTools()) {
    groups.add(tool.group);
  }
  return [...groups].sort();
}

/** Return distinct group names from locally visible metadata (TS-local only). */
export function allRegisteredToolGroups(): string[] {
  const groups = new Set<string>();
  for (const tool of allRegisteredToolMetadata()) {
    groups.add(tool.group);
  }
  return [...groups].sort();
}

/** Find a single TS-local Tool by dotted name；仅供生成、契约与内部测试。 */
export function findToolByName(name: string): ResolvedToolMetadata | undefined {
  return allRegisteredToolMetadata().find((t) => t.name === name);
}

/** Filter TS-local Tools by group；仅供生成、契约与内部测试。 */
export function filterToolsByGroup(group: string): ResolvedToolMetadata[] {
  return allRegisteredToolMetadata().filter((t) => t.group === group);
}

/** Search TS-local Tools；仅供生成、契约与内部测试。 */
export function searchTools(keyword: string): ResolvedToolMetadata[] {
  const lower = keyword.toLowerCase();
  return allRegisteredToolMetadata().filter((t) =>
    t.name.toLowerCase().includes(lower) ||
    t.summary.toLowerCase().includes(lower) ||
    t.description.toLowerCase().includes(lower) ||
    t.group.toLowerCase().includes(lower)
  ).sort((a, b) => {
    const aScore = scoreSearch(a.name.toLowerCase(), lower) + scoreSearch(a.summary.toLowerCase(), lower);
    const bScore = scoreSearch(b.name.toLowerCase(), lower) + scoreSearch(b.summary.toLowerCase(), lower);
    return bScore - aScore;
  });
}

function validateBuiltinTools(tools: ToolMetadata[], skipCliCommandBridgeCheck: boolean): void {
  const names = new Set<string>();
  const duplicateNames = new Set<string>();

  for (const tool of tools) {
    if (names.has(tool.name)) {
      duplicateNames.add(tool.name);
    }
    names.add(tool.name);

    if (!skipCliCommandBridgeCheck && toolHandler(tool) === "bridge" && tool.supportStatus !== "planned") {
      const publicRoute = toolPublicRoute(tool);
      const match = /^tools run (.+)$/.exec(publicRoute);
      if (match) {
        const toolName = match[1];
        const spec = bridgeCommandRegistry.specs().find((s) => s.toolName === toolName || s.bridgeCommand === toolName);
        if (!spec) {
          throw new Error(`Bridge tool metadata mismatch: ${tool.name} uses public route "${publicRoute}" but no bridge spec resolves toolName/bridgeCommand="${toolName}"`);
        }
        const resolved = spec.toolName ?? spec.bridgeCommand;
        if (resolved !== tool.name) {
          throw new Error(`Bridge tool metadata mismatch: ${tool.name} uses public route "${publicRoute}" but bridge registry resolves ${resolved}`);
        }
      }
    }
  }

  if (duplicateNames.size > 0) {
    throw new Error(`Duplicate built-in tool metadata names: ${[...duplicateNames].sort().join(", ")}`);
  }
}
