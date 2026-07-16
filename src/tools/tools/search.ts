import { catalogFlags, loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { supportStatusForTool, toolAvailabilityScope, toolCliCommand, toolSource } from "../metadata/index.js";

export interface ToolsSearchRequest { keyword: string; format?: "json" | "text" }
export interface ToolsSearchItem { name: string; summary: string; group: string; source: string; cliCommand?: string; destructiveHint: boolean; playModeSupport?: string; supportStatus: string; target: string; availabilityScope?: string; requiresPackages?: string[] }
export interface ToolsSearchResult { command: string; keyword: string; count: number; tools: ToolsSearchItem[]; text?: string }

export default defineTool<ToolsSearchRequest, ToolsSearchResult>({
  name: "tools.search",
  summary: "Search tools by keyword across name, summary, description, and group.",
  description: "Search the shared TS-local and live Bridge catalog.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "Search for hierarchy tools", argv: ["tools", "run", "tools.search", "--input", "{\"keyword\":\"hierarchy\"}"] }],
  async execute(request, context) {
    if (!request.keyword) throw new StructuredToolError("TOOLS_SEARCH_KEYWORD_REQUIRED", "Pass a keyword: unity_cli tools search <keyword>.", ["Run unity_cli tools group to see tool groups."]);
    const catalog = await loadToolCatalog(catalogFlags(request as unknown as Record<string, unknown>, context.flags), context, context.requestId);
    if (catalog.ok === false) throw StructuredToolError.from(catalog.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    const keyword = request.keyword.toLowerCase();
    const tools = catalog.tools.filter((tool) => [tool.name, tool.summary, tool.description, tool.group].some((value) => value.toLowerCase().includes(keyword))).map((tool) => ({
      name: tool.name,
      summary: tool.summary,
      group: tool.group,
      source: toolSource(tool),
      destructiveHint: tool.destructiveHint,
      playModeSupport: tool.playModeSupport,
      supportStatus: supportStatusForTool(tool),
      cliCommand: toolCliCommand(tool),
      target: tool.target ?? "editor",
      availabilityScope: toolAvailabilityScope(tool),
      ...(tool.requiresPackages ? { requiresPackages: tool.requiresPackages } : {})
    }));
    return { command: "tools.search", keyword: request.keyword, count: tools.length, tools, ...(request.format === "text" ? { text: tools.map((tool) => `${tool.name} [${tool.group}] ${tool.supportStatus} - ${tool.summary}`).join("\n") } : {}) };
  }
});
