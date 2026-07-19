/**
 * Tool metadata index for TS-local tools.
 *
 * 设计原则(2026-07-08 修正):
 * - 无 bridge = 不显示 C# 工具
 * - TS 本地工具(metadata 真源 = defineTool registry)离线可发现/describe/search
 * - C# built-in / extension / project 工具 descriptor 运行时从 bridge 取
 * - 不再保留任何 C# 工具的 TS 手写重复 metadata
 */

import { bridgeCommandRegistry } from "../../bridge/index.js";
import { validateBridgeRegistryContracts, validateToolMetadataContracts } from "../../contracts/command-contract.js";
import { scoreSearch } from "../../util.js";
import "../.generated-registry.js";
import { defineToolMetadata } from "../define.js";
import { builtinExtensionGroupNames } from "../../extensions/tool-manifest.js";
import type { PlayModeSupport, ResolvedToolMetadata, ToolMetadata, ToolSource } from "./types.js";
import { inferToolRiskLevel, isLongRunningTool, toolAvailabilityScope, toolCliCommand, toolSource } from "./derived.js";
import { supportStatusForTool, toolHandler } from "./status.js";

export { supportStatusForTool, toolHandler } from "./status.js";
export { inferToolRiskLevel, isLongRunningTool, toolAvailabilityScope, toolCancellation, toolCliCommand, toolDocsTopic, toolIsMutation, toolSource } from "./derived.js";
export type { PlayModeSupport, ResolvedToolMetadata, ToolCancellation, ToolCompletionContract, ToolExample, ToolHandler, ToolMetadata, ToolSource, ToolRiskLevel, ToolSupportStatus } from "./types.js";

/** Extension groups are discovered from extension manifests, not TS tool metadata. */
export const EXTENSION_GROUPS = new Set(builtinExtensionGroupNames());

/** Normalize a ToolMetadata entry to include generated source and Play Mode defaults. */
export function normalizeToolMetadata(tool: ToolMetadata, source: ToolSource = "built-in"): ResolvedToolMetadata {
  const longRunning = isLongRunningTool(tool);
  const normalized: ResolvedToolMetadata = {
    ...tool,
    source: toolSource(tool, source),
    handler: toolHandler(tool),
    playModeSupport: tool.playModeSupport ?? inferPlayModeSupport(tool),
    supportStatus: supportStatusForTool(tool),
    cliCommand: toolCliCommand(tool),
    availabilityScope: toolAvailabilityScope(tool),
    riskLevel: inferToolRiskLevel(tool),
    ...(longRunning ? { longRunning: true } : {})
  };
  return normalized;
}

/** 根据执行目标和副作用推导默认 Play Mode 语义。 */
export function inferPlayModeSupport(tool: ToolMetadata): PlayModeSupport {
  const handler = toolHandler(tool);
  if (tool.target === "runtime" || handler === "bridge-runtime") return "runtime-only";
  if (handler === "job" || handler === "offline") return "editor-only";
  if (isLongRunningTool(tool) || tool.sideEffects?.some((effect) => ["asset-import", "file-write", "package-modify"].includes(effect))) return "session-affecting";
  if (tool.destructiveHint || tool.supportsDryRun) return "runtime-only";
  return "supported";
}

/** TS 本地工具(metadata 真源 = defineTool registry)。无 bridge 时仅这些工具可见。 */
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

/** Find a single tool by its dotted name (TS-local only, offline). C# tools require live discovery. */
export function findToolByName(name: string): ResolvedToolMetadata | undefined {
  return allRegisteredToolMetadata().find((t) => t.name === name);
}

/** Filter tools by group (TS-local only, offline). C# tools require live discovery. */
export function filterToolsByGroup(group: string): ResolvedToolMetadata[] {
  return allRegisteredToolMetadata().filter((t) => t.group === group);
}

/** Search tools by keyword across name, summary, description, group (TS-local only, offline). */
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
      const cliCommand = toolCliCommand(tool);
      const match = /^tools run (.+)$/.exec(cliCommand);
      if (match) {
        const toolName = match[1];
        const spec = bridgeCommandRegistry.specs().find((s) => s.toolName === toolName || s.bridgeCommand === toolName);
        if (!spec) {
          throw new Error(`Bridge tool metadata mismatch: ${tool.name} uses cliCommand="${cliCommand}" but no bridge spec resolves toolName/bridgeCommand="${toolName}"`);
        }
        const resolved = spec.toolName ?? spec.bridgeCommand;
        if (resolved !== tool.name) {
          throw new Error(`Bridge tool metadata mismatch: ${tool.name} uses cliCommand="${cliCommand}" but bridge registry resolves ${resolved}`);
        }
      }
    }
  }

  if (duplicateNames.size > 0) {
    throw new Error(`Duplicate built-in tool metadata names: ${[...duplicateNames].sort().join(", ")}`);
  }
}
