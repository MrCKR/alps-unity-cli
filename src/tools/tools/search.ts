import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../../extension-availability.js";
import { loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { toolAvailabilityScope, toolSource } from "../metadata/index.js";

export interface ToolsSearchRequest { keyword: string }
export interface ToolsSearchItem { name: string; summary: string; group: string; source: string; destructiveHint: boolean; playModeSupport: string; target: string; availabilityScope: string; available: boolean; unavailableReason?: string; requiresPackages?: string[]; extensionId?: string }
export interface ToolsSearchResult { keyword: string; count: number; tools: ToolsSearchItem[] }

export default defineTool<ToolsSearchRequest, ToolsSearchResult>({
  name: "tools.search",
  summary: "Search tools by keyword across name, summary, description, and group.",
  description: "Search the complete Tool catalog owned by the connected Unity instance.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "Search for hierarchy tools", argv: ["tools", "run", "tools.search", "--input", "{\"keyword\":\"hierarchy\"}"] }],
  async execute(request, context) {
    if (!request.keyword) throw new StructuredToolError("TOOLS_SEARCH_KEYWORD_REQUIRED", "Pass a keyword: unity_cli tools search <keyword>.", ["Run unity_cli tools group to see tool groups."]);
    const flags = context.flags ?? {};
    const catalog = await loadToolCatalog(flags, context, `${context.requestId}_search_execution`);
    if (catalog.ok === false) throw StructuredToolError.from(catalog.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    const availabilityFlags = await extensionAvailabilityFlags(flags, context);
    const keyword = request.keyword.toLowerCase();
    const matches = catalog.tools.filter((tool) => [tool.name, tool.summary, tool.description, tool.group].some((value) => value.toLowerCase().includes(keyword)));
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
    return { keyword: request.keyword, count: tools.length, tools };
  }
});
