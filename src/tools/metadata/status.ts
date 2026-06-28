import type { ToolMetadata, ToolSupportStatus } from "./types.js";

export function supportStatusForTool(tool: ToolMetadata): ToolSupportStatus {
  if (tool.supportStatus) {
    return tool.supportStatus;
  }

  switch (tool.handler) {
    case "bridge":
      return "live-bridge";
    case "bridge-runtime":
      return "live-bridge-runtime";
    case "job":
      return "batch-job";
    case "offline":
      return "cli-only";
    case "hybrid":
      return "bridge-job";
    default:
      return "planned";
  }
}
