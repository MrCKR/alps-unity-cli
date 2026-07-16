import { bridgeCommandRegistry } from "../../bridge/index.js";
import type { ToolHandler, ToolMetadata, ToolSupportStatus } from "./types.js";

const OFFLINE_TOOL_PREFIXES = ["docs.", "reflect."];
const OFFLINE_TOOL_NAMES = new Set([
  "build.cancel",
  "build.status",
  "diagnose.capabilities",
  "editor.launch-dialog.dismiss",
  "package.bridge-remove",
  "package.deploy",
  "package.info",
  "package.list",
  "package.registry.clear",
  "package.registry.get",
  "package.registry.set",
  "package.restore",
  "package.scoped-registry.add",
  "package.scoped-registry.list",
  "package.scoped-registry.remove",
  "package.search",
  "package.status",
  "tools.describe",
  "tools.group",
  "tools.group-list",
  "tools.search"
]);
const JOB_TOOL_NAMES = new Set(["build.run", "editor.refresh", "test.run"]);
const HYBRID_TOOL_NAMES = new Set([
  "editor.compile-diagnostics",
  "package.install",
  "package.remove",
  "package.resolve",
  "profiler.start",
  "tools.run"
]);
const HYBRID_TOOL_PREFIXES = ["probuilder.", "cinemachine.", "urp.", "hdrp."];

/** 推导内部执行路由；metadata 只在无法从注册表或命名规则判断时覆盖。 */
export function toolHandler(tool: Pick<ToolMetadata, "name" | "handler">): ToolHandler {
  if (tool.handler) return tool.handler;
  if (JOB_TOOL_NAMES.has(tool.name)) return "job";
  if (OFFLINE_TOOL_NAMES.has(tool.name) || OFFLINE_TOOL_PREFIXES.some((prefix) => tool.name.startsWith(prefix))) return "offline";
  if (HYBRID_TOOL_NAMES.has(tool.name) || HYBRID_TOOL_PREFIXES.some((prefix) => tool.name.startsWith(prefix))) return "hybrid";
  const spec = bridgeCommandRegistry.specs().find((entry) => entry.toolName === tool.name || entry.bridgeCommand === tool.name);
  if (spec) return spec.supportsJob ? "hybrid" : "bridge";
  return "bridge";
}

export function supportStatusForTool(tool: ToolMetadata): ToolSupportStatus {
  if (tool.supportStatus) {
    return tool.supportStatus;
  }

  switch (toolHandler(tool)) {
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
