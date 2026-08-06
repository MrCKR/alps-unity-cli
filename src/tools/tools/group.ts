import { loadToolCatalog } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface ToolsGroupRequest {}
export interface ToolsGroupItem { name: string; summary: string; toolCount: number }
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
  description: "List every Tool group owned by the connected Unity instance, including package-local CLI capabilities.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "List all tool groups", argv: ["tools", "run", "tools.group", "--input", "{}"] }],
  async execute(request, context) {
    const catalog = await loadToolCatalog(context.flags ?? {}, context, context.requestId);
    if (catalog.ok === false) discoveryError(catalog);
    const groups = new Map<string, number>();
    for (const tool of catalog.tools) groups.set(tool.group, (groups.get(tool.group) ?? 0) + 1);
    const items = [...groups].sort(([a], [b]) => a.localeCompare(b)).map(([name, toolCount]) => ({
      name,
      summary: GROUP_SUMMARIES[name] ?? `${name} capability operations.`,
      toolCount
    }));
    return { count: items.length, groups: items };
  }
});
