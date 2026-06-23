/**
 * CLI sub-commands: tools group, tools group list, tools search, tools describe, tools run.
 *
 * Group-first discovery model:
 *   tools group               → list tool groups with counts
 *   tools group list <group>  → list tools in a group (short summary)
 *   tools describe <name>     → full metadata for a tool
 *   tools run <name>          → execute a tool
 *   tools search <keyword>    → auxiliary search across tools
 */

import { getBoolean, getString } from "./args.js";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { discoverProjectTools, discoverRuntimeTools, shouldDiscoverProjectTools, type ProjectToolRuntime } from "./project-tool-discovery.js";
import { bridgeCommandRegistry, executeBridgeCommand, executeBridgePayload } from "./bridge/index.js";
import { checkToolExtensionAvailability, collectExtensionToolDescriptors } from "./extension-availability.js";
import { ExtensionRegistry, collectAvailableTools, readInstalledUnityPackages } from "./extensions/index.js";
import {
  builtinToolMetadata,
  extensionToolMetadata,
  allRegisteredToolMetadata,
  builtinToolGroups,
  allRegisteredToolGroups,
  findToolByName,
  filterToolsByGroup,
  searchTools,
  EXTENSION_GROUPS,
  type ToolMetadata
} from "./tools/metadata/index.js";
import { failure, success } from "./output.js";
import { instanceSelectionFailure, selectInstanceForFlags, targetSummary } from "./instances.js";
import { confirmDestructiveExecution } from "./security.js";
import { supportStatusDescription, supportStatusForTool, supportStatusLabel } from "./tools/metadata/status.js";
import type { CliResult } from "./types.js";
import { handleBuild } from "./cli-commands/build.js";
import { handleEditor } from "./cli-commands/editor.js";
import { handleTest } from "./cli-commands/test.js";
import { handleJob } from "./cli-commands/job.js";
import { handlePackage } from "./cli-commands/package.js";
import { handleBatch } from "./cli-commands/batch.js";
import { handleDoctorCommand } from "./doctor.js";
import { handleCapabilitiesCommand } from "./capabilities.js";
import { handleInstancesCommand, handleTargetCommand } from "./instances.js";
import { handleDocsCommand, handleReflectCommand } from "./docs-reflect.js";
import { handleCode } from "./runtime-code.js";
import type { Runtime } from "./cli-commands/types.js";

export type ToolsRuntime = ProjectToolRuntime;

