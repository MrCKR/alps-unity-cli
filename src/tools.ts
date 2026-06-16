/**
 * CLI sub-commands: tools list, tools groups, tools search, tools describe.
 *
 * These commands expose the built-in tool metadata so that agents and
 * users can discover available capabilities without guessing.
 */

import { getBoolean, getString } from "./args.js";
import { discoverCustomTools, shouldDiscoverCustomTools, type CustomToolRuntime } from "./custom-tool-discovery.js";
import {
  builtinToolMetadata,
  builtinToolGroups,
  findToolByName,
  filterToolsByGroup,
  searchTools,
  type ToolMetadata
} from "./tools/metadata/index.js";
import { failure, success } from "./output.js";
import { supportStatusDescription, supportStatusForTool, supportStatusLabel } from "./tools/metadata/status.js";
import type { CliResult } from "./types.js";

export type ToolsRuntime = CustomToolRuntime;

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
  const includeCustom = explicitIncludeCustom || shouldDiscoverCustomTools(flags, runtime);

  let tools: ToolMetadata[] = groupFilter ? filterToolsByGroup(groupFilter) : builtinToolMetadata();

  if (includeCustom) {
    const customTools = await discoverCustomTools(flags, runtime, requestId, { requireBridge: explicitIncludeCustom });
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
      coverageScope: t.coverageScope
    };
  });

  const data: Record<string, unknown> = {
    command: "tools.list",
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
  const includeCustom = shouldDiscoverCustomTools(flags, runtime);
  const customDiscovery = includeCustom ? await discoverCustomTools(flags, runtime, requestId) : { ok: true as const, tools: [], mode: "skipped" as const };
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
  const includeCustom = getBoolean(flags, "include-custom") || shouldDiscoverCustomTools(flags, runtime);
  const customTools = includeCustom ? await discoverCustomTools(flags, runtime, requestId, { requireBridge: getBoolean(flags, "include-custom") }) : { ok: true as const, tools: [] };
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
      coverageScope: t.coverageScope
    };
  });

  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "tools.search",
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
  const tool = builtinTool ?? await findCustomToolByName(toolName, flags, runtime, requestId);
  if (!tool) {
    return failure(requestId, "TOOL_NOT_FOUND", `Tool not found: ${toolName}.`, [
      "Run unity_cli tools list to see all available tools.",
      "Run unity_cli tools list --include-custom to discover Unity project Editor custom tools.",
      "Run unity_cli tools search <keyword> to find related tools."
    ]);
  }

  const isCustomTool = builtinTool === undefined;
  const supportStatus = supportStatusForTool(tool);
  const data: Record<string, unknown> = {
    command: "tools.describe",
    mode: isCustomTool ? "bridge" : "offline",
    requiresUnity: isCustomTool,
    ...tool,
    supportStatus,
    supportStatusLabel: supportStatusLabel(supportStatus),
    supportStatusDescription: supportStatusDescription(supportStatus)
  };

  return success(requestId, data);
}

async function findCustomToolByName(
  name: string,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): Promise<ToolMetadata | undefined> {
  const customTools = await discoverCustomTools(flags, runtime, requestId);
  if (customTools.ok === false) {
    return undefined;
  }
  return customTools.tools.find((tool: ToolMetadata) => tool.name === name);
}
