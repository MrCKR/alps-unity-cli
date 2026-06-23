/**
 * Static built-in tool metadata for the Unity CLI.
 *
 * Covers every supported or declared CLI command, including live bridge, batch/job,
 * CLI-only and planned command contract entries. This data is shipped with the
 * CLI/UnityPkg release so that `tools describe` works without a live Unity instance.
 *
 * The schema is /schemas/tool-metadata.schema.json v1.0.
 *
 * ProBuilder, Cinemachine, VFX, URP, and HDRP tool metadata is not statically
 * aggregated here. These optional extensions register their tools through the
 * extension contribution loader instead.
 */

import { bridgeCommandRegistry } from "../../bridge/index.js";
import { validateBridgeRegistryContracts, validateToolMetadataContracts } from "../../contracts/command-contract.js";
import { scoreSearch } from "../../util.js";
import { SCENE_TOOLS } from "./groups/scene.js";
import { CAMERA_TOOLS } from "./groups/camera.js";
import { GO_TOOLS } from "./groups/game-object.js";
import { COMPONENT_TOOLS } from "./groups/game-object.js";
import { ASSET_TOOLS } from "./groups/asset.js";
import { PREFAB_TOOLS } from "./groups/prefab.js";
import { MATERIAL_TOOLS } from "./groups/material.js";
import { GRAPHICS_TOOLS } from "./groups/graphics.js";
import { PHYSICS_TOOLS } from "./groups/physics.js";
import { PROFILER_TOOLS } from "./groups/profiler.js";
import { SHADER_TOOLS } from "./groups/shader.js";
import { TEXTURE_TOOLS } from "./groups/texture.js";
import { ANIMATION_TOOLS } from "./groups/animation.js";
import { UI_TOOLS } from "./groups/ui.js";
import { SCRIPT_TOOLS } from "./groups/script.js";
import { SCRIPTABLE_OBJECT_TOOLS } from "./groups/scriptable-object.js";
import { EDITOR_TOOLS } from "./groups/editor.js";
import { DIAGNOSE_TOOLS } from "./groups/diagnose.js";
import { SCREENSHOT_TOOLS } from "./groups/screenshot.js";
import { CODE_TOOLS } from "./groups/code.js";
import { BUILD_TOOLS } from "./groups/build.js";
import { CONSOLE_TOOLS } from "./groups/console.js";
import { PACKAGE_TOOLS } from "./groups/package.js";
import { OBJECT_TOOLS } from "./groups/object.js";
import { REFLECTION_TOOLS } from "./groups/reflection.js";
import { CLI_ONLY_TOOLS } from "./groups/cli-only.js";
import { TOOLS_DISCOVERY_TOOLS } from "./groups/tools.js";
import type { ToolMetadata, ToolSource } from "./types.js";

export { supportStatusDescription, supportStatusForTool, supportStatusLabel } from "./status.js";
export type { ToolExample, ToolHandler, ToolMetadata, ToolSource, ToolRiskLevel, ToolSupportStatus } from "./types.js";

/** Extension groups that register through the contribution loader, not core aggregation. */
export const EXTENSION_GROUPS = new Set(["probuilder", "cinemachine", "vfx", "urp", "hdrp"]);

/** Normalize a ToolMetadata entry to include default kind/source values. */
export function normalizeToolMetadata(tool: ToolMetadata): ToolMetadata & { kind: "tool"; source: ToolSource } {
  return {
    ...tool,
    kind: tool.kind ?? "tool",
    source: tool.source ?? "built-in",
  };
}

/**
 * Core built-in tools — no extension groups.
 * Extension tools are registered separately via registerExtensionTools().
 */
const CORE_TOOLS: ToolMetadata[] = [
  ...SCENE_TOOLS,
  ...CAMERA_TOOLS,
  ...GO_TOOLS,
  ...COMPONENT_TOOLS,
  ...ASSET_TOOLS,
  ...PREFAB_TOOLS,
  ...MATERIAL_TOOLS,
  ...GRAPHICS_TOOLS,
  ...PHYSICS_TOOLS,
  ...PROFILER_TOOLS,
  ...SHADER_TOOLS,
  ...TEXTURE_TOOLS,
  ...ANIMATION_TOOLS,
  ...UI_TOOLS,
  ...SCRIPT_TOOLS,
  ...SCRIPTABLE_OBJECT_TOOLS,
  ...EDITOR_TOOLS,
  ...DIAGNOSE_TOOLS,
  ...SCREENSHOT_TOOLS,
  ...CODE_TOOLS,
  ...CONSOLE_TOOLS,
  ...PACKAGE_TOOLS,
  ...BUILD_TOOLS,
  ...OBJECT_TOOLS,
  ...REFLECTION_TOOLS,
  ...CLI_ONLY_TOOLS,
  ...TOOLS_DISCOVERY_TOOLS,
];