export async function handleToolsCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
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
    "Run unity_cli tools group list <group> to see tools in a group.",
    "Run unity_cli tools describe <tool> for full metadata.",
    "Run unity_cli tools run <tool> to execute a tool."
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
  // Core groups are always present; extension groups are included only when
  // their required Unity packages are installed in the target project.
  const availableExtensionTools = await resolveAvailableExtensionTools(flags);
  const builtinGroups = builtinToolGroups();
  const extensionGroupNames = [...new Set(availableExtensionTools.map((t) => t.group))].sort();
  const includeCustom = shouldDiscoverProjectTools(flags, runtime);
  const customDiscovery = includeCustom ? await discoverProjectTools(flags, runtime, requestId) : { ok: true as const, tools: [] as ToolMetadata[], mode: "skipped" as const };
  if (customDiscovery.ok === false) return customDiscovery;

 // Build group info with counts and descriptions
  const allTools = [...builtinToolMetadata(), ...availableExtensionTools, ...customDiscovery.tools];
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
    cinemachine: "Cinemachine smart camera system: virtual cameras, composition, blending, noise (requires com.unity.cinemachine).",
    urp: "Universal Render Pipeline: renderer features, volumes, pipeline settings (requires com.unity.render-pipelines.universal).",
    hdrp: "High Definition Render Pipeline: volumes, lighting, pipeline settings (requires com.unity.render-pipelines.high-definition).",
    shader: "Shader management: info, read, create, update.",
    texture: "Texture management: info, create, sprite, import settings.",
    animation: "Animation control: clips, states, play, stop.",
    vfx: "Visual effects: VFX Graph and particle systems (requires com.unity.visualeffectgraph).",
    ui: "UI Toolkit and uGUI: canvas, text, button, image, document.",
    script: "Script management: create, read, edit, validate.",
    editor: "Editor control: console, state, play/pause/stop, menu, selection, active tool.",
    console: "Unity Editor console: read and clear logs.",
    package: "Editor-side package operations: embed.",
    diagnose: "Diagnostics: preflight checks.",
    screenshot: "Screenshot capture.",
    code: "Code execution: exec C# code snippets.",
    object: "Generic Unity Object deep read/modify: serialized member trees, pathPatches, jsonPatch.",
    reflection: "Live Unity reflection: find and call C# methods, generate type schemas.",
    type: "Live type schema generation for C# / Unity types.",
    "cli-only": "CLI-only commands: init, package, batch, docs, reflect, test, build, job management."
  };

  const seen = new Set<string>();
  for (const tool of allTools) {
    const group = tool.group;
    if (!seen.has(group)) {
      seen.add(group);
    }
    groupCounts[group] = (groupCounts[group] ?? 0) + 1;
  }

  const groups = [...seen].sort().map((group) => ({
    name: group,
    title: groupDescriptions[group] ?? `Tools in the ${group} domain.`,
    toolCount: groupCounts[group] ?? 0,
    source: EXTENSION_GROUPS.has(group) ? "extension" : builtinGroups.includes(group) ? "core" : "project",
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

  const builtinTools = builtinToolMetadata().filter((t) => t.group === groupName);
  // Include extension tools only when they are available for the target project.
  const availableExtensionTools = await resolveAvailableExtensionTools(flags);
  const extensionTools = availableExtensionTools.filter((t) => t.group === groupName);
  const includeCustom = shouldDiscoverProjectTools(flags, runtime);
  const customDiscovery = includeCustom ? await discoverProjectTools(flags, runtime, requestId) : { ok: true as const, tools: [] as ToolMetadata[], mode: "skipped" as const };
  if (customDiscovery.ok === false) return customDiscovery;
  const customTools = customDiscovery.tools.filter((t: ToolMetadata) => t.group === groupName);

  const allTools = [...builtinTools, ...extensionTools, ...customTools];
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
      group: t.group,
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
  const availableExtensionTools = await resolveAvailableExtensionTools(flags);
  const extensionResults = availableExtensionTools.filter((t: ToolMetadata) =>
    t.name.toLowerCase().includes(keyword.toLowerCase()) ||
    t.title.toLowerCase().includes(keyword.toLowerCase()) ||
    t.description.toLowerCase().includes(keyword.toLowerCase()) ||
    t.group.toLowerCase().includes(keyword.toLowerCase())
  );

  let results = [...builtinResults, ...extensionResults];

  const includeCustom = getBoolean(flags, "include-custom") || shouldDiscoverProjectTools(flags, runtime);
  if (includeCustom) {
    const customTools = await discoverProjectTools(flags, runtime, requestId, { requireBridge: getBoolean(flags, "include-custom") });
    if (customTools.ok === false) return customTools;
    const customResults = customTools.tools.filter((t: ToolMetadata) =>
      t.name.toLowerCase().includes(keyword.toLowerCase()) ||
      t.title.toLowerCase().includes(keyword.toLowerCase()) ||
      t.description.toLowerCase().includes(keyword.toLowerCase()) ||
      t.group.toLowerCase().includes(keyword.toLowerCase())
    );
    results = [...results, ...customResults];
  }

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
      "Run unity_cli tools group to see all tool groups.",
      "Run unity_cli tools group list custom --include-custom to discover Unity project Editor custom tools.",
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

  // code.exec 是 hybrid，但需要先经过原生 handler 完成本地校验、dry-run、artifact 与 batchmode job 逻辑。
  if (builtinTool && toolName === "code.exec") {
    const input = await parseToolInput(flags);
    return handleCode("exec", mergeInputToFlags(input, flags), runtime as Runtime, requestId);
  }

  // ── Built-in bridge/hybrid tools WITH bridge spec: route through Unity Bridge ──
  // 仅当存在 bridge spec 时走 bridge 路由(scene/camera/go/.../code.exec 等)。
  // 无 bridge spec 的 hybrid/offline 工具(package.*/docs.*/reflect.*/batch.*/...)
  // 走下面的原生 handler dispatch。
  if (builtinTool && (tool.handler === "bridge" || tool.handler === "hybrid") && findBridgeSpecForTool(toolName)) {
    const input = await parseToolInput(flags);
    const mergedFlags = mergeInputToFlags(input, flags);
    const spec = findBridgeSpecForTool(toolName)!;
    // 破坏性工具确认(对齐旧 cli.ts bridge 路径)。
    if (spec.destructiveHint || tool.destructiveHint) {
      const confirmation = await confirmDestructiveExecution(mergedFlags, {
        command: toolName,
        title: tool.title,
        destructiveHint: true,
        supportsDryRun: spec.supportsDryRun ?? tool.supportsDryRun,
        requestId
      });
      if (confirmation) return confirmation;
    }
    let args = await spec.buildArgs({ command: toolName, flags: mergedFlags, positionalTarget: positional[1] });
    let bridgeCommand = spec.bridgeCommand;
    if (spec.toolName) {
      args = { name: spec.toolName, input: args };
    }
    return executeBridgePayload(requestId, bridgeCommand, args, mergedFlags, runtime);
  }

  // ── Built-in job-handler tools: route to native job CLI handlers ──
  if (builtinTool && tool.handler === "job") {
    return dispatchJobHandler(tool, flags, runtime, requestId);
  }

  // ── Built-in offline/hybrid(no bridge spec) tools: route to native CLI handlers ──
  // 迁移自旧顶层命令的 docs/reflect/package/batch/job/instances/target/diagnose/tools 发现。
  if (builtinTool && (tool.handler === "offline" || tool.handler === "hybrid")) {
    return dispatchOfflineHandler(tool, flags, runtime, requestId, positional);
  }

  // ── Built-in tools with other unsupported handlers ──
  if (builtinTool) {
    return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${toolName}" uses handler "${tool.handler}" which is not supported by tools run.`, [
      "Run unity_cli tools describe <tool> for details."
    ]);
  }

  // ── Custom project tools (editor domain): route through project-tool.run ──
  return executeBridgeCommand(requestId, "project-tool.run", flags, toolName, runtime);
}

