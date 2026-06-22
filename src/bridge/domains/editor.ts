import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const editorBridgeSpecs: BridgeCommandSpec[] = [
  { group: "editor", command: "console", bridgeCommand: "editor.console", buildArgs: editorArgs },
  { group: "editor", command: "state", bridgeCommand: "editor.state", buildArgs: editorArgs },
  { group: "editor", command: "play", bridgeCommand: "editor.play", buildArgs: editorArgs },
  { group: "editor", command: "pause", bridgeCommand: "editor.pause", buildArgs: editorArgs },
  { group: "editor", command: "stop", bridgeCommand: "editor.stop", buildArgs: editorArgs },
  { group: "editor", command: "undo", bridgeCommand: "editor.undo", buildArgs: editorArgs },
  { group: "editor", command: "redo", bridgeCommand: "editor.redo", buildArgs: editorArgs },
  { group: "editor", command: "resolution", bridgeCommand: "editor.resolution", buildArgs: editorArgs },
  { group: "editor", command: "menu", bridgeCommand: "editor.menu", buildArgs: editorArgs },
  { group: "project", command: "tag", bridgeCommand: "project.tag", buildArgs: editorArgs },
  { group: "project", command: "layer", bridgeCommand: "project.layer", buildArgs: editorArgs }
];

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
    return compactArgs({ action: stringFlag(flags, "action"), name: positionalTarget ?? stringFlag(flags, "name"), index: numberFlag(flags, "index") });
  }
  return compactArgs({ ...queryArgs(flags), path: positionalTarget ?? stringFlag(flags, "path"), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
}
