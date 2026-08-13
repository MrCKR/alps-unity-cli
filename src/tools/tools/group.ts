import { loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { groupSummary } from "./group-summaries.js";

export interface ToolsGroupRequest {}
export interface ToolsGroupItem { name: string; summary: string; toolCount: number }
export interface ToolsGroupResult { count: number; groups: ToolsGroupItem[] }

function discoveryError(result: { error?: { code: string; message: string; nextActions?: string[]; details?: unknown; transient?: boolean; retryAfterMs?: number } }): never {
  throw StructuredToolError.from(result.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
}

export default defineTool<ToolsGroupRequest, ToolsGroupResult>({
  name: "tools.group",
  summary: "List the available tool groups with counts.",
  description: "List every Tool group owned by the connected Unity instance, including package-local CLI capabilities.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "List all tool groups", argv: ["tools", "run", "tools.group", "--input", "{}"] }],
  async execute(request, context) {
    const catalog = await loadToolCatalog(context.flags ?? {}, context, `${context.requestId}_group_execution`);
    if (catalog.ok === false) discoveryError(catalog);
    const groups = new Map<string, number>();
    for (const tool of catalog.tools) groups.set(tool.group, (groups.get(tool.group) ?? 0) + 1);
    const items = [...groups].sort(([a], [b]) => a.localeCompare(b)).map(([name, toolCount]) => ({
      name,
      summary: groupSummary(name),
      toolCount
    }));
    return { count: items.length, groups: items };
  }
});
