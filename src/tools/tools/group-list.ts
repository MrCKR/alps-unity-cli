import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../../extension-availability.js";
import { loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { toolAvailabilityScope, toolSource } from "../metadata/index.js";

export interface ToolsGroupListRequest { group: string }
export interface ToolsGroupListItem { name: string; summary: string; group: string; source: string; destructiveHint: boolean; playModeSupport: string; target: string; availabilityScope: string; available: boolean; unavailableReason?: string; requiresPackages?: string[]; extensionId?: string }
export interface ToolsGroupListResult { group: string; count: number; tools: ToolsGroupListItem[] }

export default defineTool<ToolsGroupListRequest, ToolsGroupListResult>({
  name: "tools.group-list",
  summary: "List direct tools in a specific group with routing summaries and current availability.",
  description: "List every Tool in one group from the connected Unity instance catalog.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "List tools in the camera group", argv: ["tools", "run", "tools.group-list", "--input", "{\"group\":\"camera\"}"] }],
  async execute(request, context) {
    if (!request.group) throw new StructuredToolError("TOOLS_GROUP_LIST_NAME_REQUIRED", "Pass a group name: unity_cli tools group list <group>.", ["Run unity_cli tools group to see all tool groups."]);
    const flags = context.flags ?? {};
    const catalog = await loadToolCatalog(flags, context, context.requestId);
    if (catalog.ok === false) throw StructuredToolError.from(catalog.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    const availabilityFlags = await extensionAvailabilityFlags(flags, context);
    const matches = catalog.tools.filter((tool) => tool.group === request.group);
    if (matches.length === 0) throw new StructuredToolError("E_TOOL_GROUP_NOT_FOUND", `Tool group not found: ${request.group}.`, ["Run unity_cli tools group to see all tool groups."]);
    const tools = await Promise.all(matches.map(async (tool) => {
      const availability = await checkToolExtensionAvailability(tool, availabilityFlags);
      return {
        name: tool.name,
        summary: tool.summary,
        group: tool.group,
        source: toolSource(tool),
        destructiveHint: tool.destructiveHint,
        playModeSupport: tool.playModeSupport ?? "supported",
        target: tool.target ?? "editor",
        availabilityScope: toolAvailabilityScope(tool),
        available: availability.available && (tool.available ?? true),
        ...(!availability.available ? { unavailableReason: `${availability.errorCode}: ${availability.message}` } : tool.unavailableReason ? { unavailableReason: tool.unavailableReason } : {}),
        ...(tool.requiresPackages?.length ? { requiresPackages: tool.requiresPackages } : {}),
        ...(availability.extensionId ? { extensionId: availability.extensionId } : {})
      };
    }));
    return { group: request.group, count: tools.length, tools };
  }
});
