import type { ToolMetadata, ToolSupportStatus } from "./types.js";

const SUPPORT_STATUS_LABELS: Record<ToolSupportStatus, string> = {
  "live-bridge": "已路由可执行",
  "bridge-job": "已路由可执行 + 可 batch/job",
  "batch-job": "仅 batch/job",
  "cli-only": "仅 CLI 离线",
  planned: "规划中"
};

const SUPPORT_STATUS_DESCRIPTIONS: Record<ToolSupportStatus, string> = {
  "live-bridge": "Bundled Unity Editor Bridge serves this command through POST /bridge/execute.",
  "bridge-job": "Command supports live Bridge /bridge/execute and batch/job runner channels.",
  "batch-job": "Runs through Unity batchmode/job execution rather than the live Bridge request path.",
  "cli-only": "Runs locally without a Unity Editor Bridge connection.",
  planned: "Declared in the CLI contract and reserved for future bundled Unity Bridge handler coverage."
};

export function supportStatusForTool(tool: ToolMetadata): ToolSupportStatus {
  if (tool.supportStatus) {
    return tool.supportStatus;
  }

  switch (tool.handler) {
    case "bridge":
      return "live-bridge";
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

export function supportStatusLabel(status: ToolSupportStatus): string {
  return SUPPORT_STATUS_LABELS[status];
}

export function supportStatusDescription(status: ToolSupportStatus): string {
  return SUPPORT_STATUS_DESCRIPTIONS[status];
}