export function findBridgeSpecForTool(toolName: string) {
  const dotIndex = toolName.indexOf(".");
  const group = dotIndex > 0 ? toolName.substring(0, dotIndex) : toolName;
  const command = dotIndex > 0 ? toolName.substring(dotIndex + 1) : "";
  const byToolName = bridgeCommandRegistry.specs().find((spec) => spec.toolName === toolName || spec.bridgeCommand === toolName);
  return byToolName ?? bridgeCommandRegistry.lookup(group, command);
}

async function parseToolInput(flags: Record<string, string | boolean>): Promise<Record<string, unknown>> {
  const inputFilePath = getString(flags, "input-file");
  if (inputFilePath) {
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
  const structuredInput: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(input)) {
    const flagKey = camelToKebab(key);
    if (flagKey in merged) continue; // explicit CLI flags take precedence
    if (typeof value === "boolean") {
      if (value) merged[flagKey] = true;
    } else if (typeof value === "number") {
      merged[flagKey] = String(value);
    } else if (typeof value === "string") {
      merged[flagKey] = value;
    } else if (value !== undefined) {
      structuredInput[key] = value;
    }
  }
  if (Object.keys(structuredInput).length > 0) {
    merged.__structuredInput = JSON.stringify(structuredInput);
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
  // 按 tool.name 路由(README Project Baseline: cliCommand 现为 "tools run <name>")。
  const dotIndex = tool.name.indexOf(".");
  const group = dotIndex > 0 ? tool.name.substring(0, dotIndex) : tool.name;
  const sub = dotIndex > 0 ? tool.name.substring(dotIndex + 1) : "";

  const fullRuntime = runtime as Runtime;
  if (!fullRuntime.store || !fullRuntime.runner || !fullRuntime.dataDir) {
    return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${tool.name}" requires a job runner runtime.`, [
      "Run unity_cli tools describe <tool> for details."
    ]);
  }

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
      return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${tool.name}" has unmapped job handler.`, [
        "Run unity_cli tools describe <tool> for details."
      ]);
  }

  return result ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Job tool "${tool.name}" is not supported by tools run.`, [
    "Run unity_cli tools describe <tool> for details."
  ]);
}

/**
 * 分发 offline/hybrid(no bridge spec) 工具到原生 CLI handler。
 * 迁移自旧顶层命令:docs/reflect/package/batch/job/instances/target/diagnose/capabilities/tools 发现。
 */
async function dispatchOfflineHandler(
  tool: ToolMetadata,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
  positional: string[] = []
): Promise<CliResult> {
  const name = tool.name;
  const dotIndex = name.indexOf(".");
  const group = dotIndex > 0 ? name.substring(0, dotIndex) : name;
  const sub = dotIndex > 0 ? name.substring(dotIndex + 1) : "";
  const fullRuntime = runtime as Runtime;

  const input = await parseToolInput(flags);
  const mergedFlags = mergeInputToFlags(input, flags);

  // 破坏性 offline 工具确认(batch.run/job.cancel/package.remove/instances.prune/target.set/clear 等)。
  if (tool.destructiveHint) {
    const confirmation = await confirmDestructiveExecution(mergedFlags, {
      command: name,
      title: tool.title,
      destructiveHint: true,
      supportsDryRun: tool.supportsDryRun,
      requestId
    });
    if (confirmation) return confirmation;
  }

  // tools 发现工具(tools.group/group-list/search/describe/run)
  if (group === "tools") {
    return handleToolsCommand(sub === "group-list" ? "group" : sub, [], mergedFlags, runtime, requestId);
  }

  switch (group) {
    case "docs":
      return handleDocsCommand(sub === "list" ? undefined : sub, positional.slice(1), mergedFlags, fullRuntime.dataDir, requestId, fullRuntime);
    case "reflect":
      return handleReflectCommand(sub, positional.slice(1), mergedFlags, fullRuntime.dataDir, requestId);
    case "package":
      return (await handlePackage(sub, positional.slice(1), mergedFlags, fullRuntime, requestId)) ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `package.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    case "batch":
      return handleBatch(sub, positional[1], mergedFlags, fullRuntime, requestId);
    case "job":
      return handleJob(sub, mergedFlags, fullRuntime, requestId);
    case "build":
      return (await handleBuild(sub, mergedFlags, fullRuntime, requestId)) ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `build.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    case "test":
      return (await handleTest(sub, mergedFlags, fullRuntime, requestId)) ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `test.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    case "editor":
      return (await handleEditor(sub, mergedFlags, fullRuntime, requestId)) ?? failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `editor.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    case "instances":
      return handleInstancesCommand(sub === "prune" ? "prune" : "list", mergedFlags, fullRuntime.dataDir, requestId);
    case "target":
      return dispatchTargetTool(name, sub, mergedFlags, fullRuntime.dataDir, requestId, positional);
    case "diagnose":
      if (sub === "doctor") return handleDoctorCommand(mergedFlags, fullRuntime, requestId);
      if (sub === "capabilities") return handleCapabilitiesCommand(mergedFlags, fullRuntime, requestId);
      return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `diagnose.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
    default:
      return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `Tool "${name}" has unmapped offline handler.`, [
        "Run unity_cli tools describe <tool> for details."
      ]);
  }
}

