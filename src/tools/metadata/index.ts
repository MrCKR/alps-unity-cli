/**
 * Static built-in tool metadata for the Unity CLI.
 *
 * Covers every supported or declared CLI command, including live bridge, batch/job,
 * CLI-only and planned command contract entries. This data is shipped with the
 * CLI/UnityPkg release so that `tools describe` works without a live Unity instance.
 *
 * The schema is /schemas/tool-metadata.schema.json (ALP-164 frozen v1.0).
 */

import { bridgeCommandRegistry } from "../../bridge/index.js";
import { validateBridgeRegistryContracts, validateToolMetadataContracts } from "../../contracts/command-contract.js";
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
import { PROBUILDER_TOOLS } from "./groups/probuilder.js";
import { SHADER_TOOLS } from "./groups/shader.js";
import { TEXTURE_TOOLS } from "./groups/texture.js";
import { ANIMATION_TOOLS } from "./groups/animation.js";
import { VFX_TOOLS } from "./groups/vfx.js";
import { UI_TOOLS } from "./groups/ui.js";
import { SCRIPT_TOOLS } from "./groups/script.js";
import { EDITOR_TOOLS } from "./groups/editor.js";
import { DIAGNOSE_TOOLS } from "./groups/diagnose.js";
import { SCREENSHOT_TOOLS } from "./groups/screenshot.js";
import { CODE_TOOLS } from "./groups/code.js";
import { CLI_ONLY_TOOLS } from "./groups/cli-only.js";
import { TOOLS_DISCOVERY_TOOLS } from "./groups/tools.js";
import type { ToolMetadata, ToolSource } from "./types.js";

export { supportStatusDescription, supportStatusForTool, supportStatusLabel } from "./status.js";
export type { ToolExample, ToolHandler, ToolMetadata, ToolSource, ToolRiskLevel, ToolSupportStatus } from "./types.js";

/** Normalize a ToolMetadata entry to include P1 defaults for kind/source/category. */
export function normalizeToolMetadata(tool: ToolMetadata): ToolMetadata & { kind: "tool"; source: ToolSource; category: string } {
  return {
    ...tool,
    kind: tool.kind ?? "tool",
    source: tool.source ?? "built-in",
    category: tool.category ?? tool.group,
  };
}

const BUILTIN_TOOLS: ToolMetadata[] = [
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
  ...PROBUILDER_TOOLS,
  ...SHADER_TOOLS,
  ...TEXTURE_TOOLS,
  ...ANIMATION_TOOLS,
  ...VFX_TOOLS,
  ...UI_TOOLS,
  ...SCRIPT_TOOLS,
  ...EDITOR_TOOLS,
  ...DIAGNOSE_TOOLS,
  ...SCREENSHOT_TOOLS,
  ...CODE_TOOLS,
  ...CLI_ONLY_TOOLS,
  ...TOOLS_DISCOVERY_TOOLS,
];

validateBridgeRegistryContracts(bridgeCommandRegistry);
validateBuiltinTools(BUILTIN_TOOLS);
validateToolMetadataContracts(BUILTIN_TOOLS, bridgeCommandRegistry);

/** Return all built-in tool metadata entries, normalized with P1 defaults. */
export function builtinToolMetadata(): ToolMetadata[] {
  return BUILTIN_TOOLS.map(normalizeToolMetadata);
}

/** Return distinct group names from built-in metadata (uses category when set). */
export function builtinToolGroups(): string[] {
  const groups = new Set<string>();
  for (const tool of BUILTIN_TOOLS) {
    groups.add(tool.category ?? tool.group);
  }
  return [...groups].sort();
}

/** Find a single tool by its dotted name. */
export function findToolByName(name: string): ToolMetadata | undefined {
  const tool = BUILTIN_TOOLS.find((t) => t.name === name);
  return tool ? normalizeToolMetadata(tool) : undefined;
}

/** Filter tools by group (matches both group and category). */
export function filterToolsByGroup(group: string): ToolMetadata[] {
  return BUILTIN_TOOLS.filter((t) => t.group === group || t.category === group).map(normalizeToolMetadata);
}

/** Search tools by keyword across name, title, description, group. */
export function searchTools(keyword: string): ToolMetadata[] {
  const lower = keyword.toLowerCase();
  return BUILTIN_TOOLS.filter((t) =>
    t.name.toLowerCase().includes(lower) ||
    t.title.toLowerCase().includes(lower) ||
    t.description.toLowerCase().includes(lower) ||
    t.group.toLowerCase().includes(lower) ||
    (t.category ?? "").toLowerCase().includes(lower)
  ).sort((a, b) => {
    const aScore = scoreSearch(a.name.toLowerCase(), lower) + scoreSearch(a.title.toLowerCase(), lower);
    const bScore = scoreSearch(b.name.toLowerCase(), lower) + scoreSearch(b.title.toLowerCase(), lower);
    return bScore - aScore;
  });
}

function scoreSearch(text: string, query: string): number {
  if (text === query) return 8;
  if (text.startsWith(query)) return 4;
  if (text.includes(query)) return 2;
  return 0;
}

function validateBuiltinTools(tools: ToolMetadata[]): void {
  const names = new Set<string>();
  const duplicateNames = new Set<string>();

  for (const tool of tools) {
    if (names.has(tool.name)) {
      duplicateNames.add(tool.name);
    }
    names.add(tool.name);

    if (tool.handler === "bridge" && tool.supportStatus !== "planned" && tool.cliCommand) {
      const [group, ...commandParts] = tool.cliCommand.split(" ");
      const command = commandParts.join(" ");
      const spec = bridgeCommandRegistry.lookup(group, command);
      if (!spec || spec.bridgeCommand !== tool.name) {
        throw new Error(`Bridge tool metadata mismatch: ${tool.name} uses cliCommand="${tool.cliCommand}" but bridge registry resolves ${spec?.bridgeCommand ?? "<none>"}`);
      }
    }
  }

  if (duplicateNames.size > 0) {
    throw new Error(`Duplicate built-in tool metadata names: ${[...duplicateNames].sort().join(", ")}`);
  }
}
