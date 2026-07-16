import { builtinToolGroups, builtinToolMetadata } from "../../tools/metadata/index.js";
import { defineContext } from "../define.js";

export interface ToolsGroupsRequest {}

export interface ToolsGroupEntry {
  name: string;
  toolCount: number;
  cliCommand: string;
}

export interface ToolsGroupsResult {
  groups: ToolsGroupEntry[];
  count: number;
}

/** 从 CLI 本地 registry 读取工具分组,不连接 Unity。 */
function readToolGroups(): ToolsGroupsResult {
  const tools = builtinToolMetadata();
  const counts = new Map<string, number>();
  for (const tool of tools) {
    counts.set(tool.group, (counts.get(tool.group) ?? 0) + 1);
  }
  const groups = builtinToolGroups()
    .sort()
    .map((group) => ({ name: group, toolCount: counts.get(group) ?? 0, cliCommand: `tools group list ${group}` }));
  return { groups, count: groups.length };
}

export default defineContext<ToolsGroupsRequest, ToolsGroupsResult>({
  name: "tools.groups",
  description: "Read TS-local tool groups with counts and discovery commands from the local CLI registry; no Unity connection is required.",
  category: "tools",
  source: "built-in",
  target: "editor",
  availabilityScope: "always",
  playModeSupport: "supported",
  supportStatus: "cli-only",
  refreshBehavior: "static",
  examples: [{ description: "List local tool groups", argv: ["context", "read", "tools.groups", "--input", "{}"], input: {} }],
  read() {
    return readToolGroups();
  }
});
