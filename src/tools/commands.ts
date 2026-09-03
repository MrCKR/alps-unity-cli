import { getString } from "../args.js";
import { failure } from "../output.js";
import type { CliResult } from "../types.js";
import { resolveCatalogIndependentOperabilityTool } from "./catalog.js";
import { projectCatalogDescribe, projectCatalogGroups, projectCatalogPage, type CatalogPageRequest } from "./catalog-control.js";
import { groupSummary } from "./tools/group-summaries.js";
import { dispatchCatalogProjection, dispatchTool, type ToolsRuntime } from "./dispatcher.js";

export type { ToolsRuntime } from "./dispatcher.js";

/** tools discovery 属于直接控制面；只有 tools run 进入公开 Tool dispatcher。 */
export async function handleToolsCommand(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  runtime: ToolsRuntime,
  requestId: string,
): Promise<CliResult> {
  const flagError = rejectUnknownToolFlags(command, positional, flags, requestId);
  if (flagError) return flagError;
  if (command === "run") {
    const name = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool");
    return dispatchTool(name, positional[1], flags, runtime, requestId);
  }
  if (command === "group") {
    if (positional[0] === "list") {
      const group = positional[1];
      if (!group) return failure(requestId, "TOOLS_GROUP_LIST_NAME_REQUIRED", "Pass a group name: unitycli tools group list <group>.", ["Run unitycli tools group to see all tool groups."]);
      const page = pageSeed(flags);
      return dispatchCatalogProjection("catalog.group-list", { group, ...page }, flags, runtime, requestId, (rootRuntime) =>
        projectCatalogPage(`catalog.group-list:${group}`, page, flags, rootRuntime, requestId, {
          group,
          includeExtensionAvailability: true,
          extraData: { summary: groupSummary(group) },
          projectItem: (tool) => ({ name: tool.name, summary: tool.summary, ...(tool.available === false ? { available: false } : {}) }),
        }, catalogDeadline(rootRuntime)));
    }
    return dispatchCatalogProjection("catalog.groups", {}, flags, runtime, requestId, (rootRuntime) =>
      projectCatalogGroups(flags, rootRuntime, requestId, catalogDeadline(rootRuntime)));
  }
  if (command === "search") {
    const keyword = positional[0] ?? getString(flags, "keyword") ?? getString(flags, "query");
    const tokens = (keyword ?? "").split("|").map((token) => token.trim()).filter(Boolean);
    if (tokens.length === 0) return failure(requestId, "TOOLS_SEARCH_KEYWORD_REQUIRED", "Pass one or more `|`-separated keywords: unitycli tools search <k1|k2|...>.", ["Run unitycli tools group to see tool groups."]);
    if (tokens.length > 16 || tokens.some((token) => token.length > 128)) return failure(requestId, "E_SCHEMA_INVALID", "tools search accepts at most 16 keywords of at most 128 characters each.", [
      "Reduce the number or length of search keywords before retrying."
    ], { path: "$.keyword", expected: "1..16 keywords, each 1..128 characters", actual: keyword });
    const normalizedKeyword = tokens.join("|");
    const page = pageSeed(flags);
    return dispatchCatalogProjection("catalog.search", { keyword: normalizedKeyword, ...page }, flags, runtime, requestId, (rootRuntime) =>
      projectCatalogPage(`catalog.search:${normalizedKeyword.toLowerCase()}`, page, flags, rootRuntime, requestId, {
        keyword: normalizedKeyword,
        projectItem: (tool) => ({ name: tool.name, summary: tool.summary, group: tool.group }),
      }, catalogDeadline(rootRuntime)));
  }
  if (command === "describe") {
    const name = positional[0] ?? getString(flags, "name") ?? getString(flags, "tool") ?? "";
    if (!name) return failure(requestId, "TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: unitycli tools describe <tool>.", ["Run unitycli tools group to see tool groups."]);
    if (resolveCatalogIndependentOperabilityTool(name)) return projectCatalogDescribe(name, flags, runtime, requestId);
    return dispatchCatalogProjection("catalog.describe", { name }, flags, runtime, requestId, (rootRuntime) =>
      projectCatalogDescribe(name, flags, rootRuntime, requestId, catalogDeadline(rootRuntime)));
  }
  return failure(requestId, "UNKNOWN_TOOLS_COMMAND", "Expected tools group, tools group list <group>, tools describe <tool>, tools run <tool>, or tools search <k1|k2|...>.", [
    "Run unitycli tools group to see tool categories.",
    "Run unitycli tools describe <tool> for full metadata.",
  ]);
}

/** 公开 Tool 参数只允许 JSON input 与明确命令控制项。 */
function rejectUnknownToolFlags(
  command: string | undefined,
  positional: string[],
  flags: Record<string, string | boolean>,
  requestId: string,
): CliResult | undefined {
  const common = ["instance", "pretty"];
  const pagedDiscovery = command === "search" || command === "group" && positional[0] === "list";
  const allowed = command === "run"
    ? [...common, "input", "input-file", "stdin", "dry-run", "timeout-ms", "artifact-dir", "runtime"]
    : pagedDiscovery ? [...common, "page-size", "cursor"] : common;
  const unknown = Object.keys(flags).find((flag) => !allowed.includes(flag));
  if (!unknown) return undefined;
  return failure(requestId, "E_UNKNOWN_OPTION", `Unknown option for tools ${command ?? ""}: --${unknown}.`, [
    command === "run" ? "Put business fields inside --input <json>." : "Run unitycli --help for the supported options.",
  ], { option: `--${unknown}`, command: `tools ${command ?? ""}`.trim(), positional });
}

function pageSeed(flags: Record<string, string | boolean>): CatalogPageRequest {
  return {
    ...(flags["page-size"] !== undefined ? { pageSize: Number(flags["page-size"]) } : {}),
    ...(typeof flags.cursor === "string" ? { cursor: flags.cursor } : {}),
  };
}

function catalogDeadline(runtime: ToolsRuntime): number | undefined {
  return runtime.rootAdmissions?.editor?.executionAbsoluteDeadlineMs
    ?? runtime.rootAdmissions?.runtime?.executionAbsoluteDeadlineMs
    ?? runtime.rootAdmission?.executionAbsoluteDeadlineMs;
}
