/**
 * CLI sub-commands: tools group, tools group list, tools search, tools describe, tools run.
 *
 * P1 group-first discovery model:
 *   tools group               → list tool groups with counts
 *   tools group list <group>  → list tools in a group (short summary)
 *   tools describe <name>     → full metadata for a tool
 *   tools run <name>          → execute a tool
 *   tools search <keyword>    → auxiliary search across tools
 *
 * Old `tools list` (full flat enumeration) is REMOVED.
 * Calling `tools list` or bare `tools` returns a guidance error pointing to group-first.
 */

import { getBoolean, getString } from "./args.js";
import { discoverProjectTools, discoverRuntimeTools, shouldDiscoverProjectTools, type ProjectToolRuntime } from "./project-tool-discovery.js";
import { executeBridgeCommand, executeBridgePayload } from "./bridge/index.js";import { checkToolExtensionAvailability, collectExtensionToolDescriptors } from "./extension-availability.js";
import {
  builtinToolMetadata,
  builtinToolGroups,
  findToolByName,
  filterToolsByGroup,
  searchTools,
  type ToolMetadata
} from "./tools/metadata/index.js";
import { failure, success } from "./output.js";
import { instanceSelectionFailure, selectInstanceForFlags, targetSummary } from "./instances.js";
import { supportStatusDescription, supportStatusForTool, supportStatusLabel } from "./tools/metadata/status.js";
import type { CliResult } from "./types.js";
import { handleBuild } from "./cli-commands/build.js";
import { handleEditor } from "./cli-commands/editor.js";
import { handleTest } from "./cli-commands/test.js";
import type { Runtime } from "./cli-commands/types.js";

export type ToolsRuntime = ProjectToolRuntime;

export async function handleToolsCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  // Old `tools list` is REMOVED. Both bare `tools` and `tools list` return
  // a guidance error pointing to the new group-first discovery path.
  if (!command || command === "list") {
    return handleToolsListRemoved(requestId);
  }

  if (command === "run") {
    return handleToolsRun(positional, flags, runtime, requestId);
  }

  // `tools group` lists all groups; `tools group list <group>` lists tools in a group.
  if (command === "group") {
    const subCommand = positional[0];
    if (subCommand === "list") {
      return handleToolsGroupList(positional.slice(1), flags, runtime, requestId);
    }
    return handleToolsGroup(flags, runtime, requestId);
  }

  // `tools groups` is a removed legacy alias — treat as unknown command.
  if (command === "groups") {
    return handleToolsListRemoved(requestId);
  }

  if (command === "search") {
    const keyword = positional[0] ?? getString(flags, "keyword") ?? getString(flags, "query");
    return handleToolsSearch(keyword, flags, runtime, requestId);
  }

  if (command === "describe") {
    const toolName = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
    return handleToolsDescribe(toolName, flags, runtime, requestId);
  }

  return failure(requestId, "UNKNOWN_TOOLS_COMMAND", "Expected tools group, tools group list <group>, tools describe <tool>, tools run <tool>, or tools search <keyword>.", [
    "Run unity_cli tools group to see tool categories.",
    "Run unity_cli tools group list <group> to see tools in a category.",
    "Run unity_cli tools describe <tool> for full metadata.",
    "Run unity_cli tools run <tool> to execute a tool."
  ]);
}

/**
 * Old `tools list` is REMOVED in P1. Return a guidance error
 * pointing to the new group-first discovery path.
 */
function handleToolsListRemoved(requestId: string): CliResult {
  return failure(requestId, "E_TOOLS_LIST_REMOVED", "The 'tools list' command has been removed. Use group-first discovery instead.", [
    "Run unity_cli tools group to list all tool groups.",
    "Run unity_cli tools group list <group> to list tools in a group.",
    "Run unity_cli tools describe <tool> for full metadata.",
    "Run unity_cli tools search <keyword> to search across all tools."
  ]);
}

/**
 * `tools group` — list all tool groups with short descriptions and tool counts.
 * This is the primary discovery entry point.
 */
