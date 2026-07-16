import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../../extension-availability.js";
import { catalogFlags, loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { supportStatusForTool, toolCliCommand, toolSource } from "../metadata/index.js";

export interface ToolsGroupListRequest { group: string }
export interface ToolsGroupListItem { name: string; summary: string; group: string; source: string; destructiveHint: boolean; playModeSupport?: string; supportStatus: string; target: string; requiresPackages?: string[]; cliCommand: string; extensionAvailability?: string; extensionAvailabilityError?: string; extensionId?: string; missingPackageIds?: string[]; nextActions?: string[] }
export interface ToolsGroupListResult { command: string; group: string; count: number; tools: ToolsGroupListItem[]; nextActions: string[] }

export default defineTool<ToolsGroupListRequest, ToolsGroupListResult>({
  name: "tools.group-list",
  summary: "List tools in a specific group with short routing summaries and support status.",
  description: "List tools in a group from the shared TS-local and live Bridge catalog.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "List tools in the camera group", argv: ["tools", "run", "tools.group-list", "--input", "{\"group\":\"camera\"}"] }],
  async execute(request, context) {
    if (!request.group) throw new StructuredToolError("TOOLS_GROUP_LIST_NAME_REQUIRED", "Pass a group name: unity_cli tools group list <group>.", ["Run unity_cli tools group to see all tool groups."]);
    const flags = catalogFlags(request as unknown as Record<string, unknown>, context.flags);
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
        playModeSupport: tool.playModeSupport,
        supportStatus: supportStatusForTool(tool),
        target: tool.target ?? "editor",
        ...(tool.requiresPackages ? { requiresPackages: tool.requiresPackages } : {}),
        cliCommand: toolCliCommand(tool),
        ...(availability.extensionId ? {
          extensionAvailability: availability.available ? availability.availabilityResult?.status ?? "available" : "unavailable",
          extensionId: availability.extensionId,
          ...(availability.available ? {} : {
            extensionAvailabilityError: availability.errorCode,
            missingPackageIds: availability.availabilityResult?.missingPackageIds ?? tool.requiresPackages ?? [],
            nextActions: availability.nextActions
          })
        } : {})
      };
    }));
    return { command: "tools.group.list", group: request.group, count: tools.length, tools, nextActions: ["Run unity_cli tools describe <tool> for full metadata including schema.", "Run unity_cli tools run <tool> to execute a tool."] };
  }
});
