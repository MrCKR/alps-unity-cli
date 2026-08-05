import { catalogFlags, loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import { toolSource } from "../metadata/index.js";

export interface ToolsGroupRequest {}
export interface ToolsGroupItem { name: string; summary: string; toolCount: number; source: string }
export interface ToolsGroupResult { count: number; groups: ToolsGroupItem[] }

const GROUP_SUMMARIES: Record<string, string> = {
  docs: "Offline documentation lookup and search.",
  tools: "Tool catalog grouping, search, describe, and execution.",
  package: "Unity package inspection and package management.",
  scene: "Scene hierarchy, load, save, validation, and view operations.",
  probuilder: "ProBuilder shape, mesh, face, edge, and repair operations.",
  build: "Live Unity build execution with terminal BuildReport and artifact proof.",
  test: "Unity test execution and result collection.",
  editor: "Unity Editor refresh, diagnostics, and session controls.",
  reflect: "Offline C# and Unity type reflection search.",
  code: "C# code execution with explicit safety controls.",
  diagnose: "CLI, target, and Bridge capability diagnostics.",
  asset: "Unity asset discovery, import, editing, and refresh operations."
};

function discoveryError(result: { error?: { code: string; message: string; nextActions?: string[]; details?: unknown; transient?: boolean; retryAfterMs?: number } }): never {
  throw StructuredToolError.from(result.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
}

export default defineTool<ToolsGroupRequest, ToolsGroupResult>({
  name: "tools.group",
  summary: "List the available tool groups with counts.",
  description: "List tool groups from the shared TS-local and live Bridge catalog.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "List all tool groups", argv: ["tools", "run", "tools.group", "--input", "{}"] }],
  async execute(request, context) {
    const catalog = await loadToolCatalog(catalogFlags(request as unknown as Record<string, unknown>, context.flags), context, context.requestId);
    if (catalog.ok === false) discoveryError(catalog);
    const groups = new Map<string, { count: number; source: string }>();
    for (const tool of catalog.tools) {
      const current = groups.get(tool.group);
      groups.set(tool.group, { count: (current?.count ?? 0) + 1, source: current?.source ?? toolSource(tool) });
    }
    const items = [...groups].sort(([a], [b]) => a.localeCompare(b)).map(([name, value]) => ({
      name,
      summary: GROUP_SUMMARIES[name] ?? `${name} capability operations.`,
      toolCount: value.count,
      source: value.source
    }));
    return { count: items.length, groups: items };
  }
});
