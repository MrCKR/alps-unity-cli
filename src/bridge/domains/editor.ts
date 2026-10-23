import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const editorBridgeSpecs: BridgeCommandSpec[] = [
  { group: "editor", command: "play-mode-start", bridgeCommand: "catalog.editor.run", toolName: "editor.play-mode-start", buildArgs: editorArgs },
  { group: "editor", command: "pause", bridgeCommand: "catalog.editor.run", toolName: "editor.pause", buildArgs: editorArgs },
  { group: "editor", command: "play-mode-stop", bridgeCommand: "catalog.editor.run", toolName: "editor.play-mode-stop", buildArgs: editorArgs },
  { group: "editor", command: "undo", bridgeCommand: "catalog.editor.run", toolName: "editor.undo", buildArgs: editorArgs },
  { group: "editor", command: "redo", bridgeCommand: "catalog.editor.run", toolName: "editor.redo", buildArgs: editorArgs },
  { group: "editor", command: "resolution", bridgeCommand: "catalog.editor.run", toolName: "editor.resolution", buildArgs: editorArgs, destructiveHint: true },
  { group: "editor", command: "menu-execute", bridgeCommand: "catalog.editor.run", toolName: "editor.menu-execute", buildArgs: editorArgs, destructiveHint: true },
  { group: "editor", command: "menu-list", bridgeCommand: "catalog.editor.run", toolName: "editor.menu-list", buildArgs: editorArgs },
  { group: "editor", command: "selection-set", bridgeCommand: "catalog.editor.run", toolName: "editor.selection-set", buildArgs: selectionSetArgs, supportsDryRun: true },
  { group: "editor", command: "active-tool-set", bridgeCommand: "catalog.editor.run", toolName: "editor.active-tool-set", buildArgs: activeToolSetArgs },
  { group: "project-settings", command: "tag-add", bridgeCommand: "catalog.editor.run", toolName: "project-settings.tag-add", buildArgs: projectTagArgs, destructiveHint: true },
  { group: "project-settings", command: "tag-remove", bridgeCommand: "catalog.editor.run", toolName: "project-settings.tag-remove", buildArgs: projectTagArgs, destructiveHint: true },
  { group: "project-settings", command: "layer-add", bridgeCommand: "catalog.editor.run", toolName: "project-settings.layer-add", buildArgs: projectLayerArgs, destructiveHint: true },
  { group: "project-settings", command: "layer-remove", bridgeCommand: "catalog.editor.run", toolName: "project-settings.layer-remove", buildArgs: projectLayerArgs, destructiveHint: true }
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
  if (command === "editor.play-mode-start" || command === "editor.pause" || command === "editor.play-mode-stop") {
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
