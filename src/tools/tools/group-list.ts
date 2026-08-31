import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../../extension-availability.js";
import { loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { groupSummary } from "./group-summaries.js";
import { paginateDiscovery, type DiscoveryPageInfo, type DiscoveryPageRequest } from "./pagination.js";

export interface ToolsGroupListRequest extends DiscoveryPageRequest { group: string }
export interface ToolsGroupListItem { name: string; summary: string; available?: false }
export interface ToolsGroupListResult { summary: string; pageInfo: DiscoveryPageInfo; tools: ToolsGroupListItem[] }

export default defineTool<ToolsGroupListRequest, ToolsGroupListResult>({
  name: "tools.group-list",
  summary: "List one tool group's routing summaries and availability in bounded cursor pages.",
  description: "List Tools in one group from the connected Unity instance catalog, sorted by exact Tool name. pageSize/cursor bound each page; use pageInfo.nextCursor unchanged to continue. Each tools[].name is the exact invocation name, group is only a filter and must never be prepended, and available appears only when false.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "List the first camera Tool page", argv: ["tools", "run", "tools.group-list", "--input", "{\"group\":\"camera\",\"pageSize\":20}"] }],
  async execute(request, context) {
    if (!request.group) throw new StructuredToolError("TOOLS_GROUP_LIST_NAME_REQUIRED", "Pass a group name: unitycli tools group list <group>.", ["Run unitycli tools group to see all tool groups."]);
    const flags = context.flags ?? {};
    const catalog = await loadToolCatalog(flags, context, `${context.requestId}_group_list_execution`);
    if (catalog.ok === false) throw StructuredToolError.from(catalog.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    const matches = catalog.tools.filter((tool) => tool.group === request.group).sort((left, right) => left.name.localeCompare(right.name));
    if (matches.length === 0) throw new StructuredToolError("E_TOOL_GROUP_NOT_FOUND", `Tool group not found: ${request.group}.`, ["Run unitycli tools group to see all tool groups."]);
    const page = paginateDiscovery(matches, request, "tools.group-list");
    const availabilityFlags = await extensionAvailabilityFlags(flags, context);
    const tools = await Promise.all(page.items.map(async (tool): Promise<ToolsGroupListItem> => {
      const availability = await checkToolExtensionAvailability(tool, availabilityFlags);
      const available = availability.available && (tool.available ?? true);
      return { name: tool.name, summary: tool.summary, ...(!available ? { available: false as const } : {}) };
    }));
    return { summary: groupSummary(request.group), pageInfo: page.pageInfo, tools };
  },
});
