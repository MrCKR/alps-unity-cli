import type { ToolMetadata } from "../../src/tools/metadata/index.js";

/** 构造 live bridge 发现返回的工具 descriptor，供行为测试复用。 */
export function makeLiveTool(overrides: Partial<ToolMetadata> & Pick<ToolMetadata, "name" | "summary" | "description" | "group">): ToolMetadata {
  const tool: ToolMetadata = {
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    outputSchema: { type: "object", properties: {}, additionalProperties: false },
    destructiveHint: false,
    handler: "bridge",
    supportStatus: "live-bridge",
    playModeSupport: "supported",
    target: "editor",
    source: "built-in",
    ...overrides
  };
  return {
    ...tool,
    examples: tool.examples ?? [{
      description: `Run ${tool.name}.`,
      argv: ["tools", "run", tool.name, "--input", "{}"]
    }]
  };
}

/** 代表性的内置 C# 工具，用于 tools group/search/describe/run 的 live discovery 测试。 */
export function sceneHierarchyTool(overrides: Partial<ToolMetadata> = {}): ToolMetadata {
  return makeLiveTool({
    name: "scene.hierarchy",
    summary: "Read the active scene hierarchy with paging.",
    description: "Read the active scene hierarchy with paging, depth, and filter controls.",
    group: "scene",
    inputSchema: {
      type: "object",
      properties: {
        pageSize: { type: "integer" },
        cursor: { type: "string" },
        maxDepth: { type: "integer" }
      }
    },
    ...overrides
  });
}

/** 代表性的 extension 工具，用于扩展可用性与 live discovery 测试。 */
export function probuilderPingTool(overrides: Partial<ToolMetadata> = {}): ToolMetadata {
  return makeLiveTool({
    name: "probuilder.ping",
    summary: "Check whether the ProBuilder bridge handlers respond.",
    description: "Check whether the ProBuilder bridge handlers respond for the active Unity target.",
    group: "probuilder",
    source: "extension",
    requiresPackages: ["com.unity.probuilder"],
    ...overrides
  });
}

/** 代表性的 extension 写工具。 */
export function probuilderDeleteFacesTool(overrides: Partial<ToolMetadata> = {}): ToolMetadata {
  return makeLiveTool({
    name: "probuilder.delete-faces",
    summary: "Delete selected ProBuilder faces on a mesh.",
    description: "Delete selected ProBuilder faces on a mesh and rebuild the editable mesh state.",
    group: "probuilder",
    source: "extension",
    destructiveHint: true,
    requiresPackages: ["com.unity.probuilder"],
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    ...overrides
  });
}

/** 代表性的项目自定义工具。 */
export function projectAuditTool(overrides: Partial<ToolMetadata> = {}): ToolMetadata {
  return makeLiveTool({
    name: "project.audit",
    summary: "Audit project state.",
    description: "Audit project state and return project-local diagnostics.",
    group: "project",
    source: "project",
    ...overrides
  });
}
