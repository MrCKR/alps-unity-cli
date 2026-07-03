import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const editorBridgeSpecs: BridgeCommandSpec[] = [
  { group: "editor", command: "console", bridgeCommand: "project-tool.run", toolName: "editor.console", buildArgs: editorArgs },
  { group: "editor", command: "state", bridgeCommand: "project-tool.run", toolName: "editor.state", buildArgs: editorArgs },
  { group: "editor", command: "play", bridgeCommand: "project-tool.run", toolName: "editor.play", buildArgs: editorArgs },
  { group: "editor", command: "pause", bridgeCommand: "project-tool.run", toolName: "editor.pause", buildArgs: editorArgs },
  { group: "editor", command: "stop", bridgeCommand: "project-tool.run", toolName: "editor.stop", buildArgs: editorArgs },
  { group: "editor", command: "undo", bridgeCommand: "project-tool.run", toolName: "editor.undo", buildArgs: editorArgs },
  { group: "editor", command: "redo", bridgeCommand: "project-tool.run", toolName: "editor.redo", buildArgs: editorArgs },
  { group: "editor", command: "resolution", bridgeCommand: "project-tool.run", toolName: "editor.resolution", buildArgs: editorArgs },
  { group: "editor", command: "menu", bridgeCommand: "project-tool.run", toolName: "editor.menu", buildArgs: editorArgs },
  { group: "editor", command: "compile", bridgeCommand: "project-tool.run", toolName: "editor.compile", buildArgs: editorCompileArgs, supportsDryRun: true },
  { group: "editor", command: "selection-set", bridgeCommand: "project-tool.run", toolName: "editor.selection.set", buildArgs: selectionSetArgs },
  { group: "editor", command: "active-tool-set", bridgeCommand: "project-tool.run", toolName: "editor.active-tool.set", buildArgs: activeToolSetArgs },
  { group: "project", command: "tag", bridgeCommand: "project-tool.run", toolName: "project.tag", buildArgs: editorArgs },
  { group: "project", command: "layer", bridgeCommand: "project-tool.run", toolName: "project.layer", buildArgs: editorArgs }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段（project.tag/layer 的 action/name/index）
function structuredInput(flags: BridgeFlags): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function stringField(flags: BridgeFlags, key: string): string | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "string" ? value : undefined;
}

function numberField(flags: BridgeFlags, key: string): number | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "number" ? value : undefined;
}

function editorArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "editor.console") {
    return compactArgs({ ...queryArgs(flags), type: stringFlag(flags, "type"), count: numberFlag(flags, "count") ?? numberFlag(flags, "limit"), clear: boolFlag(flags, "clear") });
  }
  if (command === "editor.state") {
    return compactArgs({ ...queryArgs(flags) });
  }
  if (command === "editor.play" || command === "editor.pause" || command === "editor.stop" || command === "editor.undo" || command === "editor.redo") {
    return compactArgs({ ...queryArgs(flags), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
  }
  if (command === "editor.resolution") {
    return compactArgs({ width: numberFlag(flags, "width"), height: numberFlag(flags, "height"), fullscreen: boolFlag(flags, "fullscreen"), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
  }
  if (command === "project.tag" || command === "project.layer") {
    return compactArgs({ action: stringFlag(flags, "action") ?? stringField(flags, "action"), name: positionalTarget ?? stringFlag(flags, "name") ?? stringField(flags, "name"), index: numberFlag(flags, "index") ?? numberField(flags, "index") });
  }
  return compactArgs({ ...queryArgs(flags), path: positionalTarget ?? stringFlag(flags, "path"), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
}

function editorCompileArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    refresh: boolFlag(flags, "refresh") ?? structured.refresh,
    waitReady: boolFlag(flags, "wait-ready") ?? structured.waitReady,
    timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") ?? structured.timeoutMs,
    dryRun: boolFlag(flags, "dry-run") ?? structured.dryRun
  });
}

function selectionSetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ instanceId: numberFlag(flags, "instance-id"), target: positionalTarget ?? stringFlag(flags, "target"), path: stringFlag(flags, "path"), dryRun: boolFlag(flags, "dry-run") });
}

function activeToolSetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ tool: positionalTarget ?? stringFlag(flags, "tool") ?? stringFlag(flags, "name") });
}