/** target.* 工具按子命令分发到 handleTargetCommand。 */
async function dispatchTargetTool(name: string, sub: string, flags: Record<string, string | boolean>, dataDir: string, requestId: string, positional: string[] = []): Promise<CliResult> {
  // 位置参数:target.alias-set <instanceId> <alias>; target.set <selector>
  const posArgs = positional.slice(1); // positional[0] 是 tool name
  // target.alias-set/unset/list → handleTargetCommand("alias", [sub, instanceId, alias])
  if (sub === "alias-set" || sub === "alias-unset" || sub === "alias-list") {
    const aliasSub = sub === "alias-set" ? "set" : sub === "alias-unset" ? "unset" : "list";
    const instanceId = getString(flags, "instance-id") ?? getString(flags, "instanceId") ?? posArgs[0] ?? "";
    const alias = getString(flags, "alias") ?? posArgs[1] ?? posArgs[0] ?? "";
    return handleTargetCommand("alias", aliasSub === "set" ? [aliasSub, instanceId, alias] : [aliasSub, alias], flags, dataDir, requestId);
  }
  if (sub === "set") {
    const selector = getString(flags, "target") ?? posArgs[0] ?? "";
    return handleTargetCommand("set", [selector], flags, dataDir, requestId);
  }
  if (sub === "clear") {
    return handleTargetCommand("clear", [], flags, dataDir, requestId);
  }
  return failure(requestId, "E_TOOL_HANDLER_UNSUPPORTED", `target.${sub} not supported.`, ["Run unity_cli tools describe <tool>."]);
}

/**
 * Resolve a target summary when flags identify a reachable Unity instance.
 */
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

/**
 * Resolve which extension tools are available for the target project.
 *
 * Extension tools are shown only when their required Unity packages are installed
 * in the target project. Without --project, extension tools are not included.
 */
async function resolveAvailableExtensionTools(
  flags: Record<string, string | boolean>
): Promise<ToolMetadata[]> {
  const allExtensionTools = extensionToolMetadata();
  if (allExtensionTools.length === 0) return [];

  const projectPath = getString(flags, "project");
  if (!projectPath) {
    // Without a project path, we cannot determine availability.
    // Return NO extension tools — they are not part of core.
    return [];
  }

  try {
    const installedPackages = await readInstalledUnityPackages(projectPath);
    const registry = new ExtensionRegistry();
    const availableExtensionIds = new Set(
      registry.availableExtensions(installedPackages).map((r) => r.extensionId)
    );
    return allExtensionTools.filter((t) => {
      // Find which extension provides this tool's group
      for (const extId of availableExtensionIds) {
        const ext = registry.getExtension(extId)!;
        if (ext.manifest.contributions.tools?.some((et) => et.group === t.group)) {
          return true;
        }
      }
      return false;
    });
  } catch {
    // Cannot read packages — return no extension tools
    return [];
  }
}