/**
 * Extension tool metadata — populated by the extension contribution loader.
 * ProBuilder, Cinemachine, VFX, URP, HDRP and other optional extensions register
 * their tools here rather than being statically aggregated in CORE_TOOLS.
 */
const extensionTools: ToolMetadata[] = [];

// Validate core tools at module level (no extension groups yet).
// 生成 command-contract.json 时需要先加载 metadata,因此跳过契约反向校验以避免循环依赖。
if (process.env.UNITY_CLI_GENERATING_CONTRACT !== "1") {
  validateBridgeRegistryContracts(bridgeCommandRegistry, EXTENSION_GROUPS);
  validateToolMetadataContracts(CORE_TOOLS, bridgeCommandRegistry);
}
validateBuiltinTools(CORE_TOOLS, false);

/** Register extension tool metadata. Called by the contribution loader. */
export function registerExtensionTools(tools: ToolMetadata[]): void {
  const existingNames = new Set([...CORE_TOOLS, ...extensionTools].map((t) => t.name));
  for (const tool of tools) {
    if (existingNames.has(tool.name)) {
      throw new Error(`Duplicate extension tool metadata name: ${tool.name}`);
    }
    existingNames.add(tool.name);
  }
  extensionTools.push(...tools);
}

/** Validate extension tool metadata after registration. */
export function validateExtensionToolMetadata(): void {
  validateBuiltinTools(extensionTools, true);
  if (process.env.UNITY_CLI_GENERATING_CONTRACT !== "1") {
    validateToolMetadataContracts(extensionTools, bridgeCommandRegistry);
  }
}

/** Return core built-in tool metadata entries (no extension tools). */
export function builtinToolMetadata(): ToolMetadata[] {
  return [...CORE_TOOLS];
}

/** Return extension tool metadata entries (registered by extension contributions). */
export function extensionToolMetadata(): ToolMetadata[] {
  return [...extensionTools];
}

/** Return all registered tool metadata entries (core + extension). */
export function allRegisteredToolMetadata(): ToolMetadata[] {
  return [...CORE_TOOLS, ...extensionTools];
}

/** Return distinct core group names from built-in metadata. */
export function builtinToolGroups(): string[] {
  const groups = new Set<string>();
  for (const tool of CORE_TOOLS) {
    groups.add(tool.group);
  }
  return [...groups].sort();
}

/** Return distinct group names including extension groups. */
export function allRegisteredToolGroups(): string[] {
  const groups = new Set<string>();
  for (const tool of allRegisteredToolMetadata()) {
    groups.add(tool.group);
  }
  return [...groups].sort();
}

/** Find a single tool by its dotted name (core + extension). */
export function findToolByName(name: string): ToolMetadata | undefined {
  return allRegisteredToolMetadata().find((t) => t.name === name);
}

/** Filter tools by group (core + extension). */
export function filterToolsByGroup(group: string): ToolMetadata[] {
  return allRegisteredToolMetadata().filter((t) => t.group === group);
}

/** Search tools by keyword across name, title, description, group (core + extension). */
export function searchTools(keyword: string): ToolMetadata[] {
  const lower = keyword.toLowerCase();
  return allRegisteredToolMetadata().filter((t) =>
    t.name.toLowerCase().includes(lower) ||
    t.title.toLowerCase().includes(lower) ||
    t.description.toLowerCase().includes(lower) ||
    t.group.toLowerCase().includes(lower)
  ).sort((a, b) => {
    const aScore = scoreSearch(a.name.toLowerCase(), lower) + scoreSearch(a.title.toLowerCase(), lower);
    const bScore = scoreSearch(b.name.toLowerCase(), lower) + scoreSearch(b.title.toLowerCase(), lower);
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

    if (!skipCliCommandBridgeCheck && tool.handler === "bridge" && tool.supportStatus !== "planned" && tool.cliCommand) {
      // cliCommand 现为 "tools run <name>" 形态(README Project Baseline)。
      // 通过工具名查 bridge spec,验证 bridgeCommand/toolName 与 tool.name 一致。
      const match = /^tools run (.+)$/.exec(tool.cliCommand);
      if (match) {
        const toolName = match[1];
        const spec = bridgeCommandRegistry.specs().find((s) => s.toolName === toolName || s.bridgeCommand === toolName);
        if (!spec) {
          throw new Error(`Bridge tool metadata mismatch: ${tool.name} uses cliCommand="${tool.cliCommand}" but no bridge spec resolves toolName/bridgeCommand="${toolName}"`);
        }
        const resolved = spec.toolName ?? spec.bridgeCommand;
        if (resolved !== tool.name) {
          throw new Error(`Bridge tool metadata mismatch: ${tool.name} uses cliCommand="${tool.cliCommand}" but bridge registry resolves ${resolved}`);
        }
      }
    }
  }

  if (duplicateNames.size > 0) {
    throw new Error(`Duplicate built-in tool metadata names: ${[...duplicateNames].sort().join(", ")}`);
  }
}
