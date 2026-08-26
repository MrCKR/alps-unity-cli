import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../../extension-availability.js";
import { loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { groupSummary } from "./group-summaries.js";

export interface ToolsGroupListRequest { group: string }
export interface ToolsGroupListItem { name: string; summary: string; available: boolean }
export interface ToolsGroupListResult { group: string; summary: string; count: number; nameSemantics: { exact: true; groupPrefix: false }; tools: ToolsGroupListItem[] }

export default defineTool<ToolsGroupListRequest, ToolsGroupListResult>({
  name: "tools.group-list",
  summary: "List direct tools in a specific group with routing summaries and current availability.",
  description: "List every Tool in one group from the connected Unity instance catalog. Each tools[].name is the exact invocation name; group is only a filter and must never be prepended.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "List tools in the camera group", argv: ["tools", "run", "tools.group-list", "--input", "{\"group\":\"camera\"}"] }],
  async execute(request, context) {
    if (!request.group) throw new StructuredToolError("TOOLS_GROUP_LIST_NAME_REQUIRED", "Pass a group name: unitycli tools group list <group>.", ["Run unitycli tools group to see all tool groups."]);
    const flags = context.flags ?? {};
    const catalog = await loadToolCatalog(flags, context, `${context.requestId}_group_list_execution`);
    if (catalog.ok === false) throw StructuredToolError.from(catalog.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    const availabilityFlags = await extensionAvailabilityFlags(flags, context);
    const matches = catalog.tools.filter((tool) => tool.group === request.group);
    if (matches.length === 0) throw new StructuredToolError("E_TOOL_GROUP_NOT_FOUND", `Tool group not found: ${request.group}.`, ["Run unitycli tools group to see all tool groups."]);
    const tools = await Promise.all(matches.map(async (tool) => {
      const availability = await checkToolExtensionAvailability(tool, availabilityFlags);
      return {
        name: tool.name,
        summary: tool.summary,
        available: availability.available && (tool.available ?? true)
      };
    }));
    return { group: request.group, summary: groupSummary(request.group), count: tools.length, nameSemantics: { exact: true, groupPrefix: false }, tools };
  }
});