async function handleToolsGroup(
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const builtinGroups = builtinToolGroups();
  const includeCustom = shouldDiscoverProjectTools(flags, runtime);
  const customDiscovery = includeCustom ? await discoverProjectTools(flags, runtime, requestId) : { ok: true as const, tools: [] as ToolMetadata[], mode: "skipped" as const };
  if (customDiscovery.ok === false) return customDiscovery;

  // Build group info with counts and descriptions
  const allTools = [...builtinToolMetadata(), ...customDiscovery.tools];
  const groupCounts: Record<string, number> = {};
  const groupDescriptions: Record<string, string> = {
    scene: "Scene management: active scene, hierarchy, build settings, screenshots, create, load, save.",
    camera: "Camera inspection: list, info, screenshots.",
    "game-object": "GameObject operations: find, inspect, create, modify, delete, components.",
    asset: "Asset management: search, info, create, import, modify, delete, duplicate, move, rename.",
    prefab: "Prefab operations: info, hierarchy, create, modify, stage open/save/close.",
    material: "Material management: info, create, assign, set properties.",
    graphics: "Graphics settings: quality, anti-aliasing, pipeline, volume.",
    physics: "Physics operations: settings, raycast, simulation.",
    profiler: "Unity Profiler: status, start, stop, capture, export.",
    probuilder: "ProBuilder 3D modeling: shapes, CSG operations, mesh editing (requires com.unity.probuilder).",
    shader: "Shader management: info, read, create, update.",
    texture: "Texture management: info, create, sprite, import settings.",
    animation: "Animation control: clips, states, play, stop.",
    vfx: "Visual effects: VFX Graph and particle systems (requires com.unity.visualeffectgraph).",
    ui: "UI Toolkit and uGUI: canvas, text, button, image, document.",
    script: "Script management: create, read, edit, validate.",
    editor: "Editor control: console, state, play/pause/stop, menu.",
    diagnose: "Diagnostics: preflight checks.",
    screenshot: "Screenshot capture.",
    code: "Code execution: exec C# code snippets.",
    "cli-only": "CLI-only commands: init, package, batch, docs, reflect, test, build, job management."
  };

  const seen = new Set<string>();
  for (const tool of allTools) {
    const group = tool.category ?? tool.group;
    if (!seen.has(group)) {
      seen.add(group);
    }
    groupCounts[group] = (groupCounts[group] ?? 0) + 1;
  }

  const groups = [...seen].sort().map((group) => ({
    name: group,
    title: groupDescriptions[group] ?? `Tools in the ${group} domain.`,
    toolCount: groupCounts[group] ?? 0,
    source: builtinGroups.includes(group) ? "core" : "extension",
    cliCommand: `tools group list ${group}`
  }));

  const data: Record<string, unknown> = {
    command: "tools.group",
    count: groups.length,
    groups,
    nextActions: [
      "Run unity_cli tools group list <group> to see tools in a group.",
      "Run unity_cli tools describe <tool> for full metadata."
    ]
  };

  return success(requestId, data);
}

/**
 * `tools group list <group>` — list tools in a specific group with short summaries.
 * Only shows name, title, readOnly/destructive hints, source, availability.
 * Does NOT expand full schema.
 */
async function handleToolsGroupList(
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const groupName = positional[0] ?? getString(flags, "group");
  if (!groupName) {
    return failure(requestId, "TOOLS_GROUP_LIST_NAME_REQUIRED", "Pass a group name: unity_cli tools group list <group>.", [
      "Run unity_cli tools group to see all tool groups."
    ]);
  }

  const builtinTools = filterToolsByGroup(groupName);
  const includeCustom = shouldDiscoverProjectTools(flags, runtime);
  const customDiscovery = includeCustom ? await discoverProjectTools(flags, runtime, requestId) : { ok: true as const, tools: [] as ToolMetadata[], mode: "skipped" as const };
  if (customDiscovery.ok === false) return customDiscovery;
  const customTools = customDiscovery.tools.filter((t: ToolMetadata) => (t.category ?? t.group) === groupName);

  const allTools = [...builtinTools, ...customTools];
  if (allTools.length === 0) {
    return failure(requestId, "E_TOOL_GROUP_NOT_FOUND", `Tool group not found: ${groupName}.`, [
      "Run unity_cli tools group to see all tool groups."
    ]);
  }

  const summaries = allTools.map((t) => {
    const supportStatus = supportStatusForTool(t);
    return {
      name: t.name,
      title: t.title,
      source: t.source ?? (customTools.includes(t) ? "project" : "built-in"),
      readOnlyHint: t.readOnlyHint,
      destructiveHint: t.destructiveHint,
      supportStatus,
      supportStatusLabel: supportStatusLabel(supportStatus),
      target: t.target ?? "editor",
      requiresPackages: t.requiresPackages,
      cliCommand: t.cliCommand ?? `tools run ${t.name}`
    };
  });

  const data: Record<string, unknown> = {
    command: "tools.group.list",
    group: groupName,
    count: summaries.length,
    tools: summaries,
    nextActions: [
      "Run unity_cli tools describe <tool> for full metadata including schema.",
      "Run unity_cli tools run <tool> to execute a tool."
    ]
  };

  return success(requestId, data);
}

