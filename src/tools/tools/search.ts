import { loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { paginateDiscovery, type DiscoveryPageInfo, type DiscoveryPageRequest } from "./pagination.js";

export interface ToolsSearchRequest extends DiscoveryPageRequest { keyword: string }
export interface ToolsSearchItem { name: string; summary: string; group: string }
export interface ToolsSearchResult { pageInfo: DiscoveryPageInfo; tools: ToolsSearchItem[] }

export default defineTool<ToolsSearchRequest, ToolsSearchResult>({
  name: "tools.search",
  summary: "Search tool routing metadata with `|`-separated OR keywords and bounded cursor pages.",
  description: "Search the complete Tool catalog owned by the connected Unity instance. keyword is split on `|`; a Tool matches when any token appears in its name, summary, description, or group (case-insensitive, OR semantics). Results are sorted by exact Tool name and paged with pageSize/cursor; use pageInfo.nextCursor unchanged to continue. Results contain routing summaries only - use tools describe for schema, availability, and full details.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [
    { description: "Search for hierarchy tools", argv: ["tools", "run", "tools.search", "--input", "{\"keyword\":\"hierarchy\"}"] },
    { description: "Search several capability areas in one bounded page", argv: ["tools", "run", "tools.search", "--input", "{\"keyword\":\"editor|console|screenshot\",\"pageSize\":20}"] },
  ],
  async execute(request, context) {
    const tokens = (request.keyword ?? "")
      .split("|")
      .map((token) => token.trim().toLowerCase())
      .filter((token) => token.length > 0);
    if (tokens.length === 0) {
      throw new StructuredToolError(
        "TOOLS_SEARCH_KEYWORD_REQUIRED",
        "Pass one or more `|`-separated keywords: unitycli tools search <k1|k2|...>.",
        ["Run unitycli tools group to see tool groups."],
      );
    }
    const flags = context.flags ?? {};
    const catalog = await loadToolCatalog(flags, context, `${context.requestId}_search_execution`);
    if (catalog.ok === false) {
      throw StructuredToolError.from(catalog.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    }
    const matches = catalog.tools
      .filter((tool) => tokens.some((token) =>
        [tool.name, tool.summary, tool.description, tool.group].some((value) => value.toLowerCase().includes(token))))
      .sort((left, right) => left.name.localeCompare(right.name));
    const page = paginateDiscovery(matches, request, "tools.search");
    return {
      pageInfo: page.pageInfo,
      tools: page.items.map((tool) => ({ name: tool.name, summary: tool.summary, group: tool.group })),
    };
  },
});
