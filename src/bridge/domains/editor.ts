import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const editorBridgeSpecs: BridgeCommandSpec[] = [
  { group: "editor", command: "console", bridgeCommand: "editor.console", buildArgs: editorArgs },
  { group: "editor", command: "state", bridgeCommand: "editor.state", buildArgs: editorArgs },
  { group: "editor", command: "play", bridgeCommand: "editor.play", buildArgs: editorArgs },
  { group: "editor", command: "pause", bridgeCommand: "editor.pause", buildArgs: editorArgs },
  { group: "editor", command: "stop", bridgeCommand: "editor.stop", buildArgs: editorArgs },
  { group: "editor", command: "menu", bridgeCommand: "editor.menu", buildArgs: editorArgs }
];

function editorArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "editor.console") {
    return compactArgs({ ...queryArgs(flags), type: stringFlag(flags, "type"), count: numberFlag(flags, "count") ?? numberFlag(flags, "limit"), clear: boolFlag(flags, "clear") });
  }
  if (command === "editor.state") {
    return compactArgs({ ...queryArgs(flags) });
  }
  if (command === "editor.play" || command === "editor.pause" || command === "editor.stop") {
    return compactArgs({ ...queryArgs(flags), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
  }
  return compactArgs({ ...queryArgs(flags), path: positionalTarget ?? stringFlag(flags, "path"), timeoutMs: numberFlag(flags, "timeout-ms") ?? numberFlag(flags, "timeout") });
}