async function handleToolsSearch(
  keyword: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  if (!keyword) {
    return failure(requestId, "TOOLS_SEARCH_KEYWORD_REQUIRED", "Pass a keyword: unity_cli tools search <keyword>.", [
      "Run unity_cli tools group to see tool groups."
    ]);
  }

  const builtinResults = searchTools(keyword);
  const includeCustom = getBoolean(flags, "include-custom") || shouldDiscoverProjectTools(flags, runtime);
  const customTools = includeCustom ? await discoverProjectTools(flags, runtime, requestId, { requireBridge: getBoolean(flags, "include-custom") }) : { ok: true as const, tools: [] };
  if (customTools.ok === false) return customTools;
  const customResults = customTools.tools.filter((t: ToolMetadata) =>
    t.name.toLowerCase().includes(keyword.toLowerCase()) ||
    t.title.toLowerCase().includes(keyword.toLowerCase()) ||
    t.description.toLowerCase().includes(keyword.toLowerCase()) ||
    t.group.toLowerCase().includes(keyword.toLowerCase())
  );

  const results = [...builtinResults, ...customResults];

  const summaries = results.map((t) => {
    const supportStatus = supportStatusForTool(t);
    return {
      name: t.name,
      title: t.title,
      group: t.group,
      readOnlyHint: t.readOnlyHint,
      destructiveHint: t.destructiveHint,
      supportStatus,
      supportStatusLabel: supportStatusLabel(supportStatus),
      cliCommand: t.cliCommand,
      architectureSensitive: t.architectureSensitive,
      frozenDecision: t.frozenDecision,
      coverageScope: t.coverageScope,
      target: t.target ?? "editor",
      availabilityScope: t.availabilityScope ?? (t.target === "runtime" ? "playModeSession" : "editorSession"),
      requiresPackages: t.requiresPackages
    };
  });

  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "tools.search",
    target: await optionalTargetSummary(flags, runtime),
    keyword,
    count: summaries.length,
    tools: summaries
  };

  if (format === "text") {
    const lines = summaries.map((s) => `${s.name} [${s.group}] ${s.supportStatusLabel} — ${s.title}`);
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

async function handleToolsDescribe(
  toolName: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  if (!toolName) {
    return failure(requestId, "TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: unity_cli tools describe <tool>.", [
      "Run unity_cli tools group to see tool groups."
    ]);
  }

  const builtinTool = findToolByName(toolName);
  const tool = builtinTool ?? await findProjectToolByName(toolName, flags, runtime, requestId);
  if (!tool) {
    return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found for target: ${toolName}.`, [
      "Run unity_cli tools group to see tool groups.",
      "Run unity_cli tools group list <group> to see tools in a group.",
      "Run unity_cli tools groups to see available tool categories.",
      "Run unity_cli tools list to see all available tools.",
      "Run unity_cli tools list --include-custom to discover Unity project Editor custom tools.",
      "Run unity_cli tools search <keyword> to find related tools."
    ]);
  }

  // ── Extension availability check ──
  // If the tool requires optional Unity packages, check whether they are
  // installed in the target project. If unavailable, return the descriptor
  // with an explicit unavailable status + next_actions rather than pretending
  // the tool is usable.
  const extensionCheck = await checkToolExtensionAvailability(tool, flags);

  const isProjectTool = builtinTool === undefined;
  const supportStatus = supportStatusForTool(tool);
  const data: Record<string, unknown> = {
    command: "tools.describe",
    target: await optionalTargetSummary(flags, runtime),
    mode: isProjectTool ? "bridge" : "offline",
    requiresUnity: isProjectTool,
    ...tool,
    supportStatus,
    supportStatusLabel: supportStatusLabel(supportStatus),
    supportStatusDescription: supportStatusDescription(supportStatus)
  };

  if (!extensionCheck.available) {
    data.extensionAvailability = "unavailable";
    data.extensionId = extensionCheck.extensionId;
    data.missingPackageIds = extensionCheck.availabilityResult?.missingPackageIds ?? tool.requiresPackages ?? [];
    data.nextActions = extensionCheck.nextActions;
  } else if (extensionCheck.extensionId) {
    data.extensionAvailability = extensionCheck.availabilityResult?.status ?? "available";
    data.extensionId = extensionCheck.extensionId;
  }

  return success(requestId, data);
}

async function handleToolsRun(
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const toolName = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
  if (!toolName) {
    return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", "Tool name is required for tools run.", [
      "Run unity_cli tools group to see tool groups.",
      "Run unity_cli tools run <name> [--input <json>] [--target <instance>]."
    ]);
  }

  // Discover the tool (builtin metadata + project tools)
  const builtinTool = findToolByName(toolName);
  const includeCustom = shouldDiscoverProjectTools(flags, runtime) || getBoolean(flags, "include-custom");
  let projectTool: ToolMetadata | undefined;
  if (!builtinTool && includeCustom) {
    // For runtime tools in Play Mode, use runtime-tool.list which is exempt
    // from the Play Mode busy guard. Try runtime-tool.list first, then fall
    // back to project-tool.list for editor-domain tools.
    const runtimeDiscovery = await discoverRuntimeTools(flags, runtime, requestId);
    if (runtimeDiscovery.ok === false) return runtimeDiscovery;
    projectTool = runtimeDiscovery.tools.find((t: ToolMetadata) => t.name === toolName);

    if (!projectTool) {
      // Tool not found in runtime tools; try editor-domain project-tool.list
      const customTools = await discoverProjectTools(flags, runtime, requestId, { requireBridge: getBoolean(flags, "include-custom") });
      if (customTools.ok === false) return customTools;
      projectTool = customTools.tools.find((t: ToolMetadata) => t.name === toolName);
    }
  }

  const tool = builtinTool ?? projectTool;
  if (!tool) {
    return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found: ${toolName}.`, [
      "Run unity_cli tools group to see tool groups.",
      "Run unity_cli tools search <keyword> to find related tools."
    ]);
  }

  // ── Extension availability guard ──
  // If the tool requires optional Unity packages that are not installed,
  // block execution and return E_EXTENSION_UNAVAILABLE with next_actions.
  const extensionCheck = await checkToolExtensionAvailability(tool, flags);
  if (!extensionCheck.available) {
    return failure(
      requestId,
      extensionCheck.errorCode,
      extensionCheck.message,
      extensionCheck.nextActions,
      {
        extensionId: extensionCheck.extensionId,
        missingPackageIds: extensionCheck.availabilityResult?.missingPackageIds ?? tool.requiresPackages ?? []
      }
    );
  }

  // Determine the execution target and route accordingly
  const target = tool.target ?? "editor";
  const isRuntime = target === "runtime" || (target === "both" && getBoolean(flags, "runtime"));

  if (isRuntime) {
    // Route through RuntimeExecutor via runtime-tool.run bridge command
    return executeBridgeCommand(requestId, "runtime-tool.run", flags, toolName, runtime);
  }

  // ── Built-in bridge/hybrid tools: route through native bridge command ──
  // The tool name (e.g. "scene.hierarchy") IS the bridge command name.
  // Parse --input as the args payload directly.
  if (builtinTool && (tool.handler === "bridge" || tool.handler === "hybrid")) {
    const input = await parseToolInput(flags);
    return executeBridgePayload(requestId, toolName, input, flags, runtime);
  }

  // ── Built-in job-handler tools: route to native CLI command handlers ──
  if (builtinTool && tool.handler === "job") {
    return dispatchJobHandler(tool, flags, runtime, requestId);
  }

  // ── Built-in tools with offline/other unsupported handlers ──
  if (builtinTool) {
    return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${toolName}" uses handler "${tool.handler}" which is not yet supported by tools run.`, [
      `Use the native command instead: ${tool.cliCommand ?? toolName}`,
      "Run unity_cli tools describe <tool> for details."
    ]);
  }

  // ── Custom project tools (editor domain): route through project-tool.run ──
  return executeBridgeCommand(requestId, "project-tool.run", flags, toolName, runtime);
}

