/**
 * CLI sub-commands: tools list, tools groups, tools search, tools describe.
 *
 * These commands expose the built-in tool metadata so that agents and
 * users can discover available capabilities without guessing.
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
  if (!command || command === "list") {
    return handleToolsList(flags, runtime, requestId);
  }

  if (command === "run") {
    return handleToolsRun(positional, flags, runtime, requestId);
  }

  if (command === "groups") {
    return handleToolsGroups(flags, runtime, requestId);
  }

  if (command === "search") {
    const keyword = positional[0] ?? getString(flags, "keyword") ?? getString(flags, "query");
    return handleToolsSearch(keyword, flags, runtime, requestId);
  }

  if (command === "describe") {
    const toolName = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
    return handleToolsDescribe(toolName, flags, runtime, requestId);
  }

  return failure(requestId, "UNKNOWN_TOOLS_COMMAND", "Expected tools list, tools groups, tools search <keyword> or tools describe <tool>.", [
    "Run unity_cli tools list to see all available tools.",
    "Run unity_cli tools groups to see tool categories.",
    "Run unity_cli tools describe <tool> for full metadata."
  ]);
}

async function handleToolsList(
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<CliResult> {
  const groupFilter = getString(flags, "group");
  const explicitIncludeCustom = getBoolean(flags, "include-custom");
  const includeCustom = explicitIncludeCustom || shouldDiscoverProjectTools(flags, runtime);

  let tools: ToolMetadata[] = groupFilter ? filterToolsByGroup(groupFilter) : builtinToolMetadata();

  if (includeCustom) {
    const customTools = await discoverProjectTools(flags, runtime, requestId, { requireBridge: explicitIncludeCustom });
    if (customTools.ok === false) return customTools;
    tools = [...tools, ...(groupFilter ? customTools.tools.filter((tool: ToolMetadata) => tool.group === groupFilter) : customTools.tools)];
  }

  const format = getString(flags, "format");

  const summaries = tools.map((t) => {
    const supportStatus = supportStatusForTool(t);
    return {
      name: t.name,
      title: t.title,
      description: t.description,
      group: t.group,
      readOnlyHint: t.readOnlyHint,
      destructiveHint: t.destructiveHint,
      handler: t.handler,
      supportStatus,
      supportStatusLabel: supportStatusLabel(supportStatus),
      cliCommand: t.cliCommand,
      architectureSensitive: t.architectureSensitive,
      frozenDecision: t.frozenDecision,
      coverageScope: t.coverageScope,
      target: t.target ?? "editor",
      availabilityScope: t.availabilityScope ?? (t.target === "runtime" ? "playModeSession" : "editorSession"),
      available: t.available,
      requiresPackages: t.requiresPackages
    };
  });

  const data: Record<string, unknown> = {
    command: "tools.list",
    target: await optionalTargetSummary(flags, runtime),
    count: summaries.length,
    tools: summaries
  };

  if (format === "text") {
    const lines = summaries.map((s) => `${s.name} [${s.group}] ${s.supportStatusLabel} ${s.readOnlyHint ? "read-only" : "write"} ${s.destructiveHint ? "⚠ destructive" : ""} — ${s.title}`);
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

async function handleToolsGroups(flags: Record<string, string | boolean>, runtime: ToolsRuntime, requestId: string): Promise<CliResult> {
  const builtinGroups = builtinToolGroups();
  const includeCustom = shouldDiscoverProjectTools(flags, runtime);
  const customDiscovery = includeCustom ? await discoverProjectTools(flags, runtime, requestId) : { ok: true as const, tools: [], mode: "skipped" as const };
  if (customDiscovery.ok === false) return customDiscovery;
  const customGroups: string[] = [];
  const seen = new Set(builtinGroups);
  for (const t of customDiscovery.tools) {
    if (!seen.has(t.group)) {
      customGroups.push(t.group);
      seen.add(t.group);
    }
  }

  const groups = [...builtinGroups, ...customGroups.sort()];
  const data: Record<string, unknown> = {
    command: "tools.groups",
    count: groups.length,
    groups
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
      "Run unity_cli tools list to see all available tools."
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
      "Run unity_cli tools list to see all available tools."
    ]);
  }

  const builtinTool = findToolByName(toolName);
  const tool = builtinTool ?? await findProjectToolByName(toolName, flags, runtime, requestId);
  if (!tool) {
    return failure(requestId, "E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found for target: ${toolName}.`, [
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
      "Run unity_cli tools list to see available tools.",
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
      "Run unity_cli tools list --include-custom to see all available tools.",
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
