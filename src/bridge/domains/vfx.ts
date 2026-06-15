import { boolFlag, compactArgs, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const vfxBridgeSpecs: BridgeCommandSpec[] = [
  { group: "vfx", command: "graph", bridgeCommand: "vfx.graph", buildArgs: vfxArgs },
  { group: "vfx", command: "effect", bridgeCommand: "vfx.effect", buildArgs: vfxArgs }
];

function vfxArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "vfx.graph") {
    return compactArgs({
      action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? (flags.create ? "create" : "info"),
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "graph"),
      overwrite: boolFlag(flags, "overwrite")
    });
  }
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    graph: stringFlag(flags, "graph") ?? stringFlag(flags, "path"),
    add: boolFlag(flags, "add")
  });
}