async function parseToolInput(flags: Record<string, string | boolean>): Promise<Record<string, unknown>> {
  const inputFilePath = getString(flags, "input-file");
  if (inputFilePath) {
    const { readFile } = await import("node:fs/promises");
    const { resolve } = await import("node:path");
    const content = await readFile(resolve(inputFilePath), "utf8");
    try {
      const parsed = JSON.parse(content);
      return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : { input: parsed };
    } catch {
      return {};
    }
  }
  const inputString = getString(flags, "input");
  if (inputString !== undefined) {
    try {
      const parsed = JSON.parse(inputString);
      return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : { input: parsed };
    } catch {
      return {};
    }
  }
  return {};
}

/**
 * Convert a camelCase key to kebab-case CLI flag name.
 * e.g. "timeoutMs" → "timeout-ms", "outputPath" → "output-path"
 */
function camelToKebab(key: string): string {
  return key.replace(/[A-Z]/g, (m) => `-${m.toLowerCase()}`);
}

/**
 * Merge --input JSON fields into CLI flags (kebab-case).
 * Boolean values become true; string/number values are passed as-is.
 * Existing flags are not overwritten.
 */
function mergeInputToFlags(
  input: Record<string, unknown>,
  flags: Record<string, string | boolean>
): Record<string, string | boolean> {
  const merged = { ...flags };
  for (const [key, value] of Object.entries(input)) {
    const flagKey = camelToKebab(key);
    if (flagKey in merged) continue; // explicit CLI flags take precedence
    if (typeof value === "boolean") {
      if (value) merged[flagKey] = true;
    } else if (typeof value === "number") {
      merged[flagKey] = String(value);
    } else if (typeof value === "string") {
      merged[flagKey] = value;
    }
  }
  return merged;
}

