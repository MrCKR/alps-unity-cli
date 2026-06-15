import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const CUSTOM_TOOL_TOOLS: ToolMetadata[] = [
  {
    name: "custom-tool.list",
    title: "List Custom Tools",
    description: "List locally-registered custom tools for the current project.",
    group: "custom-tool",
    inputSchema: objSchema({ project: { type: "string" }, instance: { type: "string" } }),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "custom-tool list",
    docsTopic: "custom-tool"
  },
  {
    name: "custom-tool.run",
    title: "Run Custom Tool",
    description: "Execute a registered custom tool with input payload.",
    group: "custom-tool",
    inputSchema: objSchema({ name: { type: "string" }, input: { type: "object" }, timeoutMs: { type: "integer" } }, ["name"]),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "custom-tool run",
    docsTopic: "custom-tool"
  }
];
