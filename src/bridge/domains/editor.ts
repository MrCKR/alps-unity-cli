import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const editorBridgeSpecs: BridgeCommandSpec[] = [
  { group: "editor", command: "play", bridgeCommand: "project-tool.run", toolName: "editor.play", buildArgs: editorArgs },
  { group: "editor", command: "pause", bridgeCommand: "project-tool.run", toolName: "editor.pause", buildArgs: editorArgs },
  { group: "editor", command: "stop", bridgeCommand: "project-tool.run", toolName: "editor.stop", buildArgs: editorArgs },
  { group: "editor", command: "undo", bridgeCommand: "project-tool.run", toolName: "editor.undo", buildArgs: editorArgs },
  { group: "editor", command: "redo", bridgeCommand: "project-tool.run", toolName: "editor.redo", buildArgs: editorArgs },
  { group: "editor", command: "resolution", bridgeCommand: "project-tool.run", toolName: "editor.resolution", buildArgs: editorArgs, destructiveHint: true },
  { group: "editor", command: "menu", bridgeCommand: "project-tool.run", toolName: "editor.menu", buildArgs: editorArgs, destructiveHint: true },
  { group: "editor", command: "selection-set", bridgeCommand: "project-tool.run", toolName: "editor.selection.set", buildArgs: selectionSetArgs, supportsDryRun: true },
  { group: "editor", command: "active-tool-set", bridgeCommand: "project-tool.run", toolName: "editor.active-tool.set", buildArgs: activeToolSetArgs },
  { group: "project", command: "tag-add", bridgeCommand: "project-tool.run", toolName: "project.tag.add", buildArgs: projectTagArgs, destructiveHint: true },
  { group: "project", command: "tag-remove", bridgeCommand: "project-tool.run", toolName: "project.tag.remove", buildArgs: projectTagArgs, destructiveHint: true },
  { group: "project", command: "layer-add", bridgeCommand: "project-tool.run", toolName: "project.layer.add", buildArgs: projectLayerArgs, destructiveHint: true },
  { group: "project", command: "layer-remove", bridgeCommand: "project-tool.run", toolName: "project.layer.remove", buildArgs: projectLayerArgs, destructiveHint: true }
];

// 读取 project Tag 和 Layer Tool 的结构化输入字段。
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
  if (command === "editor.undo" || command === "editor.redo") {
    return compactArgs({ completionProbe: structuredInput(flags).completionProbe });
  }
  if (command === "editor.play" || command === "editor.pause" || command === "editor.stop") {
    return compactArgs({ ...queryArgs(flags), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
  }
  if (command === "editor.resolution") {
    return compactArgs({ width: numberFlag(flags, "width"), height: numberFlag(flags, "height"), fullscreen: boolFlag(flags, "fullscreen"), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
  }
  return compactArgs({ ...queryArgs(flags), path: positionalTarget ?? stringFlag(flags, "path"), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
}

function projectTagArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ name: positionalTarget ?? stringFlag(flags, "name") ?? stringField(flags, "name") });
}

function projectLayerArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ name: positionalTarget ?? stringFlag(flags, "name") ?? stringField(flags, "name"), index: numberFlag(flags, "index") ?? numberField(flags, "index") });
}


function selectionSetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ instanceId: numberFlag(flags, "instance-id"), target: positionalTarget ?? stringFlag(flags, "target"), path: stringFlag(flags, "path"), dryRun: boolFlag(flags, "dry-run") });
}

function activeToolSetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ tool: positionalTarget ?? stringFlag(flags, "tool") ?? stringFlag(flags, "name") });
}