/**
 * Dispatch a built-in job-handler tool to its native CLI command handler.
 *
 * Routes based on `tool.cliCommand`:
 *   "test run"    → handleTest("run", ...)
 *   "build run"   → handleBuild("run", ...)
 *   "editor refresh" → handleEditor("refresh", ...)
 *
 * Merges --input JSON into CLI flags so that `--input '{"unity":"...","project":"..."}'`
 * is equivalent to passing `--unity ... --project ...` on the native command.
 */
async function dispatchJobHandler(
  tool: ToolMetadata,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const cliCommand = tool.cliCommand ?? "";
  const [group, sub] = cliCommand.split(" ");

  // Verify the runtime has the full Runtime shape (store, runner, dataDir).
  // When called from the CLI entry point it always will; but tests may pass
  // a minimal ToolsRuntime.
  const fullRuntime = runtime as Runtime;
  if (!fullRuntime.store || !fullRuntime.runner || !fullRuntime.dataDir) {
    return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${tool.name}" requires a job runner runtime.`, [
      `Use the native command instead: ${cliCommand}`,
      "Run unity_cli tools describe <tool> for details."
    ]);
  }

  // Merge --input JSON into CLI flags (camelCase → kebab-case)
  const input = await parseToolInput(flags);
  const mergedFlags = mergeInputToFlags(input, flags);

  let result: CliResult | undefined;

  switch (group) {
    case "test":
      result = await handleTest(sub, mergedFlags, fullRuntime, requestId);
      break;
    case "build":
      result = await handleBuild(sub, mergedFlags, fullRuntime, requestId);
      break;
    case "editor":
      result = await handleEditor(sub, mergedFlags, fullRuntime, requestId);
      break;
    default:
      return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${tool.name}" has unmapped job cliCommand "${cliCommand}".`, [
        `Use the native command instead: ${cliCommand}`,
        "Run unity_cli tools describe <tool> for details."
      ]);
  }

  return result ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Job command "${cliCommand}" is not supported by tools run.`, [
    `Use the native command instead: ${cliCommand}`,
    "Run unity_cli tools describe <tool> for details."
  ]);
}

async function optionalTargetSummary(flags: Record<string, string | boolean>, runtime: ToolsRuntime): Promise<Record<string, unknown> | null> {
  try {
    const selection = await selectInstanceForFlags(flags, runtime.dataDir ?? process.cwd());
    return selection ? targetSummary(selection.instance) : null;
  } catch (error) {
    const result = instanceSelectionFailure("tools_target_probe", error);
    if (result) {
      return null;
    }
    throw error;
  }
}

async function findProjectToolByName(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<ToolMetadata | undefined> {
  const customTools = await discoverProjectTools(flags, runtime, requestId);
  if (customTools.ok === false) {
    return undefined;
  }
  return customTools.tools.find((tool: ToolMetadata) => tool.name === name);
}
