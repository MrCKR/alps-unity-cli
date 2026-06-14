/**
 * CLI sub-commands: tools list, tools groups, tools search, tools describe.
 *
 * These commands expose the built-in tool metadata so that agents and
 * users can discover available capabilities without guessing.
 */

import { getBoolean, getString } from "./args.js";
import {
  builtinToolMetadata,
  builtinToolGroups,
  findToolByName,
  filterToolsByGroup,
  searchTools,
  type ToolMetadata
} from "./tool-metadata.js";
import { failure, success } from "./output.js";
import type { CliResult } from "./types.js";

export interface ToolsRuntime {
  customTools?: ToolMetadata[];
}

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
    return handleToolsGroups(runtime, requestId);
  }

  if (command === "search") {
    const keyword = positional[0] ?? getString(flags, "keyword") ?? getString(flags, "query");
    return handleToolsSearch(keyword, flags, runtime, requestId);
  }

  if (command === "describe") {
    const toolName = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
    return handleToolsDescribe(toolName, requestId);
  }

  return failure(requestId, "UNKNOWN_TOOLS_COMMAND", "Expected tools list, tools groups, tools search <keyword> or tools describe <tool>.", [
    "Run alps-unity tools list to see all available tools.",
    "Run alps-unity tools groups to see tool categories.",
    "Run alps-unity tools describe <tool> for full metadata."
  ]);
}

function handleToolsList(
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): CliResult {
  const groupFilter = getString(flags, "group");
  const includeCustom = getBoolean(flags, "include-custom");

  let tools: ToolMetadata[] = builtinToolMetadata();

  if (groupFilter) {
    tools = filterToolsByGroup(groupFilter);
  }

  if (includeCustom && runtime.customTools && runtime.customTools.length > 0) {
    tools = [...tools, ...runtime.customTools];
  }

  const format = getString(flags, "format");

  const summaries = tools.map((t) => ({
    name: t.name,
    title: t.title,
    description: t.description,
    group: t.group,
    readOnlyHint: t.readOnlyHint,
    destructiveHint: t.destructiveHint,
    handler: t.handler,
    cliCommand: t.cliCommand
  }));

  const data: Record<string, unknown> = {
    command: "tools.list",
    count: summaries.length,
    tools: summaries
  };

  if (format === "text") {
    const lines = summaries.map((s) => `${s.name} [${s.group}] ${s.readOnlyHint ? "read-only" : "write"} ${s.destructiveHint ? "⚠ destructive" : ""} — ${s.title}`);
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

function handleToolsGroups(runtime: ToolsRuntime, requestId: string): CliResult {
  const builtinGroups = builtinToolGroups();
  const customGroups: string[] = [];

  if (runtime.customTools) {
    const seen = new Set(builtinGroups);
    for (const t of runtime.customTools) {
      if (!seen.has(t.group)) {
        customGroups.push(t.group);
        seen.add(t.group);
      }
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

function handleToolsSearch(
  keyword: string | undefined,
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string
): CliResult {
  if (!keyword) {
    return failure(requestId, "TOOLS_SEARCH_KEYWORD_REQUIRED", "Pass a keyword: alps-unity tools search <keyword>.", [
      "Run alps-unity tools list to see all available tools."
    ]);
  }

  const builtinResults = searchTools(keyword);
  const customResults = runtime.customTools
    ? runtime.customTools.filter((t) =>
      t.name.toLowerCase().includes(keyword.toLowerCase()) ||
      t.title.toLowerCase().includes(keyword.toLowerCase()) ||
      t.description.toLowerCase().includes(keyword.toLowerCase())
    )
    : [];

  const results = [...builtinResults, ...customResults];

  const summaries = results.map((t) => ({
    name: t.name,
    title: t.title,
    group: t.group,
    readOnlyHint: t.readOnlyHint,
    destructiveHint: t.destructiveHint,
    cliCommand: t.cliCommand
  }));

  const format = getString(flags, "format");
  const data: Record<string, unknown> = {
    command: "tools.search",
    keyword,
    count: summaries.length,
    tools: summaries
  };

  if (format === "text") {
    const lines = summaries.map((s) => `${s.name} [${s.group}] — ${s.title}`);
    data.text = lines.join("\n");
  }

  return success(requestId, data);
}

function handleToolsDescribe(
  toolName: string | undefined,
  requestId: string
): CliResult {
  if (!toolName) {
    return failure(requestId, "TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: alps-unity tools describe <tool>.", [
      "Run alps-unity tools list to see all available tools."
    ]);
  }

  const tool = findToolByName(toolName);
  if (!tool) {
    return failure(requestId, "TOOL_NOT_FOUND", `Tool not found: ${toolName}.`, [
      "Run alps-unity tools list to see all available tools.",
      "Run alps-unity tools search <keyword> to find related tools."
    ]);
  }

  const data: Record<string, unknown> = {
    command: "tools.describe",
    mode: "offline",
    requiresUnity: false,
    ...tool
  };

  return success(requestId, data);
}